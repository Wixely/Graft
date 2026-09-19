using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Graft.Core;
using Graft.Git;
using Graft.Storage;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: Graft.Git.Acceptance <Graft.Server DLL or executable>");
    return 2;
}

string serverPath = Path.GetFullPath(args[0]);
string root = Path.Combine(Path.GetTempPath(), "graft-mvp05-" + Guid.NewGuid().ToString("N"));
string databasePath = Path.Combine(root, "graft.db");
string repositoriesRoot = Path.Combine(root, "repositories");
string gitConfig = Path.Combine(root, "empty.gitconfig");
Directory.CreateDirectory(root);
File.WriteAllText(gitConfig, "");
int assertions = 0;
Process? server = null;
Task? outputReader = null;
Task<string>? errorReader = null;
ConcurrentQueue<string> serverLines = new();

try
{
    string adminPassword = Password();
    string alicePassword = Password();
    string bobPassword = Password();
    string carolPassword = Password();
    TokenGrant aliceToken;
    TokenGrant aliceApiToken;
    TokenGrant aliceReadApiToken;
    TokenGrant bobToken;
    TokenGrant bobApiToken;
    ProjectInfo project;
    UserAccount bob;
    AuthenticatedIdentity aliceIdentity;

    await using (GraftStore fixture = await GraftStore.OpenAsync(databasePath))
    {
        await fixture.BootstrapAdministratorAsync("admin", adminPassword);
        SessionGrant adminSession = Required(await fixture.LoginAsync("admin", adminPassword), "admin login");
        AuthenticatedIdentity admin = Required(await fixture.ResolveSessionAsync(adminSession.Secret), "admin identity");
        UserAccount alice = await fixture.CreateUserAsync(admin, "alice", alicePassword);
        bob = await fixture.CreateUserAsync(admin, "bob", bobPassword);
        UserAccount carol = await fixture.CreateUserAsync(admin, "carol", carolPassword);
        SessionGrant aliceSession = Required(await fixture.LoginAsync(alice.Username, alicePassword), "alice login");
        SessionGrant bobSession = Required(await fixture.LoginAsync(bob.Username, bobPassword), "bob login");
        aliceIdentity = Required(await fixture.ResolveSessionAsync(aliceSession.Secret), "alice identity");
        AuthenticatedIdentity bobIdentity = Required(await fixture.ResolveSessionAsync(bobSession.Secret), "bob identity");
        SessionGrant carolSession = Required(await fixture.LoginAsync(carol.Username, carolPassword), "carol login");
        AuthenticatedIdentity carolIdentity = Required(await fixture.ResolveSessionAsync(carolSession.Secret), "carol identity");

        project = await fixture.CreateProjectWithRepositoryAsync(aliceIdentity, "durable", "Durable repository",
            (created, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Repository initialized = new(new DurableRepositoryStorage(Path.Combine(repositoriesRoot, created.StorageId)));
                Check(initialized.Refs.Count == 0, "empty durable repository initialized");
                return Task.CompletedTask;
            });
        await fixture.SetMembershipAsync(aliceIdentity, project.Id, bob.Id, ProjectRole.Developer);
        aliceToken = await fixture.CreateTokenAsync(aliceIdentity, "clone-token", TokenScope.ReadRepository, DateTimeOffset.UtcNow.AddHours(1));
        aliceApiToken = await fixture.CreateTokenAsync(aliceIdentity, "api-token", TokenScope.Api | TokenScope.ReadRepository, DateTimeOffset.UtcNow.AddHours(1));
        aliceReadApiToken = await fixture.CreateTokenAsync(aliceIdentity, "read-api-token", TokenScope.ReadApi, DateTimeOffset.UtcNow.AddHours(1));
        bobToken = await fixture.CreateTokenAsync(bobIdentity, "push-token", TokenScope.WriteRepository, DateTimeOffset.UtcNow.AddHours(1));
        bobApiToken = await fixture.CreateTokenAsync(bobIdentity, "member-api-token", TokenScope.ReadApi | TokenScope.WriteRepository, DateTimeOffset.UtcNow.AddHours(1));
        await fixture.CreateProjectWithRepositoryAsync(carolIdentity, "private", "Carol private repository",
            (created, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, created.StorageId));
                return Task.CompletedTask;
            });
    }

    string address = await StartServer();
    string bobUrl = AuthenticatedUrl(address, "bob", bobToken.Secret, "alice/durable.git");
    string aliceUrl = AuthenticatedUrl(address, "alice", aliceToken.Secret, "alice/durable.git");

    await Git(root, ["clone", "--no-checkout", bobUrl, "empty-clone"]);
    Check(Directory.Exists(Path.Combine(root, "empty-clone", ".git")), "authenticated empty clone");

    string source = Path.Combine(root, "source");
    Directory.CreateDirectory(source);
    await Git(source, ["init", "--initial-branch=main"]);
    File.WriteAllText(Path.Combine(source, "readme.txt"), "durable synthetic repository\n");
    File.WriteAllText(Path.Combine(source, "hostile.html"), "<script>throw new Error('must remain text')</script>\n");
    File.WriteAllBytes(Path.Combine(source, "binary.dat"), [0, 1, 2, 0, 255]);
    await Git(source, ["add", "."]);
    await Git(source, ["commit", "-m", "Initial durable commit"]);
    await Git(source, ["remote", "add", "origin", bobUrl]);
    await Git(source, ["push", "origin", "main"]);
    string firstHead = (await Git(source, ["rev-parse", "HEAD"])).Trim();
    Check(firstHead.Length == 40, "initial push");

    await StopServer();
    address = await StartServer();
    aliceUrl = AuthenticatedUrl(address, "alice", aliceToken.Secret, "alice/durable.git");
    bobUrl = AuthenticatedUrl(address, "bob", bobToken.Secret, "alice/durable.git");
    string clone = Path.Combine(root, "clone");
    await Git(root, ["clone", "--no-checkout", aliceUrl, "clone"]);
    Check((await Git(clone, ["rev-parse", "refs/remotes/origin/main"])).Trim() == firstHead, "restart preserves refs and objects");
    await Git(clone, ["fsck", "--full", "--strict"]);
    Check(true, "restart clone passes fsck");

    await CheckApi(address, aliceApiToken.Secret, aliceReadApiToken.Secret, aliceToken.Secret, bobApiToken.Secret, project.Id, firstHead);
    await CheckBrowser(address, "alice", alicePassword, project.Id);
    await CheckPersonalAccessTokens(address, "alice", alicePassword, project.Id);
    await CheckUserAdministration(address, adminPassword, Password());

    File.AppendAllText(Path.Combine(source, "readme.txt"), "second revision\n");
    await Git(source, ["add", "."]);
    await Git(source, ["commit", "-m", "Second durable commit"]);
    await Git(source, ["remote", "set-url", "origin", bobUrl]);
    await Git(source, ["push", "origin", "main"]);
    string secondHead = (await Git(source, ["rev-parse", "HEAD"])).Trim();
    Check(secondHead != firstHead, "second push");

    string stale = Path.Combine(root, "stale");
    await Git(root, ["clone", bobUrl, "stale"]);
    File.AppendAllText(Path.Combine(source, "readme.txt"), "third revision\n");
    await Git(source, ["add", "."]);
    await Git(source, ["commit", "-m", "Third durable commit"]);
    await Git(source, ["push", "origin", "main"]);
    File.WriteAllText(Path.Combine(stale, "stale.txt"), "stale update\n");
    await Git(stale, ["add", "."]);
    await Git(stale, ["commit", "-m", "Stale competing commit"]);
    await GitFails(stale, ["push", "origin", "main"], "stale ref rejected");

    await using (GraftStore revoke = await GraftStore.OpenAsync(databasePath))
    {
        await revoke.RevokeMembershipAsync(aliceIdentity, project.Id, bob.Id);
    }
    File.AppendAllText(Path.Combine(source, "readme.txt"), "revoked writer attempt\n");
    await Git(source, ["add", "."]);
    await Git(source, ["commit", "-m", "Revoked writer attempt"]);
    await GitFails(source, ["push", "origin", "main"], "revoked membership blocks push");
    await Git(clone, ["remote", "set-url", "origin", aliceUrl]);
    await Git(clone, ["fetch", "origin"]);
    Check(true, "owner read token fetches after member revocation");

    await using (GraftStore revoke = await GraftStore.OpenAsync(databasePath))
    {
        await revoke.RevokeTokenAsync(aliceIdentity, aliceToken.Id);
        await revoke.RevokeTokenAsync(aliceIdentity, aliceApiToken.Id);
    }
    await GitFails(clone, ["fetch", "origin"], "revoked token blocks fetch");
    using (HttpResponseMessage revokedApi = await ApiGet(address, "/api/v4/user", aliceApiToken.Secret))
        Check(revokedApi.StatusCode == System.Net.HttpStatusCode.Unauthorized, "revoked token blocks API");
    string crossProjectUrl = AuthenticatedUrl(address, "bob", bobToken.Secret, "carol/private.git");
    await GitFails(root, ["ls-remote", crossProjectUrl], "cross-project path does not disclose repository");

    string repositoryPath = Path.Combine(repositoriesRoot, project.StorageId);
    Repository reopened = new(new DurableRepositoryStorage(repositoryPath));
    string durableHead = reopened.Refs["refs/heads/main"];
    Check(durableHead != firstHead && durableHead != secondHead, "latest accepted ref persisted");
    Check(reopened.Objects.ContainsKey(durableHead), "latest accepted object persisted");

    TestInterruptedCommitsLeaveRefsUnchanged(reopened, repositoryPath);

    await using (GraftStore pending = await GraftStore.OpenAsync(databasePath))
    {
        await Throws<IOException>(() => pending.CreateProjectWithRepositoryAsync(aliceIdentity, "interrupted", "Interrupted",
            (_, _) => throw new IOException("synthetic initializer failure")), "project initializer failure surfaces");
        Check(await pending.FindReadyProjectAsync("alice", "interrupted") is null, "pending project is not exposed");
    }

    Console.WriteLine($"RESULT: PASS; assertions: {assertions}");
    Console.WriteLine("BOUNDARY: loopback HTTPS with an ephemeral untrusted certificate; one writer process and bounded repositories.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("RESULT: FAIL: " + error.GetType().Name + ": " + Redact(error.Message));
    return 1;
}
finally
{
    await StopServer();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try
    {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
    catch (IOException) { Console.Error.WriteLine("WARN: temporary acceptance cleanup failed"); }
}

async Task<string> StartServer()
{
    TaskCompletionSource<string> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    serverLines = new();
    ProcessStartInfo start = ServerStart();
    server = Process.Start(start)!;
    Process active = server;
    errorReader = active.StandardError.ReadToEndAsync();
    outputReader = Task.Run(async () =>
    {
        while (await active.StandardOutput.ReadLineAsync() is string line)
        {
            serverLines.Enqueue(line);
            if (line.StartsWith("READY: ", StringComparison.Ordinal)) ready.TrySetResult(line[7..]);
        }
    });
    return await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
}

async Task StopServer()
{
    if (server is null) return;
    if (!server.HasExited) server.Kill(entireProcessTree: true);
    await server.WaitForExitAsync();
    if (outputReader is not null) await outputReader;
    if (errorReader is not null)
    {
        string errors = await errorReader;
        if (!string.IsNullOrWhiteSpace(errors)) throw new InvalidOperationException("Server wrote an error.");
    }
    server.Dispose();
    server = null;
    outputReader = null;
    errorReader = null;
}

ProcessStartInfo ServerStart()
{
    bool dll = serverPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    ProcessStartInfo start = new(dll ? "dotnet" : serverPath)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = root
    };
    if (dll) start.ArgumentList.Add(serverPath);
    start.ArgumentList.Add("--database");
    start.ArgumentList.Add(databasePath);
    start.ArgumentList.Add("--repositories");
    start.ArgumentList.Add(repositoriesRoot);
    return start;
}

async Task<string> Git(string directory, string[] arguments)
{
    (int exitCode, string output) = await RunGit(directory, arguments);
    if (exitCode != 0) throw new InvalidOperationException("Git " + arguments[0] + " failed.");
    return output;
}

async Task GitFails(string directory, string[] arguments, string name)
{
    (int exitCode, _) = await RunGit(directory, arguments);
    Check(exitCode != 0, name);
}

async Task<(int ExitCode, string Output)> RunGit(string directory, string[] arguments)
{
    ProcessStartInfo start = new("git")
    {
        WorkingDirectory = directory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
        start.Environment.Remove(key);
    start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
    start.Environment["GIT_CONFIG_GLOBAL"] = gitConfig;
    start.Environment["GIT_TERMINAL_PROMPT"] = "0";
    foreach (string value in new[]
    {
        "user.name=Fixture", "user.email=fixture@example.invalid", "commit.gpgSign=false", "credential.helper=",
        "core.autocrlf=false", "http.proxy=", "http.sslVerify=false", "protocol.file.allow=never"
    })
    {
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(value);
    }
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using Process process = Process.Start(start)!;
    Task<string> output = process.StandardOutput.ReadToEndAsync();
    Task<string> errors = process.StandardError.ReadToEndAsync();
    try
    {
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        _ = await errors;
        return (process.ExitCode, await output);
    }
    finally
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
    }
}

async Task CheckApi(string address, string apiToken, string readApiToken, string repositoryOnlyToken, string memberApiToken, long projectId, string expectedHead)
{
    using (HttpResponseMessage missing = await ApiGet(address, "/api/v4/user", null))
        Check(missing.StatusCode == System.Net.HttpStatusCode.Unauthorized, "API requires token");
    using (HttpResponseMessage insufficient = await ApiGet(address, "/api/v4/user", repositoryOnlyToken))
        Check(insufficient.StatusCode == System.Net.HttpStatusCode.Forbidden, "repository-only token cannot read user API");

    using (HttpResponseMessage user = await ApiGet(address, "/api/v4/user", apiToken))
    {
        Check(user.IsSuccessStatusCode, "current-user API succeeds");
        using JsonDocument json = JsonDocument.Parse(await user.Content.ReadAsStreamAsync());
        Check(json.RootElement.GetProperty("username").GetString() == "alice", "current-user identity");
    }

    using (HttpResponseMessage projects = await ApiGet(address, "/api/v4/projects?per_page=1&page=1", apiToken))
    {
        Check(projects.IsSuccessStatusCode && projects.Headers.GetValues("X-Total").Single() == "1", "visible-project pagination");
        using JsonDocument json = JsonDocument.Parse(await projects.Content.ReadAsStreamAsync());
        Check(json.RootElement.GetArrayLength() == 1 && json.RootElement[0].GetProperty("id").GetInt64() == projectId,
            "project list excludes inaccessible project");
    }

    using (HttpResponseMessage numeric = await ApiGet(address, "/api/v4/projects/" + projectId, apiToken))
        Check(numeric.IsSuccessStatusCode, "numeric project lookup");
    using (HttpResponseMessage encoded = await ApiGet(address, "/api/v4/projects/" + Uri.EscapeDataString("alice/durable"), apiToken))
    {
        Check(encoded.IsSuccessStatusCode, "encoded project lookup");
        using JsonDocument json = JsonDocument.Parse(await encoded.Content.ReadAsStreamAsync());
        Check(json.RootElement.GetProperty("path_with_namespace").GetString() == "alice/durable", "encoded project response");
    }
    using (HttpResponseMessage member = await ApiGet(address, "/api/v4/projects/" + projectId, memberApiToken))
        Check(member.IsSuccessStatusCode, "member project lookup");

    using (HttpResponseMessage missingCreate = await ApiPost(address, "/api/v4/projects", null,
        new { name = "Missing token", path = "missing-token", visibility = "private" }))
        Check(missingCreate.StatusCode == System.Net.HttpStatusCode.Unauthorized, "project creation requires token");
    using (HttpResponseMessage readOnlyCreate = await ApiPost(address, "/api/v4/projects", readApiToken,
        new { name = "Read only", path = "read-only", visibility = "private" }))
        Check(readOnlyCreate.StatusCode == System.Net.HttpStatusCode.Forbidden, "read_api token cannot create project");
    using (HttpResponseMessage created = await ApiPost(address, "/api/v4/projects", apiToken,
        new { name = "API Created", path = "api-created", description = "Created through REST", visibility = "private" }))
    {
        Check(created.StatusCode == System.Net.HttpStatusCode.Created, "API creates private project");
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        Check(json.RootElement.GetProperty("path_with_namespace").GetString() == "alice/api-created" &&
            json.RootElement.GetProperty("default_branch").ValueKind == JsonValueKind.Null, "created project response and empty repository");
    }
    using (HttpResponseMessage derived = await ApiPost(address, "/api/v4/projects", apiToken,
        new { name = "Derived Path", visibility = "private" }))
    {
        using JsonDocument json = JsonDocument.Parse(await derived.Content.ReadAsStreamAsync());
        Check(derived.StatusCode == System.Net.HttpStatusCode.Created &&
            json.RootElement.GetProperty("path").GetString() == "derived-path", "project path derives from name");
    }
    using (HttpResponseMessage duplicate = await ApiPost(address, "/api/v4/projects", apiToken,
        new { name = "Duplicate", path = "api-created", visibility = "private" }))
        Check(duplicate.StatusCode == System.Net.HttpStatusCode.BadRequest, "duplicate project path rejected");
    using (HttpResponseMessage invalid = await ApiPost(address, "/api/v4/projects", apiToken,
        new { name = "Invalid", path = "../invalid", visibility = "private" }))
        Check(invalid.StatusCode == System.Net.HttpStatusCode.BadRequest, "invalid project path rejected");
    using (HttpResponseMessage special = await ApiPost(address, "/api/v4/projects", apiToken,
        new { name = "Consecutive", path = "invalid--path", visibility = "private" }))
        Check(special.StatusCode == System.Net.HttpStatusCode.BadRequest, "consecutive path punctuation rejected");
    using (HttpResponseMessage visibility = await ApiPost(address, "/api/v4/projects", apiToken,
        new { name = "Public", path = "public-project", visibility = "public" }))
        Check(visibility.StatusCode == System.Net.HttpStatusCode.BadRequest, "unsupported visibility rejected");
    using (HttpResponseMessage property = await ApiPost(address, "/api/v4/projects", apiToken,
        new { name = "Imported", path = "imported", visibility = "private", import_url = "https://example.invalid/repository.git" }))
        Check(property.StatusCode == System.Net.HttpStatusCode.BadRequest, "unsupported project property rejected");

    await CheckGitLab180Fixture(address, apiToken, readApiToken);

    string projectApi = "/api/v4/projects/" + projectId + "/repository";
    using (HttpResponseMessage branches = await ApiGet(address, projectApi + "/branches", apiToken))
    {
        Check(branches.IsSuccessStatusCode, "branches API");
        using JsonDocument json = JsonDocument.Parse(await branches.Content.ReadAsStreamAsync());
        Check(json.RootElement[0].GetProperty("name").GetString() == "main" &&
            json.RootElement[0].GetProperty("commit").GetProperty("id").GetString() == expectedHead, "branch head matches Git");
    }
    using (HttpResponseMessage commits = await ApiGet(address, projectApi + "/commits?ref_name=main", apiToken))
    {
        Check(commits.IsSuccessStatusCode, "commits API");
        using JsonDocument json = JsonDocument.Parse(await commits.Content.ReadAsStreamAsync());
        Check(json.RootElement[0].GetProperty("id").GetString() == expectedHead, "commit history matches Git");
    }
    using (HttpResponseMessage tree = await ApiGet(address, projectApi + "/tree?ref=main", apiToken))
    {
        Check(tree.IsSuccessStatusCode, "tree API");
        using JsonDocument json = JsonDocument.Parse(await tree.Content.ReadAsStreamAsync());
        Check(json.RootElement.EnumerateArray().Any(entry => entry.GetProperty("name").GetString() == "hostile.html"), "tree lists hostile filename");
    }
    using (HttpResponseMessage hostile = await ApiGet(address, projectApi + "/files/hostile.html?ref=main", apiToken))
    {
        Check(hostile.IsSuccessStatusCode && hostile.Content.Headers.ContentType?.MediaType == "application/json", "hostile file is JSON data");
        using JsonDocument json = JsonDocument.Parse(await hostile.Content.ReadAsStreamAsync());
        string content = Encoding.UTF8.GetString(Convert.FromBase64String(json.RootElement.GetProperty("content").GetString()!));
        Check(content.StartsWith("<script>", StringComparison.Ordinal), "hostile HTML returned encoded, unchanged");
    }
    using (HttpResponseMessage binary = await ApiGet(address, projectApi + "/files/binary.dat?ref=main", apiToken))
    {
        using JsonDocument json = JsonDocument.Parse(await binary.Content.ReadAsStreamAsync());
        Check(binary.IsSuccessStatusCode && Convert.FromBase64String(json.RootElement.GetProperty("content").GetString()!).SequenceEqual(new byte[] { 0, 1, 2, 0, 255 }),
            "binary file round trip");
    }
    using (HttpResponseMessage traversal = await ApiGet(address, projectApi + "/files/..?ref=main", apiToken))
        Check(traversal.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound, "file traversal rejected");
    using (HttpResponseMessage unsupported = await ApiGet(address, "/api/v4/not-supported", apiToken))
    {
        Check(unsupported.StatusCode == System.Net.HttpStatusCode.NotFound && unsupported.Content.Headers.ContentType?.MediaType == "application/json",
            "unknown API route returns JSON 404");
    }
}

async Task<HttpResponseMessage> ApiGet(string address, string path, string? token)
{
    HttpClientHandler handler = new() { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseProxy = false };
    HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };
    HttpRequestMessage request = new(HttpMethod.Get, path);
    if (token is not null) request.Headers.Add("PRIVATE-TOKEN", token);
    HttpResponseMessage response = await client.SendAsync(request);
    request.Dispose();
    client.Dispose();
    handler.Dispose();
    return response;
}

async Task<HttpResponseMessage> ApiPost(string address, string path, string? token, object body)
{
    HttpClientHandler handler = new() { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseProxy = false };
    HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };
    HttpRequestMessage request = new(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
    if (token is not null) request.Headers.Add("PRIVATE-TOKEN", token);
    HttpResponseMessage response = await client.SendAsync(request);
    request.Dispose();
    client.Dispose();
    handler.Dispose();
    return response;
}

async Task CheckGitLab180Fixture(string address, string apiToken, string readApiToken)
{
    string resource = Assembly.GetExecutingAssembly().GetManifestResourceNames()
        .Single(name => name.EndsWith("gitlab-ce-18.0.0-rest-v4.json", StringComparison.Ordinal));
    using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!;
    using JsonDocument fixture = await JsonDocument.ParseAsync(stream);
    JsonElement root = fixture.RootElement;
    Check(root.GetProperty("commit").GetString() == "504fd9e5236e13d674e051c6b8a1e9892b371c58" &&
        root.GetProperty("sources").GetArrayLength() == 5, "GitLab 18.0 fixture provenance");

    foreach (JsonElement item in root.GetProperty("creation_cases").EnumerateArray())
    {
        string id = item.GetProperty("id").GetString()!;
        string? token = item.GetProperty("token").GetString() switch
        {
            "api" => apiToken,
            "read_api" => readApiToken,
            "none" => null,
            _ => throw new InvalidDataException("Unknown fixture token for " + id)
        };
        using HttpResponseMessage response = await ApiPostJson(address, "/api/v4/projects", token,
            item.GetProperty("body").GetRawText());
        int expected = item.GetProperty("graft_status").GetInt32();
        Check((int)response.StatusCode == expected, "GitLab 18.0 fixture " + id + " status");
        if (expected != 201) continue;
        using JsonDocument result = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Check(result.RootElement.GetProperty("name").GetString() == item.GetProperty("expected_name").GetString() &&
            result.RootElement.GetProperty("path").GetString() == item.GetProperty("expected_path").GetString(),
            "GitLab 18.0 fixture " + id + " response");
    }
}

async Task<HttpResponseMessage> ApiPostJson(string address, string path, string? token, string body)
{
    HttpClientHandler handler = new() { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseProxy = false };
    HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };
    HttpRequestMessage request = new(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    if (token is not null) request.Headers.Add("PRIVATE-TOKEN", token);
    HttpResponseMessage response = await client.SendAsync(request);
    request.Dispose();
    client.Dispose();
    handler.Dispose();
    return response;
}

async Task CheckBrowser(string address, string username, string password, long projectId)
{
    CookieContainer cookies = new();
    using HttpClientHandler handler = new()
    {
        CookieContainer = cookies,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        UseProxy = false
    };
    using HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };
    using (HttpResponseMessage shell = await client.GetAsync("/"))
    {
        string html = await shell.Content.ReadAsStringAsync();
        Check(shell.IsSuccessStatusCode && shell.Content.Headers.ContentType?.MediaType == "text/html" &&
            html.Contains("blazor.webassembly.js", StringComparison.Ordinal), "embedded Blazor shell");
        Check(shell.Headers.Contains("Content-Security-Policy"), "browser security policy");
    }
    using (HttpRequestMessage assetRequest = new(HttpMethod.Get, "/app.css"))
    {
        assetRequest.Headers.AcceptEncoding.ParseAdd("br");
        using HttpResponseMessage asset = await client.SendAsync(assetRequest);
        Check(asset.IsSuccessStatusCode && asset.Content.Headers.ContentEncoding.Contains("br") && asset.Headers.ETag is not null,
            "compressed embedded asset with cache validator");
    }
    using (HttpResponseMessage deep = await client.GetAsync("/alice/durable"))
        Check(deep.IsSuccessStatusCode && deep.Content.Headers.ContentType?.MediaType == "text/html", "browser deep link shell");
    using (HttpResponseMessage anonymous = await client.GetAsync("/api/session"))
    {
        using JsonDocument json = JsonDocument.Parse(await anonymous.Content.ReadAsStreamAsync());
        Check(anonymous.IsSuccessStatusCode && !json.RootElement.GetProperty("authenticated").GetBoolean(), "browser starts anonymous");
    }
    using HttpRequestMessage login = new(HttpMethod.Post, "/api/login");
    login.Headers.Add("Origin", address);
    login.Content = JsonContent.Create(new { username, password });
    using (HttpResponseMessage response = await client.SendAsync(login))
        Check(response.IsSuccessStatusCode, "browser login");

    string csrf;
    using (HttpResponseMessage session = await client.GetAsync("/api/session"))
    {
        using JsonDocument json = JsonDocument.Parse(await session.Content.ReadAsStreamAsync());
        csrf = json.RootElement.GetProperty("csrf").GetString()!;
        Check(json.RootElement.GetProperty("authenticated").GetBoolean() && csrf.Length == 64, "browser session and CSRF");
    }
    using (HttpResponseMessage projects = await client.GetAsync("/api/projects"))
    {
        using JsonDocument json = JsonDocument.Parse(await projects.Content.ReadAsStreamAsync());
        Check(projects.IsSuccessStatusCode && json.RootElement.EnumerateArray().Any(item =>
            item.GetProperty("id").GetInt64() == projectId), "browser project visibility");
    }
    using (HttpRequestMessage createUser = new(HttpMethod.Post, "/api/users")
    {
        Content = JsonContent.Create(new { username = "forbidden-user", password = Password() })
    })
    {
        createUser.Headers.Add("Origin", address);
        createUser.Headers.Add("X-Graft-CSRF", csrf);
        using HttpResponseMessage response = await client.SendAsync(createUser);
        Check(response.StatusCode == System.Net.HttpStatusCode.Forbidden, "ordinary browser user cannot create users");
    }
    using (HttpRequestMessage grant = new(HttpMethod.Post, $"/api/projects/{projectId}/members")
    {
        Content = JsonContent.Create(new { username = "carol", role = "Reporter" })
    })
    {
        grant.Headers.Add("Origin", address);
        grant.Headers.Add("X-Graft-CSRF", csrf);
        using HttpResponseMessage response = await client.SendAsync(grant);
        Check(response.StatusCode == System.Net.HttpStatusCode.NoContent, "browser grants project membership");
    }
    long carolId;
    using (HttpResponseMessage members = await client.GetAsync($"/api/projects/{projectId}/members"))
    {
        using JsonDocument json = JsonDocument.Parse(await members.Content.ReadAsStreamAsync());
        JsonElement carol = json.RootElement.EnumerateArray().Single(item => item.GetProperty("username").GetString() == "carol");
        carolId = carol.GetProperty("user_id").GetInt64();
        Check(members.IsSuccessStatusCode && carol.GetProperty("role").GetString() == "Reporter", "browser lists project membership");
    }
    using (HttpRequestMessage update = new(HttpMethod.Post, $"/api/projects/{projectId}/members")
    {
        Content = JsonContent.Create(new { username = "carol", role = "Developer" })
    })
    {
        update.Headers.Add("Origin", address);
        update.Headers.Add("X-Graft-CSRF", csrf);
        using HttpResponseMessage response = await client.SendAsync(update);
        Check(response.StatusCode == System.Net.HttpStatusCode.NoContent, "browser changes project membership");
    }
    using (HttpRequestMessage revoke = new(HttpMethod.Delete, $"/api/projects/{projectId}/members/{carolId}"))
    {
        revoke.Headers.Add("Origin", address);
        revoke.Headers.Add("X-Graft-CSRF", csrf);
        using HttpResponseMessage response = await client.SendAsync(revoke);
        Check(response.StatusCode == System.Net.HttpStatusCode.NoContent, "browser revokes project membership");
    }
    using HttpRequestMessage rejectedCreate = new(HttpMethod.Post, "/api/projects")
    {
        Content = JsonContent.Create(new { name = "Rejected browser", path = "rejected-browser", visibility = "private" })
    };
    rejectedCreate.Headers.Add("Origin", address);
    rejectedCreate.Headers.Add("X-Graft-CSRF", new string('0', 64));
    using (HttpResponseMessage rejected = await client.SendAsync(rejectedCreate))
        Check(rejected.StatusCode == System.Net.HttpStatusCode.Forbidden, "browser project creation rejects wrong CSRF");
    using HttpRequestMessage create = new(HttpMethod.Post, "/api/projects")
    {
        Content = JsonContent.Create(new { name = "Browser Created", description = "Created in browser", visibility = "private" })
    };
    create.Headers.Add("Origin", address);
    create.Headers.Add("X-Graft-CSRF", csrf);
    using (HttpResponseMessage created = await client.SendAsync(create))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        Check(created.StatusCode == System.Net.HttpStatusCode.Created &&
            json.RootElement.GetProperty("path_with_namespace").GetString() == "alice/browser-created", "browser creates private project");
    }
    using (HttpResponseMessage repository = await client.GetAsync($"/api/browser/projects/{projectId}?ref=main&file=hostile.html"))
    {
        using JsonDocument json = JsonDocument.Parse(await repository.Content.ReadAsStreamAsync());
        Check(repository.IsSuccessStatusCode && json.RootElement.GetProperty("branches")[0].GetProperty("name").GetString() == "main",
            "browser repository branches");
        string content = Encoding.UTF8.GetString(Convert.FromBase64String(json.RootElement.GetProperty("file").GetProperty("content").GetString()!));
        Check(content.StartsWith("<script>", StringComparison.Ordinal), "browser file content stays encoded in JSON");
    }
    using HttpRequestMessage rejectedLogout = new(HttpMethod.Post, "/api/logout");
    rejectedLogout.Headers.Add("Origin", address);
    rejectedLogout.Headers.Add("X-Graft-CSRF", new string('0', 64));
    using (HttpResponseMessage rejected = await client.SendAsync(rejectedLogout))
        Check(rejected.StatusCode == System.Net.HttpStatusCode.Forbidden, "browser logout rejects wrong CSRF");
    using HttpRequestMessage logout = new(HttpMethod.Post, "/api/logout");
    logout.Headers.Add("Origin", address);
    logout.Headers.Add("X-Graft-CSRF", csrf);
    using (HttpResponseMessage response = await client.SendAsync(logout))
        Check(response.StatusCode == System.Net.HttpStatusCode.NoContent, "browser logout");
    using (HttpResponseMessage replay = await client.GetAsync("/api/projects"))
        Check(replay.StatusCode == System.Net.HttpStatusCode.Unauthorized, "logged-out browser session rejected");
}

async Task CheckUserAdministration(string address, string adminPassword, string createdPassword)
{
    CookieContainer adminCookies = new();
    using HttpClientHandler adminHandler = new()
    {
        CookieContainer = adminCookies,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        UseProxy = false
    };
    using HttpClient adminClient = new(adminHandler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };
    using HttpRequestMessage login = new(HttpMethod.Post, "/api/login")
    {
        Content = JsonContent.Create(new { username = "admin", password = adminPassword })
    };
    login.Headers.Add("Origin", address);
    using (HttpResponseMessage response = await adminClient.SendAsync(login)) Check(response.IsSuccessStatusCode, "administrator browser login");
    string adminCsrf;
    using (HttpResponseMessage session = await adminClient.GetAsync("/api/session"))
    {
        using JsonDocument json = JsonDocument.Parse(await session.Content.ReadAsStreamAsync());
        adminCsrf = json.RootElement.GetProperty("csrf").GetString()!;
        Check(json.RootElement.GetProperty("is_administrator").GetBoolean(), "browser session exposes administrator status");
    }
    using (HttpRequestMessage create = new(HttpMethod.Post, "/api/users")
    {
        Content = JsonContent.Create(new { username = "webuser", password = createdPassword })
    })
    {
        create.Headers.Add("Origin", address);
        create.Headers.Add("X-Graft-CSRF", adminCsrf);
        using HttpResponseMessage response = await adminClient.SendAsync(create);
        Check(response.StatusCode == System.Net.HttpStatusCode.Created, "administrator creates user in browser API");
    }
    using (HttpRequestMessage duplicate = new(HttpMethod.Post, "/api/users")
    {
        Content = JsonContent.Create(new { username = "webuser", password = createdPassword })
    })
    {
        duplicate.Headers.Add("Origin", address);
        duplicate.Headers.Add("X-Graft-CSRF", adminCsrf);
        using HttpResponseMessage response = await adminClient.SendAsync(duplicate);
        Check(response.StatusCode == System.Net.HttpStatusCode.BadRequest, "browser user creation rejects duplicate username");
    }

    long userId;
    long adminId;
    using (HttpResponseMessage response = await adminClient.GetAsync("/api/users"))
    {
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        JsonElement user = json.RootElement.EnumerateArray().Single(item => item.GetProperty("username").GetString() == "webuser");
        JsonElement administrator = json.RootElement.EnumerateArray().Single(item => item.GetProperty("username").GetString() == "admin");
        userId = user.GetProperty("id").GetInt64();
        adminId = administrator.GetProperty("id").GetInt64();
        Check(response.IsSuccessStatusCode && !user.GetProperty("disabled").GetBoolean(), "administrator lists active users");
    }
    using (HttpRequestMessage selfReset = new(HttpMethod.Post, $"/api/users/{adminId}/reset-password")
    {
        Content = JsonContent.Create(new { new_password = Password() })
    })
    {
        selfReset.Headers.Add("Origin", address);
        selfReset.Headers.Add("X-Graft-CSRF", adminCsrf);
        using HttpResponseMessage response = await adminClient.SendAsync(selfReset);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "administrator cannot reset own password without current password");
    }

    CookieContainer userCookies = new();
    using HttpClientHandler userHandler = new()
    {
        CookieContainer = userCookies,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        UseProxy = false
    };
    using HttpClient userClient = new(userHandler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };

    async Task<HttpResponseMessage> LoginUser(string password)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/login")
        {
            Content = JsonContent.Create(new { username = "webuser", password })
        };
        request.Headers.Add("Origin", address);
        HttpResponseMessage response = await userClient.SendAsync(request);
        request.Dispose();
        return response;
    }

    async Task<string> UserCsrf()
    {
        using HttpResponseMessage response = await userClient.GetAsync("/api/session");
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        return json.RootElement.GetProperty("csrf").GetString()!;
    }

    async Task<string> CreateUserToken(string csrf, string name)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/tokens")
        {
            Content = JsonContent.Create(new { name, scopes = new[] { "read_api" }, expires_in_days = 30 })
        };
        request.Headers.Add("Origin", address);
        request.Headers.Add("X-Graft-CSRF", csrf);
        using HttpResponseMessage response = await userClient.SendAsync(request);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        return json.RootElement.GetProperty("secret").GetString()!;
    }

    using (HttpResponseMessage response = await LoginUser(createdPassword)) Check(response.IsSuccessStatusCode, "browser-created user can log in");
    using (HttpResponseMessage response = await userClient.GetAsync("/api/users"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "ordinary user cannot list users");
    string userCsrf = await UserCsrf();
    string resetRevokedToken = await CreateUserToken(userCsrf, "before-reset");
    string resetPassword = Password();
    using (HttpRequestMessage reset = new(HttpMethod.Post, $"/api/users/{userId}/reset-password")
    {
        Content = JsonContent.Create(new { new_password = resetPassword })
    })
    {
        reset.Headers.Add("Origin", address);
        reset.Headers.Add("X-Graft-CSRF", adminCsrf);
        using HttpResponseMessage response = await adminClient.SendAsync(reset);
        Check(response.StatusCode == HttpStatusCode.NoContent, "administrator resets another user's password");
    }
    using (HttpResponseMessage response = await userClient.GetAsync("/api/projects"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "password reset revokes browser sessions");
    using (HttpResponseMessage response = await ApiGet(address, "/api/v4/user", resetRevokedToken))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "password reset revokes personal tokens");
    using (HttpResponseMessage response = await LoginUser(createdPassword))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "old password rejected after reset");
    using (HttpResponseMessage response = await LoginUser(resetPassword)) Check(response.IsSuccessStatusCode, "reset password authenticates");

    userCsrf = await UserCsrf();
    using (HttpRequestMessage wrongCurrent = new(HttpMethod.Post, "/api/password")
    {
        Content = JsonContent.Create(new { current_password = Password(), new_password = Password() })
    })
    {
        wrongCurrent.Headers.Add("Origin", address);
        wrongCurrent.Headers.Add("X-Graft-CSRF", userCsrf);
        using HttpResponseMessage response = await userClient.SendAsync(wrongCurrent);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "password change rejects wrong current password");
    }
    using (HttpResponseMessage response = await userClient.GetAsync("/api/projects"))
        Check(response.IsSuccessStatusCode, "rejected password change preserves session");
    string changeRevokedToken = await CreateUserToken(userCsrf, "before-change");
    string changedPassword = Password();
    using (HttpRequestMessage change = new(HttpMethod.Post, "/api/password")
    {
        Content = JsonContent.Create(new { current_password = resetPassword, new_password = changedPassword })
    })
    {
        change.Headers.Add("Origin", address);
        change.Headers.Add("X-Graft-CSRF", userCsrf);
        using HttpResponseMessage response = await userClient.SendAsync(change);
        Check(response.StatusCode == HttpStatusCode.NoContent, "user changes password with current password");
    }
    using (HttpResponseMessage response = await userClient.GetAsync("/api/projects"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "password change revokes browser sessions");
    using (HttpResponseMessage response = await ApiGet(address, "/api/v4/user", changeRevokedToken))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "password change revokes personal tokens");
    using (HttpResponseMessage response = await LoginUser(resetPassword))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "previous password rejected after change");
    using (HttpResponseMessage response = await LoginUser(changedPassword)) Check(response.IsSuccessStatusCode, "changed password authenticates");

    userCsrf = await UserCsrf();
    string disableRevokedToken = await CreateUserToken(userCsrf, "before-disable");
    using (HttpRequestMessage disable = new(HttpMethod.Post, $"/api/users/{userId}/disable"))
    {
        disable.Headers.Add("Origin", address);
        disable.Headers.Add("X-Graft-CSRF", adminCsrf);
        using HttpResponseMessage response = await adminClient.SendAsync(disable);
        Check(response.StatusCode == HttpStatusCode.NoContent, "administrator disables another user");
    }
    using (HttpResponseMessage response = await userClient.GetAsync("/api/projects"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "disable revokes browser sessions");
    using (HttpResponseMessage response = await ApiGet(address, "/api/v4/user", disableRevokedToken))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "disable revokes personal tokens");
    using (HttpResponseMessage response = await LoginUser(changedPassword))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "disabled user cannot log in");
    using (HttpResponseMessage response = await adminClient.GetAsync("/api/users"))
    {
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        JsonElement user = json.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetInt64() == userId);
        Check(response.IsSuccessStatusCode && user.GetProperty("disabled").GetBoolean(), "administrator list exposes disabled state");
    }
}

async Task CheckPersonalAccessTokens(string address, string username, string password, long projectId)
{
    CookieContainer cookies = new();
    using HttpClientHandler handler = new()
    {
        CookieContainer = cookies,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        UseProxy = false
    };
    using HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };
    using HttpRequestMessage login = new(HttpMethod.Post, "/api/login")
    {
        Content = JsonContent.Create(new { username, password })
    };
    login.Headers.Add("Origin", address);
    using (HttpResponseMessage response = await client.SendAsync(login)) Check(response.IsSuccessStatusCode, "token workflow browser login");

    string csrf;
    using (HttpResponseMessage session = await client.GetAsync("/api/session"))
    {
        using JsonDocument json = JsonDocument.Parse(await session.Content.ReadAsStreamAsync());
        csrf = json.RootElement.GetProperty("csrf").GetString()!;
        Check(csrf.Length == 64, "token workflow CSRF");
    }
    using (HttpRequestMessage invalid = new(HttpMethod.Post, "/api/tokens")
    {
        Content = JsonContent.Create(new { name = "invalid", scopes = Array.Empty<string>(), expires_in_days = 30 })
    })
    {
        invalid.Headers.Add("Origin", address);
        invalid.Headers.Add("X-Graft-CSRF", csrf);
        using HttpResponseMessage response = await client.SendAsync(invalid);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "personal token requires a scope");
    }

    long tokenId;
    string tokenSecret;
    using (HttpRequestMessage create = new(HttpMethod.Post, "/api/tokens")
    {
        Content = JsonContent.Create(new { name = "browser-git", scopes = new[] { "read_repository" }, expires_in_days = 30 })
    })
    {
        create.Headers.Add("Origin", address);
        create.Headers.Add("X-Graft-CSRF", csrf);
        using HttpResponseMessage response = await client.SendAsync(create);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        tokenId = json.RootElement.GetProperty("id").GetInt64();
        tokenSecret = json.RootElement.GetProperty("secret").GetString()!;
        DateTimeOffset expiry = json.RootElement.GetProperty("expires_at_utc").GetDateTimeOffset();
        Check(response.StatusCode == HttpStatusCode.Created && tokenSecret.StartsWith("graft_pat_", StringComparison.Ordinal) &&
            json.RootElement.GetProperty("scopes")[0].GetString() == "read_repository" &&
            expiry > DateTimeOffset.UtcNow.AddDays(29) && expiry <= DateTimeOffset.UtcNow.AddDays(31),
            "browser creates scoped expiring personal token");
    }
    using (HttpResponseMessage response = await client.GetAsync("/api/tokens"))
    {
        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);
        JsonElement listed = json.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetInt64() == tokenId);
        Check(response.IsSuccessStatusCode && listed.GetProperty("name").GetString() == "browser-git" &&
            !content.Contains(tokenSecret, StringComparison.Ordinal), "token list omits one-time secret");
    }

    string gitUrl = AuthenticatedUrl(address, username, tokenSecret, $"alice/durable.git");
    await Git(root, ["ls-remote", gitUrl]);
    Check(true, "browser-created personal token authenticates Git HTTPS");
    using (HttpRequestMessage wrongCsrf = new(HttpMethod.Delete, $"/api/tokens/{tokenId}"))
    {
        wrongCsrf.Headers.Add("Origin", address);
        wrongCsrf.Headers.Add("X-Graft-CSRF", new string('0', 64));
        using HttpResponseMessage response = await client.SendAsync(wrongCsrf);
        Check(response.StatusCode == HttpStatusCode.Forbidden, "personal token revocation rejects wrong CSRF");
    }
    using (HttpRequestMessage revoke = new(HttpMethod.Delete, $"/api/tokens/{tokenId}"))
    {
        revoke.Headers.Add("Origin", address);
        revoke.Headers.Add("X-Graft-CSRF", csrf);
        using HttpResponseMessage response = await client.SendAsync(revoke);
        Check(response.StatusCode == HttpStatusCode.NoContent, "browser revokes personal token");
    }
    using (HttpResponseMessage response = await client.GetAsync("/api/tokens"))
    {
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Check(response.IsSuccessStatusCode && !json.RootElement.EnumerateArray().Any(item => item.GetProperty("id").GetInt64() == tokenId),
            "revoked personal token leaves active list");
    }
    await GitFails(root, ["ls-remote", gitUrl], "revoked browser-created token blocks Git HTTPS");
}

void TestInterruptedCommitsLeaveRefsUnchanged(Repository current, string repositoryPath)
{
    string before = current.Refs["refs/heads/main"];
    foreach (DurableCommitPhase phase in Enum.GetValues<DurableCommitPhase>())
    {
        GitObject tree = new(2, []);
        GitObject commit = Commit(tree.Id, before, "failure at " + phase);
        DurableRepositoryStorage storage = new(repositoryPath, observed =>
        {
            if (observed == phase) throw new IOException("synthetic storage interruption");
        });
        Repository guarded = new(storage);
        byte[] request = Push((before, commit.Id, "refs/heads/main"), Pack.Write([tree, commit]));
        try { guarded.Receive(request); }
        catch (IOException) { assertions++; }
        Repository disk = new(new DurableRepositoryStorage(repositoryPath));
        Check(guarded.Refs["refs/heads/main"] == before, phase + " leaves memory ref unchanged");
        Check(disk.Refs["refs/heads/main"] == before, phase + " leaves disk ref unchanged");
    }
}

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("Assertion failed: " + name);
    assertions++;
}

T Required<T>(T? value, string name) where T : class
{
    Check(value is not null, name);
    return value!;
}

async Task Throws<TException>(Func<Task> action, string name) where TException : Exception
{
    try { await action(); }
    catch (TException) { assertions++; return; }
    throw new InvalidOperationException("Expected " + typeof(TException).Name + ": " + name);
}

static string Password() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
static string AuthenticatedUrl(string address, string username, string token, string path) =>
    address.Replace("https://", "https://" + Uri.EscapeDataString(username) + ":" + Uri.EscapeDataString(token) + "@", StringComparison.Ordinal) + "/" + path;
static string Redact(string message) => message.Contains("graft_pat_", StringComparison.Ordinal) ? "credential-bearing operation failed" : message;
static GitObject Commit(string tree, string parent, string message) => new(1, Encoding.UTF8.GetBytes(
    $"tree {tree}\nparent {parent}\nauthor Fixture <fixture@example.invalid> 1700000000 +0000\ncommitter Fixture <fixture@example.invalid> 1700000000 +0000\n\n{message}\n"));
static byte[] Push((string Old, string New, string Name) command, byte[] pack)
{
    using MemoryStream output = new();
    Packet.Write(output, command.Old + " " + command.New + " " + command.Name + "\0report-status ofs-delta");
    Packet.Flush(output);
    output.Write(pack);
    return output.ToArray();
}
