using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
// Flipped between server restarts, because the operator switch is not something a running server can be
// asked to change: the point of it is that it is decided before the process starts.
bool publicBrowsing = false;
bool pushToCreate = false;
bool webhooksEnabled = false;
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
    AuthenticatedIdentity administratorIdentity;

    await using (GraftStore fixture = await GraftStore.OpenAsync(databasePath))
    {
        await fixture.BootstrapAdministratorAsync("admin", adminPassword);
        SessionGrant adminSession = Required(await fixture.LoginAsync("admin", adminPassword), "admin login");
        AuthenticatedIdentity admin = Required(await fixture.ResolveSessionAsync(adminSession.Secret), "admin identity");
        administratorIdentity = admin;
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
    // After the API checks, which assert on project counts this one would otherwise change.
    await CheckGlobalSlug(address, aliceApiToken.Secret, aliceReadApiToken.Secret, bobToken.Secret, project.Id, source);
    await CheckBrowser(address, "alice", alicePassword, project.Id);
    await CheckSessionRenewal(address, "alice", alicePassword);
    await CheckPersonalAccessTokens(address, "alice", alicePassword, project.Id);
    await CheckUserAdministration(address, adminPassword, Password());

    // Rendering a README turns repository content into markup, which is the one place a stored cross-site
    // scripting bug could live, so the corpus is pushed as real repository content and read back through the
    // real endpoint rather than being handed to the renderer in isolation. It goes on a branch of its own so
    // the main-branch assertions above and below it keep testing what they were written to test.
    await Git(source, ["checkout", "-b", "readme-corpus"]);
    Directory.CreateDirectory(Path.Combine(source, "docs"));
    File.WriteAllText(Path.Combine(source, "README.md"), ReadmeCorpus());
    File.WriteAllBytes(Path.Combine(source, "docs", "pixel.png"), OnePixelPng());
    File.WriteAllText(Path.Combine(source, "docs", "fake.png"), "<script>alert(1)</script> not an image at all\n");
    await Git(source, ["add", "."]);
    await Git(source, ["commit", "-m", "README rendering corpus"]);
    await Git(source, ["remote", "set-url", "origin", bobUrl]);
    await Git(source, ["push", "origin", "readme-corpus"]);
    await CheckReadmeRendering(address, "alice", alicePassword, project.Id);
    await Git(source, ["checkout", "main"]);

    await CheckDiffMatchesGit(source, Path.Combine(repositoriesRoot, project.StorageId), bobUrl);
    await CheckCommitDiffApi(address, "alice", alicePassword, project.Id, source);

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

    await CheckLargerRepository(address, source, aliceUrl, bobUrl, aliceApiToken.Secret, project.Id);

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
    TestServerAuthoredObjectsMatchGit();
    TestDefaultBranchResolution();

    await using (GraftStore pending = await GraftStore.OpenAsync(databasePath))
    {
        await Throws<IOException>(() => pending.CreateProjectWithRepositoryAsync(aliceIdentity, "interrupted", "Interrupted",
            (_, _) => throw new IOException("synthetic initializer failure")), "project initializer failure surfaces");
        Check(await pending.FindReadyProjectAsync("alice", "interrupted") is null, "pending project is not exposed");
    }

    address = await CheckPublicBrowsing(address, aliceIdentity);
    await CheckChunkedPush(address, aliceIdentity);
    address = await CheckPushToCreate(address, aliceIdentity, bob.Username, bobPassword);
    await CheckTokenProjectSettings(address, aliceIdentity, bob.Username, bobPassword);
    // Last, because writing a file creates a branch and several checks above count branches.
    await CheckApiFileWrites(address, aliceIdentity, project.Id);
    await CheckProjectPages(address, aliceIdentity, source);
    await CheckCollaboration(address, aliceIdentity, bob.Username, "carol");
    await CheckProjectDeletion(address, aliceIdentity, administratorIdentity, bob.Username, alicePassword);
    await CheckReview(address, aliceIdentity, bob.Username, "carol");
    await CheckPlanning(address, aliceIdentity, bob.Username, "carol");
    await CheckReviewOptions(address, aliceIdentity, bob.Username, "carol");
    await CheckReleases(address, aliceIdentity, "carol");
    await CheckKnowledge(address, aliceIdentity, bob.Username, "carol");
    await CheckSearch(address, aliceIdentity, "carol");
    address = await CheckForks(address, aliceIdentity, administratorIdentity);
    await CheckLargeFiles(address, aliceIdentity);
    await CheckPackages(address, aliceIdentity, "carol");
    await CheckCompressedGitRequests(address, aliceIdentity);
    await CheckGitEngine(address, aliceIdentity);
    await CheckFetching(address, aliceIdentity, "carol");
    await CheckEmptyRepository(address, aliceIdentity, alicePassword);
    address = await CheckWebhooks(address, aliceIdentity);
    // After everything that writes an audit row, so the trail it reads already contains them.
    await CheckAuditTrail(address, adminPassword, alicePassword);

    Console.WriteLine($"RESULT: PASS; assertions: {assertions}");
    Console.WriteLine("BOUNDARY: loopback HTTPS with an ephemeral untrusted certificate; one writer process and bounded repositories.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("RESULT: FAIL: " + error.GetType().Name + ": " + Redact(error.Message));
    Console.Error.WriteLine(Redact(error.StackTrace ?? ""));
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
    start.ArgumentList.Add("--public-browsing");
    start.ArgumentList.Add(publicBrowsing ? "true" : "false");
    start.ArgumentList.Add("--push-to-create");
    start.ArgumentList.Add(pushToCreate ? "true" : "false");
    // A pages origin is always configured for the suite, so the site assertions can reach it by Host
    // header without a second listener or a hosts-file entry.
    start.ArgumentList.Add("--pages-origin");
    start.ArgumentList.Add("https://pages.graft.test");
    // Outbound notification is off unless a check asks for it, exactly as a deployment would have it, so
    // every other assertion runs against a server that cannot call anything.
    start.ArgumentList.Add("--webhooks");
    start.ArgumentList.Add(webhooksEnabled ? "true" : "false");
    return start;
}

async Task CheckLargerRepository(string address, string source, string readUrl, string writeUrl, string token, long projectId)
{
    await Git(source, ["checkout", "-b", "scale-validation"]);
    byte[] random = RandomNumberGenerator.GetBytes(20 * 1024 * 1024);
    File.WriteAllBytes(Path.Combine(source, "scale-random.bin"), random);
    File.WriteAllBytes(Path.Combine(source, "scale-zero.bin"), new byte[40 * 1024 * 1024]);
    await Git(source, ["add", "."]);
    await Git(source, ["commit", "-m", "Exercise larger repository transport"]);
    await Git(source, ["push", writeUrl, "scale-validation"]);
    Check(true, "HTTP push exceeds old request, object and repository budgets");
    string cloned = Path.Combine(root, "scale-clone");
    await Git(root, ["clone", "--single-branch", "--branch", "scale-validation", readUrl, cloned]);
    Check(File.ReadAllBytes(Path.Combine(cloned, "scale-random.bin")).AsSpan().SequenceEqual(random),
        "larger HTTP clone preserves incompressible bytes");
    await Git(cloned, ["fsck", "--full", "--strict"]);
    using HttpResponseMessage response = await ApiGet(address,
        $"/api/v4/projects/{projectId}/repository/archive.zip?sha=scale-validation", token);
    Check(response.IsSuccessStatusCode, "larger repository archive downloads");
    using Stream content = await response.Content.ReadAsStreamAsync();
    using System.IO.Compression.ZipArchive archive = new(content);
    System.IO.Compression.ZipArchiveEntry entry = archive.Entries.Single(item => item.FullName.EndsWith("scale-random.bin", StringComparison.Ordinal));
    using Stream extracted = entry.Open();
    byte[] digest = SHA256.HashData(extracted);
    Check(digest.AsSpan().SequenceEqual(SHA256.HashData(random)), "disk-backed archive preserves large file bytes");
    await Git(source, ["checkout", "main"]);
}

async Task<string> Git(string directory, string[] arguments)
{
    (int exitCode, string output, string errors) = await RunGit(directory, arguments);
    // Git reports why it refused on standard error, and without that a failure here is undiagnosable.
    if (exitCode != 0) throw new InvalidOperationException("Git " + arguments[0] + " failed: " + Redact(errors.Trim()));
    return output;
}

async Task GitFails(string directory, string[] arguments, string name)
{
    (int exitCode, _, _) = await RunGit(directory, arguments);
    Check(exitCode != 0, name);
}

async Task<(int ExitCode, string Output, string Errors)> RunGit(string directory, string[] arguments, IDictionary<string, string>? environment = null)
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
    if (environment is not null) foreach ((string key, string value) in environment) start.Environment[key] = value;
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
        return (process.ExitCode, await output, await errors);
    }
    finally
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
    }
}

/// <summary>
/// The project page and its static site: a curated surface over a repository that stays private, and a
/// site served from a hostname of its own.
///
/// <para>
/// Most of this is about what must not be reachable. A page is deny-by-default, so the assertions that
/// matter are the ones where a file exists in the repository and the page refuses to serve it.
/// </para>
/// </summary>
async Task CheckProjectPages(string address, AuthenticatedIdentity alice, string worktree)
{
    const string PagesHost = "pages.graft.test";
    TokenGrant grant;
    long pageProjectId;
    string slug;
    await using (GraftStore fixture = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo created = await fixture.CreateProjectWithRepositoryAsync(alice, "pagesample", "Page sample",
            (project, token) => { token.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; },
            "A private project with a public page.");
        pageProjectId = created.Id;
        slug = created.GlobalSlug;
        grant = await fixture.CreateTokenAsync(alice, "pages", TokenScope.Api | TokenScope.WriteRepository, DateTimeOffset.UtcNow.AddHours(1));
    }

    // A repository holding both the things the page names and the things it must never serve.
    string source = Path.Combine(root, "pages-source");
    Directory.CreateDirectory(source);
    await Git(source, ["init", "--initial-branch=main"]);
    Directory.CreateDirectory(Path.Combine(source, ".graft"));
    Directory.CreateDirectory(Path.Combine(source, "site"));
    Directory.CreateDirectory(Path.Combine(source, "docs"));
    Directory.CreateDirectory(Path.Combine(source, "src"));
    File.WriteAllText(Path.Combine(source, ".graft", "project.json"), """
        {"version":1,"page":{"title":"Sample","description":"Curated.","readme":"README.md",
         "files":["README.md","docs/**/*.md"],"site":{"root":"site","index":"index.html"}}}
        """);
    File.WriteAllText(Path.Combine(source, "README.md"), "# Sample\n\nCurated page.\n");
    File.WriteAllText(Path.Combine(source, "docs", "guide.md"), "# Guide\n");
    File.WriteAllText(Path.Combine(source, "docs", "notes.txt"), "not markdown, must not be listed\n");
    File.WriteAllText(Path.Combine(source, "src", "secret.cs"), "const string Key = \"must-never-be-served\";\n");
    File.WriteAllText(Path.Combine(source, "site", "index.html"), "<!doctype html><title>Sample site</title><h1>Sample site</h1>\n");
    File.WriteAllText(Path.Combine(source, "site", "style.css"), "body { color: #333; }\n");
    File.WriteAllText(Path.Combine(source, "site", "payload.exe"), "not a servable type\n");
    await Git(source, ["add", "."]);
    await Git(source, ["commit", "-m", "Page config, curated files and a site"]);
    await Git(source, ["push", AuthenticatedUrl(address, "alice", grant.Secret, "alice/pagesample.git"), "main"]);

    // Nothing is public until the page is published, which is a database act and never a config one: a
    // push must never be able to widen access.
    using (HttpResponseMessage beforePublish = await PagesGet(address, null, "/api/graft/pages/" + slug))
        Check(beforePublish.StatusCode == System.Net.HttpStatusCode.NotFound,
            "a page is not served until it is published, however complete its config");
    using (HttpResponseMessage siteBefore = await PagesGet(address, PagesHost, "/" + slug + "/"))
        Check(siteBefore.StatusCode == System.Net.HttpStatusCode.NotFound, "and neither is its site");

    await using (GraftStore fixture = await GraftStore.OpenAsync(databasePath))
        await fixture.SetPagePublishedAsync(alice, pageProjectId, true);

    // The curated page, to a reader with no credentials at all.
    using (HttpResponseMessage page = await PagesGet(address, null, "/api/graft/pages/" + slug))
    {
        Check(page.IsSuccessStatusCode, "a published page is readable without an account (got " +
            (int)page.StatusCode + ": " + Redact(await page.Content.ReadAsStringAsync()) + ")");
        using JsonDocument json = JsonDocument.Parse(await page.Content.ReadAsStreamAsync());
        Check(json.RootElement.GetProperty("title").GetString() == "Sample", "and carries the title its config gave it");
        string[] files = [.. json.RootElement.GetProperty("files").EnumerateArray().Select(entry => entry.GetString()!)];
        Check(files.Length == 2 && files.Contains("README.md") && files.Contains("docs/guide.md"),
            "and lists exactly the files its allowlist admits");
        Check(!files.Contains("src/secret.cs") && !files.Contains("docs/notes.txt") && !files.Contains(".graft/project.json"),
            "and nothing else the repository happens to hold");
        Check(json.RootElement.GetProperty("readme").ValueKind != JsonValueKind.Null, "the named readme is rendered");
        Check(json.RootElement.GetProperty("site_url").GetString()!.Contains(PagesHost, StringComparison.Ordinal),
            "and the site is advertised on the pages origin, not this one");
    }

    // Files, through the page. Named paths only.
    using (HttpResponseMessage allowed = await PagesGet(address, null, "/api/graft/pages/" + slug + "/files/" + Uri.EscapeDataString("docs/guide.md")))
        Check(allowed.IsSuccessStatusCode, "a page serves a file its allowlist names");
    foreach (string refused in new[] { "src/secret.cs", "docs/notes.txt", ".graft/project.json" })
        using (HttpResponseMessage denied = await PagesGet(address, null, "/api/graft/pages/" + slug + "/files/" + Uri.EscapeDataString(refused)))
            Check(denied.StatusCode == System.Net.HttpStatusCode.NotFound, "and refuses one it does not: " + refused);

    // The repository itself is untouched by any of this: a page grant reaches no repository surface.
    using (HttpResponseMessage browse = await PagesGet(address, null, "/api/browser/projects/alice/pagesample"))
        Check(browse.StatusCode == System.Net.HttpStatusCode.NotFound,
            "a published page does not open the repository view");

    // The site, on its own hostname.
    using (HttpResponseMessage index = await PagesGet(address, PagesHost, "/" + slug + "/"))
    {
        Check(index.IsSuccessStatusCode, "a site is served from the pages origin");
        Check(index.Content.Headers.ContentType?.MediaType == "text/html", "with the type its extension declares");
        Check((await index.Content.ReadAsStringAsync()).Contains("Sample site", StringComparison.Ordinal),
            "and is the file from the repository's site folder");
    }
    using (HttpResponseMessage style = await PagesGet(address, PagesHost, "/" + slug + "/style.css"))
        Check(style.IsSuccessStatusCode && style.Content.Headers.ContentType?.MediaType == "text/css",
            "a site's other files are served with their own types");

    // Everything outside the configured root, however it is asked for.
    foreach (string outside in new[] { "/src/secret.cs", "/README.md", "/.graft/project.json", "/../src/secret.cs", "/site/../src/secret.cs" })
        using (HttpResponseMessage denied = await PagesGet(address, PagesHost, "/" + slug + outside))
            Check(denied.StatusCode == System.Net.HttpStatusCode.NotFound,
                "the pages origin serves nothing outside the site root: " + outside);

    // The guide an agent reads when it does not speak the Model Context Protocol. Unauthenticated, because
    // a guide nobody can read without credentials helps nobody, and it describes how Graft works rather
    // than what this instance holds.
    using (HttpResponseMessage guide = await PagesGet(address, null, "/llms.txt"))
    {
        Check(guide.IsSuccessStatusCode, "the forge publishes a guide for agents at /llms.txt");
        Check(guide.Content.Headers.ContentType?.MediaType == "text/plain", "as plain text");
        string text = await guide.Content.ReadAsStringAsync();
        Check(text.Contains("graft_whoami", StringComparison.Ordinal) && text.Contains("instance-wide slug", StringComparison.Ordinal),
            "carrying the same overview the protocol handshake sends");
        Check(text.Contains(".graft/project.json", StringComparison.Ordinal),
            "and every topic in one document, since a reader here cannot ask for one");
    }
    // It describes the product, not this server's contents, so it stays off the pages origin where only a
    // project's own site lives.
    using (HttpResponseMessage elsewhere = await PagesGet(address, PagesHost, "/llms.txt"))
        Check(elsewhere.StatusCode == System.Net.HttpStatusCode.NotFound, "and is not served from the pages origin");

    // Linking back to the forge without naming it. A site that hard-codes the forge's hostname is wrong
    // the day that hostname changes, so Graft answers these itself with wherever the forge is now.
    using (HttpResponseMessage back = await PagesGet(address, PagesHost, "/" + slug + "/-/repo"))
    {
        Check(back.StatusCode == System.Net.HttpStatusCode.Found, "a site can link back to the project without naming a host");
        string target = back.Headers.Location?.ToString() ?? "";
        Check(target.StartsWith(address.TrimEnd('/'), StringComparison.Ordinal) && target.EndsWith("/-/p/" + slug, StringComparison.Ordinal),
            "and is sent to the project's page on the forge as it is addressed today");
    }
    using (HttpResponseMessage describe = await PagesGet(address, PagesHost, "/" + slug + "/-/graft.json"))
    {
        Check(describe.IsSuccessStatusCode, "a site can also read where the forge is, to render its own link");
        using JsonDocument json = JsonDocument.Parse(await describe.Content.ReadAsStreamAsync());
        Check(json.RootElement.GetProperty("forge").GetString() == address.TrimEnd('/'), "which reports the forge's current origin");
        Check(json.RootElement.GetProperty("slug").GetString() == slug, "and the project it belongs to");
    }

    // A type that is not on the allowlist is refused rather than guessed at.
    using (HttpResponseMessage unknown = await PagesGet(address, PagesHost, "/" + slug + "/payload.exe"))
        Check(unknown.StatusCode == System.Net.HttpStatusCode.NotFound, "and no file whose extension it does not serve");

    // The root of the pages origin belongs to no project. A reader who trimmed a site's URL back to it is
    // sent to the forge rather than told the URL shape they cannot act on.
    using (HttpResponseMessage root = await PagesGet(address, PagesHost, "/"))
    {
        Check(root.StatusCode == System.Net.HttpStatusCode.Found, "the pages root redirects rather than explaining itself");
        // Trimmed on both sides: a Uri normalises an empty path to "/", so the header reads
        // https://host:port/ where the address the suite holds has no trailing slash.
        Check((root.Headers.Location?.ToString() ?? "").TrimEnd('/') == address.TrimEnd('/'), "and lands on the forge");
    }
    // A project that is not there stays a miss, so the redirect never hides a typo or answers a probe.
    using (HttpResponseMessage unknown = await PagesGet(address, PagesHost, "/no-such-project/"))
        Check(unknown.StatusCode == System.Net.HttpStatusCode.NotFound, "while an unknown project is still not found");

    // The pages origin is not the forge. None of the application exists on that host.
    foreach (string route in new[] { "/api/session", "/api/browse", "/healthz", "/-/login" })
        using (HttpResponseMessage absent = await PagesGet(address, PagesHost, route))
            Check(absent.StatusCode == System.Net.HttpStatusCode.NotFound,
                "the pages origin carries no application route: " + route);

    using (HttpResponseMessage headers = await PagesGet(address, PagesHost, "/" + slug + "/"))
    {
        Check(headers.Headers.TryGetValues("X-Content-Type-Options", out IEnumerable<string>? nosniff) && nosniff.Contains("nosniff"),
            "a served site declines to have its types guessed");
        Check(headers.Headers.TryGetValues("Content-Security-Policy", out IEnumerable<string>? policy) &&
              policy.Any(value => value.Contains("frame-ancestors 'none'", StringComparison.Ordinal)),
            "and cannot be framed");
    }

    // An invalid config unpublishes the page rather than serving it with defaults.
    File.WriteAllText(Path.Combine(source, ".graft", "project.json"), """{"page":{"flies":["*"]}}""");
    await Git(source, ["add", "."]);
    await Git(source, ["commit", "-m", "Break the config"]);
    await Git(source, ["push", AuthenticatedUrl(address, "alice", grant.Secret, "alice/pagesample.git"), "main"]);
    using (HttpResponseMessage broken = await PagesGet(address, null, "/api/graft/pages/" + slug))
        Check(broken.StatusCode == System.Net.HttpStatusCode.NotFound,
            "an unparseable config unpublishes the page rather than serving it with defaults");
    using (HttpResponseMessage brokenSite = await PagesGet(address, PagesHost, "/" + slug + "/"))
        Check(brokenSite.StatusCode == System.Net.HttpStatusCode.NotFound, "and takes the site down with it");
}

/// <summary>
/// Writing repository content through the API rather than through a browser upload or a git push. This is
/// the half of ADR 0005 that API clients never had, and it is what lets an agent propose a change at all.
///
/// <para>
/// Runs late in the suite on purpose: a write creates a branch, and several checks above count branches.
/// </para>
/// </summary>
async Task CheckApiFileWrites(string address, AuthenticatedIdentity alice, long projectId)
{
    // Fresh credentials, minted here rather than threaded in: the suite revokes tokens while exercising
    // account administration, and a check this late cannot assume an earlier one still holds.
    TokenGrant apiGrant;
    TokenGrant readGrant;
    await using (GraftStore fixture = await GraftStore.OpenAsync(databasePath))
    {
        apiGrant = await fixture.CreateTokenAsync(alice, "file-write-api", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1));
        readGrant = await fixture.CreateTokenAsync(alice, "file-write-read", TokenScope.ReadApi, DateTimeOffset.UtcNow.AddHours(1));
    }
    string apiToken = apiGrant.Secret;
    string readApiToken = readGrant.Secret;

    // Writing a file through the API, which is the half of ADR 0005 that API clients never had: the browser
    // commits through upload receipts, and nothing else could commit at all.
    using (HttpResponseMessage written = await ApiPut(address, "/api/v4/projects/" + projectId + "/repository/files/" +
        Uri.EscapeDataString("docs/written-by-api.md"), apiToken,
        new { branch = "api-writes", commit_message = "Write a file through the API", encoding = "text", content = "# Written through the API\n" }))
    {
        Check(written.StatusCode == System.Net.HttpStatusCode.Created,
            "a file can be written through the API (got " + (int)written.StatusCode + ": " + Redact(await written.Content.ReadAsStringAsync()) + ")");
        using JsonDocument json = JsonDocument.Parse(await written.Content.ReadAsStreamAsync());
        Check(json.RootElement.GetProperty("commit_id").GetString()!.Length == 40, "and the commit it made is reported back");
        Check(json.RootElement.GetProperty("created_branch").GetBoolean(), "writing to a branch that is not there creates it");
    }

    // Read it back through the route that was already there, so the write is confirmed by the product
    // rather than by its own response.
    using (HttpResponseMessage read = await ApiGet(address, "/api/v4/projects/" + projectId + "/repository/files/" +
        Uri.EscapeDataString("docs/written-by-api.md") + "?ref=api-writes", apiToken))
    {
        Check(read.IsSuccessStatusCode, "the written file reads back");
        using JsonDocument json = JsonDocument.Parse(await read.Content.ReadAsStreamAsync());
        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(json.RootElement.GetProperty("content").GetString()!));
        Check(decoded == "# Written through the API\n", "with exactly the content that was sent");
    }

    // Base64 content, which is how an agent sends anything whose bytes must survive unchanged.
    using (HttpResponseMessage encoded = await ApiPut(address, "/api/v4/projects/" + projectId + "/repository/files/" +
        Uri.EscapeDataString("docs/encoded.txt"), apiToken,
        new { branch = "api-writes", commit_message = "Write base64 content", encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes("quotes \" and newlines\n")) }))
        Check(encoded.StatusCode == System.Net.HttpStatusCode.Created, "base64 content is accepted");
    using (HttpResponseMessage bad = await ApiPut(address, "/api/v4/projects/" + projectId + "/repository/files/" +
        Uri.EscapeDataString("docs/bad.txt"), apiToken,
        new { branch = "api-writes", commit_message = "Not base64", encoding = "base64", content = "not base64 at all!" }))
        Check(bad.StatusCode == System.Net.HttpStatusCode.BadRequest, "and content that is not base64 is refused rather than stored");

    // A read-only token may not write, and the refusal comes from the same scope rules a git push obeys.
    using (HttpResponseMessage refused = await ApiPut(address, "/api/v4/projects/" + projectId + "/repository/files/" +
        Uri.EscapeDataString("docs/denied.md"), readApiToken,
        new { branch = "api-writes", commit_message = "Should not happen", content = "nope" }))
        Check(refused.StatusCode == System.Net.HttpStatusCode.NotFound,
            "a token that may not push cannot write a file either");

    // Deleting a file, which the write route could not express: a commit whose tree lacks the path. The
    // branch and message travel in a JSON body first, as GitLab documents, then on the query string, which is
    // the form for a client whose DELETE carries no body.
    using (HttpResponseMessage denied = await ApiDeleteJson(address, "/api/v4/projects/" + projectId + "/repository/files/" +
        Uri.EscapeDataString("docs/written-by-api.md"), readApiToken, new { branch = "api-writes", commit_message = "Should not happen" }))
        Check(denied.StatusCode == System.Net.HttpStatusCode.NotFound, "a token that may not push cannot delete a file");
    using (HttpResponseMessage deleted = await ApiDeleteJson(address, "/api/v4/projects/" + projectId + "/repository/files/" +
        Uri.EscapeDataString("docs/written-by-api.md"), apiToken, new { branch = "api-writes", commit_message = "Delete a file through the API" }))
        Check(deleted.StatusCode == System.Net.HttpStatusCode.NoContent,
            "a file can be deleted through the API (got " + (int)deleted.StatusCode + ": " + Redact(await deleted.Content.ReadAsStringAsync()) + ")");
    using (HttpResponseMessage gone = await ApiGet(address, "/api/v4/projects/" + projectId + "/repository/files/" +
        Uri.EscapeDataString("docs/written-by-api.md") + "?ref=api-writes", apiToken))
        Check(gone.StatusCode == System.Net.HttpStatusCode.NotFound, "and it is no longer on the branch");
    using (HttpResponseMessage kept = await ApiGet(address, "/api/v4/projects/" + projectId + "/repository/files/" +
        Uri.EscapeDataString("docs/encoded.txt") + "?ref=api-writes", apiToken))
        Check(kept.IsSuccessStatusCode, "while the other file in the same directory is still there");
    using (HttpResponseMessage again = await ApiDeleteJson(address, "/api/v4/projects/" + projectId + "/repository/files/" +
        Uri.EscapeDataString("docs/written-by-api.md"), apiToken, new { branch = "api-writes", commit_message = "Delete it again" }))
        Check(again.StatusCode == System.Net.HttpStatusCode.NotFound, "deleting a file that is not there is refused, and no commit is made");
    using (HttpResponseMessage directory = await ApiDeleteJson(address, "/api/v4/projects/" + projectId + "/repository/files/docs",
        apiToken, new { branch = "api-writes", commit_message = "Delete a directory" }))
        Check(directory.StatusCode == System.Net.HttpStatusCode.BadRequest, "a directory is not a file, and is refused by name");
    using (HttpResponseMessage noBranch = await ApiDeleteJson(address, "/api/v4/projects/" + projectId + "/repository/files/" +
        Uri.EscapeDataString("docs/encoded.txt"), apiToken, new { branch = "never-made", commit_message = "Delete on a branch that is not there" }))
        Check(noBranch.StatusCode == System.Net.HttpStatusCode.BadRequest, "and a branch that does not exist cannot have a file deleted from it");
    using (HttpResponseMessage missing = await ApiDelete(address, "/api/v4/projects/" + projectId + "/repository/files/" +
        Uri.EscapeDataString("docs/encoded.txt"), apiToken))
        Check(missing.StatusCode == System.Net.HttpStatusCode.BadRequest, "a deletion without a branch is refused before anything is read");

    // The last file in the directory, by query string. Git has no empty trees, so the directory must go with
    // it, and the result must be a tree Git itself accepts.
    using (HttpResponseMessage last = await ApiDelete(address, "/api/v4/projects/" + projectId + "/repository/files/" +
        Uri.EscapeDataString("docs/encoded.txt") + "?branch=api-writes&commit_message=" + Uri.EscapeDataString("Delete the last file in docs"), apiToken))
        Check(last.StatusCode == System.Net.HttpStatusCode.NoContent, "the branch and message may travel on the query string instead");
    using (HttpResponseMessage tree = await ApiGet(address, "/api/v4/projects/" + projectId + "/repository/tree?ref=api-writes", apiToken))
    {
        Check(tree.IsSuccessStatusCode, "the tree of the branch still lists");
        using JsonDocument json = JsonDocument.Parse(await tree.Content.ReadAsStreamAsync());
        Check(!json.RootElement.EnumerateArray().Any(entry => entry.GetProperty("name").GetString() == "docs"),
            "and the directory emptied by the deletion is gone with its last file");
    }
    string deletionClone = Path.Combine(root, "deletion-clone");
    await Git(root, ["clone", "--quiet", "--branch", "api-writes", AuthenticatedUrl(address, "alice", apiToken, "alice/durable.git"), deletionClone]);
    Check(!Directory.Exists(Path.Combine(deletionClone, "docs")), "a clone of the branch has no docs directory");
    (int fsck, _, string fsckErrors) = await RunGit(deletionClone, ["fsck", "--full", "--strict"]);
    Check(fsck == 0, "and Git accepts every object the deletions wrote (" + fsckErrors.Trim() + ")");
    string log = await Git(deletionClone, ["log", "--format=%s", "--diff-filter=D", "--name-only", "api-writes"]);
    Check(log.Contains("docs/written-by-api.md", StringComparison.Ordinal) && log.Contains("docs/encoded.txt", StringComparison.Ordinal),
        "and Git reads both deletions as deletions in the history");
}

async Task<HttpResponseMessage> ApiDeleteJson(string address, string path, string? token, object body)
{
    HttpClientHandler handler = new() { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseProxy = false };
    HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };
    HttpRequestMessage request = new(HttpMethod.Delete, path) { Content = JsonContent.Create(body) };
    if (token is not null) request.Headers.Add("PRIVATE-TOKEN", token);
    HttpResponseMessage response = await client.SendAsync(request);
    request.Dispose();
    client.Dispose();
    handler.Dispose();
    return response;
}

/// <summary>
/// The instance-wide slug: a second name for a project that carries no account name, so a link can be
/// shared without naming its owner. What matters here is that it is only a name. It must reach the same
/// project, clone and push exactly as the owned path does, and change nothing about who may do either.
/// </summary>
async Task CheckGlobalSlug(string address, string apiToken, string readApiToken, string pushToken, long projectId, string worktree)
{
    string slug;
    using (HttpResponseMessage response = await ApiGet(address, "/api/v4/projects/" + projectId, apiToken))
    {
        Check(response.IsSuccessStatusCode, "the project reads back by id");
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        slug = json.RootElement.GetProperty("global_slug").GetString() ?? "";
        Check(slug == "durable", "a project is given its own path as its instance-wide slug when that name is free");
        Check(json.RootElement.GetProperty("global_web_url").GetString() == address.TrimEnd('/') + "/projects/durable",
            "and a shareable page URL built from it");
        // The point of the feature: the shared address contains no account name.
        Check(!json.RootElement.GetProperty("global_web_url").GetString()!.Contains("alice", StringComparison.Ordinal),
            "the shared URL does not name the owner");
    }

    // The REST surface accepts the slug wherever it accepts a path, so a caller can use the one name it was
    // given. Two segments stay GitLab's owner/slug; one segment that is not a number is this.
    using (HttpResponseMessage bySlug = await ApiGet(address, "/api/v4/projects/" + slug, apiToken))
    {
        Check(bySlug.IsSuccessStatusCode, "the REST API resolves a project by its instance-wide slug");
        using JsonDocument json = JsonDocument.Parse(await bySlug.Content.ReadAsStreamAsync());
        Check(json.RootElement.GetProperty("id").GetInt64() == projectId, "and resolves it to the same project");
    }

    // The browser page behind the shared link, which must answer without the owner's name anywhere in it.
    using (HttpResponseMessage page = await ApiGet(address, "/api/graft/slugs/" + slug, apiToken))
        Check(page.StatusCode == System.Net.HttpStatusCode.NotFound,
            "the shared page is a browser-session route and refuses a token");

    // Clone by the slug, with a credential that has no more rights than before.
    string clonePath = "projects/" + slug + ".git";
    string cloneUrl = AuthenticatedUrl(address, "bob", pushToken, clonePath);
    await Git(root, ["clone", "--no-checkout", cloneUrl, "slug-clone"]);
    Check(Directory.Exists(Path.Combine(root, "slug-clone", ".git")), "a repository clones by its instance-wide slug");

    // And push by the slug. This is the assertion that makes the slug a real address rather than a link:
    // the pushed commit has to arrive in the same repository the owned path serves.
    //
    // On a branch of its own, so the main-branch assertions elsewhere in this suite keep testing what they
    // were written to test rather than whatever this pushed last.
    await Git(worktree, ["checkout", "-b", "shared-slug-push"]);
    File.AppendAllText(Path.Combine(worktree, "readme.txt"), "pushed through the shared slug\n");
    await Git(worktree, ["add", "."]);
    await Git(worktree, ["commit", "-m", "Push through the instance-wide slug"]);
    await Git(worktree, ["push", cloneUrl, "shared-slug-push"]);
    await Git(worktree, ["checkout", "main"]);

    string throughOwnedPath = await Git(root, ["ls-remote", AuthenticatedUrl(address, "bob", pushToken, "alice/durable.git"), "refs/heads/shared-slug-push"]);
    string throughSlug = await Git(root, ["ls-remote", cloneUrl, "refs/heads/shared-slug-push"]);
    Check(throughOwnedPath.Length > 0 && throughOwnedPath.Split('\t')[0] == throughSlug.Split('\t')[0],
        "a push through the slug lands in the repository the owned path serves");

    // An unauthenticated caller is refused exactly as it is on the owned path. Asserted as "the same
    // answer" rather than a fixed status, because the invariant that matters is that the slug is a name
    // and not a second, looser way in; whatever the owned path says to a stranger, this must say too.
    using (HttpResponseMessage bySlug = await ApiGet(address, "/api/v4/projects/" + slug, null))
    using (HttpResponseMessage byPath = await ApiGet(address, "/api/v4/projects/" + Uri.EscapeDataString("alice/durable"), null))
        Check(bySlug.StatusCode == byPath.StatusCode && bySlug.StatusCode != System.Net.HttpStatusCode.OK,
            "an anonymous caller gets the same refusal through the slug as through the owned path");

    // A slug nobody has is a miss, and it is the same miss as a project somebody may not see, so the flat
    // namespace cannot be probed to learn what exists.
    using (HttpResponseMessage missing = await ApiGet(address, "/api/v4/projects/no-such-slug-here", apiToken))
        Check(missing.StatusCode == System.Net.HttpStatusCode.NotFound, "an unused slug is not found");
    using (HttpResponseMessage hidden = await ApiGet(address, "/api/v4/projects/private", apiToken))
        Check(hidden.StatusCode == System.Net.HttpStatusCode.NotFound,
            "another account's private project answers the same way through its slug as it does by path");

    // A project avatar through the API. The browser sets one through the resumable upload transport, which
    // carries a receipt rather than bytes, so until now no API client could set an avatar at all.
    using (HttpResponseMessage avatar = await ApiPutBytes(address, "/api/v4/projects/" + projectId + "/avatar", apiToken,
        OnePixelPng(), "image/png"))
    {
        Check(avatar.IsSuccessStatusCode, "a project avatar can be set through the API");
        using JsonDocument json = JsonDocument.Parse(await avatar.Content.ReadAsStreamAsync());
        Check(json.RootElement.GetProperty("avatar_url").ValueKind != JsonValueKind.Null,
            "and the project then reports an avatar URL");
    }

    // Validated by the same code the browser route uses, so anything that is not an image is refused
    // rather than stored and served back later as one.
    using (HttpResponseMessage notAnImage = await ApiPutBytes(address, "/api/v4/projects/" + projectId + "/avatar", apiToken,
        Encoding.UTF8.GetBytes("<script>alert(1)</script>"), "image/png"))
        Check(notAnImage.StatusCode == System.Net.HttpStatusCode.BadRequest, "content that is not an image is refused");

    using (HttpResponseMessage denied = await ApiPutBytes(address, "/api/v4/projects/" + projectId + "/avatar", readApiToken,
        OnePixelPng(), "image/png"))
        Check(denied.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden,
            "a token that may not manage the project cannot set its avatar");

    // Membership through the API. The browser has always been able to grant a role; a token could not, so
    // an agent could see a project and never who was allowed to work on it.
    using (HttpResponseMessage members = await ApiGet(address, "/api/v4/projects/" + projectId + "/members", apiToken))
    {
        Check(members.IsSuccessStatusCode, "members can be listed through the API");
        using JsonDocument json = JsonDocument.Parse(await members.Content.ReadAsStreamAsync());
        Check(json.RootElement.EnumerateArray().Any(member => member.GetProperty("username").GetString() == "bob"),
            "and the existing member is there");
    }

    using (HttpResponseMessage granted = await ApiPost(address, "/api/v4/projects/" + projectId + "/members", apiToken,
        new { username = "carol", role = "reporter" }))
    {
        Check(granted.IsSuccessStatusCode, "a role can be granted through the API");
        using JsonDocument json = JsonDocument.Parse(await granted.Content.ReadAsStreamAsync());
        Check(json.RootElement.GetProperty("role").GetString() == "reporter", "and the granted role is reported back");
    }

    // An account that is not there is a bad request rather than the project's own not-found, which would
    // say something untrue about the project.
    using (HttpResponseMessage absent = await ApiPost(address, "/api/v4/projects/" + projectId + "/members", apiToken,
        new { username = "nobody-at-all", role = "reporter" }))
        Check(absent.StatusCode == System.Net.HttpStatusCode.BadRequest, "granting to an account that is not there is a bad request");
    using (HttpResponseMessage badRole = await ApiPost(address, "/api/v4/projects/" + projectId + "/members", apiToken,
        new { username = "carol", role = "owner" }))
        Check(badRole.StatusCode == System.Net.HttpStatusCode.BadRequest, "and a role Graft does not have is refused by name");

    using (HttpResponseMessage removed = await ApiDelete(address, "/api/v4/projects/" + projectId + "/members/carol", apiToken))
        Check(removed.StatusCode == System.Net.HttpStatusCode.NoContent, "a role can be taken away through the API");
    using (HttpResponseMessage after = await ApiGet(address, "/api/v4/projects/" + projectId + "/members", apiToken))
    {
        using JsonDocument json = JsonDocument.Parse(await after.Content.ReadAsStreamAsync());
        Check(!json.RootElement.EnumerateArray().Any(member => member.GetProperty("username").GetString() == "carol"),
            "and the listing reflects it");
    }

    // Reading the membership is itself a Maintainer-equivalent act, so a token that may only read the
    // project cannot enumerate who works on it.
    using (HttpResponseMessage denied = await ApiGet(address, "/api/v4/projects/" + projectId + "/members", readApiToken))
        Check(denied.StatusCode == System.Net.HttpStatusCode.NotFound, "a read-only token cannot enumerate a project's members");

    // A project's text, after it was created. Until this route accepted them, a description could be set
    // once and never corrected, and the name could not be changed at all.
    using (HttpResponseMessage updated = await ApiPut(address, "/api/v4/projects/" + projectId, apiToken,
        new { name = "Durable, renamed", description = "Edited after creation" }))
    {
        Check(updated.IsSuccessStatusCode, "a project's name and description can be changed after creation");
        using JsonDocument json = JsonDocument.Parse(await updated.Content.ReadAsStreamAsync());
        Check(json.RootElement.GetProperty("name").GetString() == "Durable, renamed" &&
              json.RootElement.GetProperty("description").GetString() == "Edited after creation",
            "and the response reports what it now holds");
        // Neither is an address, so neither may move the project.
        Check(json.RootElement.GetProperty("path_with_namespace").GetString() == "alice/durable" &&
              json.RootElement.GetProperty("global_slug").GetString() == "durable",
            "changing text moves neither the owned path nor the instance-wide slug");
    }

    // One field at a time, because a caller that has to resend both races anybody else editing the other.
    using (HttpResponseMessage one = await ApiPut(address, "/api/v4/projects/" + projectId, apiToken,
        new { description = "Only the description this time" }))
    {
        Check(one.IsSuccessStatusCode, "one attribute can be sent on its own");
        using JsonDocument json = JsonDocument.Parse(await one.Content.ReadAsStreamAsync());
        Check(json.RootElement.GetProperty("name").GetString() == "Durable, renamed", "and the others are left alone");
    }

    // Still refused by name, so a caller is never told it succeeded at something that did not happen.
    using (HttpResponseMessage unsupported = await ApiPut(address, "/api/v4/projects/" + projectId, apiToken,
        new { path = "renamed-path" }))
        Check(unsupported.StatusCode == System.Net.HttpStatusCode.BadRequest,
            "an attribute this route does not implement is still refused by name");
    using (HttpResponseMessage empty = await ApiPut(address, "/api/v4/projects/" + projectId, apiToken, new { }))
        Check(empty.StatusCode == System.Net.HttpStatusCode.BadRequest, "and a body asking for nothing is refused");

    // The collision rule. Carol already owns a project whose path is "private", so she holds that name
    // instance-wide; alice may still create a project at the same path in her own namespace, and it has to
    // be given a different shared name rather than taking Carol's or failing.
    using (HttpResponseMessage colliding = await ApiPost(address, "/api/v4/projects", apiToken,
        new { name = "Same path as somebody else", path = "private", visibility = "private" }))
    {
        Check(colliding.StatusCode == System.Net.HttpStatusCode.Created, "a path another account already uses is still creatable");
        using JsonDocument json = JsonDocument.Parse(await colliding.Content.ReadAsStreamAsync());
        string taken = json.RootElement.GetProperty("global_slug").GetString() ?? "";
        Check(taken != "private" && taken.StartsWith("private-", StringComparison.Ordinal) && taken.Length > "private-".Length,
            "the second project to want a name keeps its path and gains a suffix");
        Check(json.RootElement.GetProperty("path_with_namespace").GetString() == "alice/private",
            "while its owned path is untouched");
    }

    // Pushing to an unused slug never creates anything, even where push-to-create is enabled, because a
    // slug names no owner to create it under.
    await GitFails(root, ["clone", AuthenticatedUrl(address, "bob", pushToken, "projects/invented-by-a-push.git"), "invented"],
        "a push or fetch at an unused slug does not create a project");
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

/// <summary>
/// Publishing, and the two ways a project can become readable without credentials. The repository's
/// visibility opens the whole repository including clone; a published page opens a curated surface and
/// nothing that walks the object graph. Both are asserted from the outside, with no session and no token,
/// because that is the only vantage point from which "not available" can be believed.
/// </summary>
async Task<string> CheckPublicBrowsing(string address, AuthenticatedIdentity alice)
{
    long cardId;
    TokenGrant pushToken;

    // Rows are written straight through the store while the server is still refusing to publish anything.
    // That is deliberate: it reproduces a database that was published on one host and carried to another,
    // and it is the only way to prove the operator switch withdraws a grant the data still claims.
    await using (GraftStore fixture = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo open = await fixture.CreateProjectWithRepositoryAsync(alice, "open", "Open source",
            (created, token) => { token.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, created.StorageId)); return Task.CompletedTask; },
            "Readable by anyone.");
        ProjectInfo hidden = await fixture.CreateProjectWithRepositoryAsync(alice, "hidden", "Unlisted project",
            (created, token) => { token.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, created.StorageId)); return Task.CompletedTask; },
            "Reachable only by its path.");
        ProjectInfo card = await fixture.CreateProjectWithRepositoryAsync(alice, "card", "Page only",
            (created, token) => { token.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, created.StorageId)); return Task.CompletedTask; },
            "A page over a private repository.");
        cardId = card.Id;

        await fixture.SetProjectVisibilityAsync(alice, open.Id, ProjectVisibility.Public);
        await fixture.SetProjectVisibilityAsync(alice, hidden.Id, ProjectVisibility.Unlisted);
        await fixture.SetPagePublishedAsync(alice, card.Id, true);
        pushToken = await fixture.CreateTokenAsync(alice, "publish-push", TokenScope.WriteRepository, DateTimeOffset.UtcNow.AddHours(1));

        // The store's own view of the precedence rule, before any of it is routed through HTTP.
        PermissionDecision anonymousOpen = await fixture.GetPermissionAsync(null, open.Id, ProjectAction.FetchRepository);
        Check(anonymousOpen.Allowed && anonymousOpen.Grant == AccessGrant.Repository,
            "a public repository grants an anonymous fetch through visibility");
        PermissionDecision anonymousPush = await fixture.GetPermissionAsync(null, open.Id, ProjectAction.PushRepository);
        Check(!anonymousPush.Allowed, "a public repository still refuses an anonymous push");
        PermissionDecision ownerOpen = await fixture.GetPermissionAsync(alice, open.Id, ProjectAction.PushRepository);
        Check(ownerOpen.Allowed && ownerOpen.Grant == AccessGrant.Member,
            "the owner of a public project is still a member, not a visitor");
        PermissionDecision cardBrowse = await fixture.GetPermissionAsync(null, card.Id, ProjectAction.BrowseRepository);
        Check(!cardBrowse.Allowed, "a published page does not open the repository behind it");
        PermissionDecision cardPage = await fixture.GetPermissionAsync(null, card.Id, ProjectAction.ReadPage);
        Check(cardPage.Allowed && cardPage.Grant == AccessGrant.Page,
            "a published page grants only the page");
        PermissionDecision hiddenPage = await fixture.GetPermissionAsync(null, hidden.Id, ProjectAction.ReadPage);
        Check(hiddenPage.Allowed && hiddenPage.Grant == AccessGrant.Repository,
            "an unlisted repository is read through visibility, not through a page");
    }

    string origin = address.TrimEnd('/');

    // With publishing disabled, every one of those stored rows is inert.
    using (HttpResponseMessage browse = await ApiGet(address, "/api/browse", null))
        Check(browse.StatusCode == HttpStatusCode.NotFound, "the public listing is absent until an operator enables it");
    using (HttpResponseMessage page = await ApiGet(address, "/api/browser/projects/alice/open", null))
        Check(page.StatusCode == HttpStatusCode.NotFound, "a stored public project is not served while publishing is disabled");
    await GitFails(root, ["ls-remote", origin + "/alice/open.git"],
        "a stored public repository does not clone while publishing is disabled");

    // Now the operator allows it, and the same rows start meaning what they say.
    await StopServer();
    publicBrowsing = true;
    address = await StartServer();
    origin = address.TrimEnd('/');

    // Content, so the assertions below read a repository rather than an empty one.
    string source = Path.Combine(root, "publish-source");
    Directory.CreateDirectory(source);
    await Git(source, ["init", "--initial-branch=main"]);
    File.WriteAllText(Path.Combine(source, "README.md"), "# Open source\n\nAnyone may read this.\n");
    await Git(source, ["add", "."]);
    await Git(source, ["commit", "-m", "Public initial commit"]);
    await Git(source, ["remote", "add", "origin", AuthenticatedUrl(address, "alice", pushToken.Secret, "alice/open.git")]);
    await Git(source, ["push", "origin", "main"]);
    // The unlisted project gets the same content, so the assertion below tests who may read it rather than
    // whether an empty repository has a tree to show.
    await Git(source, ["remote", "add", "unlisted", AuthenticatedUrl(address, "alice", pushToken.Secret, "alice/hidden.git")]);
    await Git(source, ["push", "unlisted", "main"]);

    // Anonymous clone, which is the whole point of public visibility and the one thing a page can never do.
    await Git(root, ["clone", "--no-checkout", origin + "/alice/open.git", "anonymous-clone"]);
    Check(Directory.Exists(Path.Combine(root, "anonymous-clone", ".git")), "a public repository clones with no credentials");
    await Git(root, ["ls-remote", origin + "/alice/hidden.git"]);
    Check(true, "an unlisted repository is reachable by its path with no credentials");
    await GitFails(root, ["ls-remote", origin + "/alice/card.git"],
        "a published page does not make its repository cloneable");
    await GitFails(root, ["ls-remote", origin + "/carol/private.git"],
        "a private repository is still refused with no credentials");

    // The listing. Public and page-only appear; unlisted does not, which is the whole of what unlisted means.
    using (HttpResponseMessage response = await ApiGet(address, "/api/browse", null))
    {
        Check(response.IsSuccessStatusCode, "the public listing is served with no credentials");
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        string[] paths = [.. body.RootElement.GetProperty("projects").EnumerateArray()
            .Select(item => item.GetProperty("path").GetString()!)];
        Check(paths.Contains("alice/open"), "a public project is listed");
        Check(paths.Contains("alice/card"), "a project that publishes only a page is listed");
        Check(!paths.Contains("alice/hidden"), "an unlisted project is never listed");
        Check(!paths.Contains("alice/durable") && !paths.Contains("carol/private"), "a private project is never listed");
        JsonElement card = body.RootElement.GetProperty("projects").EnumerateArray()
            .Single(item => item.GetProperty("path").GetString() == "alice/card");
        Check(card.GetProperty("source_public").GetBoolean() is false,
            "a page-only project does not claim its source is public");
        Check(body.RootElement.GetProperty("projects").EnumerateArray()
            .Single(item => item.GetProperty("path").GetString() == "alice/open")
            .GetProperty("source_public").GetBoolean(), "a public project reports that its source is public");
    }

    // Search answers over the published set only, so it cannot become a way to confirm a private project.
    using (HttpResponseMessage hit = await ApiGet(address, "/api/browse?search=Readable", null))
    {
        using JsonDocument body = JsonDocument.Parse(await hit.Content.ReadAsStreamAsync());
        Check(body.RootElement.GetProperty("projects").GetArrayLength() == 1 &&
            body.RootElement.GetProperty("projects")[0].GetProperty("path").GetString() == "alice/open",
            "search matches a published description");
    }
    using (HttpResponseMessage miss = await ApiGet(address, "/api/browse?search=durable", null))
    {
        using JsonDocument body = JsonDocument.Parse(await miss.Content.ReadAsStreamAsync());
        Check(body.RootElement.GetProperty("projects").GetArrayLength() == 0, "search does not reach a private project");
    }
    // A caller-supplied wildcard would otherwise match everything, which is what makes this worth asserting
    // rather than assuming: the pattern is escaped, so the percent is searched for as a character.
    using (HttpResponseMessage wildcard = await ApiGet(address, "/api/browse?search=%25", null))
    {
        using JsonDocument body = JsonDocument.Parse(await wildcard.Content.ReadAsStreamAsync());
        Check(body.RootElement.GetProperty("projects").GetArrayLength() == 0, "a wildcard in a search is searched for, not interpreted");
    }

    // The repository view, anonymously. Public and unlisted open; page-only and private do not.
    using (HttpResponseMessage open = await ApiGet(address, "/api/browser/projects/alice/open", null))
    {
        Check(open.IsSuccessStatusCode, "a public project opens with no credentials");
        using JsonDocument body = JsonDocument.Parse(await open.Content.ReadAsStreamAsync());
        Check(body.RootElement.GetProperty("project").GetProperty("visibility").GetString() == "public",
            "the project reports the visibility it actually has");
        Check(body.RootElement.GetProperty("can_push").GetBoolean() is false &&
            body.RootElement.GetProperty("can_manage").GetBoolean() is false,
            "a visitor is offered neither push nor management");
        Check(body.RootElement.GetProperty("readme").ValueKind != JsonValueKind.Null,
            "a public project renders its README for a visitor");
    }
    using (HttpResponseMessage hidden = await ApiGet(address, "/api/browser/projects/alice/hidden", null))
        Check(hidden.IsSuccessStatusCode, "an unlisted project opens for anyone holding its path");
    using (HttpResponseMessage card = await ApiGet(address, "/api/browser/projects/alice/card", null))
        Check(card.StatusCode == HttpStatusCode.NotFound, "a page-only project refuses the repository view");
    using (HttpResponseMessage secret = await ApiGet(address, "/api/browser/projects/carol/private", null))
        Check(secret.StatusCode == HttpStatusCode.NotFound, "a private project refuses the repository view");
    using (HttpResponseMessage absent = await ApiGet(address, "/api/browser/projects/alice/no-such-project", null))
        Check(absent.StatusCode == HttpStatusCode.NotFound, "a project that does not exist is refused identically");

    // A diff prints file contents, so it follows the repository and never a page.
    using (HttpResponseMessage diff = await ApiGet(address, "/api/browser/projects/" + cardId + "/commit/" + new string('0', 40), null))
        Check(diff.StatusCode == HttpStatusCode.NotFound, "a page-only project refuses a commit diff");

    // The REST surface answers an anonymous caller for a public project, and no token is a different thing
    // from a token that does not resolve.
    using (HttpResponseMessage rest = await ApiGet(address, "/api/v4/projects/alice%2Fopen", null))
        Check(rest.IsSuccessStatusCode, "the REST project route answers a public project without a token");
    using (HttpResponseMessage restPrivate = await ApiGet(address, "/api/v4/projects/carol%2Fprivate", null))
        Check(restPrivate.StatusCode == HttpStatusCode.NotFound, "the REST project route conceals a private project from a visitor");
    using (HttpResponseMessage badToken = await ApiGet(address, "/api/v4/projects/alice%2Fopen", "graft_pat_" + new string('0', 64)))
        Check(badToken.StatusCode == HttpStatusCode.Unauthorized,
            "a token that does not resolve is an error rather than an anonymous request");
    using (HttpResponseMessage tree = await ApiGet(address, "/api/v4/projects/alice%2Fopen/repository/tree?ref=main", null))
        Check(tree.IsSuccessStatusCode, "a public project's tree is readable without a token");
    using (HttpResponseMessage cardTree = await ApiGet(address, "/api/v4/projects/alice%2Fcard/repository/tree?ref=main", null))
        Check(cardTree.StatusCode == HttpStatusCode.NotFound, "a page-only project's tree is refused without a token");

    // An avatar route is a cheap way to ask which accounts exist, so one that has no avatar is a 404 whether
    // or not the account is real.
    using (HttpResponseMessage ownerAvatar = await ApiGet(address, "/api/graft/avatars/user/" + alice.UserId, null))
        Check(ownerAvatar.StatusCode == HttpStatusCode.NotFound, "an owner with no avatar set is still a 404");

    return address;
}

/// <summary>
/// Push to a path that does not exist, and the project is created to receive it. Most of these assertions
/// are about the cases that must <em>not</em> create, because a feature whose job is to make things appear
/// is only safe if the list of ways to make it appear is short and checked.
/// </summary>
async Task<string> CheckPushToCreate(string address, AuthenticatedIdentity alice, string bobUsername, string bobPassword)
{
    TokenGrant alicePush;
    TokenGrant aliceReadOnly;
    TokenGrant bobPush;

    await using (GraftStore fixture = await GraftStore.OpenAsync(databasePath))
    {
        alicePush = await fixture.CreateTokenAsync(alice, "create-push", TokenScope.WriteRepository, DateTimeOffset.UtcNow.AddHours(1));
        aliceReadOnly = await fixture.CreateTokenAsync(alice, "create-read", TokenScope.ReadRepository, DateTimeOffset.UtcNow.AddHours(1));
        SessionGrant bobSession = Required(await fixture.LoginAsync(bobUsername, bobPassword), "bob login for push-to-create");
        AuthenticatedIdentity bobIdentity = Required(await fixture.ResolveSessionAsync(bobSession.Secret), "bob identity for push-to-create");
        bobPush = await fixture.CreateTokenAsync(bobIdentity, "bob-create-push", TokenScope.WriteRepository, DateTimeOffset.UtcNow.AddHours(1));
    }

    string source = Path.Combine(root, "create-source");
    Directory.CreateDirectory(source);
    await Git(source, ["init", "--initial-branch=main"]);
    File.WriteAllText(Path.Combine(source, "created.txt"), "created by a push\n");
    await Git(source, ["add", "."]);
    await Git(source, ["commit", "-m", "Commit that creates its own project"]);

    async Task<bool> Exists(string slug)
    {
        await using GraftStore check = await GraftStore.OpenAsync(databasePath);
        return await check.FindReadyProjectAsync("alice", slug) is not null;
    }

    // Off by default. A push to a path that does not exist is a miss, and nothing appears.
    await GitFails(source, ["push", AuthenticatedUrl(address, "alice", alicePush.Secret, "alice/absent.git"), "main"],
        "a push to a missing project is refused while push-to-create is disabled");
    Check(!await Exists("absent"), "a refused push creates nothing while push-to-create is disabled");

    await StopServer();
    pushToCreate = true;
    address = await StartServer();

    // The feature itself.
    await Git(source, ["push", AuthenticatedUrl(address, "alice", alicePush.Secret, "alice/created.git"), "main"]);
    Check(await Exists("created"), "a push to a path that does not exist creates the project");

    await using (GraftStore check = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo created = Required(await check.FindReadyProjectAsync("alice", "created"), "created project");
        Check(created.OwnerUserId == alice.UserId, "the pusher owns what their push created");
        Check(created.Visibility == ProjectVisibility.Private,
            "a project created by a push is private, because publishing is never a side effect");
        Check(!created.PagePublished, "a project created by a push publishes no page");
        PermissionDecision anonymous = await check.GetPermissionAsync(null, created.Id, ProjectAction.BrowseRepository);
        Check(!anonymous.Allowed, "a project created by a push is not readable by a stranger");
    }

    // Pushing again to the same path is an ordinary push to an existing project, not a second creation.
    File.AppendAllText(Path.Combine(source, "created.txt"), "second revision\n");
    await Git(source, ["add", "."]);
    await Git(source, ["commit", "-m", "Second commit"]);
    await Git(source, ["push", AuthenticatedUrl(address, "alice", alicePush.Secret, "alice/created.git"), "main"]);
    Check(true, "a second push to the same path is an ordinary push");

    // Everything below must refuse, and must leave nothing behind.
    await GitFails(source, ["push", AuthenticatedUrl(address, "bob", bobPush.Secret, "alice/bobs-attempt.git"), "main"],
        "a push cannot create a project in somebody else's namespace");
    Check(!await Exists("bobs-attempt"), "a refused cross-namespace push creates nothing");

    await GitFails(source, ["push", AuthenticatedUrl(address, "alice", aliceReadOnly.Secret, "alice/read-only.git"), "main"],
        "a token that cannot push cannot create by pushing");
    Check(!await Exists("read-only"), "a refused read-only push creates nothing");

    await GitFails(source, ["push", address.TrimEnd('/') + "/alice/anonymous.git", "main"],
        "an anonymous push creates nothing");
    Check(!await Exists("anonymous"), "a refused anonymous push creates nothing");

    // A path the REST route would reject must not become creatable because it arrived over Git. Two
    // consecutive separators rather than dots or traversal, so the case is decided by Graft's own path rule
    // and not by a client or a router normalising the URL before it arrives.
    await GitFails(source, ["push", AuthenticatedUrl(address, "alice", alicePush.Secret, "alice/bad--name.git"), "main"],
        "a push cannot create a project at a path the API would reject");
    Check(!await Exists("bad--name"), "a refused invalid path creates nothing");

    // Fetch is not a creation verb. The clone below must fail and must leave nothing behind, or every
    // mistyped clone URL would litter the namespace with empty projects.
    await GitFails(root, ["clone", AuthenticatedUrl(address, "alice", alicePush.Secret, "alice/never-fetched.git"), "never-fetched"],
        "a fetch of a missing project is still a miss");
    Check(!await Exists("never-fetched"), "a fetch creates nothing");

    // The audit trail distinguishes the two ways a project can come into being.
    await using (GraftStore audit = await GraftStore.OpenAsync(databasePath))
        Check(await audit.CountAuditEventsAsync() > 0, "project creation is audited");

    return address;
}

/// <summary>
/// A push large enough that the client streams it instead of sending it in one buffer. Git cannot replay a
/// chunked body after a challenge, so it spends an empty request on authentication first and only then
/// streams the pack. A server that answers that probe with an error refuses every push above the client's
/// default buffer while every smaller push keeps working.
///
/// <para>
/// This suite missed exactly that for as long as every repository it pushed was a few kilobytes. The
/// repository below is deliberately incompressible so the pack stays over the threshold and the transfer is
/// really chunked, and the push runs at the client's default settings because raising
/// <c>http.postBuffer</c> is what hides the bug.
/// </para>
/// </summary>
async Task CheckChunkedPush(string address, AuthenticatedIdentity alice)
{
    TokenGrant token;
    await using (GraftStore fixture = await GraftStore.OpenAsync(databasePath))
    {
        await fixture.CreateProjectWithRepositoryAsync(alice, "chunked", "Chunked push",
            (created, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, created.StorageId));
                return Task.CompletedTask;
            }, "A repository too large to send in one buffer.");
        token = await fixture.CreateTokenAsync(alice, "chunked-push", TokenScope.WriteRepository, DateTimeOffset.UtcNow.AddHours(1));
    }

    string source = Path.Combine(root, "chunked-source");
    Directory.CreateDirectory(source);
    await Git(source, ["init", "--initial-branch=main"]);
    File.WriteAllBytes(Path.Combine(source, "noise.bin"), RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
    await Git(source, ["add", "."]);
    await Git(source, ["commit", "-m", "A commit larger than the client's buffer"]);
    string head = (await Git(source, ["rev-parse", "HEAD"])).Trim();

    string remote = AuthenticatedUrl(address, "alice", token.Secret, "alice/chunked.git");
    await Git(source, ["remote", "add", "origin", remote]);
    await Git(source, ["push", "origin", "main"]);
    Check(true, "a push larger than the client's buffer succeeds at default settings");

    // Read it back rather than trusting the push's own report, and check the bytes survived the transfer.
    string clone = Path.Combine(root, "chunked-clone");
    await Git(root, ["clone", remote, "chunked-clone"]);
    Check((await Git(clone, ["rev-parse", "HEAD"])).Trim() == head, "the chunked push landed the commit it sent");
    await Git(clone, ["fsck", "--full", "--strict"]);
    Check(new FileInfo(Path.Combine(clone, "noise.bin")).Length == 2 * 1024 * 1024,
        "the chunked push landed the whole file");
}

/// <summary>
/// A session that renews has to tell the browser so. The browser drops the cookie at exactly the moment the
/// server stops honouring it, so a stored expiry that moves without a refreshed cookie leaves the next
/// request anonymous against a session that is still perfectly good. The store suite cannot see that half
/// of it, which is where this went wrong the first time.
/// </summary>
async Task CheckSessionRenewal(string address, string username, string password)
{
    CookieContainer cookies = new();
    using HttpClientHandler handler = new()
    {
        CookieContainer = cookies,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        UseProxy = false
    };
    using HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };

    using (HttpRequestMessage login = new(HttpMethod.Post, "/api/login"))
    {
        login.Headers.Add("Origin", address.TrimEnd('/'));
        login.Content = JsonContent.Create(new { username, password });
        using HttpResponseMessage response = await client.SendAsync(login);
        Check(response.IsSuccessStatusCode, "session renewal fixture signs in");
    }

    static bool SetsSessionCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values) &&
        values.Any(value => value.StartsWith("__Host-graft-session=", StringComparison.Ordinal));

    using (HttpResponseMessage fresh = await client.GetAsync("/api/session"))
        Check(!SetsSessionCookie(fresh),
            "a session well inside its window is not rewritten on every request");

    // Age the stored row past the halfway point rather than waiting out the window.
    await using (Microsoft.Data.Sqlite.SqliteConnection db = new($"Data Source={databasePath};Pooling=False"))
    {
        await db.OpenAsync();
        await using Microsoft.Data.Sqlite.SqliteCommand command = db.CreateCommand();
        command.CommandText = "UPDATE BrowserSessions SET ExpiresAtUtc = $expires WHERE RevokedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$expires", DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    using (HttpResponseMessage renewed = await client.GetAsync("/api/session"))
    {
        Check(renewed.IsSuccessStatusCode, "an aged session still authenticates");
        Check(SetsSessionCookie(renewed), "a renewed session arrives with a refreshed cookie");
        string cookie = renewed.Headers.GetValues("Set-Cookie")
            .First(value => value.StartsWith("__Host-graft-session=", StringComparison.Ordinal));
        Check(cookie.Contains("expires=", StringComparison.OrdinalIgnoreCase) &&
            cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) &&
            cookie.Contains("secure", StringComparison.OrdinalIgnoreCase),
            "the refreshed cookie keeps the protections the original had");
    }

    // The refreshed cookie carries the new expiry, so the browser holds a session good for a full window
    // again rather than one about to lapse.
    Cookie stored = cookies.GetCookies(new(address))["__Host-graft-session"]!;
    Check(stored.Expires.ToUniversalTime() > DateTime.UtcNow.AddMinutes(2),
        "the browser's copy of the session now outlives the minute it had left");

    using (HttpResponseMessage after = await client.GetAsync("/api/projects"))
        Check(after.IsSuccessStatusCode, "the session keeps working after renewal");
}

/// <summary>
/// The two project settings, reached with a token and no browser. Until this existed an API client could
/// create a project already public but could never change one, and could not publish a page at all, so an
/// automated caller could set up a forge and then not administer it.
/// </summary>
async Task CheckTokenProjectSettings(string address, AuthenticatedIdentity alice, string bobUsername, string bobPassword)
{
    TokenGrant apiToken;
    TokenGrant readOnlyToken;
    TokenGrant bobToken;
    await using (GraftStore fixture = await GraftStore.OpenAsync(databasePath))
    {
        await fixture.CreateProjectWithRepositoryAsync(alice, "settings", "Settings target",
            (created, token) => { token.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, created.StorageId)); return Task.CompletedTask; },
            "Driven entirely by token.");
        apiToken = await fixture.CreateTokenAsync(alice, "settings-api", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1));
        readOnlyToken = await fixture.CreateTokenAsync(alice, "settings-read", TokenScope.ReadApi, DateTimeOffset.UtcNow.AddHours(1));
        SessionGrant bobSession = Required(await fixture.LoginAsync(bobUsername, bobPassword), "bob login for settings");
        AuthenticatedIdentity bobIdentity = Required(await fixture.ResolveSessionAsync(bobSession.Secret), "bob identity for settings");
        bobToken = await fixture.CreateTokenAsync(bobIdentity, "settings-bob", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1));
    }

    const string target = "/api/v4/projects/alice%2Fsettings";
    const string pageRoute = "/api/graft/projects/alice%2Fsettings/page";

    // The happy path, with nothing but a token.
    using (HttpResponseMessage response = await ApiPutJson(address, target, apiToken.Secret, "{\"visibility\":\"public\"}"))
    {
        Check(response.IsSuccessStatusCode, "a token can change a project's visibility");
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Check(body.RootElement.GetProperty("visibility").GetString() == "public",
            "the edit route reports the visibility it just set");
    }
    using (HttpResponseMessage response = await ApiGet(address, target, apiToken.Secret))
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Check(body.RootElement.GetProperty("visibility").GetString() == "public", "the change is persisted");
    }
    using (HttpResponseMessage response = await ApiPutJson(address, pageRoute, apiToken.Secret, "{\"published\":true}"))
    {
        Check(response.IsSuccessStatusCode, "a token can publish a project page");
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Check(body.RootElement.GetProperty("published").GetBoolean(), "the page route reports the state it set");
    }
    using (HttpResponseMessage response = await ApiGet(address, pageRoute, apiToken.Secret))
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Check(body.RootElement.GetProperty("published").GetBoolean(), "the page state reads back");
    }

    // Refusals. Each one matters: this route can publish somebody's private source to the world.
    using (HttpResponseMessage response = await ApiPutJson(address, target, readOnlyToken.Secret, "{\"visibility\":\"private\"}"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "a read_api token cannot change visibility");
    using (HttpResponseMessage response = await ApiPutJson(address, pageRoute, readOnlyToken.Secret, "{\"published\":false}"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "a read_api token cannot publish a page");
    using (HttpResponseMessage response = await ApiPutJson(address, target, bobToken.Secret, "{\"visibility\":\"private\"}"))
        Check(response.StatusCode == HttpStatusCode.NotFound,
            "somebody else's token cannot change a project, and is told nothing about whether it exists");
    using (HttpResponseMessage response = await ApiPutJson(address, target, null, "{\"visibility\":\"private\"}"))
        Check(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound,
            "an anonymous caller cannot change a project");

    // The same field vocabulary the create route enforces, and the same refusal to ignore what it was sent.
    using (HttpResponseMessage response = await ApiPutJson(address, target, apiToken.Secret, "{\"visibility\":\"internal\"}"))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "the edit route refuses a visibility Graft does not implement");
    // `name` is implemented now, so the refusal is asserted with something that still is not: `path` is an
    // address, and this route does not move projects.
    using (HttpResponseMessage response = await ApiPutJson(address, target, apiToken.Secret, "{\"path\":\"renamed\"}"))
        Check(response.StatusCode == HttpStatusCode.BadRequest,
            "the edit route refuses a property it does not implement rather than ignoring it");
    using (HttpResponseMessage response = await ApiPutJson(address, target, apiToken.Secret, "{}"))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "the edit route requires the one field it accepts");

    // Put it back, so the state this leaves behind matches what the project started as.
    using (HttpResponseMessage response = await ApiPutJson(address, pageRoute, apiToken.Secret, "{\"published\":false}"))
        Check(response.IsSuccessStatusCode, "a token can withdraw a project page");
    using (HttpResponseMessage response = await ApiPutJson(address, target, apiToken.Secret, "{\"visibility\":\"private\"}"))
        Check(response.IsSuccessStatusCode, "a token can make a project private again");
}

async Task<HttpResponseMessage> ApiPutJson(string address, string path, string? token, string body)
{
    HttpClientHandler handler = new() { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseProxy = false };
    HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };
    HttpRequestMessage request = new(HttpMethod.Put, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    if (token is not null) request.Headers.Add("PRIVATE-TOKEN", token);
    HttpResponseMessage response = await client.SendAsync(request);
    request.Dispose();
    client.Dispose();
    handler.Dispose();
    return response;
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

/// <summary>
/// A plain anonymous GET, optionally pretending to arrive at another hostname. The pages origin is
/// selected by Host rather than by a second listener, so the suite needs no hosts-file entry to exercise
/// the one thing that makes a site safe: that it is a different origin.
/// </summary>
/// <summary>Claims a newly created token's secret, which the creation response deliberately withholds.</summary>
async Task<string> RevealSecret(HttpClient client, string csrf, string reveal)
{
    using HttpRequestMessage request = new(HttpMethod.Post, "/api/tokens/reveal")
    {
        Content = JsonContent.Create(new { reveal })
    };
    request.Headers.Add("Origin", client.BaseAddress!.ToString().TrimEnd('/'));
    request.Headers.Add("X-Graft-CSRF", csrf);
    using HttpResponseMessage response = await client.SendAsync(request);
    using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
    return json.RootElement.GetProperty("secret").GetString()!;
}

async Task<HttpResponseMessage> PagesGet(string address, string? host, string path)
{
    // Redirects are not followed: the pages origin answers one with a link back to the forge, and following
    // it would hide both the status and the target this is checking.
    HttpClientHandler handler = new() { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseProxy = false, AllowAutoRedirect = false };
    HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
    // An absolute URI, because a relative one plus a Host header makes HttpClient try to build the address
    // out of the header and fail. The connection still goes to the real listener; only the Host differs.
    HttpRequestMessage request = new(HttpMethod.Get, address.TrimEnd('/') + path);
    if (host is not null) request.Headers.Host = host;
    HttpResponseMessage response = await client.SendAsync(request);
    request.Dispose();
    client.Dispose();
    handler.Dispose();
    return response;
}

async Task<HttpResponseMessage> ApiPutBytes(string address, string path, string? token, byte[] content, string contentType)
{
    HttpClientHandler handler = new() { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseProxy = false };
    HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };
    HttpRequestMessage request = new(HttpMethod.Put, path) { Content = new ByteArrayContent(content) };
    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
    if (token is not null) request.Headers.Add("PRIVATE-TOKEN", token);
    HttpResponseMessage response = await client.SendAsync(request);
    request.Dispose();
    client.Dispose();
    handler.Dispose();
    return response;
}

async Task<HttpResponseMessage> ApiDelete(string address, string path, string? token)
{
    HttpClientHandler handler = new() { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseProxy = false };
    HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };
    HttpRequestMessage request = new(HttpMethod.Delete, path);
    if (token is not null) request.Headers.Add("PRIVATE-TOKEN", token);
    HttpResponseMessage response = await client.SendAsync(request);
    request.Dispose();
    client.Dispose();
    handler.Dispose();
    return response;
}

async Task<HttpResponseMessage> ApiPut(string address, string path, string? token, object body)
{
    HttpClientHandler handler = new() { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseProxy = false };
    HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };
    HttpRequestMessage request = new(HttpMethod.Put, path) { Content = JsonContent.Create(body) };
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
    // The clone menu's copy buttons are the only part of it that needs script. If this asset stopped being
    // embedded they would fail silently, so its delivery is asserted rather than assumed.
    using (HttpResponseMessage clipboard = await client.GetAsync("/graft-clipboard.js"))
    {
        string body = await clipboard.Content.ReadAsStringAsync();
        Check(clipboard.IsSuccessStatusCode &&
            clipboard.Content.Headers.ContentType?.MediaType == "text/javascript" &&
            body.Contains("graftClipboard", StringComparison.Ordinal),
            "the clipboard helper the clone menu depends on is served");
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
        // The secret does not travel with the creation response. It is claimed once, afterwards, which is
        // what keeps it out of the page that would otherwise have to render it to be copied.
        using HttpRequestMessage reveal = new(HttpMethod.Post, "/api/tokens/reveal")
        {
            Content = JsonContent.Create(new { reveal = json.RootElement.GetProperty("reveal").GetString() })
        };
        reveal.Headers.Add("Origin", address);
        reveal.Headers.Add("X-Graft-CSRF", csrf);
        using HttpResponseMessage revealed = await userClient.SendAsync(reveal);
        using JsonDocument secret = JsonDocument.Parse(await revealed.Content.ReadAsStreamAsync());
        return secret.RootElement.GetProperty("secret").GetString()!;
    }

    using (HttpResponseMessage response = await LoginUser(createdPassword)) Check(response.IsSuccessStatusCode, "browser-created user can log in");
    using (HttpResponseMessage response = await userClient.GetAsync("/api/users"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "ordinary user cannot list users");
    string userCsrf = await UserCsrf();
    using (HttpRequestMessage forbiddenEnable = new(HttpMethod.Post, $"/api/users/{userId}/enable")
    {
        Content = JsonContent.Create(new { new_password = Password() })
    })
    {
        forbiddenEnable.Headers.Add("Origin", address);
        forbiddenEnable.Headers.Add("X-Graft-CSRF", userCsrf);
        using HttpResponseMessage response = await userClient.SendAsync(forbiddenEnable);
        Check(response.StatusCode == HttpStatusCode.Forbidden, "ordinary user cannot enable accounts");
    }
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

    string enabledPassword = Password();
    using (HttpRequestMessage wrongCsrf = new(HttpMethod.Post, $"/api/users/{userId}/enable")
    {
        Content = JsonContent.Create(new { new_password = enabledPassword })
    })
    {
        wrongCsrf.Headers.Add("Origin", address);
        wrongCsrf.Headers.Add("X-Graft-CSRF", new string('0', 64));
        using HttpResponseMessage response = await adminClient.SendAsync(wrongCsrf);
        Check(response.StatusCode == HttpStatusCode.Forbidden, "account enable rejects wrong CSRF");
    }
    using (HttpRequestMessage enable = new(HttpMethod.Post, $"/api/users/{userId}/enable")
    {
        Content = JsonContent.Create(new { new_password = enabledPassword })
    })
    {
        enable.Headers.Add("Origin", address);
        enable.Headers.Add("X-Graft-CSRF", adminCsrf);
        using HttpResponseMessage response = await adminClient.SendAsync(enable);
        Check(response.StatusCode == HttpStatusCode.NoContent, "administrator enables account with fresh password");
    }
    using (HttpResponseMessage response = await LoginUser(changedPassword))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "pre-disable password stays invalid after enable");
    using (HttpResponseMessage response = await ApiGet(address, "/api/v4/user", disableRevokedToken))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "enable does not restore revoked tokens");
    using (HttpResponseMessage response = await LoginUser(enabledPassword)) Check(response.IsSuccessStatusCode, "fresh enable password authenticates");
    using (HttpResponseMessage response = await adminClient.GetAsync("/api/users"))
    {
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        JsonElement user = json.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetInt64() == userId);
        Check(response.IsSuccessStatusCode && !user.GetProperty("disabled").GetBoolean(), "administrator list exposes re-enabled state");
    }
    using (HttpRequestMessage duplicateEnable = new(HttpMethod.Post, $"/api/users/{userId}/enable")
    {
        Content = JsonContent.Create(new { new_password = Password() })
    })
    {
        duplicateEnable.Headers.Add("Origin", address);
        duplicateEnable.Headers.Add("X-Graft-CSRF", adminCsrf);
        using HttpResponseMessage response = await adminClient.SendAsync(duplicateEnable);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "active account cannot be enabled again");
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
        DateTimeOffset expiry = json.RootElement.GetProperty("expires_at_utc").GetDateTimeOffset();
        // The creation response carries a one-time claim rather than the secret itself, so a token nobody
        // collects never reaches the browser and a collected one never has to be rendered to be copied.
        Check(!json.RootElement.TryGetProperty("secret", out _), "creating a token does not return its secret");
        string reveal = json.RootElement.GetProperty("reveal").GetString()!;
        Check(response.StatusCode == HttpStatusCode.Created &&
            json.RootElement.GetProperty("scopes")[0].GetString() == "read_repository" &&
            expiry > DateTimeOffset.UtcNow.AddDays(29) && expiry <= DateTimeOffset.UtcNow.AddDays(31),
            "browser creates scoped expiring personal token");

        tokenSecret = await RevealSecret(client, csrf, reveal);
        Check(tokenSecret.StartsWith("graft_pat_", StringComparison.Ordinal), "the secret is claimed once, afterwards");

        // Single use, so a stolen or replayed handle is worth nothing after its owner has collected.
        using (HttpRequestMessage again = new(HttpMethod.Post, "/api/tokens/reveal")
        {
            Content = JsonContent.Create(new { reveal })
        })
        {
            again.Headers.Add("Origin", address);
            again.Headers.Add("X-Graft-CSRF", csrf);
            using HttpResponseMessage replay = await client.SendAsync(again);
            Check(replay.StatusCode == HttpStatusCode.NotFound, "and a second claim on the same handle finds nothing");
        }

        // Recoverable secrets are off unless an operator asked for them, and this suite's server did not.
        // So a token issued here can never be read back, by its owner or anybody else.
        Check(!json.RootElement.TryGetProperty("recoverable", out JsonElement recoverable) || !recoverable.GetBoolean(),
            "a token is not recoverable unless the operator asked for that");
        using (HttpRequestMessage secret = new(HttpMethod.Post, $"/api/tokens/{tokenId}/secret")
        {
            Content = JsonContent.Create(new { password })
        })
        {
            secret.Headers.Add("Origin", address);
            secret.Headers.Add("X-Graft-CSRF", csrf);
            using HttpResponseMessage refused = await client.SendAsync(secret);
            Check(refused.StatusCode == HttpStatusCode.NotFound,
                "and reading an existing token's secret is refused on a server that keeps none");
        }

        // The claim is a browser act like every other token operation, so it needs the CSRF header.
        using (HttpRequestMessage noCsrf = new(HttpMethod.Post, "/api/tokens/reveal")
        {
            Content = JsonContent.Create(new { reveal = "0123456789abcdef0123456789abcdef" })
        })
        {
            noCsrf.Headers.Add("Origin", address);
            using HttpResponseMessage refused = await client.SendAsync(noCsrf);
            Check(refused.StatusCode == HttpStatusCode.Forbidden, "claiming a secret without the CSRF header is refused");
        }
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

    // Rotation: the same token, a new secret. The id, name, scopes and expiry stay, the old secret stops at
    // once, and the new one is collected through the same one-time claim a new token's is.
    using (HttpRequestMessage wrongCsrf = new(HttpMethod.Post, $"/api/tokens/{tokenId}/rotate"))
    {
        wrongCsrf.Headers.Add("Origin", address);
        wrongCsrf.Headers.Add("X-Graft-CSRF", new string('0', 64));
        using HttpResponseMessage response = await client.SendAsync(wrongCsrf);
        Check(response.StatusCode == HttpStatusCode.Forbidden, "rotating a token rejects wrong CSRF");
    }
    using (HttpRequestMessage missing = new(HttpMethod.Post, "/api/tokens/999999/rotate"))
    {
        missing.Headers.Add("Origin", address);
        missing.Headers.Add("X-Graft-CSRF", csrf);
        using HttpResponseMessage response = await client.SendAsync(missing);
        Check(response.StatusCode == HttpStatusCode.NotFound, "and a token that is not the caller's cannot be rotated");
    }
    string previousSecret = tokenSecret;
    using (HttpRequestMessage rotate = new(HttpMethod.Post, $"/api/tokens/{tokenId}/rotate"))
    {
        rotate.Headers.Add("Origin", address);
        rotate.Headers.Add("X-Graft-CSRF", csrf);
        using HttpResponseMessage response = await client.SendAsync(rotate);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Check(response.IsSuccessStatusCode && json.RootElement.GetProperty("id").GetInt64() == tokenId &&
            json.RootElement.GetProperty("name").GetString() == "browser-git" &&
            json.RootElement.GetProperty("scopes")[0].GetString() == "read_repository",
            "a browser rotates a token and it keeps its id, name and scopes (got " + (int)response.StatusCode + ")");
        Check(!json.RootElement.TryGetProperty("secret", out _), "rotation does not return the secret either");
        tokenSecret = await RevealSecret(client, csrf, json.RootElement.GetProperty("reveal").GetString()!);
        Check(tokenSecret.StartsWith("graft_pat_", StringComparison.Ordinal) && tokenSecret != previousSecret, "and the claimed secret is a new one");
    }
    (int oldSecretExit, _, _) = await RunGit(root, ["ls-remote", AuthenticatedUrl(address, username, previousSecret, "alice/durable.git")]);
    Check(oldSecretExit != 0, "the previous secret stops working the moment the new one exists");
    await Git(root, ["ls-remote", AuthenticatedUrl(address, username, tokenSecret, "alice/durable.git")]);
    Check(true, "and the new secret authenticates Git HTTPS");
    using (HttpResponseMessage response = await client.GetAsync("/api/tokens"))
    {
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(json.RootElement.EnumerateArray().Count(item => item.GetProperty("id").GetInt64() == tokenId) == 1 &&
            json.RootElement.EnumerateArray().Count(item => item.GetProperty("name").GetString() == "browser-git") == 1,
            "the list still shows one token of that name, not a revoked one and a new one");
    }

    // GitLab's shape, for a script rotating the credential it holds. The new secret travels in the response
    // here, to the caller who already held the old one and has no clipboard to fetch it into.
    string apiSecret;
    long apiTokenId;
    using (HttpRequestMessage create = new(HttpMethod.Post, "/api/tokens")
    {
        Content = JsonContent.Create(new { name = "rotate-api", scopes = new[] { "api" }, expires_in_days = 30 })
    })
    {
        create.Headers.Add("Origin", address);
        create.Headers.Add("X-Graft-CSRF", csrf);
        using HttpResponseMessage response = await client.SendAsync(create);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        apiTokenId = json.RootElement.GetProperty("id").GetInt64();
        apiSecret = await RevealSecret(client, csrf, json.RootElement.GetProperty("reveal").GetString()!);
    }
    using (HttpResponseMessage refused = await ApiPostJson(address, "/api/v4/personal_access_tokens/self/rotate", tokenSecret, "{}"))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "a token without the api scope cannot rotate itself, as GitLab requires");
    using (HttpResponseMessage anonymous = await ApiPostJson(address, "/api/v4/personal_access_tokens/self/rotate", null, "{}"))
        Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "and no token means no rotation");
    string previousApiSecret = apiSecret;
    using (HttpResponseMessage rotated = await ApiPostJson(address, "/api/v4/personal_access_tokens/self/rotate", apiSecret, "{}"))
    {
        using JsonDocument json = JsonDocument.Parse(await rotated.Content.ReadAsStreamAsync());
        Check(rotated.IsSuccessStatusCode && json.RootElement.GetProperty("id").GetInt64() == apiTokenId &&
            json.RootElement.GetProperty("name").GetString() == "rotate-api" && json.RootElement.GetProperty("active").GetBoolean() &&
            !json.RootElement.GetProperty("revoked").GetBoolean() && json.RootElement.GetProperty("scopes")[0].GetString() == "api",
            "a token rotates itself over the GitLab-shaped route and keeps its identity (got " + (int)rotated.StatusCode + ")");
        apiSecret = json.RootElement.GetProperty("token").GetString()!;
        Check(apiSecret.StartsWith("graft_pat_", StringComparison.Ordinal) && apiSecret != previousApiSecret,
            "and the response carries the new secret, as GitLab's does");
        Check(DateOnly.TryParseExact(json.RootElement.GetProperty("expires_at").GetString(), "yyyy-MM-dd", out DateOnly kept) &&
            kept == DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)), "the expiry is kept rather than reset to a week");
    }
    using (HttpResponseMessage stale = await ApiGet(address, "/api/v4/projects", previousApiSecret))
        Check(stale.StatusCode == HttpStatusCode.Unauthorized, "the previous secret is refused by the API at once");
    using (HttpResponseMessage fresh = await ApiGet(address, "/api/v4/projects", apiSecret))
        Check(fresh.IsSuccessStatusCode, "and the new secret is accepted");
    using (HttpResponseMessage badDate = await ApiPostJson(address, $"/api/v4/personal_access_tokens/{apiTokenId}/rotate", apiSecret, """{"expires_at":"soon"}"""))
        Check(badDate.StatusCode == HttpStatusCode.BadRequest, "expires_at that is not a date is refused");
    using (HttpResponseMessage farDate = await ApiPostJson(address, $"/api/v4/personal_access_tokens/{apiTokenId}/rotate", apiSecret,
        $$"""{"expires_at":"{{DateTime.UtcNow.AddYears(2):yyyy-MM-dd}}"}"""))
        Check(farDate.StatusCode == HttpStatusCode.BadRequest, "and one beyond a year is refused, as a new token's would be");
    using (HttpResponseMessage check = await ApiGet(address, "/api/v4/projects", apiSecret))
        Check(check.IsSuccessStatusCode, "a refused rotation leaves the current secret working");
    string newDate = DateTime.UtcNow.AddDays(90).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    using (HttpResponseMessage byId = await ApiPostJson(address, $"/api/v4/personal_access_tokens/{tokenId}/rotate", apiSecret,
        $$"""{"expires_at":"{{newDate}}"}"""))
    {
        using JsonDocument json = JsonDocument.Parse(await byId.Content.ReadAsStreamAsync());
        Check(byId.IsSuccessStatusCode && json.RootElement.GetProperty("id").GetInt64() == tokenId &&
            json.RootElement.GetProperty("expires_at").GetString() == newDate,
            "an api token rotates another of its owner's tokens by id and may move its expiry");
        tokenSecret = json.RootElement.GetProperty("token").GetString()!;
    }
    using (HttpResponseMessage other = await ApiPostJson(address, "/api/v4/personal_access_tokens/999999/rotate", apiSecret, "{}"))
        Check(other.StatusCode == HttpStatusCode.NotFound, "and a token that is not its owner's is not found");
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

/// <summary>
/// Server-authored objects must be byte-identical to what Git itself would have written, or a repository
/// built through the browser would not round-trip through its own transport.
///
/// <para>
/// The expected ids below are not computed by this code: each was produced by running Git 2.53 over the same
/// content with the same identity, message and fixed timestamp, then recorded. Comparing object ids compares
/// every byte of every blob, tree and commit at once, including the tree ordering rule that sorts a directory
/// as though its name ended in a slash. Regenerate them with real Git if the fixture content ever changes.
/// </para>
/// </summary>
void TestServerAuthoredObjectsMatchGit()
{
    GitIdentity who = new("Graft Fixture", "fixture@example.invalid",
        DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).ToOffset(TimeSpan.Zero));

    Repository authored = new();
    AuthoredCommit first = authored.CommitFiles("main",
    [
        new("readme.txt", "hello graft\n"u8.ToArray()),
        new("a.txt", "top level a\n"u8.ToArray()),
        new("a/b.txt", "nested b\n"u8.ToArray()),
        new("a/c/d.txt", "deep d\n"u8.ToArray()),
        new("run.sh", "#!/bin/sh\necho hi\n"u8.ToArray(), Executable: true),
        new("zz.txt", "last\n"u8.ToArray())
    ], who, "Author the initial tree");
    Check(first.CommitId == "52ecd2c9fc239449212956723f3a8f3a434a31de", "authored commit matches Git");
    Check(first.TreeId == "52a54943ef50c44a3cc1ea55c32f05476d5b1983", "authored tree matches Git");
    Check(first.CreatedBranch && authored.Refs["refs/heads/main"] == first.CommitId, "authoring creates the branch");

    // Replacing one file and adding another proves untouched subtrees are reused by id rather than rebuilt,
    // and that the parent link is correct.
    AuthoredCommit second = authored.CommitFiles("main",
        [new("a/b.txt", "nested b changed\n"u8.ToArray()), new("a/c/e.txt", "new e\n"u8.ToArray())],
        who, "Change one file and add another");
    Check(second.CommitId == "1902889d625b664eb059152944127e86e08486dd", "authored follow-up commit matches Git");
    Check(second.TreeId == "3203ecd726d169b59110df7df4d4fcd3dea96356", "authored follow-up tree matches Git");
    Check(!second.CreatedBranch, "authoring onto an existing branch does not report creation");
    Check(RepositoryView.ReadCommit(authored, second.CommitId).ParentIds.Single() == first.CommitId, "authored parent link");
    Check(RepositoryView.File(authored, "main", "a/c/d.txt") is not null, "untouched file survives a later commit");

    // Names straddling the directory-sorts-as-if-it-ended-in-slash rule, plus non-ASCII, which must sort as
    // raw UTF-8 bytes. Case-only variants are excluded: the reference repository was built on a
    // case-insensitive filesystem, where they would have collided into one file.
    Repository ordering = new();
    AuthoredCommit sorted = ordering.CommitFiles("main",
    [
        new("a-", "dash\n"u8.ToArray()),
        new("ab", "bee\n"u8.ToArray()),
        new("a.txt", "dot\n"u8.ToArray()),
        new("a/x", "in a\n"u8.ToArray()),
        new("a0", "zero\n"u8.ToArray()),
        new("é.txt", "e acute\n"u8.ToArray()),
        new("é/inner", "e acute dir\n"u8.ToArray()),
        new("Z", "upper\n"u8.ToArray())
    ], who, "Sort order edge cases");
    Check(sorted.TreeId == "989afa6f4d9fbebf156ed6ea4d5215b37d5c76b3", "authored tree ordering matches Git");
    Check(sorted.CommitId == "49227f858fed623224800c000bcb5129eb0f5984", "authored ordering commit matches Git");

    // Nothing that would produce a hostile or unusable path may reach a tree.
    foreach (string rejected in new[]
    {
        "../escape.txt", "a/../../escape.txt", "./here.txt", ".git/config", "nested/.GIT/config",
        "/absolute.txt", "trailing/", "double//slash.txt", "back\\slash.txt", "control.txt", ""
    })
    {
        Repository guard = new();
        ThrowsSync<GitInputException>(() => guard.CommitFiles("main",
            [new(rejected, "x"u8.ToArray())], who, "rejected"), "rejects path " + rejected);
    }

    Repository limits = new();
    ThrowsSync<GitInputException>(() => limits.CommitFiles("main",
        [new("big.bin", new byte[Limits.ObjectBytes + 1])], who, "too large"), "rejects an oversized blob");
    ThrowsSync<GitInputException>(() => limits.CommitFiles("main",
        [new("same.txt", "a"u8.ToArray()), new("same.txt", "b"u8.ToArray())], who, "duplicate"), "rejects duplicate paths");
    ThrowsSync<GitInputException>(() => limits.CommitFiles("main", [], who, "empty"), "rejects an empty change set");
    ThrowsSync<GitInputException>(() => limits.CommitFiles("..", [new("a.txt", "a"u8.ToArray())], who, "bad branch"), "rejects an invalid branch");
    ThrowsSync<GitInputException>(() => limits.CommitFiles("main", [new("a.txt", "a"u8.ToArray())], who, "  "), "rejects an empty message");

    // A file and a directory cannot occupy the same name, in either order.
    Repository conflict = new();
    conflict.CommitFiles("main", [new("thing/inner.txt", "a"u8.ToArray())], who, "make a directory");
    ThrowsSync<GitInputException>(() => conflict.CommitFiles("main",
        [new("thing", "a"u8.ToArray())], who, "file over directory"), "rejects a file replacing a directory");
    ThrowsSync<GitInputException>(() => conflict.CommitFiles("main",
        [new("thing/inner.txt/deeper", "a"u8.ToArray())], who, "directory over file"), "rejects a directory replacing a file");

    // A rejected batch must leave nothing behind.
    string head = conflict.Refs["refs/heads/main"];
    int objects = conflict.Objects.Count;
    ThrowsSync<GitInputException>(() => conflict.CommitFiles("main",
        [new("ok.txt", "a"u8.ToArray()), new("../bad.txt", "b"u8.ToArray())], who, "partial"), "rejects a batch with one bad path");
    Check(conflict.Refs["refs/heads/main"] == head && conflict.Objects.Count == objects, "a rejected batch leaves no partial state");
}

/// <summary>
/// A repository's default branch. Graft previously assumed "main" everywhere, so a repository holding only
/// "master" browsed as empty and advertised a dangling HEAD to clones. The branch is now resolved, with a
/// stored per-project preference on top.
/// </summary>
void TestDefaultBranchResolution()
{
    GitIdentity who = new("Graft Fixture", "fixture@example.invalid",
        DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).ToOffset(TimeSpan.Zero));
    Repository Build(params string[] branches)
    {
        Repository built = new();
        foreach (string name in branches)
            built.CommitFiles(name, [new(name + ".txt", "x"u8.ToArray())], who, "on " + name);
        return built;
    }

    Check(new Repository().DefaultBranch is null, "an empty repository has no default branch");
    Check(Build("master").DefaultBranch == "master", "a master-only repository defaults to master");
    Check(Build("main").DefaultBranch == "main", "a main-only repository defaults to main");
    Check(Build("master", "main").DefaultBranch == "main", "main wins when both exist");
    Check(Build("develop").DefaultBranch == "develop", "a repository with neither uses its only branch");
    Check(Build("zeta", "alpha").DefaultBranch == "alpha", "with no convention match the first branch by name wins");

    // A project's stored choice outranks convention, but only while that branch still exists: a preference
    // is a name, and the branch behind it can be deleted.
    Repository preferred = Build("main", "release");
    preferred.PreferredDefaultBranch = "release";
    Check(preferred.DefaultBranch == "release", "a stored preference outranks convention");
    preferred.PreferredDefaultBranch = "no-such-branch";
    Check(preferred.DefaultBranch == "main", "a preference naming a missing branch falls back to convention");
    preferred.PreferredDefaultBranch = null;
    Check(preferred.DefaultBranch == "main", "clearing the preference returns to convention");

    // HEAD and the clone advertisement must agree with that resolution, which is what a real client follows.
    Repository masterOnly = Build("master");
    Check(RepositoryView.ResolveCommit(masterOnly, "HEAD") == masterOnly.Refs["refs/heads/master"],
        "HEAD resolves through the default branch");
    string advertised = Encoding.UTF8.GetString(masterOnly.Advertise("git-upload-pack"));
    Check(advertised.Contains("symref=HEAD:refs/heads/master", StringComparison.Ordinal),
        "the clone advertisement names the real default branch");
    Check(!advertised.Contains("refs/heads/main", StringComparison.Ordinal), "no phantom main branch is advertised");

    // An empty repository must still advertise something a client can parse rather than a broken symref.
    string empty = Encoding.UTF8.GetString(new Repository().Advertise("git-upload-pack"));
    Check(!empty.Contains("symref=", StringComparison.Ordinal), "an empty repository advertises no symref");

    // Browsing a branch that is not the default is the other half of multi-branch support.
    Repository several = Build("main", "feature");
    Check(RepositoryView.File(several, "feature", "feature.txt") is not null, "a non-default branch can be browsed");
    Check(RepositoryView.File(several, "main", "feature.txt") is null, "branches stay isolated from each other");
    Check(RepositoryView.Branches(several).Count == 2, "every branch is listed");
}

void ThrowsSync<TException>(Action action, string name) where TException : Exception
{
    try { action(); }
    catch (TException) { assertions++; return; }
    throw new InvalidOperationException("Expected " + typeof(TException).Name + ": " + name);
}

// A 1x1 PNG, the smallest thing the image validator will accept as genuine. Used to prove a relative image
// in a README resolves to repository content, and to contrast with a file that only claims to be one.
static byte[] OnePixelPng() => Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

/// <summary>
/// The README pushed into the repository for the rendering checks. The first half is hostile: every entry is
/// a construct that must not reach the browser as markup. The second half is ordinary markdown, present
/// because a renderer that escaped everything would pass every attack case and still be useless -- the
/// checks assert both that nothing dangerous survives and that nothing legitimate is flattened.
/// </summary>
static string ReadmeCorpus() => """
    # Rendering corpus

    ## Constructs that must not survive

    <script>alert('raw script block')</script>
    <img src=x onerror=alert('event handler')>
    <svg onload=alert('svg handler')></svg>
    <iframe src="https://evil.invalid/frame"></iframe>
    <object data="https://evil.invalid/x.swf"></object>
    <embed src="https://evil.invalid/x.swf">
    <form action="https://evil.invalid/steal"><input name="password"></form>
    <base href="https://evil.invalid/">
    <link rel="stylesheet" href="https://evil.invalid/x.css">
    <style>body { display: none }</style>
    <a href="#" onclick="alert('inline handler')">handler on an anchor</a>
    <!-- --><script>alert('comment break out')</script><!-- -->
    text</a><script>alert('tag break out')</script>

    [plain scheme](javascript:alert('plain'))
    [mixed case](JaVaScRiPt:alert('mixed'))
    [entity encoded](&#106;avascript:alert&#40;'entity'&#41;)
    [named entity](javascript&colon;alert('named'))
    [tab inside](jav&#x09;ascript:alert('tab'))
    [null inside](jav&#0;ascript:alert('null'))
    [percent encoded](%6aavascript:alert('percent'))
    [leading space]( javascript:alert('space'))
    [vbscript](vbscript:msgbox('vb'))
    [data document](data:text/html,<script>alert('data')</script>)
    [local file](file:///etc/passwd)
    [protocol relative](//evil.invalid/page)
    [server absolute](/etc/passwd)
    [reference style][hostile]
    ![image scheme](javascript:alert('image'))
    ![reference image][hostile]
    ![blob image](blob:https://evil.invalid/x)
    ![off site image](https://evil.invalid/tracking-pixel.png)
    [generic attributes](docs/guide.md){onclick="alert('attributes')"}
    [traversal](../../../etc/passwd)
    ![traversal image](../../../etc/passwd)
    <javascript:alert('autolink')>
    <vbscript:msgbox('autolink')>

    [hostile]: javascript:alert('definition')

    ## Ordinary markdown that must still render

    ### Emphasis

    *emphasis*, **strong**, ~~struck through~~, `inline code`, ++inserted++, ==marked==, ^super^ and ~sub~.

    ### Lists

    - first
    - second
      - nested

    1. one
    2. two

    - [x] a completed task
    - [ ] an outstanding task

    ### Quoting and rules

    > a block quote
    >
    > with two paragraphs

    ---

    ### Code

    ```
    a fence with no language
    ```

    ```csharp
    var rendered = MarkdownContent.Render(source, resolve);
    ```

    ```html
    <script>this is a sample, not an attack</script>
    ```

    ```mermaid
    graph TD;
      Push[git push] --> Render[render README];
      Render --> Sandbox[diagram sandbox];
    ```

    ### Tables

    | left | centre | right |
    | :--- | :----: | ----: |
    | one  |  two   | three |

    ### Definitions

    Graft
    :   a locally hosted forge
    :   a graft, in horticulture

    ### Links and images

    [a relative document](docs/guide.md)
    [an external page](https://example.com/page)
    [an electronic mail address](mailto:someone@example.invalid)
    [an in-document anchor](#ordinary-markdown-that-must-still-render)
    <https://example.com/autolinked>
    ![a committed image](docs/pixel.png)
    ![a committed image with a caption](docs/pixel.png "the caption")

    ### Prose that resembles an attack but is not

    The `onerror` handler, the javascript: scheme and an onerror= mention all appear here as text.

    ![an alt text containing onerror=alert(1)](docs/pixel.png)

    [a title containing onerror](docs/guide.md "onerror=alert(1)")

    ### Footnotes

    A claim needing a source[^source].

    [^source]: the footnote text
    """;

/// <summary>
/// Reads the rendered README back through the endpoint the browser uses, and exercises the image route it
/// points at. The assertions come in two halves: nothing dangerous survives, and nothing ordinary is lost.
/// </summary>
async Task CheckReadmeRendering(string address, string username, string password, long projectId)
{
    CookieContainer cookies = new();
    using HttpClientHandler handler = new()
    {
        CookieContainer = cookies,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        UseProxy = false
    };
    using HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };
    using (HttpRequestMessage login = new(HttpMethod.Post, "/api/login"))
    {
        login.Headers.Add("Origin", address);
        login.Content = JsonContent.Create(new { username, password });
        using HttpResponseMessage response = await client.SendAsync(login);
        Check(response.IsSuccessStatusCode, "README corpus login");
    }

    string html;
    bool hasDiagrams;
    using (HttpResponseMessage page = await client.GetAsync($"/api/browser/projects/{projectId}?ref=readme-corpus"))
    {
        using JsonDocument json = JsonDocument.Parse(await page.Content.ReadAsStreamAsync());
        Check(page.IsSuccessStatusCode, "README corpus page loads");
        JsonElement readme = json.RootElement.GetProperty("readme");
        Check(readme.ValueKind == JsonValueKind.Object, "the branch README is found and rendered");
        Check(readme.GetProperty("path").GetString() == "README.md", "the rendered README names its own path");
        html = readme.GetProperty("html").GetString()!;
        hasDiagrams = readme.GetProperty("has_diagrams").GetBoolean();
    }

    // Everything the renderer produces is markup and everything it copies from the document is escaped, so
    // the dangerous-construct assertions run against the tags alone: "javascript:" appearing in prose is
    // text, and searching the whole document for it would prove nothing either way.
    string markup = string.Concat(html.Split('<').Skip(1).Select(part => "<" + part.Split('>')[0] + ">"));
    Check(!markup.Contains("<script", StringComparison.OrdinalIgnoreCase), "no script element survives");
    Check(!markup.Contains("<iframe", StringComparison.OrdinalIgnoreCase), "no frame element survives");
    Check(!markup.Contains("<object", StringComparison.OrdinalIgnoreCase) &&
        !markup.Contains("<embed", StringComparison.OrdinalIgnoreCase), "no plugin element survives");
    Check(!markup.Contains("<form", StringComparison.OrdinalIgnoreCase) &&
        !markup.Contains("<input name", StringComparison.OrdinalIgnoreCase), "no form element survives");
    Check(!markup.Contains("<svg", StringComparison.OrdinalIgnoreCase) &&
        !markup.Contains("<base", StringComparison.OrdinalIgnoreCase) &&
        !markup.Contains("<style", StringComparison.OrdinalIgnoreCase) &&
        !markup.Contains("<link", StringComparison.OrdinalIgnoreCase), "no element outside the allowed set survives");
    // An attribute's name and its value have to be judged separately. A caption reading "onerror=alert(1)"
    // is text that happens to sit inside an alt attribute and is perfectly safe; what would not be safe is an
    // attribute actually named onerror. So the handler check runs with every value blanked out.
    Check(!Regex.IsMatch(Regex.Replace(markup, "\"[^\"]*\"", "\"\""), @"\son[a-zA-Z]+\s*="),
        "no event handler attribute survives");

    // Conversely a scheme only matters in an attribute that is a URL, so those values are read out and
    // judged on their own rather than by searching the document for the word.
    string[] destinations = Regex.Matches(markup, "(?:href|src)=\"([^\"]*)\"")
        .Select(match => match.Groups[1].Value).ToArray();
    Check(destinations.Length > 0, "the rendered README contains destinations to judge");
    Check(!destinations.Any(destination => Regex.IsMatch(destination,
        @"^\s*(?:javascript|vbscript|data|file|blob)\s*:", RegexOptions.IgnoreCase)),
        "no executable or inline scheme survives in a destination");
    Check(!destinations.Any(destination => destination.Contains("evil.invalid", StringComparison.OrdinalIgnoreCase)
        && markup.Contains("<img src=\"" + destination, StringComparison.Ordinal)),
        "no off-site image is requested");
    Check(markup.Contains("class=\"external-image\"", StringComparison.Ordinal),
        "an off-site image is downgraded to a link rather than fetched");
    Check(!destinations.Any(destination => destination.StartsWith("//", StringComparison.Ordinal)
        || destination.StartsWith("/etc/", StringComparison.Ordinal)),
        "protocol-relative and server-absolute destinations are dropped");

    // A renderer that escaped everything would satisfy every assertion above and be worthless, so the
    // ordinary syntax has to be proved present as well.
    Check(!html.StartsWith("<pre>", StringComparison.Ordinal), "the document is not degraded to plain text");
    Check(html.Contains("<h1 id=\"rendering-corpus\">", StringComparison.Ordinal), "headings render with anchors");
    Check(html.Contains("<em>emphasis</em>", StringComparison.Ordinal) &&
        html.Contains("<strong>strong</strong>", StringComparison.Ordinal) &&
        html.Contains("<del>struck through</del>", StringComparison.Ordinal), "emphasis renders");
    Check(html.Contains("<ins>inserted</ins>", StringComparison.Ordinal) &&
        html.Contains("<mark>marked</mark>", StringComparison.Ordinal) &&
        html.Contains("<sup>super</sup>", StringComparison.Ordinal) &&
        html.Contains("<sub>sub</sub>", StringComparison.Ordinal), "emphasis extras render");
    Check(html.Contains("<ul>", StringComparison.Ordinal) && html.Contains("<ol>", StringComparison.Ordinal),
        "bulleted and numbered lists render");
    Check(html.Contains("type=\"checkbox\"", StringComparison.Ordinal), "task lists render as checkboxes");
    Check(html.Contains("<blockquote>", StringComparison.Ordinal) && html.Contains("<hr", StringComparison.Ordinal),
        "block quotes and rules render");
    Check(html.Contains("<code class=\"language-csharp\">", StringComparison.Ordinal),
        "fenced code keeps its language");
    Check(html.Contains("&lt;script&gt;this is a sample", StringComparison.Ordinal),
        "a code sample containing a script tag is shown as text rather than rejecting the document");
    Check(html.Contains("<table>", StringComparison.Ordinal) && html.Contains("<th", StringComparison.Ordinal),
        "tables render");
    // Column alignment has to arrive as a class. Expressed as an inline style, which is what the parser does
    // by default, the browser refuses it under style-src and the alignment is lost with no visible cause.
    Check(!markup.Contains("style=", StringComparison.OrdinalIgnoreCase),
        "nothing is rendered as an inline style, which the page policy would refuse");
    Check(html.Contains("class=\"align-centre\"", StringComparison.Ordinal) &&
        html.Contains("class=\"align-right\"", StringComparison.Ordinal),
        "table column alignment survives as a class the stylesheet can apply");
    Check(html.Contains("<dl>", StringComparison.Ordinal) && html.Contains("<dd", StringComparison.Ordinal),
        "definition lists render");
    Check(html.Contains("class=\"footnotes\"", StringComparison.Ordinal), "footnotes render");
    Check(html.Contains("<code class=\"language-mermaid\">", StringComparison.Ordinal) && hasDiagrams,
        "a diagram is reported and left for the sandbox to draw");
    Check(html.Contains("<a href=\"https://example.com/page\" rel=\"noopener noreferrer nofollow\" target=\"_blank\">", StringComparison.Ordinal),
        "an external link cannot leak a referrer or a window handle");
    Check(html.Contains("<a href=\"https://example.com/autolinked\" rel=\"noopener noreferrer nofollow\" target=\"_blank\">", StringComparison.Ordinal),
        "an autolink is treated as strictly as a written link");
    Check(html.Contains("alt=\"an alt text containing onerror=alert(1)\"", StringComparison.Ordinal),
        "text that resembles an attack is kept as text instead of degrading the document");

    // The images a README points at come back through a route of its own, which identifies them by structure.
    string imageRoute = $"/api/graft/projects/{projectId}/image?ref=readme-corpus&path=";
    Check(html.Contains("/image?ref=readme-corpus&amp;path=docs%2Fpixel.png", StringComparison.Ordinal),
        "a committed image resolves to the repository image route");
    using (HttpResponseMessage image = await client.GetAsync(imageRoute + "docs%2Fpixel.png"))
        Check(image.IsSuccessStatusCode && image.Content.Headers.ContentType?.MediaType == "image/png",
            "a committed image is served as the type its bytes prove it to be");
    using (HttpResponseMessage fake = await client.GetAsync(imageRoute + "docs%2Ffake.png"))
        Check(fake.StatusCode == HttpStatusCode.UnsupportedMediaType,
            "a file that only claims to be an image is refused");
    using (HttpResponseMessage markdown = await client.GetAsync(imageRoute + "README.md"))
        Check(markdown.StatusCode == HttpStatusCode.UnsupportedMediaType,
            "the image route cannot be used to serve a repository's text");
    using (HttpResponseMessage traversal = await client.GetAsync(imageRoute + "..%2F..%2F..%2Fetc%2Fpasswd"))
        Check(traversal.StatusCode == HttpStatusCode.NotFound,
            "the image route resolves through the object store, so traversal reaches nothing");

    // A README belongs to the root of a branch. A subdirectory is a different view, and a README found
    // deeper in the tree is an ordinary file.
    using (HttpResponseMessage subdirectory = await client.GetAsync($"/api/browser/projects/{projectId}?ref=readme-corpus&path=docs"))
    {
        using JsonDocument json = JsonDocument.Parse(await subdirectory.Content.ReadAsStreamAsync());
        Check(subdirectory.IsSuccessStatusCode &&
            json.RootElement.GetProperty("readme").ValueKind == JsonValueKind.Null,
            "a subdirectory listing carries no README");
    }

    // The default branch has no README at all, which must read as absence rather than as an error.
    using (HttpResponseMessage main = await client.GetAsync($"/api/browser/projects/{projectId}?ref=main"))
    {
        using JsonDocument json = JsonDocument.Parse(await main.Content.ReadAsStreamAsync());
        Check(main.IsSuccessStatusCode && json.RootElement.GetProperty("readme").ValueKind == JsonValueKind.Null,
            "a branch without a README reports none");
    }
}

/// <summary>
/// Proves Graft's diff against the one real Git produces for the same commits.
///
/// <para>
/// A diff that is merely valid is not good enough: for any pair of files many correct edit scripts exist,
/// and a reader comparing Graft to Git would see them disagree. Git uses Myers by default and so does this
/// engine, so the hunks should match exactly, and this asserts that rather than assuming it. Rename
/// detection is off on both sides because this engine does not implement it.
/// </para>
/// </summary>
async Task CheckDiffMatchesGit(string worktree, string repositoryPath, string pushUrl)
{
    const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    await Git(worktree, ["checkout", "-b", "diff-corpus"]);

    // Each step exercises a different shape the renderer and the engine have to get right.
    string alpha = string.Join("\n", Enumerable.Range(1, 20).Select(line => "alpha line " + line)) + "\n";
    Directory.CreateDirectory(Path.Combine(worktree, "nested"));
    File.WriteAllText(Path.Combine(worktree, "alpha.txt"), alpha);
    File.WriteAllText(Path.Combine(worktree, "nested", "beta.txt"), "one\ntwo\nthree\n");
    File.WriteAllBytes(Path.Combine(worktree, "image.bin"), [0x89, 0x50, 0x00, 0x01, 0x02, 0x00, 0xFF]);
    await Commit("diff: add files");

    // Two separate edits far enough apart to produce two hunks rather than one.
    string[] lines = alpha.Split('\n');
    lines[1] = "alpha line 2 changed";
    lines[17] = "alpha line 18 changed";
    File.WriteAllText(Path.Combine(worktree, "alpha.txt"), string.Join("\n", lines));
    File.WriteAllText(Path.Combine(worktree, "nested", "beta.txt"), "completely\ndifferent\ncontent\nentirely\n");
    await Commit("diff: two hunks and a rewrite");

    // A file with no newline at its end, which Git marks explicitly and a naive line split loses.
    File.Delete(Path.Combine(worktree, "nested", "beta.txt"));
    File.WriteAllText(Path.Combine(worktree, "gamma.txt"), "no trailing newline here");
    await Commit("diff: delete one file and add one without a trailing newline");

    // Giving that file a trailing newline changes the last line even though its text is unchanged.
    File.WriteAllText(Path.Combine(worktree, "gamma.txt"), "no trailing newline here\n");
    await Commit("diff: add the trailing newline");

    // A mode-only change carries no text at all.
    await Git(worktree, ["update-index", "--chmod=+x", "alpha.txt"]);
    await Commit("diff: make a file executable");

    File.WriteAllBytes(Path.Combine(worktree, "image.bin"), [0x89, 0x50, 0x00, 0x09, 0x08, 0x00, 0x01]);
    await Commit("diff: change a binary file");

    await Git(worktree, ["remote", "set-url", "origin", pushUrl]);
    await Git(worktree, ["push", "origin", "diff-corpus"]);

    string[] shas = (await Git(worktree, ["rev-list", "--reverse", "diff-corpus"])).Trim()
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).ToArray();
    Check(shas.Length >= 7, "the diff corpus produced a history to compare");

    Repository graft = new(new DurableRepositoryStorage(repositoryPath));
    int compared = await CompareWithGit(graft, worktree, shas);
    Check(compared >= 8, "every changed file in the corpus was compared against Git");
    CheckDiffReconstructsContent(graft, shas, "diff corpus");

    // A change sitting beside identical lines can be placed several equally minimal ways. A real repository
    // produced exactly this and Graft disagreed with Git about it, so the shape is pinned here and now held
    // to the same standard as the rest: the same hunks Git produces, and content that rebuilds exactly.
    await Git(worktree, ["checkout", "-b", "diff-ambiguous"]);
    File.WriteAllText(Path.Combine(worktree, "fences.md"), """
        opening

        ```yaml
        key: value


        ## heading

        ```yaml
        other: value
        """);
    await Commit("diff: a file with ambiguous blank runs");
    File.WriteAllText(Path.Combine(worktree, "fences.md"), """
        opening

        ```yaml
        key: value
        ```

        ## heading

        ```yaml
        other: value
        ```
        """);
    await Commit("diff: close both fences, changing two separated places");
    await Git(worktree, ["push", "origin", "diff-ambiguous"]);

    Repository withAmbiguity = new(new DurableRepositoryStorage(repositoryPath));
    string[] ambiguous = (await Git(worktree, ["rev-list", "--reverse", "main..diff-ambiguous"])).Trim()
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).ToArray();
    Check(await CompareWithGit(withAmbiguity, worktree, ambiguous) > 0,
        "the ambiguous corpus was compared against Git");
    CheckDiffReconstructsContent(withAmbiguity, ambiguous, "ambiguous placement");

    await Git(worktree, ["checkout", "main"]);

    async Task<int> CompareWithGit(Repository graft, string tree, string[] commits)
    {
        int compared = 0;
        foreach (string sha in commits)
        {
            GitCommit commit = RepositoryView.ReadCommit(graft, sha);
            string baseline = commit.ParentIds.Count > 0 ? commit.ParentIds[0] : EmptyTree;
            string expected = await Git(tree,
                ["diff", "--no-color", "--no-renames", "--unified=3", baseline, sha]);

            Dictionary<string, List<string>> fromGit = ParseGitDiff(expected);
            CommitDiff actual = RepositoryDiff.ForCommit(graft, sha);
            Dictionary<string, List<string>> fromGraft = actual.Files.ToDictionary(
                file => file.Path,
                file => file.IsBinary
                    ? ["BINARY"]
                    : file.Hunks.SelectMany(hunk => new[] { HunkHeader(hunk) }.Concat(
                        hunk.Lines.SelectMany(line => line.NoNewline
                            ? new[] { line.Kind + line.Text, @"\ No newline at end of file" }
                            : [line.Kind + line.Text]))).ToList(),
                StringComparer.Ordinal);

            Check(fromGit.Keys.OrderBy(path => path, StringComparer.Ordinal)
                .SequenceEqual(fromGraft.Keys.OrderBy(path => path, StringComparer.Ordinal), StringComparer.Ordinal),
                "the same paths change as Git reports for " + commit.Title);

            foreach (string path in fromGit.Keys)
            {
                Check(fromGit[path].SequenceEqual(fromGraft[path], StringComparer.Ordinal),
                    "the diff of " + path + " matches Git for " + commit.Title);
                compared++;
            }
        }
        return compared;
    }

    async Task Commit(string message)
    {
        await Git(worktree, ["add", "--all"]);
        await Git(worktree, ["commit", "-m", message]);
    }
}

/// <summary>Formats a hunk header the way a unified diff writes one, so the two sides compare as text.</summary>
static string HunkHeader(DiffHunk hunk) =>
    "@@ -" + Range(hunk.OldStart, hunk.OldLines) + " +" + Range(hunk.NewStart, hunk.NewLines) + " @@";

static string Range(int start, int count) => count == 1
    ? start.ToString(System.Globalization.CultureInfo.InvariantCulture)
    : start.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
        count.ToString(System.Globalization.CultureInfo.InvariantCulture);

/// <summary>
/// Reduces Git's own diff output to the same shape Graft produces: a path, then its hunk headers and the
/// lines under them. Everything else Git prints is formatting this engine does not claim to reproduce.
/// </summary>
static Dictionary<string, List<string>> ParseGitDiff(string output)
{
    Dictionary<string, List<string>> files = new(StringComparer.Ordinal);
    List<string>? current = null;
    bool binary = false;
    string path = "";
    foreach (string raw in output.Split('\n'))
    {
        string line = raw.TrimEnd('\r');
        if (line.StartsWith("diff --git ", StringComparison.Ordinal))
        {
            if (current is not null) files[path] = binary ? ["BINARY"] : current;
            // "diff --git a/<path> b/<path>"; the b-side name is the one Graft reports.
            int marker = line.IndexOf(" b/", StringComparison.Ordinal);
            path = line[(marker + 3)..];
            current = [];
            binary = false;
            continue;
        }
        if (current is null) continue;
        if (line.StartsWith("Binary files ", StringComparison.Ordinal) ||
            line.StartsWith("GIT binary patch", StringComparison.Ordinal)) { binary = true; continue; }
        if (line.StartsWith("@@ ", StringComparison.Ordinal))
        {
            // Git may append the enclosing function after the second "@@"; only the ranges are compared.
            int end = line.IndexOf("@@", 3, StringComparison.Ordinal);
            current.Add(line[..(end + 2)]);
            continue;
        }
        if (line.StartsWith("index ", StringComparison.Ordinal) ||
            line.StartsWith("--- ", StringComparison.Ordinal) ||
            line.StartsWith("+++ ", StringComparison.Ordinal) ||
            line.StartsWith("new file mode", StringComparison.Ordinal) ||
            line.StartsWith("deleted file mode", StringComparison.Ordinal) ||
            line.StartsWith("old mode", StringComparison.Ordinal) ||
            line.StartsWith("new mode", StringComparison.Ordinal) ||
            line.Length == 0) continue;
        current.Add(line);
    }
    if (current is not null) files[path] = binary ? ["BINARY"] : current;
    return files;
}

/// <summary>
/// Reads a commit back through the endpoint the browser uses, and compares it to what Git reports for the
/// same commit. The engine is already proved against Git in process; this proves the route, the contract and
/// the serialisation carry that result out intact rather than losing it on the way.
/// </summary>
async Task CheckCommitDiffApi(string address, string username, string password, long projectId, string worktree)
{
    CookieContainer cookies = new();
    using HttpClientHandler handler = new()
    {
        CookieContainer = cookies,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        UseProxy = false
    };
    using HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };
    using (HttpRequestMessage login = new(HttpMethod.Post, "/api/login"))
    {
        login.Headers.Add("Origin", address);
        login.Content = JsonContent.Create(new { username, password });
        using HttpResponseMessage response = await client.SendAsync(login);
        Check(response.IsSuccessStatusCode, "commit diff login");
    }

    string head = (await Git(worktree, ["rev-parse", "diff-corpus"])).Trim();
    string root = (await Git(worktree, ["rev-list", "--max-parents=0", "diff-corpus"])).Trim()
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

    using (HttpResponseMessage response = await client.GetAsync($"/api/browser/projects/{projectId}/commit/{head}"))
    {
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Check(response.IsSuccessStatusCode, "a commit diff is served");
        JsonElement body = json.RootElement;
        Check(body.GetProperty("commit").GetProperty("id").GetString() == head, "the diff names the commit asked for");

        string expected = await Git(worktree, ["diff", "--no-color", "--no-renames", "--unified=3", head + "^", head]);
        Dictionary<string, List<string>> fromGit = ParseGitDiff(expected);
        string[] paths = body.GetProperty("files").EnumerateArray()
            .Select(file => file.GetProperty("path").GetString()!).ToArray();
        Check(paths.OrderBy(path => path, StringComparer.Ordinal)
            .SequenceEqual(fromGit.Keys.OrderBy(path => path, StringComparer.Ordinal), StringComparer.Ordinal),
            "the served diff names the same files as Git");

        // The binary change in the corpus must arrive as a flag rather than as bytes nobody can read.
        JsonElement binary = body.GetProperty("files").EnumerateArray()
            .Single(file => file.GetProperty("path").GetString() == "image.bin");
        Check(binary.GetProperty("is_binary").GetBoolean() && binary.GetProperty("hunks").GetArrayLength() == 0,
            "a binary file is reported as binary and carries no hunks");
    }

    // A root commit has no parent, which has to read as a whole tree added rather than as an error.
    using (HttpResponseMessage response = await client.GetAsync($"/api/browser/projects/{projectId}/commit/{root}"))
    {
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Check(response.IsSuccessStatusCode, "the first commit has a diff");
        Check(json.RootElement.GetProperty("parent_id").ValueKind == JsonValueKind.Null, "the first commit reports no parent");
        Check(json.RootElement.GetProperty("files").EnumerateArray()
            .All(file => file.GetProperty("status").GetString() == "added"),
            "every file in the first commit is an addition");
        Check(json.RootElement.GetProperty("added").GetInt32() > 0 && json.RootElement.GetProperty("removed").GetInt32() == 0,
            "the first commit adds lines and removes none");
    }

    using (HttpResponseMessage missing = await client.GetAsync(
        $"/api/browser/projects/{projectId}/commit/0123456789012345678901234567890123456789"))
        Check(missing.StatusCode == HttpStatusCode.NotFound, "an unknown commit is not found");
    using (HttpResponseMessage malformed = await client.GetAsync($"/api/browser/projects/{projectId}/commit/not-a-sha"))
        Check(malformed.StatusCode == HttpStatusCode.NotFound, "a malformed commit id is refused");
    using (HttpResponseMessage blob = await client.GetAsync(
        $"/api/browser/projects/{projectId}/commit/{(await Git(worktree, ["rev-parse", "diff-corpus:alpha.txt"])).Trim()}"))
        Check(blob.StatusCode == HttpStatusCode.NotFound, "an object that is not a commit is refused");
    using (HttpResponseMessage parent = await client.GetAsync($"/api/browser/projects/{projectId}/commit/{head}?parent=99"))
        Check(parent.StatusCode == HttpStatusCode.BadRequest, "an out-of-range parent is refused");
}

/// <summary>
/// Applies a commit's diff to the parent's content and checks the result is the commit's own content, for
/// every text file it touches.
///
/// <para>
/// This is the property that actually has to hold. Matching Git hunk for hunk is a stronger statement but a
/// narrower one: where a change sits beside identical lines there are several equally minimal scripts, Git
/// slides its groups to a preferred one and this engine does not, so the two can legitimately disagree about
/// placement. They cannot disagree about the result, and that is what this asserts.
/// </para>
/// </summary>
void CheckDiffReconstructsContent(Repository graft, IEnumerable<string> commitIds, string label)
{
    int checkedFiles = 0;
    int ambiguous = 0;
    foreach (string sha in commitIds)
    {
        CommitDiff diff = RepositoryDiff.ForCommit(graft, sha);
        foreach (DiffFile file in diff.Files)
        {
            if (file.IsBinary || file.TooLarge) continue;

            string? oldText = file.OldBlobId is null ? null : BlobText(graft, file.OldBlobId);
            string expected = file.NewBlobId is null ? "" : BlobText(graft, file.NewBlobId);
            string rebuilt = ApplyHunks(oldText ?? "", file.Hunks);
            Check(string.Equals(rebuilt, expected, StringComparison.Ordinal),
                label + ": applying the diff of " + file.Path + " reproduces the file");
            checkedFiles++;
            if (file.Hunks.Count > 1) ambiguous++;
        }
    }
    Check(checkedFiles > 0, label + ": there were files to reconstruct");
    Check(ambiguous > 0, label + ": at least one file needed more than a single hunk");
}

static string BlobText(Repository repository, string blobId) =>
    System.Text.Encoding.UTF8.GetString(repository.Objects[blobId].Data);

/// <summary>
/// Rebuilds a file from its previous content and the hunks describing the change. The hunks carry absolute
/// line numbers, so each one replaces a known range and everything between hunks is carried across
/// untouched, which is exactly how a patch tool applies one.
/// </summary>
static string ApplyHunks(string oldText, IReadOnlyList<DiffHunk> hunks)
{
    bool oldEndsWithNewline = oldText.Length == 0 || oldText.EndsWith('\n');
    string[] oldLines = oldText.Length == 0
        ? []
        : (oldEndsWithNewline ? oldText[..^1] : oldText).Split('\n');

    List<string> result = [];
    bool endsWithNewline = true;
    int consumed = 0;
    foreach (DiffHunk hunk in hunks)
    {
        int from = hunk.OldLines == 0 ? hunk.OldStart : hunk.OldStart - 1;
        for (int index = consumed; index < from && index < oldLines.Length; index++) result.Add(oldLines[index]);
        consumed = Math.Max(consumed, from);

        foreach (DiffLine line in hunk.Lines)
        {
            if (line.Kind is ' ' or '+')
            {
                result.Add(line.Text);
                // Only a line that survives into the new file decides whether that file ends with a newline.
                endsWithNewline = !line.NoNewline || line.Kind == '-';
            }
            if (line.Kind is ' ' or '-') consumed++;
        }
    }
    for (int index = consumed; index < oldLines.Length; index++)
    {
        result.Add(oldLines[index]);
        endsWithNewline = oldEndsWithNewline;
    }

    if (result.Count == 0) return "";
    return string.Join("\n", result) + (endsWithNewline ? "\n" : "");
}


/// <summary>
/// Issues, merge proposals and comments, over the GitLab-compatible routes.
///
/// <para>
/// The project is its own, with branches of its own, because a merge moves a branch and several checks
/// elsewhere in this suite count the branches of the shared project.
/// </para>
/// </summary>
async Task CheckCollaboration(string address, AuthenticatedIdentity alice, string bobUsername, string strangerUsername)
{
    // Fresh credentials, like every other check this late in the suite: account administration above
    // revokes tokens, so nothing threaded in from the fixture can be assumed to still work.
    long projectId;
    string aliceApi;
    string aliceReadApi;
    string repositoryOnly;
    string bobApi;
    string strangerApi;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo created = await store.CreateProjectWithRepositoryAsync(alice, "talk", "Collaboration",
            (project, cancellation) => { cancellation.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; });
        projectId = created.Id;
        UserAccount bob = Required(await store.FindActiveUserAsync(bobUsername), "bob account");
        UserAccount stranger = Required(await store.FindActiveUserAsync(strangerUsername), "stranger account");
        await store.SetMembershipAsync(alice, projectId, bob.Id, ProjectRole.Developer);
        AuthenticatedIdentity bobIdentity = new(bob.Id, bob.Username, false, IdentityKind.BrowserSession);
        AuthenticatedIdentity strangerIdentity = new(stranger.Id, stranger.Username, false, IdentityKind.BrowserSession);
        aliceApi = (await store.CreateTokenAsync(alice, "talk-api", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        aliceReadApi = (await store.CreateTokenAsync(alice, "talk-read-api", TokenScope.ReadApi, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        repositoryOnly = (await store.CreateTokenAsync(alice, "talk-repository", TokenScope.ReadRepository | TokenScope.WriteRepository, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        bobApi = (await store.CreateTokenAsync(bobIdentity, "talk-member-api", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        strangerApi = (await store.CreateTokenAsync(strangerIdentity, "talk-stranger-api", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
    }
    string prefix = "/api/v4/projects/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Branches to propose between, pushed as real Git content so the merge runs against a repository a
    // client built rather than one this suite assembled.
    string clone = Path.Combine(root, "talk-clone");
    string writeUrl = AuthenticatedUrl(address, "alice", aliceApi, "alice/talk.git");
    Directory.CreateDirectory(clone);
    await Git(clone, ["init", "--initial-branch=main"]);
    File.WriteAllText(Path.Combine(clone, "shared.txt"), "one\n");
    File.WriteAllText(Path.Combine(clone, "base.txt"), "base\n");
    await Git(clone, ["add", "."]);
    await Git(clone, ["commit", "-m", "Base"]);
    await Git(clone, ["remote", "add", "origin", writeUrl]);
    await Git(clone, ["push", "origin", "main"]);

    // A branch that only adds a file, so it merges three ways against a main that has also moved.
    await Git(clone, ["checkout", "-b", "feature"]);
    File.WriteAllText(Path.Combine(clone, "feature.txt"), "feature work\n");
    await Git(clone, ["add", "."]);
    await Git(clone, ["commit", "-m", "Add feature"]);
    await Git(clone, ["push", "origin", "feature"]);
    // A target that never moves, so a proposal into it is a fast-forward rather than a merge: the whole
    // distinction is whether the target gained anything the source does not already have.
    await Git(clone, ["branch", "trunk", "main"]);
    await Git(clone, ["push", "origin", "trunk"]);
    await Git(clone, ["checkout", "-b", "forward", "main"]);
    File.WriteAllText(Path.Combine(clone, "forward.txt"), "ahead\n");
    await Git(clone, ["add", "."]);
    await Git(clone, ["commit", "-m", "Move ahead"]);
    await Git(clone, ["push", "origin", "forward"]);
    // Two branches that change the same file differently, which is the case Graft refuses.
    await Git(clone, ["checkout", "-b", "mine", "main"]);
    File.WriteAllText(Path.Combine(clone, "shared.txt"), "mine\n");
    await Git(clone, ["commit", "-am", "Change shared from one side"]);
    await Git(clone, ["push", "origin", "mine"]);
    await Git(clone, ["checkout", "-b", "theirs", "main"]);
    File.WriteAllText(Path.Combine(clone, "shared.txt"), "theirs\n");
    await Git(clone, ["commit", "-am", "Change shared from the other side"]);
    await Git(clone, ["push", "origin", "theirs"]);
    // main moves too, so the feature proposal is a genuine three-way rather than a fast-forward.
    await Git(clone, ["checkout", "main"]);
    File.WriteAllText(Path.Combine(clone, "base.txt"), "base moved\n");
    await Git(clone, ["commit", "-am", "Move main"]);
    await Git(clone, ["push", "origin", "main"]);

    long issueNumber;
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/issues", aliceApi,
        new { title = "The lantern flickers", description = "It dims every few seconds." }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        issueNumber = json.RootElement.GetProperty("iid").GetInt64();
        Check(created.StatusCode == HttpStatusCode.Created && issueNumber == 1 &&
            json.RootElement.GetProperty("state").GetString() == "opened" &&
            json.RootElement.GetProperty("author").GetProperty("username").GetString() == "alice",
            "an issue is opened and numbered from one within its project");
    }
    using (HttpResponseMessage second = await ApiPost(address, prefix + "/issues", bobApi, new { title = "A developer files one too" }))
    {
        using JsonDocument json = JsonDocument.Parse(await second.Content.ReadAsStreamAsync());
        Check(second.StatusCode == HttpStatusCode.Created && json.RootElement.GetProperty("iid").GetInt64() == 2,
            "a second issue takes the next number");
    }

    using (HttpResponseMessage listing = await ApiGet(address, prefix + "/issues", aliceReadApi))
    {
        using JsonDocument json = JsonDocument.Parse(await listing.Content.ReadAsStreamAsync());
        Check(listing.IsSuccessStatusCode && json.RootElement.GetProperty("total").GetInt64() == 2 &&
            json.RootElement.GetProperty("issues")[0].GetProperty("iid").GetInt64() == 2,
            "a read_api token lists issues, newest first");
    }
    using (HttpResponseMessage refused = await ApiPost(address, prefix + "/issues", aliceReadApi, new { title = "Not with this token" }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "a read_api token cannot open an issue");
    using (HttpResponseMessage refused = await ApiGet(address, prefix + "/issues", repositoryOnly))
        Check(refused.StatusCode == HttpStatusCode.NotFound, "a repository-only token cannot read issues");
    using (HttpResponseMessage refused = await ApiGet(address, prefix + "/issues", null))
        Check(refused.StatusCode == HttpStatusCode.NotFound, "a private project's issues are concealed from a stranger");

    using (HttpResponseMessage comment = await ApiPost(address,
        prefix + "/issues/" + issueNumber + "/notes", bobApi, new { body = "I see it too." }))
    {
        using JsonDocument json = JsonDocument.Parse(await comment.Content.ReadAsStreamAsync());
        Check(comment.StatusCode == HttpStatusCode.Created &&
            json.RootElement.GetProperty("author").GetProperty("username").GetString() == bobUsername,
            "a member comments on somebody else's issue");
    }
    using (HttpResponseMessage notes = await ApiGet(address, prefix + "/issues/" + issueNumber + "/notes", aliceReadApi))
    {
        using JsonDocument json = JsonDocument.Parse(await notes.Content.ReadAsStreamAsync());
        Check(notes.IsSuccessStatusCode && json.RootElement.GetArrayLength() == 1, "comments are listed in order");
    }
    using (HttpResponseMessage read = await ApiGet(address, prefix + "/issues/" + issueNumber, aliceReadApi))
    {
        using JsonDocument json = JsonDocument.Parse(await read.Content.ReadAsStreamAsync());
        Check(read.IsSuccessStatusCode && json.RootElement.GetProperty("user_notes_count").GetInt64() == 1,
            "an issue reports how many comments it has");
    }

    using (HttpResponseMessage refused = await ApiPut(address, prefix + "/issues/" + issueNumber, bobApi,
        new { title = "Rewriting somebody else's words" }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "a Developer cannot rewrite another author's issue");
    using (HttpResponseMessage refused = await ApiPut(address, prefix + "/issues/" + issueNumber, bobApi, new { state_event = "close" }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "a Developer cannot close another author's issue");
    using (HttpResponseMessage closed = await ApiPut(address, prefix + "/issues/" + issueNumber, aliceApi, new { state_event = "close" }))
    {
        using JsonDocument json = JsonDocument.Parse(await closed.Content.ReadAsStreamAsync());
        Check(closed.IsSuccessStatusCode && json.RootElement.GetProperty("state").GetString() == "closed" &&
            json.RootElement.GetProperty("closed_at").ValueKind != JsonValueKind.Null,
            "the author closes their own issue, and the time it closed is recorded");
    }
    using (HttpResponseMessage closed = await ApiPut(address, prefix + "/issues/2", aliceApi, new { state_event = "close" }))
        Check(closed.IsSuccessStatusCode, "a Maintainer closes somebody else's issue");
    using (HttpResponseMessage reopened = await ApiPut(address, prefix + "/issues/2", aliceApi, new { state_event = "reopen" }))
        Check(reopened.IsSuccessStatusCode, "and can reopen it");
    using (HttpResponseMessage reopened = await ApiPut(address, prefix + "/issues/" + issueNumber, aliceApi, new { state_event = "reopen" }))
    {
        using JsonDocument json = JsonDocument.Parse(await reopened.Content.ReadAsStreamAsync());
        Check(reopened.IsSuccessStatusCode && json.RootElement.GetProperty("state").GetString() == "opened" &&
            json.RootElement.GetProperty("closed_at").ValueKind == JsonValueKind.Null,
            "reopening an issue clears the time it closed");
    }
    using (HttpResponseMessage refused = await ApiPut(address, prefix + "/issues/" + issueNumber, aliceApi, new { state_event = "resolve" }))
        Check(refused.StatusCode == HttpStatusCode.BadRequest, "state_event takes only close or reopen");
    using (HttpResponseMessage missing = await ApiGet(address, prefix + "/issues/99", aliceApi))
        Check(missing.StatusCode == HttpStatusCode.NotFound, "an issue number that names nothing is a 404");
    using (HttpResponseMessage refused = await ApiPost(address, prefix + "/issues", aliceApi, new { description = "no title" }))
        Check(refused.StatusCode == HttpStatusCode.BadRequest, "an issue needs a title");

    long forward;
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/merge_requests", aliceApi,
        new { source_branch = "forward", target_branch = "nothing", title = "Into a branch that is not there" }))
        Check(created.StatusCode == HttpStatusCode.BadRequest, "a proposal cannot name a branch that does not exist");
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/merge_requests", aliceApi,
        new { source_branch = "forward", target_branch = "forward", title = "Into itself" }))
        Check(created.StatusCode == HttpStatusCode.BadRequest, "a proposal needs two different branches");

    using (HttpResponseMessage created = await ApiPost(address, prefix + "/merge_requests", aliceApi,
        new { source_branch = "forward", target_branch = "trunk", title = "Move trunk ahead" }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        forward = json.RootElement.GetProperty("iid").GetInt64();
        Check(created.StatusCode == HttpStatusCode.Created && forward == 1 &&
            json.RootElement.GetProperty("source_branch").GetString() == "forward",
            "a merge proposal is created and numbered separately from issues");
    }
    using (HttpResponseMessage duplicate = await ApiPost(address, prefix + "/merge_requests", aliceApi,
        new { source_branch = "forward", target_branch = "trunk", title = "The same pair again" }))
        Check(duplicate.StatusCode == HttpStatusCode.Conflict, "a second open proposal for the same branches is refused");

    using (HttpResponseMessage listing = await ApiGet(address, prefix + "/merge_requests", aliceReadApi))
    {
        using JsonDocument json = JsonDocument.Parse(await listing.Content.ReadAsStreamAsync());
        Check(listing.IsSuccessStatusCode &&
            json.RootElement.GetProperty("merge_requests")[0].GetProperty("merge_status").GetString() == "unchecked",
            "a listing does not decide whether each proposal merges");
    }

    long conflicting;
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/merge_requests", aliceApi,
        new { source_branch = "theirs", target_branch = "mine", title = "Both sides changed one file" }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        conflicting = json.RootElement.GetProperty("iid").GetInt64();
        Check(created.StatusCode == HttpStatusCode.Created &&
            json.RootElement.GetProperty("merge_method").GetString() == "conflict" &&
            json.RootElement.GetProperty("merge_status").GetString() == "cannot_be_merged" &&
            json.RootElement.GetProperty("conflicts")[0].GetString() == "shared.txt",
            "a proposal whose branches changed one file reports the conflict and names the file");
    }
    using (HttpResponseMessage refused = await ApiPut(address,
        prefix + "/merge_requests/" + conflicting + "/merge", aliceApi, new { }))
    {
        string body = await refused.Content.ReadAsStringAsync();
        Check(refused.StatusCode == HttpStatusCode.Conflict && body.Contains("shared.txt", StringComparison.Ordinal),
            "merging a conflicting proposal is refused and says which file");
    }

    long feature;
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/merge_requests", aliceApi,
        new { source_branch = "feature", target_branch = "main", title = "Add the feature", description = "Only adds files." }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        feature = json.RootElement.GetProperty("iid").GetInt64();
        Check(created.StatusCode == HttpStatusCode.Created &&
            json.RootElement.GetProperty("merge_method").GetString() == "three_way" &&
            json.RootElement.GetProperty("merge_status").GetString() == "can_be_merged",
            "a proposal over disjoint files reports that it merges three ways");
    }
    using (HttpResponseMessage changes = await ApiGet(address, prefix + "/merge_requests/" + feature + "/changes", aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await changes.Content.ReadAsStreamAsync());
        JsonElement files = json.RootElement.GetProperty("files");
        // Measured from the merge base, so the file main gained afterwards is not attributed to this branch.
        Check(changes.IsSuccessStatusCode && files.GetArrayLength() == 1 &&
            files[0].GetProperty("path").GetString() == "feature.txt" &&
            files[0].GetProperty("status").GetString() == "added" &&
            json.RootElement.GetProperty("base_sha").GetString()!.Length == 40,
            "a proposal's changes are measured from where the branches last agreed");
    }

    using (HttpResponseMessage refused = await ApiPut(address, prefix + "/merge_requests/" + feature + "/merge", aliceReadApi, new { }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "merging needs the permission a push needs");

    using (HttpResponseMessage merged = await ApiPut(address, prefix + "/merge_requests/" + forward + "/merge", aliceApi, new { }))
    {
        using JsonDocument json = JsonDocument.Parse(await merged.Content.ReadAsStreamAsync());
        Check(merged.IsSuccessStatusCode && json.RootElement.GetProperty("method").GetString() == "fast_forward" &&
            json.RootElement.GetProperty("merge_request").GetProperty("state").GetString() == "merged",
            "a proposal whose target is an ancestor of its source fast-forwards");
    }
    using (HttpResponseMessage merged = await ApiPut(address, prefix + "/merge_requests/" + feature + "/merge", aliceApi, new { }))
    {
        using JsonDocument json = JsonDocument.Parse(await merged.Content.ReadAsStreamAsync());
        Check(merged.IsSuccessStatusCode && json.RootElement.GetProperty("method").GetString() == "three_way" &&
            json.RootElement.GetProperty("merge_commit_sha").GetString()!.Length == 40,
            "a proposal over disjoint files merges with a merge commit");
    }
    using (HttpResponseMessage again = await ApiPut(address, prefix + "/merge_requests/" + feature + "/merge", aliceApi, new { }))
        Check(again.StatusCode == HttpStatusCode.Conflict, "a merged proposal cannot be merged twice");
    using (HttpResponseMessage reopen = await ApiPut(address, prefix + "/merge_requests/" + feature, aliceApi, new { state_event = "reopen" }))
        Check(reopen.StatusCode == HttpStatusCode.Conflict, "a merged proposal cannot be reopened");

    // The merge is a real Git commit: a client fetches it, sees two parents, and finds both sides' files.
    await Git(clone, ["fetch", "origin"]);
    string mergeCommit = (await Git(clone, ["rev-parse", "origin/main"])).Trim();
    string parents = (await Git(clone, ["rev-list", "--parents", "-n", "1", mergeCommit])).Trim();
    Check(parents.Split(' ').Length == 3, "the merge commit real Git fetches has two parents");
    await Git(clone, ["checkout", "-B", "verify", "origin/main"]);
    Check(File.Exists(Path.Combine(clone, "feature.txt")) &&
        File.ReadAllText(Path.Combine(clone, "base.txt")).Trim() == "base moved",
        "the merged tree carries both sides' changes");
    Check((await RunGit(clone, ["fsck", "--strict", "--no-progress"])).ExitCode == 0, "the merged objects pass git fsck");

    using (HttpResponseMessage closed = await ApiPut(address, prefix + "/merge_requests/" + conflicting, aliceApi, new { state_event = "close" }))
    {
        using JsonDocument json = JsonDocument.Parse(await closed.Content.ReadAsStreamAsync());
        Check(closed.IsSuccessStatusCode && json.RootElement.GetProperty("state").GetString() == "closed",
            "a proposal nobody will merge can be closed");
    }

    // A public project's conversation is readable by a stranger; writing on it still is not.
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
        await store.SetProjectVisibilityAsync(alice, projectId, ProjectVisibility.Public);
    using (HttpResponseMessage anonymous = await ApiGet(address, prefix + "/issues", null))
    {
        using JsonDocument json = JsonDocument.Parse(await anonymous.Content.ReadAsStreamAsync());
        Check(anonymous.IsSuccessStatusCode && json.RootElement.GetProperty("total").GetInt64() == 2,
            "a public project's issues are readable with no credential");
    }
    using (HttpResponseMessage refused = await ApiPost(address, prefix + "/issues", null, new { title = "From nobody" }))
        Check(refused.StatusCode == HttpStatusCode.Unauthorized, "an anonymous caller cannot open an issue on a public project");
    using (HttpResponseMessage refused = await ApiPost(address, prefix + "/issues", strangerApi, new { title = "From a stranger with an account" }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden,
            "reading a public project does not make a stranger able to write on it");
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
        await store.SetProjectVisibilityAsync(alice, projectId, ProjectVisibility.Private);
}

/// <summary>
/// Deleting a project: who may, what it removes, and what the browser demands before it happens.
/// </summary>
async Task CheckProjectDeletion(string address, AuthenticatedIdentity alice, AuthenticatedIdentity administrator,
    string bobUsername, string alicePassword)
{
    long projectId;
    string storageId;
    string aliceApi;
    string bobApi;
    string administratorApi;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        aliceApi = (await store.CreateTokenAsync(alice, "delete-api", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        administratorApi = (await store.CreateTokenAsync(administrator, "delete-admin-api", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        ProjectInfo created = await store.CreateProjectWithRepositoryAsync(alice, "doomed", "Doomed",
            (project, cancellation) => { cancellation.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; });
        projectId = created.Id;
        storageId = created.StorageId;
        UserAccount bob = Required(await store.FindActiveUserAsync(bobUsername), "bob account");
        await store.SetMembershipAsync(alice, projectId, bob.Id, ProjectRole.Maintainer);
        bobApi = (await store.CreateTokenAsync(new(bob.Id, bob.Username, false, IdentityKind.BrowserSession),
            "delete-member-api", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        Issue issue = await store.CreateIssueAsync(alice, projectId, "Something to leave behind");
        await store.AddCommentAsync(alice, projectId, CommentSubject.Issue, issue.Iid, "A comment too.");
    }
    string directory = Path.Combine(repositoriesRoot, storageId);
    Check(Directory.Exists(directory), "the project to be deleted has a repository on disk");

    string path = "/api/v4/projects/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    using (HttpResponseMessage refused = await ApiDelete(address, path, bobApi))
        Check(refused.StatusCode == HttpStatusCode.Forbidden,
            "a Maintainer who does not own the project cannot delete it");
    using (HttpResponseMessage refused = await ApiDelete(address, path, null))
        Check(refused.StatusCode == HttpStatusCode.NotFound, "an anonymous caller cannot delete a project");

    // The browser asks for the path to be typed back, so a misclick cannot end a repository.
    CookieContainer cookies = new();
    using HttpClientHandler handler = new()
    {
        CookieContainer = cookies,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        UseProxy = false
    };
    using HttpClient browser = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };
    using (HttpRequestMessage login = new(HttpMethod.Post, "/api/login"))
    {
        login.Headers.Add("Origin", address);
        login.Content = JsonContent.Create(new { username = "alice", password = alicePassword });
        using HttpResponseMessage response = await browser.SendAsync(login);
        Check(response.IsSuccessStatusCode, "the owner signs in to delete a project");
    }
    string csrf;
    using (HttpResponseMessage session = await browser.GetAsync("/api/session"))
    {
        using JsonDocument json = JsonDocument.Parse(await session.Content.ReadAsStreamAsync());
        csrf = json.RootElement.GetProperty("csrf").GetString()!;
    }
    string browserPath = "/api/projects/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    using (HttpRequestMessage wrong = new(HttpMethod.Delete, browserPath))
    {
        wrong.Headers.Add("Origin", address);
        wrong.Headers.Add("X-Graft-CSRF", csrf);
        wrong.Content = JsonContent.Create(new { path = "alice/not-this-one" });
        using HttpResponseMessage response = await browser.SendAsync(wrong);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "the browser refuses a deletion whose confirmation does not match");
    }
    using (HttpRequestMessage noCsrf = new(HttpMethod.Delete, browserPath))
    {
        noCsrf.Headers.Add("Origin", address);
        noCsrf.Content = JsonContent.Create(new { path = "alice/doomed" });
        using HttpResponseMessage response = await browser.SendAsync(noCsrf);
        Check(response.StatusCode == HttpStatusCode.Forbidden, "a deletion without the CSRF header is refused");
    }
    Check(Directory.Exists(directory), "no refused deletion removed anything");

    using (HttpRequestMessage confirmed = new(HttpMethod.Delete, browserPath))
    {
        confirmed.Headers.Add("Origin", address);
        confirmed.Headers.Add("X-Graft-CSRF", csrf);
        confirmed.Content = JsonContent.Create(new { path = "alice/doomed" });
        using HttpResponseMessage response = await browser.SendAsync(confirmed);
        Check(response.StatusCode == HttpStatusCode.NoContent, "a confirmed deletion answers 204 because it is already done");
    }
    Check(!Directory.Exists(directory), "deleting a project removes its repository from disk");
    using (HttpResponseMessage gone = await ApiGet(address, path, aliceApi))
        Check(gone.StatusCode == HttpStatusCode.NotFound, "a deleted project is no longer served");
    // The Git transport authenticates with Basic rather than a token header, so a refusal here arrives as
    // the challenge a client would be prompted by. What matters is that no advertisement comes back.
    using (HttpResponseMessage clone = await ApiGet(address, "/alice/doomed.git/info/refs?service=git-upload-pack", aliceApi))
        Check(!clone.IsSuccessStatusCode, "a deleted project no longer answers the Git transport");

    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        Check(await store.FindReadyProjectAsync("alice", "doomed") is null, "the deleted project's row is gone");
        // The path is free again, which is the test that nothing was left holding it: both the namespace
        // pair and the instance-wide slug were released.
        ProjectInfo again = await store.CreateProjectWithRepositoryAsync(alice, "doomed", "Doomed again",
            (project, cancellation) => { cancellation.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; });
        Check(again.Id != projectId && again.GlobalSlug == "doomed",
            "the deleted project's path and instance-wide slug are free again");
    }

    // Read straight from the database, because nothing can ask an API about a project that no longer
    // exists. A row left behind would be keyed to an id some later project could be given.
    Check(await CountRows("Issues", projectId) == 0 && await CountRows("Comments", projectId) == 0,
        "the deleted project's issues and comments went with it");
    Check(await CountRows("ProjectMemberships", projectId) == 0,
        "the deleted project's membership went with it, so nothing can inherit it");

    // An administrator may delete somebody else's project, over the API, with no confirmation: the caller
    // is a program that already named the project in the URL.
    long adminTarget;
    string adminStorage;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo created = await store.CreateProjectWithRepositoryAsync(alice, "admin-removes", "Administrator removes this",
            (project, cancellation) => { cancellation.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; });
        adminTarget = created.Id;
        adminStorage = created.StorageId;
    }
    using (HttpResponseMessage deleted = await ApiDelete(address,
        "/api/v4/projects/" + adminTarget.ToString(System.Globalization.CultureInfo.InvariantCulture), administratorApi))
        Check(deleted.StatusCode == HttpStatusCode.NoContent, "an administrator deletes a project over the API without a confirmation");
    Check(!Directory.Exists(Path.Combine(repositoriesRoot, adminStorage)), "the administrator's deletion removed the repository too");
}

/// <summary>
/// The audit trail, which until now was written on every sensitive act and readable by nobody.
/// </summary>
async Task CheckAuditTrail(string address, string adminPassword, string alicePassword)
{
    CookieContainer cookies = new();
    using HttpClientHandler handler = new()
    {
        CookieContainer = cookies,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        UseProxy = false
    };
    using HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };

    using (HttpResponseMessage anonymous = await client.GetAsync("/api/graft/admin/audit"))
        Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "the audit trail needs a session");

    using (HttpRequestMessage login = new(HttpMethod.Post, "/api/login"))
    {
        login.Headers.Add("Origin", address);
        login.Content = JsonContent.Create(new { username = "alice", password = alicePassword });
        using HttpResponseMessage response = await client.SendAsync(login);
        Check(response.IsSuccessStatusCode, "an ordinary account signs in");
    }
    using (HttpResponseMessage refused = await client.GetAsync("/api/graft/admin/audit"))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "an ordinary account cannot read the audit trail");

    // A second browser rather than signing the first one out, so the two sessions are independent and the
    // ordinary account's refusal above cannot be explained by anything this one does.
    CookieContainer adminCookies = new();
    using HttpClientHandler adminHandler = new()
    {
        CookieContainer = adminCookies,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        UseProxy = false
    };
    using HttpClient administrator = new(adminHandler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };
    using (HttpRequestMessage login = new(HttpMethod.Post, "/api/login"))
    {
        login.Headers.Add("Origin", address);
        login.Content = JsonContent.Create(new { username = "admin", password = adminPassword });
        using HttpResponseMessage response = await administrator.SendAsync(login);
        Check(response.IsSuccessStatusCode, "the administrator signs in");
    }

    long total;
    using (HttpResponseMessage page = await administrator.GetAsync("/api/graft/admin/audit?per_page=20"))
    {
        using JsonDocument json = JsonDocument.Parse(await page.Content.ReadAsStreamAsync());
        JsonElement entries = json.RootElement.GetProperty("entries");
        total = json.RootElement.GetProperty("total").GetInt64();
        Check(page.IsSuccessStatusCode && total > 20 && entries.GetArrayLength() == 20,
            "an administrator reads the audit trail, a page at a time");
        Check(page.Headers.Contains("X-Total") && page.Headers.Contains("X-Page"), "the audit trail paginates like the rest of the API");
        // Newest first, which is the only ordering that makes the first page the useful one.
        long first = entries[0].GetProperty("id").GetInt64();
        long next = entries[1].GetProperty("id").GetInt64();
        Check(first > next, "the audit trail reads newest first");
        Check(entries[0].GetProperty("actor_username").GetString() is not null &&
            entries[0].GetProperty("created_at").GetString()!.Length > 0 &&
            entries[0].GetProperty("detail").GetString()!.Length > 0,
            "an entry names who did it, when, and what happened");
    }
    using (HttpResponseMessage filtered = await administrator.GetAsync("/api/graft/admin/audit?action=delete-project"))
    {
        using JsonDocument json = JsonDocument.Parse(await filtered.Content.ReadAsStreamAsync());
        JsonElement entries = json.RootElement.GetProperty("entries");
        Check(filtered.IsSuccessStatusCode && entries.GetArrayLength() >= 2 &&
            entries.EnumerateArray().All(entry => entry.GetProperty("action").GetString() == "delete-project"),
            "the trail filters by action, and the deletions above are in it");
        Check(entries.EnumerateArray().Any(entry =>
            entry.GetProperty("detail").GetString()!.Contains("repository storage", StringComparison.Ordinal)),
            "a deletion's entry names the repository directory, which nothing else records afterwards");
    }
    using (HttpResponseMessage filtered = await administrator.GetAsync("/api/graft/admin/audit?target_type=issue"))
    {
        using JsonDocument json = JsonDocument.Parse(await filtered.Content.ReadAsStreamAsync());
        Check(filtered.IsSuccessStatusCode && json.RootElement.GetProperty("entries").EnumerateArray()
            .All(entry => entry.GetProperty("target_type").GetString() == "issue"),
            "the trail filters by what was acted on");
    }
    using (HttpResponseMessage filtered = await administrator.GetAsync("/api/graft/admin/audit?action=nothing-does-this"))
    {
        using JsonDocument json = JsonDocument.Parse(await filtered.Content.ReadAsStreamAsync());
        Check(filtered.IsSuccessStatusCode && json.RootElement.GetProperty("total").GetInt64() == 0,
            "a filter that matches nothing is an empty page rather than an error");
    }
    using (HttpResponseMessage refused = await administrator.GetAsync("/api/graft/admin/audit?sort=detail"))
        Check(refused.StatusCode == HttpStatusCode.BadRequest, "the audit trail refuses a query it does not implement");

    // The trail records that things happened and never what they were. A token's secret passing through it
    // would be the one place a credential outlived its creation, so every entry is searched for one.
    using (HttpResponseMessage everything = await administrator.GetAsync("/api/graft/admin/audit?per_page=100"))
    {
        string body = await everything.Content.ReadAsStringAsync();
        Check(everything.IsSuccessStatusCode && !body.Contains("graft_pat_", StringComparison.Ordinal) &&
            !body.Contains(adminPassword, StringComparison.Ordinal) && !body.Contains(alicePassword, StringComparison.Ordinal),
            "no audit entry carries a token, a password or any other secret");
    }
}

/// <summary>
/// Counts rows in one table for one project. Used only to prove that a deleted project left none: the
/// table name comes from a literal at each call site and never from input.
/// </summary>
async Task<long> CountRows(string table, long projectId)
{
    await using Microsoft.Data.Sqlite.SqliteConnection db = new(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false
    }.ToString());
    await db.OpenAsync();
    await using Microsoft.Data.Sqlite.SqliteCommand command = db.CreateCommand();
    command.CommandText = table switch
    {
        "Issues" => "SELECT COUNT(*) FROM Issues WHERE ProjectId = $id;",
        "Comments" => "SELECT COUNT(*) FROM Comments WHERE ProjectId = $id;",
        "ProjectMemberships" => "SELECT COUNT(*) FROM ProjectMemberships WHERE ProjectId = $id;",
        _ => throw new ArgumentOutOfRangeException(nameof(table))
    };
    command.Parameters.AddWithValue("$id", projectId);
    return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The rest of the review journey: branches managed without a Git client, comments that point at a line,
/// approvals that stop counting when the branch moves, and removing the source branch with the merge.
/// </summary>
async Task CheckReview(string address, AuthenticatedIdentity alice, string bobUsername, string strangerUsername)
{
    long projectId;
    string aliceApi;
    string reviewerApi;
    string strangerApi;
    string readApi;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo created = await store.CreateProjectWithRepositoryAsync(alice, "review", "Review",
            (project, cancellation) => { cancellation.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; });
        projectId = created.Id;
        UserAccount bob = Required(await store.FindActiveUserAsync(bobUsername), "bob account");
        UserAccount stranger = Required(await store.FindActiveUserAsync(strangerUsername), "stranger account");
        // A Developer, so they can push and therefore approve; approving is gated on pushing rather than
        // on commenting, because an approval is what permits a merge.
        await store.SetMembershipAsync(alice, projectId, bob.Id, ProjectRole.Developer);
        // A Reporter, who may comment and must not be able to approve.
        await store.SetMembershipAsync(alice, projectId, stranger.Id, ProjectRole.Reporter);
        AuthenticatedIdentity bobIdentity = new(bob.Id, bob.Username, false, IdentityKind.BrowserSession);
        AuthenticatedIdentity strangerIdentity = new(stranger.Id, stranger.Username, false, IdentityKind.BrowserSession);
        aliceApi = (await store.CreateTokenAsync(alice, "review-api", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        readApi = (await store.CreateTokenAsync(alice, "review-read", TokenScope.ReadApi, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        reviewerApi = (await store.CreateTokenAsync(bobIdentity, "review-member", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        strangerApi = (await store.CreateTokenAsync(strangerIdentity, "review-reporter", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
    }
    string prefix = "/api/v4/projects/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    string clone = Path.Combine(root, "review-clone");
    Directory.CreateDirectory(clone);
    string pushUrl = AuthenticatedUrl(address, "alice", aliceApi, "alice/review.git");
    await Git(clone, ["init", "--initial-branch=main"]);
    File.WriteAllText(Path.Combine(clone, "lamp.txt"), "one\ntwo\nthree\n");
    await Git(clone, ["add", "."]);
    await Git(clone, ["commit", "-m", "Base"]);
    await Git(clone, ["remote", "add", "origin", pushUrl]);
    await Git(clone, ["push", "origin", "main"]);

    // Branches, managed without a Git client at all.
    using (HttpResponseMessage refused = await ApiPost(address, prefix + "/repository/branches", readApi,
        new { branch = "from-read-token", @ref = "main" }))
        Check(refused.StatusCode == HttpStatusCode.NotFound, "a read-only token cannot create a branch");
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/repository/branches", aliceApi,
        new { branch = "wick", @ref = "main" }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        Check(created.StatusCode == HttpStatusCode.Created &&
            json.RootElement.GetProperty("name").GetString() == "wick" &&
            json.RootElement.GetProperty("commit").GetProperty("id").GetString()!.Length == 40,
            "a branch is created from a named revision with no Git client");
    }
    using (HttpResponseMessage again = await ApiPost(address, prefix + "/repository/branches", aliceApi,
        new { branch = "wick", @ref = "main" }))
        Check(again.StatusCode == HttpStatusCode.Conflict, "creating a branch that already exists is refused rather than moving it");
    using (HttpResponseMessage nowhere = await ApiPost(address, prefix + "/repository/branches", aliceApi,
        new { branch = "nowhere", @ref = "no-such-revision" }))
        Check(nowhere.StatusCode == HttpStatusCode.BadRequest, "a branch cannot start from a revision that does not resolve");
    using (HttpResponseMessage spare = await ApiPost(address, prefix + "/repository/branches", aliceApi,
        new { branch = "spare", @ref = "main" }))
        Check(spare.StatusCode == HttpStatusCode.Created, "a second branch is created to be deleted");
    using (HttpResponseMessage deleted = await ApiDelete(address, prefix + "/repository/branches/spare", aliceApi))
        Check(deleted.StatusCode == HttpStatusCode.NoContent, "a branch is deleted");
    using (HttpResponseMessage gone = await ApiDelete(address, prefix + "/repository/branches/spare", aliceApi))
        Check(gone.StatusCode == HttpStatusCode.NotFound, "deleting a branch that is not there is a 404");
    using (HttpResponseMessage theDefault = await ApiDelete(address, prefix + "/repository/branches/main", aliceApi))
        Check(theDefault.StatusCode == HttpStatusCode.Conflict,
            "the branch the project opens at cannot be deleted while it is the default");
    // The commits behind a deleted branch are still there, which is what makes deleting one safe.
    using (HttpResponseMessage branches = await ApiGet(address, prefix + "/repository/branches", aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await branches.Content.ReadAsStreamAsync());
        Check(branches.IsSuccessStatusCode && json.RootElement.GetArrayLength() == 2,
            "the deleted branch is gone from the listing and the others remain");
    }

    // Work on the branch, so there is something to review.
    await Git(clone, ["fetch", "origin"]);
    await Git(clone, ["checkout", "-B", "wick", "origin/wick"]);
    File.WriteAllText(Path.Combine(clone, "lamp.txt"), "one\ntwo changed\nthree\nfour\n");
    await Git(clone, ["commit", "-am", "Change the wick"]);
    await Git(clone, ["push", "origin", "wick"]);

    long iid;
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/merge_requests", aliceApi,
        new { source_branch = "wick", target_branch = "main", title = "Replace the wick" }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        iid = json.RootElement.GetProperty("iid").GetInt64();
        Check(created.StatusCode == HttpStatusCode.Created &&
            json.RootElement.GetProperty("approvals_required").GetInt32() == 0 &&
            json.RootElement.GetProperty("approvals_left").GetInt32() == 0,
            "a project requires no approvals until somebody asks for them");
    }
    string notes = prefix + "/merge_requests/" + iid + "/notes";

    // A comment that points at a line.
    using (HttpResponseMessage half = await ApiPost(address, notes, aliceApi, new { body = "Half a position", path = "lamp.txt" }))
        Check(half.StatusCode == HttpStatusCode.BadRequest, "a comment on a line needs path, line and side together");
    using (HttpResponseMessage sideways = await ApiPost(address, notes, aliceApi,
        new { body = "Bad side", path = "lamp.txt", line = 2, side = "middle" }))
        Check(sideways.StatusCode == HttpStatusCode.BadRequest, "side must be old or new");

    string anchor;
    using (HttpResponseMessage anchored = await ApiPost(address, notes, reviewerApi,
        new { body = "Why four?", path = "lamp.txt", line = 4, side = "new" }))
    {
        using JsonDocument json = JsonDocument.Parse(await anchored.Content.ReadAsStreamAsync());
        anchor = json.RootElement.GetProperty("anchor_commit_sha").GetString()!;
        Check(anchored.StatusCode == HttpStatusCode.Created &&
            json.RootElement.GetProperty("path").GetString() == "lamp.txt" &&
            json.RootElement.GetProperty("line").GetInt32() == 4 &&
            json.RootElement.GetProperty("side").GetString() == "new" &&
            anchor.Length == 40 && !json.RootElement.GetProperty("outdated").GetBoolean(),
            "a comment anchors to a line, a side and the commit the diff was computed from");
    }
    using (HttpResponseMessage reporter = await ApiPost(address, notes, strangerApi,
        new { body = "A Reporter may comment on a line too.", path = "lamp.txt", line = 2, side = "new" }))
        Check(reporter.StatusCode == HttpStatusCode.Created, "commenting on a line needs membership, not the ability to push");
    using (HttpResponseMessage onIssue = await ApiPost(address, prefix + "/issues", aliceApi, new { title = "Nothing to diff" }))
    {
        using JsonDocument json = JsonDocument.Parse(await onIssue.Content.ReadAsStreamAsync());
        long issue = json.RootElement.GetProperty("iid").GetInt64();
        using HttpResponseMessage refused = await ApiPost(address, prefix + "/issues/" + issue + "/notes", aliceApi,
            new { body = "On a line of what?", path = "lamp.txt", line = 1, side = "new" });
        Check(refused.StatusCode == HttpStatusCode.BadRequest, "an issue has no diff, so a line comment on one is refused");
    }

    // Conversation: replies, editing, resolving, and what deleting may not do.
    long rootId;
    long replyId;
    using (HttpResponseMessage listing = await ApiGet(address, notes, aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await listing.Content.ReadAsStreamAsync());
        rootId = json.RootElement.EnumerateArray().First(note => note.GetProperty("line").GetInt32() == 4).GetProperty("id").GetInt64();
    }
    using (HttpResponseMessage reply = await ApiPost(address, notes, aliceApi, new { body = "Because the wick needs a fourth strand.", parent_id = rootId }))
    {
        using JsonDocument json = JsonDocument.Parse(await reply.Content.ReadAsStreamAsync());
        replyId = json.RootElement.GetProperty("id").GetInt64();
        Check(reply.StatusCode == HttpStatusCode.Created && json.RootElement.GetProperty("parent_id").GetInt64() == rootId &&
            json.RootElement.GetProperty("path").ValueKind == JsonValueKind.Null,
            "a reply joins its thread and carries no anchor of its own");
    }
    using (HttpResponseMessage nested = await ApiPost(address, notes, reviewerApi, new { body = "Agreed.", parent_id = replyId }))
    {
        using JsonDocument json = JsonDocument.Parse(await nested.Content.ReadAsStreamAsync());
        Check(nested.StatusCode == HttpStatusCode.Created && json.RootElement.GetProperty("parent_id").GetInt64() == rootId,
            "a reply to a reply attaches to the thread's first comment, so threads stay one level deep");
    }
    using (HttpResponseMessage both = await ApiPost(address, notes, aliceApi,
        new { body = "A reply on a line", parent_id = rootId, path = "lamp.txt", line = 2, side = "new" }))
        Check(both.StatusCode == HttpStatusCode.BadRequest, "a reply cannot name a line of its own");
    using (HttpResponseMessage elsewhere = await ApiPost(address, prefix + "/issues", aliceApi, new { title = "Another subject" }))
    {
        using JsonDocument json = JsonDocument.Parse(await elsewhere.Content.ReadAsStreamAsync());
        long issue = json.RootElement.GetProperty("iid").GetInt64();
        using HttpResponseMessage refused = await ApiPost(address, prefix + "/issues/" + issue + "/notes", aliceApi,
            new { body = "Answering a comment that is not here", parent_id = rootId });
        Check(refused.StatusCode == HttpStatusCode.NotFound, "a reply cannot answer a comment on a different issue or proposal");
    }

    using (HttpResponseMessage refused = await ApiPut(address, notes + "/" + rootId, strangerApi, new { body = "Rewritten by a Reporter" }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "a Reporter cannot edit somebody else's comment");
    using (HttpResponseMessage edited = await ApiPut(address, notes + "/" + rootId, reviewerApi, new { body = "Why four strands?" }))
    {
        using JsonDocument json = JsonDocument.Parse(await edited.Content.ReadAsStreamAsync());
        Check(edited.IsSuccessStatusCode && json.RootElement.GetProperty("body").GetString() == "Why four strands?" &&
            json.RootElement.GetProperty("edited_at").ValueKind != JsonValueKind.Null &&
            json.RootElement.GetProperty("path").GetString() == "lamp.txt",
            "the author edits their own comment, the edit is recorded, and the anchor survives");
    }
    using (HttpResponseMessage edited = await ApiPut(address, notes + "/" + replyId, aliceApi, new { body = "Because the wick needs a fourth strand, as agreed." }))
        Check(edited.IsSuccessStatusCode, "a Maintainer edits somebody else's comment");
    using (HttpResponseMessage nothing = await ApiPut(address, notes + "/" + rootId, aliceApi, new { }))
        Check(nothing.StatusCode == HttpStatusCode.BadRequest, "an edit has to change the words or the thread");

    using (HttpResponseMessage refused = await ApiPut(address, notes + "/" + rootId, strangerApi, new { resolved = true }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden,
            "resolving a thread needs the permission to push, or to have started it; a Reporter has neither");
    using (HttpResponseMessage resolved = await ApiPut(address, notes + "/" + replyId, reviewerApi, new { resolved = true }))
    {
        using JsonDocument json = JsonDocument.Parse(await resolved.Content.ReadAsStreamAsync());
        Check(resolved.IsSuccessStatusCode && json.RootElement.GetProperty("id").GetInt64() == rootId &&
            json.RootElement.GetProperty("resolved").GetBoolean() &&
            json.RootElement.GetProperty("resolved_by").GetString() == bobUsername,
            "resolving through a reply settles the whole thread and names who settled it");
    }
    using (HttpResponseMessage listing = await ApiGet(address, notes, aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await listing.Content.ReadAsStreamAsync());
        JsonElement root = json.RootElement.EnumerateArray().First(note => note.GetProperty("id").GetInt64() == rootId);
        JsonElement reply = json.RootElement.EnumerateArray().First(note => note.GetProperty("id").GetInt64() == replyId);
        Check(root.GetProperty("resolved").GetBoolean() && !reply.GetProperty("resolved").GetBoolean(),
            "a listing reports the thread's state on its first comment and not on its replies");
    }
    using (HttpResponseMessage reopened = await ApiPut(address, notes + "/" + rootId, aliceApi, new { resolved = false }))
    {
        using JsonDocument json = JsonDocument.Parse(await reopened.Content.ReadAsStreamAsync());
        Check(reopened.IsSuccessStatusCode && !json.RootElement.GetProperty("resolved").GetBoolean() &&
            json.RootElement.GetProperty("resolved_by").ValueKind == JsonValueKind.Null,
            "a resolved thread can be reopened");
    }
    // The Reporter started a thread of their own, which they may resolve despite being unable to push.
    long reporterThread;
    using (HttpResponseMessage listing = await ApiGet(address, notes, aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await listing.Content.ReadAsStreamAsync());
        reporterThread = json.RootElement.EnumerateArray()
            .First(note => note.GetProperty("author").GetProperty("username").GetString() == strangerUsername).GetProperty("id").GetInt64();
    }
    using (HttpResponseMessage own = await ApiPut(address, notes + "/" + reporterThread, strangerApi, new { resolved = true }))
        Check(own.IsSuccessStatusCode, "whoever started a thread may resolve it, whatever their role");

    using (HttpResponseMessage refused = await ApiDelete(address, notes + "/" + rootId, reviewerApi))
        Check(refused.StatusCode == HttpStatusCode.Conflict, "a comment with replies cannot be deleted");
    using (HttpResponseMessage wrongDoor = await ApiDelete(address, prefix + "/merge_requests/99/notes/" + replyId, aliceApi))
        Check(wrongDoor.StatusCode == HttpStatusCode.NotFound, "a comment is not reachable beneath a proposal it is not on");
    using (HttpResponseMessage deleted = await ApiDelete(address, notes + "/" + replyId, aliceApi))
        Check(deleted.StatusCode == HttpStatusCode.NoContent, "a reply can be deleted over the GitLab-shaped route");

    // Approvals.
    using (HttpResponseMessage required = await ApiPut(address, prefix + "/approvals", aliceApi, new { approvals_before_merge = 1 }))
        Check(required.StatusCode == HttpStatusCode.NoContent, "a Maintainer sets how many approvals a merge needs");
    using (HttpResponseMessage refused = await ApiPut(address, prefix + "/approvals", reviewerApi, new { approvals_before_merge = 0 }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "a Developer cannot change the approval rule");
    using (HttpResponseMessage blocked = await ApiPut(address, prefix + "/merge_requests/" + iid + "/merge", aliceApi, new { }))
    {
        string body = await blocked.Content.ReadAsStringAsync();
        Check(blocked.StatusCode == HttpStatusCode.Conflict && body.Contains("outstanding", StringComparison.Ordinal),
            "a merge is refused while approvals are outstanding");
    }
    using (HttpResponseMessage own = await ApiPost(address, prefix + "/merge_requests/" + iid + "/approve", aliceApi, new { }))
        Check(own.StatusCode == HttpStatusCode.Conflict, "an author cannot approve their own proposal");
    using (HttpResponseMessage reporter = await ApiPost(address, prefix + "/merge_requests/" + iid + "/approve", strangerApi, new { }))
        Check(reporter.StatusCode == HttpStatusCode.Forbidden,
            "approving needs the permission to push, which a Reporter does not have");
    using (HttpResponseMessage approved = await ApiPost(address, prefix + "/merge_requests/" + iid + "/approve", reviewerApi, new { }))
    {
        using JsonDocument json = JsonDocument.Parse(await approved.Content.ReadAsStreamAsync());
        Check(approved.IsSuccessStatusCode && json.RootElement.GetProperty("approvals_left").GetInt32() == 0 &&
            json.RootElement.GetProperty("approved_by")[0].GetProperty("current").GetBoolean(),
            "a reviewer approves, and the gate is satisfied");
    }

    // Pushing supersedes both the approval and the anchored comment, because neither was about this code.
    File.WriteAllText(Path.Combine(clone, "lamp.txt"), "one\ntwo changed\nthree\nfour\nfive\n");
    await Git(clone, ["commit", "-am", "And a fifth line"]);
    await Git(clone, ["push", "origin", "wick"]);

    using (HttpResponseMessage after = await ApiGet(address, prefix + "/merge_requests/" + iid, aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await after.Content.ReadAsStreamAsync());
        Check(after.IsSuccessStatusCode && json.RootElement.GetProperty("approvals_left").GetInt32() == 1 &&
            !json.RootElement.GetProperty("approved_by")[0].GetProperty("current").GetBoolean(),
            "a push supersedes an approval, which is shown but no longer counts");
    }
    using (HttpResponseMessage blocked = await ApiPut(address, prefix + "/merge_requests/" + iid + "/merge", aliceApi, new { }))
        Check(blocked.StatusCode == HttpStatusCode.Conflict, "and the merge is refused again");
    using (HttpResponseMessage stale = await ApiGet(address, notes, aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await stale.Content.ReadAsStreamAsync());
        Check(stale.IsSuccessStatusCode && json.RootElement.EnumerateArray()
            .Where(note => note.GetProperty("path").ValueKind != JsonValueKind.Null)
            .All(note => note.GetProperty("outdated").GetBoolean()),
            "a comment written against an earlier commit is reported as outdated rather than moved");
        Check(json.RootElement[0].GetProperty("anchor_commit_sha").GetString() == anchor,
            "and still names the commit it was written against");
    }

    using (HttpResponseMessage approved = await ApiPost(address, prefix + "/merge_requests/" + iid + "/approve", reviewerApi, new { }))
        Check(approved.IsSuccessStatusCode, "the reviewer approves the new work");
    using (HttpResponseMessage withdrawn = await ApiPost(address, prefix + "/merge_requests/" + iid + "/unapprove", reviewerApi, new { }))
    {
        using JsonDocument json = JsonDocument.Parse(await withdrawn.Content.ReadAsStreamAsync());
        Check(withdrawn.IsSuccessStatusCode && json.RootElement.GetProperty("approved_by").GetArrayLength() == 0,
            "an approval can be withdrawn");
    }
    using (HttpResponseMessage approved = await ApiPost(address, prefix + "/merge_requests/" + iid + "/approve", reviewerApi, new { }))
        Check(approved.IsSuccessStatusCode, "and given again");

    // Merging, taking the source branch with it.
    using (HttpResponseMessage merged = await ApiPut(address, prefix + "/merge_requests/" + iid + "/merge", aliceApi,
        new { should_remove_source_branch = true }))
    {
        using JsonDocument json = JsonDocument.Parse(await merged.Content.ReadAsStreamAsync());
        Check(merged.IsSuccessStatusCode && json.RootElement.GetProperty("source_branch_removed").GetBoolean(),
            "a merge can take the source branch with it");
    }
    using (HttpResponseMessage branches = await ApiGet(address, prefix + "/repository/branches", aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await branches.Content.ReadAsStreamAsync());
        Check(branches.IsSuccessStatusCode && json.RootElement.GetArrayLength() == 1 &&
            json.RootElement[0].GetProperty("name").GetString() == "main",
            "and the branch is gone afterwards");
    }
    // The work survives the branch that carried it, which is the point of deleting one being safe.
    await Git(clone, ["fetch", "origin", "--prune"]);
    await Git(clone, ["checkout", "-B", "verify", "origin/main"]);
    Check(File.ReadAllText(Path.Combine(clone, "lamp.txt")).Contains("five", StringComparison.Ordinal),
        "the merged work is on the target branch after the source branch was removed");

    using (HttpResponseMessage reset = await ApiPut(address, prefix + "/approvals", aliceApi, new { approvals_before_merge = 0 }))
        Check(reset.StatusCode == HttpStatusCode.NoContent, "the approval rule can be removed again");
}

/// <summary>
/// Planning: labels, milestones, assignees, due dates, confidential issues and boards, and the rule that a
/// Reporter files while a Developer organises.
/// </summary>
async Task CheckPlanning(string address, AuthenticatedIdentity alice, string bobUsername, string reporterUsername)
{
    long projectId;
    string aliceApi;
    string developerApi;
    string reporterApi;
    string readApi;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo created = await store.CreateProjectWithRepositoryAsync(alice, "plan", "Planning",
            (project, cancellation) => { cancellation.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; });
        projectId = created.Id;
        UserAccount bob = Required(await store.FindActiveUserAsync(bobUsername), "bob account");
        UserAccount reporter = Required(await store.FindActiveUserAsync(reporterUsername), "reporter account");
        await store.SetMembershipAsync(alice, projectId, bob.Id, ProjectRole.Developer);
        await store.SetMembershipAsync(alice, projectId, reporter.Id, ProjectRole.Reporter);
        aliceApi = (await store.CreateTokenAsync(alice, "plan-api", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        readApi = (await store.CreateTokenAsync(alice, "plan-read", TokenScope.ReadApi, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        developerApi = (await store.CreateTokenAsync(new(bob.Id, bob.Username, false, IdentityKind.BrowserSession), "plan-developer", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        reporterApi = (await store.CreateTokenAsync(new(reporter.Id, reporter.Username, false, IdentityKind.BrowserSession), "plan-reporter", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
    }
    string prefix = "/api/v4/projects/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Labels: a Developer's vocabulary, not a Reporter's.
    using (HttpResponseMessage refused = await ApiPost(address, prefix + "/labels", reporterApi, new { name = "bug", color = "#d73a4a" }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "a Reporter cannot create a label");
    long bugId;
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/labels", developerApi, new { name = "bug", color = "#d73a4a", description = "Something is wrong" }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        bugId = json.RootElement.GetProperty("id").GetInt64();
        Check(created.StatusCode == HttpStatusCode.Created && json.RootElement.GetProperty("name").GetString() == "bug" &&
            json.RootElement.GetProperty("text_color").GetString() == "#ffffff",
            "a Developer creates a label, and the text colour that reads against it is computed");
    }
    using (HttpResponseMessage duplicate = await ApiPost(address, prefix + "/labels", developerApi, new { name = "Bug", color = "#000000" }))
        Check(duplicate.StatusCode == HttpStatusCode.Conflict, "label names are unique without regard to case");
    using (HttpResponseMessage colour = await ApiPost(address, prefix + "/labels", developerApi, new { name = "odd", color = "red" }))
        Check(colour.StatusCode == HttpStatusCode.BadRequest, "a colour is six hexadecimal digits and nothing else");
    using (HttpResponseMessage comma = await ApiPost(address, prefix + "/labels", developerApi, new { name = "a,b", color = "#000000" }))
        Check(comma.StatusCode == HttpStatusCode.BadRequest, "a label name cannot hold a comma, which separates labels in requests");
    using (HttpResponseMessage renamed = await ApiPut(address, prefix + "/labels/bug", aliceApi, new { name = "defect", color = "#fff" }))
    {
        using JsonDocument json = JsonDocument.Parse(await renamed.Content.ReadAsStreamAsync());
        Check(renamed.IsSuccessStatusCode && json.RootElement.GetProperty("name").GetString() == "defect" &&
            json.RootElement.GetProperty("color").GetString() == "#ffffff" && json.RootElement.GetProperty("text_color").GetString() == "#1f2328",
            "a label is addressed by name as well as id, and a short colour is expanded");
    }
    using (HttpResponseMessage urgent = await ApiPost(address, prefix + "/labels", developerApi, new { name = "urgent", color = "#e4a11b" }))
        Check(urgent.StatusCode == HttpStatusCode.Created, "a second label is created");
    using (HttpResponseMessage listing = await ApiGet(address, prefix + "/labels", readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await listing.Content.ReadAsStreamAsync());
        Check(listing.IsSuccessStatusCode && json.RootElement.GetArrayLength() == 2, "a read_api token lists labels");
    }

    // Milestones.
    long milestoneId;
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/milestones", developerApi, new { title = "v1", due_date = "2027-03-01" }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        milestoneId = json.RootElement.GetProperty("id").GetInt64();
        Check(created.StatusCode == HttpStatusCode.Created && json.RootElement.GetProperty("iid").GetInt64() == 1 &&
            json.RootElement.GetProperty("state").GetString() == "active" && json.RootElement.GetProperty("due_date").GetString() == "2027-03-01",
            "a milestone is created, numbered from one, active, with a due date");
    }
    using (HttpResponseMessage backwards = await ApiPost(address, prefix + "/milestones", developerApi, new { title = "v2", start_date = "2027-06-01", due_date = "2027-03-01" }))
        Check(backwards.StatusCode == HttpStatusCode.BadRequest, "a milestone cannot start after it is due");
    using (HttpResponseMessage refused = await ApiPost(address, prefix + "/milestones", reporterApi, new { title = "mine" }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "a Reporter cannot create a milestone");

    // An issue with everything set at once.
    long planned;
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/issues", aliceApi, new
    {
        title = "The lantern smokes", labels = "defect, urgent", milestone_id = milestoneId,
        assignee_usernames = new[] { bobUsername }, due_date = "2027-02-01"
    }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        planned = json.RootElement.GetProperty("iid").GetInt64();
        Check(created.StatusCode == HttpStatusCode.Created &&
            json.RootElement.GetProperty("labels").GetArrayLength() == 2 &&
            json.RootElement.GetProperty("label_details")[0].GetProperty("color").GetString()!.Length == 7 &&
            json.RootElement.GetProperty("milestone").GetProperty("title").GetString() == "v1" &&
            json.RootElement.GetProperty("assignees")[0].GetProperty("username").GetString() == bobUsername &&
            json.RootElement.GetProperty("due_date").GetString() == "2027-02-01",
            "an issue is opened with labels, a milestone, an assignee and a due date");
    }
    using (HttpResponseMessage unknown = await ApiPost(address, prefix + "/issues", aliceApi, new { title = "Mislabelled", labels = "nonsense" }))
        Check(unknown.StatusCode == HttpStatusCode.BadRequest, "a label that does not exist is refused by name rather than created");
    using (HttpResponseMessage outsider = await ApiPost(address, prefix + "/issues", aliceApi, new { title = "Assigned outside", assignee_usernames = new[] { "admin" } }))
        Check(outsider.StatusCode == HttpStatusCode.BadRequest, "only a member of the project can be assigned");
    using (HttpResponseMessage date = await ApiPost(address, prefix + "/issues", aliceApi, new { title = "Bad date", due_date = "tomorrow" }))
        Check(date.StatusCode == HttpStatusCode.BadRequest, "a due date is YYYY-MM-DD");

    using (HttpResponseMessage refused = await ApiPut(address, prefix + "/issues/" + planned, reporterApi, new { add_labels = "urgent" }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "a Reporter cannot relabel somebody else's issue");
    long reporters;
    using (HttpResponseMessage own = await ApiPost(address, prefix + "/issues", reporterApi, new { title = "Filed by a Reporter", labels = "urgent" }))
    {
        using JsonDocument json = JsonDocument.Parse(await own.Content.ReadAsStreamAsync());
        reporters = json.RootElement.GetProperty("iid").GetInt64();
        Check(own.StatusCode == HttpStatusCode.Created && json.RootElement.GetProperty("labels")[0].GetString() == "urgent",
            "a Reporter labels their own issue");
    }
    using (HttpResponseMessage triaged = await ApiPut(address, prefix + "/issues/" + planned, developerApi, new { remove_labels = "urgent", assignee_usernames = new[] { bobUsername, reporterUsername } }))
    {
        using JsonDocument json = JsonDocument.Parse(await triaged.Content.ReadAsStreamAsync());
        Check(triaged.IsSuccessStatusCode && json.RootElement.GetProperty("labels").GetArrayLength() == 1 &&
            json.RootElement.GetProperty("labels")[0].GetString() == "defect" &&
            json.RootElement.GetProperty("assignees").GetArrayLength() == 2,
            "a Developer triages another author's issue: labels adjusted, people assigned");
    }

    using (HttpResponseMessage filtered = await ApiGet(address, prefix + "/issues?labels=urgent", readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await filtered.Content.ReadAsStreamAsync());
        Check(filtered.IsSuccessStatusCode && json.RootElement.GetProperty("total").GetInt64() == 1 &&
            json.RootElement.GetProperty("issues")[0].GetProperty("iid").GetInt64() == reporters,
            "issues are filtered by label");
    }
    using (HttpResponseMessage filtered = await ApiGet(address, prefix + "/issues?milestone_id=" + milestoneId + "&assignee_username=" + bobUsername, readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await filtered.Content.ReadAsStreamAsync());
        Check(filtered.IsSuccessStatusCode && json.RootElement.GetProperty("total").GetInt64() == 1 &&
            json.RootElement.GetProperty("issues")[0].GetProperty("iid").GetInt64() == planned,
            "issues are filtered by milestone and assignee together");
    }
    using (HttpResponseMessage filtered = await ApiGet(address, prefix + "/issues?milestone_id=None&search=reporter", readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await filtered.Content.ReadAsStreamAsync());
        Check(filtered.IsSuccessStatusCode && json.RootElement.GetProperty("total").GetInt64() == 1,
            "issues with no milestone are filtered, and a search narrows by title");
    }
    using (HttpResponseMessage filtered = await ApiGet(address, prefix + "/issues?without_labels=defect,urgent", readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await filtered.Content.ReadAsStreamAsync());
        Check(filtered.IsSuccessStatusCode && json.RootElement.GetProperty("total").GetInt64() == 0,
            "a board's first column asks for issues carrying none of its labels");
    }
    using (HttpResponseMessage milestone = await ApiGet(address, prefix + "/milestones/" + milestoneId, readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await milestone.Content.ReadAsStreamAsync());
        Check(milestone.IsSuccessStatusCode && json.RootElement.GetProperty("open_issues").GetInt64() == 1,
            "a milestone counts the issues in it");
    }
    using (HttpResponseMessage cleared = await ApiPut(address, prefix + "/issues/" + planned, aliceApi, new { milestone_id = 0, due_date = "" }))
    {
        using JsonDocument json = JsonDocument.Parse(await cleared.Content.ReadAsStreamAsync());
        Check(cleared.IsSuccessStatusCode && json.RootElement.GetProperty("milestone").ValueKind == JsonValueKind.Null &&
            json.RootElement.GetProperty("due_date").ValueKind == JsonValueKind.Null,
            "a milestone of zero and an empty due date clear them, GitLab's way");
    }

    // Confidential issues: members and the author, never the public grant.
    long secret;
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/issues", aliceApi, new { title = "The safe combination", confidential = true }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        secret = json.RootElement.GetProperty("iid").GetInt64();
        Check(created.StatusCode == HttpStatusCode.Created && json.RootElement.GetProperty("confidential").GetBoolean(),
            "a confidential issue is opened");
    }
    using (HttpResponseMessage member = await ApiGet(address, prefix + "/issues/" + secret, reporterApi))
        Check(member.IsSuccessStatusCode, "a Reporter who is a member reads a confidential issue");
    using (HttpResponseMessage refused = await ApiPut(address, prefix + "/issues/" + reporters, developerApi, new { confidential = true }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden,
            "making an issue confidential needs the author or a Maintainer, because it changes who may read it");
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
        await store.SetProjectVisibilityAsync(alice, projectId, ProjectVisibility.Public);
    using (HttpResponseMessage anonymous = await ApiGet(address, prefix + "/issues?state=all", null))
    {
        using JsonDocument json = JsonDocument.Parse(await anonymous.Content.ReadAsStreamAsync());
        Check(anonymous.IsSuccessStatusCode && json.RootElement.GetProperty("total").GetInt64() == 2 &&
            json.RootElement.GetProperty("issues").EnumerateArray().All(issue => !issue.GetProperty("confidential").GetBoolean()),
            "a public project's listing conceals its confidential issues from a stranger");
    }
    using (HttpResponseMessage hidden = await ApiGet(address, prefix + "/issues/" + secret, null))
        Check(hidden.StatusCode == HttpStatusCode.NotFound, "a confidential issue read directly by a stranger does not exist");
    using (HttpResponseMessage hidden = await ApiGet(address, prefix + "/issues/" + secret + "/notes", null))
        Check(hidden.StatusCode == HttpStatusCode.NotFound, "and neither does its conversation");
    using (HttpResponseMessage members = await ApiGet(address, prefix + "/users", reporterApi))
    {
        using JsonDocument json = JsonDocument.Parse(await members.Content.ReadAsStreamAsync());
        Check(members.IsSuccessStatusCode && json.RootElement.GetArrayLength() == 3,
            "a member lists who can be assigned: the owner and every member");
    }
    using (HttpResponseMessage stranger = await ApiGet(address, prefix + "/users", null))
        Check(stranger.StatusCode == HttpStatusCode.Unauthorized, "a stranger cannot list who can be assigned, even on a public project");
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
        await store.SetProjectVisibilityAsync(alice, projectId, ProjectVisibility.Private);
    using (HttpResponseMessage revealed = await ApiPut(address, prefix + "/issues/" + secret, aliceApi, new { confidential = false }))
    {
        using JsonDocument json = JsonDocument.Parse(await revealed.Content.ReadAsStreamAsync());
        Check(revealed.IsSuccessStatusCode && !json.RootElement.GetProperty("confidential").GetBoolean(), "the author makes it visible again");
    }

    // Boards: lists bound to labels, over the project's issues.
    long boardId;
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/boards", developerApi, new { name = "Flow" }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        boardId = json.RootElement.GetProperty("id").GetInt64();
        Check(created.StatusCode == HttpStatusCode.Created && json.RootElement.GetProperty("lists").GetArrayLength() == 0, "a board is created with no lists");
    }
    using (HttpResponseMessage list = await ApiPost(address, prefix + "/boards/" + boardId + "/lists", developerApi, new { label_id = bugId }))
        Check(list.StatusCode == HttpStatusCode.Created, "a list is added for a label by id");
    long urgentList;
    using (HttpResponseMessage list = await ApiPost(address, prefix + "/boards/" + boardId + "/lists", developerApi, new { label = "urgent" }))
    {
        using JsonDocument json = JsonDocument.Parse(await list.Content.ReadAsStreamAsync());
        JsonElement lists = json.RootElement.GetProperty("lists");
        urgentList = lists[1].GetProperty("id").GetInt64();
        Check(list.StatusCode == HttpStatusCode.Created && lists.GetArrayLength() == 2 &&
            lists[1].GetProperty("label").GetProperty("name").GetString() == "urgent" && lists[1].GetProperty("position").GetInt32() == 2,
            "a list is added for a label by name, after the first");
    }
    using (HttpResponseMessage again = await ApiPost(address, prefix + "/boards/" + boardId + "/lists", developerApi, new { label = "urgent" }))
        Check(again.StatusCode == HttpStatusCode.Conflict, "a label has one list per board");
    using (HttpResponseMessage moved = await ApiPut(address, prefix + "/boards/" + boardId + "/lists/" + urgentList, developerApi, new { position = 1 }))
    {
        using JsonDocument json = JsonDocument.Parse(await moved.Content.ReadAsStreamAsync());
        Check(moved.IsSuccessStatusCode && json.RootElement.GetProperty("lists")[0].GetProperty("label").GetProperty("name").GetString() == "urgent",
            "a list is moved to the front");
    }
    using (HttpResponseMessage refused = await ApiDelete(address, prefix + "/boards/" + boardId, reporterApi))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "a Reporter cannot delete a board");
    using (HttpResponseMessage removed = await ApiDelete(address, prefix + "/boards/" + boardId + "/lists/" + urgentList, developerApi))
        Check(removed.StatusCode == HttpStatusCode.NoContent, "a list is removed");
    using (HttpResponseMessage boards = await ApiGet(address, prefix + "/boards", readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await boards.Content.ReadAsStreamAsync());
        Check(boards.IsSuccessStatusCode && json.RootElement[0].GetProperty("lists").GetArrayLength() == 1, "the board lists what remains");
    }

    // A proposal with reviewers and a draft flag.
    string clone = Path.Combine(root, "plan-clone");
    Directory.CreateDirectory(clone);
    string pushUrl = AuthenticatedUrl(address, "alice", aliceApi, "alice/plan.git");
    await Git(clone, ["init", "--initial-branch=main"]);
    File.WriteAllText(Path.Combine(clone, "plan.txt"), "one\n");
    await Git(clone, ["add", "."]);
    await Git(clone, ["commit", "-m", "Base"]);
    await Git(clone, ["remote", "add", "origin", pushUrl]);
    await Git(clone, ["push", "origin", "main"]);
    await Git(clone, ["checkout", "-b", "work"]);
    File.WriteAllText(Path.Combine(clone, "work.txt"), "work\n");
    await Git(clone, ["add", "."]);
    await Git(clone, ["commit", "-m", "Work"]);
    await Git(clone, ["push", "origin", "work"]);

    long draft;
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/merge_requests", aliceApi, new
    {
        source_branch = "work", target_branch = "main", title = "Not ready", draft = true,
        reviewer_usernames = new[] { bobUsername }, labels = "defect", milestone_id = milestoneId
    }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        draft = json.RootElement.GetProperty("iid").GetInt64();
        Check(created.StatusCode == HttpStatusCode.Created && json.RootElement.GetProperty("draft").GetBoolean() &&
            json.RootElement.GetProperty("reviewers")[0].GetProperty("username").GetString() == bobUsername &&
            json.RootElement.GetProperty("labels")[0].GetString() == "defect" &&
            json.RootElement.GetProperty("milestone").GetProperty("id").GetInt64() == milestoneId,
            "a proposal is opened as a draft with a reviewer, a label and a milestone");
    }
    using (HttpResponseMessage blocked = await ApiPut(address, prefix + "/merge_requests/" + draft + "/merge", aliceApi, new { }))
    {
        string body = await blocked.Content.ReadAsStringAsync();
        Check(blocked.StatusCode == HttpStatusCode.Conflict && body.Contains("draft", StringComparison.Ordinal),
            "a draft cannot be merged, however mergeable its branches are");
    }
    using (HttpResponseMessage refused = await ApiPut(address, prefix + "/merge_requests/" + draft, developerApi, new { draft = false }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "only the author or a Maintainer says a proposal is ready");
    using (HttpResponseMessage filtered = await ApiGet(address, prefix + "/merge_requests?reviewer_username=" + bobUsername, readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await filtered.Content.ReadAsStreamAsync());
        Check(filtered.IsSuccessStatusCode && json.RootElement.GetProperty("total").GetInt64() == 1, "proposals are filtered by reviewer");
    }
    using (HttpResponseMessage ready = await ApiPut(address, prefix + "/merge_requests/" + draft, aliceApi, new { draft = false }))
    {
        using JsonDocument json = JsonDocument.Parse(await ready.Content.ReadAsStreamAsync());
        Check(ready.IsSuccessStatusCode && !json.RootElement.GetProperty("draft").GetBoolean(), "the author marks it ready");
    }
    using (HttpResponseMessage milestone = await ApiGet(address, prefix + "/milestones/" + milestoneId, readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await milestone.Content.ReadAsStreamAsync());
        Check(milestone.IsSuccessStatusCode && json.RootElement.GetProperty("merge_requests").GetInt64() == 1, "a milestone counts its proposals");
    }

    // Removing the vocabulary leaves the work.
    using (HttpResponseMessage closed = await ApiPut(address, prefix + "/milestones/" + milestoneId, developerApi, new { state_event = "close" }))
    {
        using JsonDocument json = JsonDocument.Parse(await closed.Content.ReadAsStreamAsync());
        Check(closed.IsSuccessStatusCode && json.RootElement.GetProperty("state").GetString() == "closed", "a milestone is closed");
    }
    using (HttpResponseMessage deleted = await ApiDelete(address, prefix + "/milestones/" + milestoneId, developerApi))
        Check(deleted.StatusCode == HttpStatusCode.NoContent, "a milestone is deleted");
    using (HttpResponseMessage after = await ApiGet(address, prefix + "/merge_requests/" + draft, readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await after.Content.ReadAsStreamAsync());
        Check(after.IsSuccessStatusCode && json.RootElement.GetProperty("milestone").ValueKind == JsonValueKind.Null,
            "deleting a milestone leaves the proposal with none rather than deleting the proposal");
    }
    using (HttpResponseMessage deleted = await ApiDelete(address, prefix + "/labels/" + bugId, developerApi))
        Check(deleted.StatusCode == HttpStatusCode.NoContent, "a label is deleted");
    using (HttpResponseMessage after = await ApiGet(address, prefix + "/issues/" + planned, readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await after.Content.ReadAsStreamAsync());
        Check(after.IsSuccessStatusCode && json.RootElement.GetProperty("labels").GetArrayLength() == 0,
            "deleting a label removes it from everything it was on");
    }
}

/// <summary>
/// Code owners, the approval rule built on them, rebasing a proposal, and squashing a merge.
/// </summary>
async Task CheckReviewOptions(string address, AuthenticatedIdentity alice, string bobUsername, string reporterUsername)
{
    long projectId;
    string aliceApi;
    string bobApi;
    string reporterApi;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo created = await store.CreateProjectWithRepositoryAsync(alice, "options", "Review options",
            (project, cancellation) => { cancellation.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; });
        projectId = created.Id;
        UserAccount bob = Required(await store.FindActiveUserAsync(bobUsername), "bob account");
        UserAccount reporter = Required(await store.FindActiveUserAsync(reporterUsername), "reporter account");
        await store.SetMembershipAsync(alice, projectId, bob.Id, ProjectRole.Developer);
        await store.SetMembershipAsync(alice, projectId, reporter.Id, ProjectRole.Reporter);
        aliceApi = (await store.CreateTokenAsync(alice, "options-api", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        bobApi = (await store.CreateTokenAsync(new(bob.Id, bob.Username, false, IdentityKind.BrowserSession), "options-developer", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        reporterApi = (await store.CreateTokenAsync(new(reporter.Id, reporter.Username, false, IdentityKind.BrowserSession), "options-reporter", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
    }
    string prefix = "/api/v4/projects/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    string clone = Path.Combine(root, "options-clone");
    Directory.CreateDirectory(clone);
    string pushUrl = AuthenticatedUrl(address, "alice", aliceApi, "alice/options.git");
    await Git(clone, ["init", "--initial-branch=main"]);
    Directory.CreateDirectory(Path.Combine(clone, "docs"));
    File.WriteAllText(Path.Combine(clone, "CODEOWNERS"), "# who looks after what\ndocs/ @" + bobUsername + "\n*.cs @alice\n");
    File.WriteAllText(Path.Combine(clone, "docs", "guide.md"), "guide\n");
    File.WriteAllText(Path.Combine(clone, "other.txt"), "other\n");
    await Git(clone, ["add", "."]);
    await Git(clone, ["commit", "-m", "Base"]);
    await Git(clone, ["remote", "add", "origin", pushUrl]);
    await Git(clone, ["push", "origin", "main"]);
    // The proposal: two commits, one owned path and one not.
    await Git(clone, ["checkout", "-b", "feature"]);
    File.WriteAllText(Path.Combine(clone, "docs", "guide.md"), "guide, revised\n");
    await Git(clone, ["commit", "-am", "Revise the guide"]);
    File.WriteAllText(Path.Combine(clone, "other.txt"), "other, revised\n");
    await Git(clone, ["commit", "-am", "Revise the other file"]);
    await Git(clone, ["push", "origin", "feature"]);
    // main moves, so the proposal is a three-way merge and a rebase has something to do.
    await Git(clone, ["checkout", "main"]);
    File.WriteAllText(Path.Combine(clone, "main-only.txt"), "ahead\n");
    await Git(clone, ["add", "."]);
    await Git(clone, ["commit", "-m", "Move main"]);
    await Git(clone, ["push", "origin", "main"]);

    long iid;
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/merge_requests", aliceApi,
        new { source_branch = "feature", target_branch = "main", title = "Revise things" }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        iid = json.RootElement.GetProperty("iid").GetInt64();
        JsonElement owners = json.RootElement.GetProperty("code_owners");
        Check(created.StatusCode == HttpStatusCode.Created && owners.GetArrayLength() == 1 &&
            owners[0].GetProperty("path").GetString() == "docs/guide.md" &&
            owners[0].GetProperty("owners")[0].GetString() == bobUsername &&
            !json.RootElement.GetProperty("code_owner_approval_required").GetBoolean(),
            "a proposal reports who owns each changed path the target's CODEOWNERS names, and nothing for unowned paths");
    }
    using (HttpResponseMessage rule = await ApiPut(address, prefix + "/approvals", aliceApi, new { require_code_owner_approval = true }))
        Check(rule.StatusCode == HttpStatusCode.NoContent, "a Maintainer turns the code owner rule on");
    using (HttpResponseMessage refused = await ApiPut(address, prefix + "/approvals", bobApi, new { require_code_owner_approval = false }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "a Developer cannot turn it off");
    using (HttpResponseMessage blocked = await ApiPut(address, prefix + "/merge_requests/" + iid + "/merge", aliceApi, new { }))
    {
        string body = await blocked.Content.ReadAsStringAsync();
        Check(blocked.StatusCode == HttpStatusCode.Conflict && body.Contains("docs/guide.md", StringComparison.Ordinal),
            "a merge is refused while an owned path lacks its owner's approval, and names the path");
    }
    using (HttpResponseMessage reporter = await ApiPost(address, prefix + "/merge_requests/" + iid + "/approve", reporterApi, new { }))
        Check(reporter.StatusCode == HttpStatusCode.Forbidden, "a Reporter still cannot approve, owner or not");
    using (HttpResponseMessage owner = await ApiPost(address, prefix + "/merge_requests/" + iid + "/approve", bobApi, new { }))
        Check(owner.IsSuccessStatusCode, "the owner approves");
    using (HttpResponseMessage read = await ApiGet(address, prefix + "/merge_requests/" + iid, aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await read.Content.ReadAsStreamAsync());
        Check(read.IsSuccessStatusCode && json.RootElement.GetProperty("code_owner_approval_required").GetBoolean() &&
            json.RootElement.GetProperty("code_owner_approvals_missing").GetArrayLength() == 0 &&
            json.RootElement.GetProperty("merge_method").GetString() == "three_way",
            "with the owner's approval nothing is missing, and the proposal still merges three ways");
    }

    // Rebase: the branch is rewritten onto main, which supersedes the approval, because it is about
    // commits that are no longer on the branch.
    using (HttpResponseMessage refused = await ApiPut(address, prefix + "/merge_requests/" + iid + "/rebase", reporterApi, new { }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "rebasing needs the permission a push needs");
    string rebasedTip;
    using (HttpResponseMessage rebased = await ApiPut(address, prefix + "/merge_requests/" + iid + "/rebase", bobApi, new { }))
    {
        using JsonDocument json = JsonDocument.Parse(await rebased.Content.ReadAsStreamAsync());
        rebasedTip = json.RootElement.GetProperty("sha").GetString()!;
        JsonElement request = json.RootElement.GetProperty("merge_request");
        Check(rebased.IsSuccessStatusCode && json.RootElement.GetProperty("rewritten_commits").GetInt32() == 2 &&
            rebasedTip != json.RootElement.GetProperty("previous_sha").GetString() &&
            request.GetProperty("merge_method").GetString() == "fast_forward" &&
            !request.GetProperty("approved_by")[0].GetProperty("current").GetBoolean() &&
            request.GetProperty("code_owner_approvals_missing").GetArrayLength() == 1,
            "a rebase rewrites both commits onto main, makes the proposal a fast-forward, and supersedes the approval");
    }
    await Git(clone, ["fetch", "origin"]);
    Check((await Git(clone, ["rev-parse", "origin/feature"])).Trim() == rebasedTip, "real Git sees the rebased branch where the API said it was");
    Check((await Git(clone, ["log", "--format=%s", "origin/main..origin/feature"])).Trim().Split('\n').Length == 2 &&
        (await Git(clone, ["log", "-1", "--format=%an", "origin/feature"])).Trim().Length > 0,
        "the rebased branch carries both original commits, with their messages, on top of main");
    using (HttpResponseMessage again = await ApiPut(address, prefix + "/merge_requests/" + iid + "/rebase", bobApi, new { }))
    {
        using JsonDocument json = JsonDocument.Parse(await again.Content.ReadAsStreamAsync());
        Check(again.IsSuccessStatusCode && json.RootElement.GetProperty("rewritten_commits").GetInt32() == 0,
            "rebasing a branch already on the target rewrites nothing");
    }
    using (HttpResponseMessage owner = await ApiPost(address, prefix + "/merge_requests/" + iid + "/approve", bobApi, new { }))
        Check(owner.IsSuccessStatusCode, "the owner approves the rebased branch");

    // Squash: one commit lands on main, and the branch's two stay behind.
    using (HttpResponseMessage merged = await ApiPut(address, prefix + "/merge_requests/" + iid + "/merge", aliceApi,
        new { squash = true, squash_commit_message = "Revise things, squashed" }))
    {
        using JsonDocument json = JsonDocument.Parse(await merged.Content.ReadAsStreamAsync());
        Check(merged.IsSuccessStatusCode && json.RootElement.GetProperty("method").GetString() == "squash" &&
            json.RootElement.GetProperty("merge_commit_sha").GetString()!.Length == 40,
            "a squash merge lands as one commit");
    }
    await Git(clone, ["fetch", "origin"]);
    string squashed = (await Git(clone, ["rev-parse", "origin/main"])).Trim();
    Check((await Git(clone, ["rev-list", "--parents", "-n", "1", squashed])).Trim().Split(' ').Length == 2 &&
        (await Git(clone, ["log", "-1", "--format=%s", squashed])).Trim() == "Revise things, squashed",
        "the squash commit has one parent and the message it was given");
    await Git(clone, ["checkout", "-B", "verify", "origin/main"]);
    Check(File.ReadAllText(Path.Combine(clone, "docs", "guide.md")).Contains("revised", StringComparison.Ordinal) &&
        File.ReadAllText(Path.Combine(clone, "other.txt")).Contains("revised", StringComparison.Ordinal) &&
        File.Exists(Path.Combine(clone, "main-only.txt")),
        "the squashed tree carries both branches' changes");
    Check((await RunGit(clone, ["merge-base", "--is-ancestor", rebasedTip, squashed])).ExitCode != 0,
        "the branch's own commits are not on main: a squash forgets them on purpose");
    Check((await RunGit(clone, ["fsck", "--strict", "--no-progress"])).ExitCode == 0, "the squashed objects pass git fsck");

    // What a rebase refuses, by name.
    await Git(clone, ["checkout", "-b", "clash", "origin/main"]);
    File.WriteAllText(Path.Combine(clone, "other.txt"), "clash\n");
    await Git(clone, ["commit", "-am", "Clash"]);
    await Git(clone, ["push", "origin", "clash"]);
    await Git(clone, ["checkout", "-B", "main", "origin/main"]);
    File.WriteAllText(Path.Combine(clone, "other.txt"), "main again\n");
    await Git(clone, ["commit", "-am", "Main changes other"]);
    await Git(clone, ["push", "origin", "main"]);
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/merge_requests", aliceApi,
        new { source_branch = "clash", target_branch = "main", title = "Clash" }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        long clash = json.RootElement.GetProperty("iid").GetInt64();
        using HttpResponseMessage refused = await ApiPut(address, prefix + "/merge_requests/" + clash + "/rebase", aliceApi, new { });
        string body = await refused.Content.ReadAsStringAsync();
        Check(refused.StatusCode == HttpStatusCode.Conflict && body.Contains("other.txt", StringComparison.Ordinal),
            "a rebase that would have to merge a file is refused and names the file");
    }
    await Git(clone, ["checkout", "-b", "side", "origin/main"]);
    File.WriteAllText(Path.Combine(clone, "side.txt"), "side\n");
    await Git(clone, ["add", "."]);
    await Git(clone, ["commit", "-m", "Side"]);
    await Git(clone, ["checkout", "-b", "merged", "origin/main"]);
    File.WriteAllText(Path.Combine(clone, "merged.txt"), "merged\n");
    await Git(clone, ["add", "."]);
    await Git(clone, ["commit", "-m", "Merged work"]);
    await Git(clone, ["merge", "--no-ff", "-m", "Merge side", "side"]);
    await Git(clone, ["push", "origin", "merged"]);
    await Git(clone, ["checkout", "-B", "main", "origin/main"]);
    File.WriteAllText(Path.Combine(clone, "main-only.txt"), "ahead again\n");
    await Git(clone, ["commit", "-am", "Main moves again"]);
    await Git(clone, ["push", "origin", "main"]);
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/merge_requests", aliceApi,
        new { source_branch = "merged", target_branch = "main", title = "With a merge commit" }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        long withMerge = json.RootElement.GetProperty("iid").GetInt64();
        using HttpResponseMessage refused = await ApiPut(address, prefix + "/merge_requests/" + withMerge + "/rebase", aliceApi, new { });
        string body = await refused.Content.ReadAsStringAsync();
        Check(refused.StatusCode == HttpStatusCode.Conflict && body.Contains("merge commit", StringComparison.Ordinal),
            "a branch holding a merge commit is not rebased here, and the refusal says why");
    }
    using (HttpResponseMessage rule = await ApiPut(address, prefix + "/approvals", aliceApi, new { require_code_owner_approval = false }))
        Check(rule.StatusCode == HttpStatusCode.NoContent, "the code owner rule can be turned off again");
}

/// <summary>
/// Releases: notes, links and files on a tag, readable wherever the repository is and published by
/// whoever could push.
/// </summary>
async Task CheckReleases(string address, AuthenticatedIdentity alice, string reporterUsername)
{
    long projectId;
    string aliceApi;
    string readApi;
    string reporterApi;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo created = await store.CreateProjectWithRepositoryAsync(alice, "ship", "Shipping",
            (project, cancellation) => { cancellation.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; });
        projectId = created.Id;
        UserAccount reporter = Required(await store.FindActiveUserAsync(reporterUsername), "reporter account");
        await store.SetMembershipAsync(alice, projectId, reporter.Id, ProjectRole.Reporter);
        aliceApi = (await store.CreateTokenAsync(alice, "ship-api", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        readApi = (await store.CreateTokenAsync(alice, "ship-read", TokenScope.ReadApi, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        reporterApi = (await store.CreateTokenAsync(new(reporter.Id, reporter.Username, false, IdentityKind.BrowserSession), "ship-reporter", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
    }
    string prefix = "/api/v4/projects/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    string clone = Path.Combine(root, "ship-clone");
    Directory.CreateDirectory(clone);
    await Git(clone, ["init", "--initial-branch=main"]);
    File.WriteAllText(Path.Combine(clone, "ship.txt"), "ready\n");
    await Git(clone, ["add", "."]);
    await Git(clone, ["commit", "-m", "Ready"]);
    await Git(clone, ["remote", "add", "origin", AuthenticatedUrl(address, "alice", aliceApi, "alice/ship.git")]);
    await Git(clone, ["push", "origin", "main"]);
    using (HttpResponseMessage tagged = await ApiPost(address, prefix + "/repository/tags", aliceApi, new { tag_name = "v1.0", @ref = "main" }))
        Check(tagged.StatusCode == HttpStatusCode.Created, "a tag is created to release");

    using (HttpResponseMessage refused = await ApiPost(address, prefix + "/releases", reporterApi, new { tag_name = "v1.0", name = "First" }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "publishing a release needs the permission to push");
    using (HttpResponseMessage missing = await ApiPost(address, prefix + "/releases", aliceApi, new { tag_name = "v9.9", name = "Nothing" }))
        Check(missing.StatusCode == HttpStatusCode.BadRequest, "a release needs a tag that exists");
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/releases", aliceApi,
        new { tag_name = "v1.0", name = "First light", description = "The first release." }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        Check(created.StatusCode == HttpStatusCode.Created && json.RootElement.GetProperty("name").GetString() == "First light" &&
            json.RootElement.GetProperty("tag_exists").GetBoolean() &&
            json.RootElement.GetProperty("commit").GetProperty("id").GetString()!.Length == 40 &&
            json.RootElement.GetProperty("assets").GetProperty("sources").GetArrayLength() == 2,
            "a release is created on a tag, with the commit it names and the two archives of it");
    }
    using (HttpResponseMessage duplicate = await ApiPost(address, prefix + "/releases", aliceApi, new { tag_name = "v1.0", name = "Again" }))
        Check(duplicate.StatusCode == HttpStatusCode.Conflict, "one release per tag");
    using (HttpResponseMessage hidden = await ApiGet(address, prefix + "/releases", null))
        Check(hidden.StatusCode == HttpStatusCode.NotFound, "a private project's releases are concealed from a stranger");
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
        await store.SetProjectVisibilityAsync(alice, projectId, ProjectVisibility.Public);
    using (HttpResponseMessage open = await ApiGet(address, prefix + "/releases", null))
    {
        using JsonDocument json = JsonDocument.Parse(await open.Content.ReadAsStreamAsync());
        Check(open.IsSuccessStatusCode && json.RootElement.GetArrayLength() == 1, "a public project's releases are readable with no credential");
    }
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
        await store.SetProjectVisibilityAsync(alice, projectId, ProjectVisibility.Private);

    string release = prefix + "/releases/v1.0";
    using (HttpResponseMessage link = await ApiPost(address, release + "/assets/links", aliceApi, new { name = "Runbook", url = "https://example.invalid/runbook", link_type = "runbook" }))
    {
        using JsonDocument json = JsonDocument.Parse(await link.Content.ReadAsStreamAsync());
        Check(link.StatusCode == HttpStatusCode.Created && json.RootElement.GetProperty("assets").GetProperty("links")[0].GetProperty("link_type").GetString() == "runbook",
            "a link is added to a release");
    }
    using (HttpResponseMessage bad = await ApiPost(address, release + "/assets/links", aliceApi, new { name = "Odd", url = "ftp://example.invalid/x" }))
        Check(bad.StatusCode == HttpStatusCode.BadRequest, "a link is an http or https address");
    using (HttpResponseMessage again = await ApiPost(address, release + "/assets/links", aliceApi, new { name = "Runbook", url = "https://example.invalid/other" }))
        Check(again.StatusCode == HttpStatusCode.Conflict, "link names are unique on a release");

    byte[] build = Encoding.UTF8.GetBytes("pretend this is a build\n");
    string sha = Convert.ToHexStringLower(SHA256.HashData(build));
    using (HttpResponseMessage refused = await ApiPutBytes(address, release + "/assets/files/build.bin", reporterApi, build, "application/octet-stream"))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "a Reporter cannot attach a file");
    using (HttpResponseMessage stored = await ApiPutBytes(address, release + "/assets/files/build.bin", aliceApi, build, "application/octet-stream"))
    {
        string storedBody = await stored.Content.ReadAsStringAsync();
        Check(stored.StatusCode == HttpStatusCode.Created, "attaching a file answers 201 rather than " + ((int)stored.StatusCode) + ": " + storedBody);
        using JsonDocument json = JsonDocument.Parse(storedBody);
        JsonElement files = json.RootElement.GetProperty("assets").GetProperty("files");
        Check(stored.StatusCode == HttpStatusCode.Created && files.GetArrayLength() == 1 &&
            files[0].GetProperty("sha256").GetString() == sha && files[0].GetProperty("size").GetInt64() == build.Length,
            "a file is attached as the request body, hashed and measured");
    }
    using (HttpResponseMessage download = await ApiGet(address, release + "/assets/files/build.bin", readApi))
    {
        byte[] bytes = await download.Content.ReadAsByteArrayAsync();
        Check(download.IsSuccessStatusCode && bytes.AsSpan().SequenceEqual(build) &&
            download.Content.Headers.ContentType?.MediaType == "application/octet-stream" &&
            download.Content.Headers.ContentDisposition?.DispositionType == "attachment" &&
            download.Headers.TryGetValues("X-Content-Type-Options", out IEnumerable<string>? sniff) && sniff.Contains("nosniff"),
            "the file downloads byte for byte, as an attachment of unknown type");
    }
    byte[] rebuilt = Encoding.UTF8.GetBytes("a newer build\n");
    using (HttpResponseMessage replaced = await ApiPutBytes(address, release + "/assets/files/build.bin", aliceApi, rebuilt, "application/octet-stream"))
    {
        using JsonDocument json = JsonDocument.Parse(await replaced.Content.ReadAsStreamAsync());
        JsonElement files = json.RootElement.GetProperty("assets").GetProperty("files");
        Check(replaced.StatusCode == HttpStatusCode.Created && files.GetArrayLength() == 1 &&
            files[0].GetProperty("size").GetInt64() == rebuilt.Length, "re-uploading a file of the same name replaces it");
    }
    using (HttpResponseMessage removed = await ApiDelete(address, release + "/assets/files/build.bin", aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await removed.Content.ReadAsStreamAsync());
        Check(removed.IsSuccessStatusCode && json.RootElement.GetProperty("assets").GetProperty("files").GetArrayLength() == 0, "a file is removed");
    }
    using (HttpResponseMessage renamed = await ApiPut(address, release, aliceApi, new { name = "First light, revised" }))
    {
        using JsonDocument json = JsonDocument.Parse(await renamed.Content.ReadAsStreamAsync());
        Check(renamed.IsSuccessStatusCode && json.RootElement.GetProperty("name").GetString() == "First light, revised", "a release is edited");
    }

    // The tag goes; the release stays and says so.
    using (HttpResponseMessage untagged = await ApiDelete(address, prefix + "/repository/tags/v1.0", aliceApi))
        Check(untagged.StatusCode == HttpStatusCode.NoContent, "the tag is deleted");
    using (HttpResponseMessage after = await ApiGet(address, release, readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await after.Content.ReadAsStreamAsync());
        Check(after.IsSuccessStatusCode && !json.RootElement.GetProperty("tag_exists").GetBoolean() &&
            json.RootElement.GetProperty("commit").ValueKind == JsonValueKind.Null &&
            json.RootElement.GetProperty("assets").GetProperty("sources").GetArrayLength() == 0,
            "a release outlives its tag and reports that the tag is gone");
    }
    using (HttpResponseMessage deleted = await ApiDelete(address, release, aliceApi))
        Check(deleted.StatusCode == HttpStatusCode.NoContent, "a release is deleted");
    using (HttpResponseMessage gone = await ApiGet(address, release, readApi))
        Check(gone.StatusCode == HttpStatusCode.NotFound, "and is gone");
}

/// <summary>
/// The wiki with its history, snippets, and time tracking on an issue.
/// </summary>
async Task CheckKnowledge(string address, AuthenticatedIdentity alice, string bobUsername, string reporterUsername)
{
    long projectId;
    string aliceApi;
    string developerApi;
    string reporterApi;
    string readApi;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo created = await store.CreateProjectWithRepositoryAsync(alice, "lore", "Knowledge",
            (project, cancellation) => { cancellation.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; });
        projectId = created.Id;
        UserAccount bob = Required(await store.FindActiveUserAsync(bobUsername), "bob account");
        UserAccount reporter = Required(await store.FindActiveUserAsync(reporterUsername), "reporter account");
        await store.SetMembershipAsync(alice, projectId, bob.Id, ProjectRole.Developer);
        await store.SetMembershipAsync(alice, projectId, reporter.Id, ProjectRole.Reporter);
        aliceApi = (await store.CreateTokenAsync(alice, "lore-api", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        readApi = (await store.CreateTokenAsync(alice, "lore-read", TokenScope.ReadApi, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        developerApi = (await store.CreateTokenAsync(new(bob.Id, bob.Username, false, IdentityKind.BrowserSession), "lore-developer", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        reporterApi = (await store.CreateTokenAsync(new(reporter.Id, reporter.Username, false, IdentityKind.BrowserSession), "lore-reporter", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
    }
    string prefix = "/api/v4/projects/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Wiki.
    using (HttpResponseMessage refused = await ApiPost(address, prefix + "/wikis", reporterApi, new { title = "Home", content = "hello" }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "writing the wiki needs the permission to push");
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/wikis", developerApi,
        new { title = "Getting Started!", content = "# Welcome\n\nSome *prose* and <script>alert(1)</script>.", message = "First draft" }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        string html = json.RootElement.GetProperty("html").GetString()!;
        Check(created.StatusCode == HttpStatusCode.Created && json.RootElement.GetProperty("slug").GetString() == "getting-started" &&
            html.Contains("<h1", StringComparison.Ordinal) && html.Contains("<em>prose</em>", StringComparison.Ordinal) &&
            !html.Contains("<script", StringComparison.Ordinal) && json.RootElement.GetProperty("versions").GetInt64() == 1,
            "a page is created with an address from its title, rendered under the README's rules");
    }
    using (HttpResponseMessage clash = await ApiPost(address, prefix + "/wikis", developerApi, new { title = "getting started", content = "again" }))
        Check(clash.StatusCode == HttpStatusCode.Conflict, "two titles that make one address are refused");
    using (HttpResponseMessage edited = await ApiPut(address, prefix + "/wikis/getting-started", developerApi, new { content = "# Welcome\n\nRevised.", message = "Second draft" }))
    {
        using JsonDocument json = JsonDocument.Parse(await edited.Content.ReadAsStreamAsync());
        Check(edited.IsSuccessStatusCode && json.RootElement.GetProperty("versions").GetInt64() == 2 &&
            json.RootElement.GetProperty("updated_by").GetString() == bobUsername, "an edit keeps the previous text as a version");
    }
    long firstVersion;
    using (HttpResponseMessage versions = await ApiGet(address, prefix + "/wikis/getting-started/versions", readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await versions.Content.ReadAsStreamAsync());
        firstVersion = json.RootElement[1].GetProperty("id").GetInt64();
        Check(versions.IsSuccessStatusCode && json.RootElement.GetArrayLength() == 2 &&
            json.RootElement[0].GetProperty("message").GetString() == "Second draft", "versions list newest first with their messages");
    }
    using (HttpResponseMessage old = await ApiGet(address, prefix + "/wikis/getting-started?version=" + firstVersion, readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await old.Content.ReadAsStreamAsync());
        Check(old.IsSuccessStatusCode && json.RootElement.GetProperty("content").GetString()!.Contains("prose", StringComparison.Ordinal),
            "an earlier version is read back as it was");
    }
    using (HttpResponseMessage listing = await ApiGet(address, prefix + "/wikis", readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await listing.Content.ReadAsStreamAsync());
        Check(listing.IsSuccessStatusCode && json.RootElement.GetArrayLength() == 1 && json.RootElement[0].GetProperty("content").GetString() == "",
            "a listing names the pages without their content");
    }
    using (HttpResponseMessage hidden = await ApiGet(address, prefix + "/wikis", null))
        Check(hidden.StatusCode == HttpStatusCode.NotFound, "a private project's wiki is concealed from a stranger");
    using (HttpResponseMessage deleted = await ApiDelete(address, prefix + "/wikis/getting-started", developerApi))
        Check(deleted.StatusCode == HttpStatusCode.NoContent, "a page is deleted with its history");

    // Snippets.
    long snippetId;
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/snippets", reporterApi,
        new { title = "Handy query", file_name = "query.sql", content = "SELECT 1;\n", description = "For later." }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        snippetId = json.RootElement.GetProperty("id").GetInt64();
        Check(created.StatusCode == HttpStatusCode.Created && json.RootElement.GetProperty("file_name").GetString() == "query.sql" &&
            json.RootElement.GetProperty("author").GetProperty("username").GetString() == reporterUsername,
            "a Reporter creates a snippet, like a comment with a file");
    }
    using (HttpResponseMessage slash = await ApiPost(address, prefix + "/snippets", reporterApi, new { title = "Bad", file_name = "a/b.txt", content = "x" }))
        Check(slash.StatusCode == HttpStatusCode.BadRequest, "a snippet's file name is one path segment");
    using (HttpResponseMessage refused = await ApiPut(address, prefix + "/snippets/" + snippetId, developerApi, new { title = "Rewritten" }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "a Developer cannot rewrite another author's snippet");
    using (HttpResponseMessage edited = await ApiPut(address, prefix + "/snippets/" + snippetId, aliceApi, new { content = "SELECT 2;\n" }))
        Check(edited.IsSuccessStatusCode, "a Maintainer can");
    using (HttpResponseMessage raw = await ApiGet(address, prefix + "/snippets/" + snippetId + "/raw", readApi))
    {
        string body = await raw.Content.ReadAsStringAsync();
        Check(raw.IsSuccessStatusCode && body == "SELECT 2;\n" && raw.Content.Headers.ContentType?.MediaType == "application/octet-stream" &&
            raw.Content.Headers.ContentDisposition?.FileName?.Trim('"') == "query.sql",
            "the raw snippet is served as a download named for its file");
    }
    using (HttpResponseMessage listing = await ApiGet(address, prefix + "/snippets", readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await listing.Content.ReadAsStreamAsync());
        Check(listing.IsSuccessStatusCode && json.RootElement.GetArrayLength() == 1 && json.RootElement[0].GetProperty("content").ValueKind == JsonValueKind.Null,
            "a listing names the snippets without their content");
    }
    using (HttpResponseMessage deleted = await ApiDelete(address, prefix + "/snippets/" + snippetId, reporterApi))
        Check(deleted.StatusCode == HttpStatusCode.NoContent, "the author deletes their snippet");

    // Time tracking.
    long issue;
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/issues", reporterApi, new { title = "Takes a while" }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        issue = json.RootElement.GetProperty("iid").GetInt64();
    }
    string time = prefix + "/issues/" + issue;
    using (HttpResponseMessage estimated = await ApiPost(address, time + "/time_estimate", reporterApi, new { duration = "1d 2h" }))
    {
        using JsonDocument json = JsonDocument.Parse(await estimated.Content.ReadAsStreamAsync());
        Check(estimated.IsSuccessStatusCode && json.RootElement.GetProperty("time_estimate").GetInt64() == 10 * 3600 &&
            json.RootElement.GetProperty("human_time_estimate").GetString() == "1d 2h",
            "the author estimates their issue in GitLab's vocabulary, a day being eight hours");
    }
    using (HttpResponseMessage bad = await ApiPost(address, time + "/time_estimate", reporterApi, new { duration = "soonish" }))
        Check(bad.StatusCode == HttpStatusCode.BadRequest, "a duration that is not one is refused");
    using (HttpResponseMessage spent = await ApiPost(address, time + "/add_spent_time", developerApi, new { duration = "3h 30m", summary = "Dug in" }))
    {
        using JsonDocument json = JsonDocument.Parse(await spent.Content.ReadAsStreamAsync());
        Check(spent.IsSuccessStatusCode && json.RootElement.GetProperty("total_time_spent").GetInt64() == 12600 &&
            json.RootElement.GetProperty("human_total_time_spent").GetString() == "3h 30m", "a member records time spent");
    }
    using (HttpResponseMessage back = await ApiPost(address, time + "/add_spent_time", developerApi, new { duration = "-30m" }))
    {
        using JsonDocument json = JsonDocument.Parse(await back.Content.ReadAsStreamAsync());
        Check(back.IsSuccessStatusCode && json.RootElement.GetProperty("total_time_spent").GetInt64() == 10800, "a negative duration takes time back");
    }
    using (HttpResponseMessage below = await ApiPost(address, time + "/add_spent_time", developerApi, new { duration = "-5h" }))
        Check(below.StatusCode == HttpStatusCode.BadRequest, "but not below zero");
    using (HttpResponseMessage entries = await ApiGet(address, time + "/time_entries", readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await entries.Content.ReadAsStreamAsync());
        Check(entries.IsSuccessStatusCode && json.RootElement.GetArrayLength() == 2 &&
            json.RootElement[1].GetProperty("summary").GetString() == "Dug in" && json.RootElement[1].GetProperty("username").GetString() == bobUsername,
            "the ledger says who spent what and why");
    }
    using (HttpResponseMessage onIssue = await ApiGet(address, time, readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await onIssue.Content.ReadAsStreamAsync());
        Check(onIssue.IsSuccessStatusCode && json.RootElement.GetProperty("time_stats").GetProperty("total_time_spent").GetInt64() == 10800,
            "the issue carries its time totals");
    }
    using (HttpResponseMessage refused = await ApiPost(address, time + "/reset_spent_time", developerApi, new { }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "clearing everybody's time needs the author or a Maintainer");
    using (HttpResponseMessage reset = await ApiPost(address, time + "/reset_spent_time", aliceApi, new { }))
    {
        using JsonDocument json = JsonDocument.Parse(await reset.Content.ReadAsStreamAsync());
        Check(reset.IsSuccessStatusCode && json.RootElement.GetProperty("total_time_spent").GetInt64() == 0, "a Maintainer clears it");
    }
}

/// <summary>
/// Search: a project's code, commits, issues, proposals and wiki, and the server's projects and issues
/// across whatever the caller may see.
/// </summary>
async Task CheckSearch(string address, AuthenticatedIdentity alice, string strangerUsername)
{
    long projectId;
    string aliceApi;
    string strangerApi;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo created = await store.CreateProjectWithRepositoryAsync(alice, "haystack", "Haystack",
            (project, cancellation) => { cancellation.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; }, "A place to lose a needle");
        projectId = created.Id;
        UserAccount stranger = Required(await store.FindActiveUserAsync(strangerUsername), "stranger account");
        aliceApi = (await store.CreateTokenAsync(alice, "find-api", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        strangerApi = (await store.CreateTokenAsync(new(stranger.Id, stranger.Username, false, IdentityKind.BrowserSession), "find-stranger", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
    }
    string prefix = "/api/v4/projects/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    string clone = Path.Combine(root, "haystack-clone");
    Directory.CreateDirectory(clone);
    await Git(clone, ["init", "--initial-branch=main"]);
    Directory.CreateDirectory(Path.Combine(clone, "src"));
    File.WriteAllText(Path.Combine(clone, "src", "lamp.cs"), "// the Needle is here\nclass Lamp { }\n");
    File.WriteAllText(Path.Combine(clone, "notes.txt"), "nothing to see\n");
    File.WriteAllBytes(Path.Combine(clone, "blob.bin"), [0, 1, 2, (byte)'n', (byte)'e', (byte)'e', (byte)'d', (byte)'l', (byte)'e', 0]);
    await Git(clone, ["add", "."]);
    await Git(clone, ["commit", "-m", "Hide a needle in the haystack"]);
    await Git(clone, ["remote", "add", "origin", AuthenticatedUrl(address, "alice", aliceApi, "alice/haystack.git")]);
    await Git(clone, ["push", "origin", "main"]);
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/issues", aliceApi, new { title = "Lost a needle", description = "Somewhere in the haystack." }))
        Check(created.StatusCode == HttpStatusCode.Created, "an issue to find is opened");
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/issues", aliceApi, new { title = "Secret needle", confidential = true }))
        Check(created.StatusCode == HttpStatusCode.Created, "a confidential issue to not find is opened");
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/wikis", aliceApi, new { title = "Sewing", content = "Keep the needle in the cushion." }))
        Check(created.StatusCode == HttpStatusCode.Created, "a wiki page to find is written");

    using (HttpResponseMessage found = await ApiGet(address, prefix + "/search?scope=blobs&search=needle", aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await found.Content.ReadAsStreamAsync());
        JsonElement blobs = json.RootElement.GetProperty("blobs");
        Check(found.IsSuccessStatusCode && blobs.GetArrayLength() == 1 && blobs[0].GetProperty("path").GetString() == "src/lamp.cs" &&
            blobs[0].GetProperty("startline").GetInt32() == 1 && blobs[0].GetProperty("data").GetString()!.Contains("Needle", StringComparison.Ordinal),
            "code search finds the line, without regard to case, and skips the binary file");
    }
    using (HttpResponseMessage found = await ApiGet(address, prefix + "/search?scope=commits&search=haystack", aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await found.Content.ReadAsStreamAsync());
        Check(found.IsSuccessStatusCode && json.RootElement.GetProperty("commits").GetArrayLength() == 1, "commit search finds the message");
    }
    using (HttpResponseMessage found = await ApiGet(address, prefix + "/search?scope=issues&search=needle", aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await found.Content.ReadAsStreamAsync());
        Check(found.IsSuccessStatusCode && json.RootElement.GetProperty("issues").GetArrayLength() == 2, "issue search finds both for a member");
    }
    using (HttpResponseMessage found = await ApiGet(address, prefix + "/search?scope=wiki_blobs&search=cushion", aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await found.Content.ReadAsStreamAsync());
        Check(found.IsSuccessStatusCode && json.RootElement.GetProperty("wiki_blobs").GetArrayLength() == 1 &&
            json.RootElement.GetProperty("wiki_blobs")[0].GetProperty("slug").GetString() == "sewing", "wiki search finds the page by its content");
    }
    using (HttpResponseMessage bad = await ApiGet(address, prefix + "/search?scope=users&search=needle", aliceApi))
        Check(bad.StatusCode == HttpStatusCode.BadRequest, "an unknown scope is refused");
    using (HttpResponseMessage bad = await ApiGet(address, prefix + "/search?scope=blobs&search=", aliceApi))
        Check(bad.StatusCode == HttpStatusCode.BadRequest, "an empty search is refused");

    // Across the server.
    using (HttpResponseMessage found = await ApiGet(address, "/api/v4/search?scope=projects&search=haystack", aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await found.Content.ReadAsStreamAsync());
        Check(found.IsSuccessStatusCode && json.RootElement.GetProperty("projects").EnumerateArray()
            .Any(project => project.GetProperty("path").GetString() == "haystack"), "a global search finds a project by name");
    }
    using (HttpResponseMessage found = await ApiGet(address, "/api/v4/search?scope=issues&search=needle", aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await found.Content.ReadAsStreamAsync());
        Check(found.IsSuccessStatusCode && json.RootElement.GetProperty("issues").GetArrayLength() == 2, "a global search finds an owner's issues, confidential included");
    }
    using (HttpResponseMessage found = await ApiGet(address, "/api/v4/search?scope=issues&search=needle", strangerApi))
    {
        using JsonDocument json = JsonDocument.Parse(await found.Content.ReadAsStreamAsync());
        Check(found.IsSuccessStatusCode && json.RootElement.GetProperty("issues").GetArrayLength() == 0, "a stranger's global search finds nothing in a private project");
    }
    using (HttpResponseMessage anonymous = await ApiGet(address, "/api/v4/search?scope=issues&search=needle", null))
    {
        // With publishing on, a stranger may search and sees nothing of a private project; with it off, the
        // search itself is refused, because there is nothing a stranger could be shown.
        if (publicBrowsing)
        {
            using JsonDocument json = JsonDocument.Parse(await anonymous.Content.ReadAsStreamAsync());
            Check(anonymous.IsSuccessStatusCode && json.RootElement.GetProperty("issues").GetArrayLength() == 0,
                "an anonymous global search sees nothing of a private project");
        }
        else Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "an anonymous global search needs public browsing, which this server has off");
    }
    using (HttpResponseMessage hidden = await ApiGet(address, prefix + "/search?scope=blobs&search=needle", strangerApi))
        Check(hidden.StatusCode == HttpStatusCode.NotFound, "a stranger cannot search a private project");
}

/// <summary>
/// Forks: a stranger copies a public project into their own namespace, pushes to the copy, proposes the
/// branch to the original, talks about their own proposal and nothing else, and the original's maintainer
/// merges it without ever reading the fork at merge time.
/// </summary>
async Task<string> CheckForks(string address, AuthenticatedIdentity alice, AuthenticatedIdentity administrator)
{
    bool wasPublic = publicBrowsing;
    if (!publicBrowsing)
    {
        await StopServer();
        publicBrowsing = true;
        address = await StartServer();
    }
    long upstreamId;
    string aliceApi;
    string erinApi;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo created = await store.CreateProjectWithRepositoryAsync(alice, "upstream", "Upstream",
            (project, cancellation) => { cancellation.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; });
        upstreamId = created.Id;
        await store.SetProjectVisibilityAsync(alice, upstreamId, ProjectVisibility.Public);
        UserAccount erin = await store.CreateUserAsync(administrator, "erin", Password());
        AuthenticatedIdentity erinIdentity = new(erin.Id, erin.Username, false, IdentityKind.BrowserSession);
        aliceApi = (await store.CreateTokenAsync(alice, "fork-owner", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        erinApi = (await store.CreateTokenAsync(erinIdentity, "fork-stranger", TokenScope.Api | TokenScope.WriteRepository, DateTimeOffset.UtcNow.AddHours(1))).Secret;
    }
    string upstream = "/api/v4/projects/" + upstreamId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    string clone = Path.Combine(root, "upstream-clone");
    Directory.CreateDirectory(clone);
    await Git(clone, ["init", "--initial-branch=main"]);
    File.WriteAllText(Path.Combine(clone, "up.txt"), "upstream\n");
    await Git(clone, ["add", "."]);
    await Git(clone, ["commit", "-m", "Upstream base"]);
    await Git(clone, ["remote", "add", "origin", AuthenticatedUrl(address, "alice", aliceApi, "alice/upstream.git")]);
    await Git(clone, ["push", "origin", "main"]);
    using (HttpResponseMessage created = await ApiPost(address, upstream + "/issues", aliceApi, new { title = "Not erin's" }))
        Check(created.StatusCode == HttpStatusCode.Created, "an issue a stranger will not be able to comment on");

    // Erin cannot write to upstream, and can fork it.
    await Git(clone, ["remote", "add", "erin", AuthenticatedUrl(address, "erin", erinApi, "alice/upstream.git")]);
    await GitFails(clone, ["push", "erin", "main:refs/heads/erin-cannot"], "a stranger cannot push to a public project");
    long forkId;
    using (HttpResponseMessage forked = await ApiPost(address, upstream + "/fork", erinApi, new { }))
    {
        using JsonDocument json = JsonDocument.Parse(await forked.Content.ReadAsStreamAsync());
        forkId = json.RootElement.GetProperty("id").GetInt64();
        Check(forked.StatusCode == HttpStatusCode.Created &&
            json.RootElement.GetProperty("path_with_namespace").GetString() == "erin/upstream" &&
            json.RootElement.GetProperty("forked_from_project").GetProperty("id").GetInt64() == upstreamId &&
            json.RootElement.GetProperty("visibility").GetString() == "public",
            "a stranger forks a public project into their own namespace, keeping its visibility");
    }
    using (HttpResponseMessage again = await ApiPost(address, upstream + "/fork", erinApi, new { }))
        Check(again.StatusCode == HttpStatusCode.Conflict, "one fork per person per project");
    using (HttpResponseMessage own = await ApiPost(address, upstream + "/fork", aliceApi, new { }))
        Check(own.StatusCode == HttpStatusCode.Conflict, "a project is not forked into the namespace it lives in");
    using (HttpResponseMessage read = await ApiGet(address, upstream, aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await read.Content.ReadAsStreamAsync());
        Check(read.IsSuccessStatusCode && json.RootElement.GetProperty("forks_count").GetInt64() == 1, "the original counts its forks");
    }
    using (HttpResponseMessage forks = await ApiGet(address, upstream + "/forks", null))
    {
        using JsonDocument json = JsonDocument.Parse(await forks.Content.ReadAsStreamAsync());
        Check(forks.IsSuccessStatusCode && json.RootElement.GetArrayLength() == 1 &&
            json.RootElement[0].GetProperty("path_with_namespace").GetString() == "erin/upstream", "and lists the public ones to anybody");
    }

    // The fork is a real repository with the original's content, and Erin can push to it.
    string forkClone = Path.Combine(root, "fork-clone");
    await Git(root, ["clone", AuthenticatedUrl(address, "erin", erinApi, "erin/upstream.git"), "fork-clone"]);
    Check(File.ReadAllText(Path.Combine(forkClone, "up.txt")).Trim() == "upstream", "the fork clones with the original's content");
    await Git(forkClone, ["checkout", "-b", "fix"]);
    File.WriteAllText(Path.Combine(forkClone, "fix.txt"), "from the fork\n");
    await Git(forkClone, ["add", "."]);
    await Git(forkClone, ["commit", "-m", "A fix from outside"]);
    await Git(forkClone, ["push", "origin", "fix"]);

    long iid;
    using (HttpResponseMessage proposed = await ApiPost(address, upstream + "/merge_requests", erinApi,
        new { source_project_id = forkId, source_branch = "fix", target_branch = "main", title = "A fix from outside" }))
    {
        using JsonDocument json = JsonDocument.Parse(await proposed.Content.ReadAsStreamAsync());
        iid = json.RootElement.GetProperty("iid").GetInt64();
        Check(proposed.StatusCode == HttpStatusCode.Created &&
            json.RootElement.GetProperty("source_project_id").GetInt64() == forkId &&
            json.RootElement.GetProperty("source_project_path").GetString() == "erin/upstream" &&
            json.RootElement.GetProperty("merge_method").GetString() == "fast_forward",
            "a stranger proposes a branch of their fork to the original, which already knows how it would merge");
    }
    await Git(clone, ["push", "origin", "main:refs/heads/other"]);
    using (HttpResponseMessage refused = await ApiPost(address, upstream + "/merge_requests", erinApi,
        new { source_branch = "other", target_branch = "main", title = "Not from a fork" }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "a stranger still cannot propose from the original's own branches");
    using (HttpResponseMessage talk = await ApiPost(address, upstream + "/merge_requests/" + iid + "/notes", erinApi, new { body = "Here is why." }))
        Check(talk.StatusCode == HttpStatusCode.Created, "the author of a proposal from a fork may talk about it");
    using (HttpResponseMessage refused = await ApiPost(address, upstream + "/issues/1/notes", erinApi, new { body = "And about this?" }))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "and about nothing else on the original");
    using (HttpResponseMessage changes = await ApiGet(address, upstream + "/merge_requests/" + iid + "/changes", aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await changes.Content.ReadAsStreamAsync());
        Check(changes.IsSuccessStatusCode && json.RootElement.GetProperty("files")[0].GetProperty("path").GetString() == "fix.txt",
            "the original's maintainer sees the fork's change as a diff");
    }
    string advertised = await Git(clone, ["ls-remote", "origin"]);
    Check(advertised.Contains("refs/merge-requests/" + iid + "/head", StringComparison.Ordinal),
        "the original advertises the proposal's tip under refs/merge-requests, as GitLab does");
    await GitFails(clone, ["push", "origin", "main:refs/merge-requests/" + iid + "/head"], "a client cannot push a proposal reference");
    using (HttpResponseMessage rebase = await ApiPut(address, upstream + "/merge_requests/" + iid + "/rebase", aliceApi, new { }))
        Check(rebase.StatusCode == HttpStatusCode.Conflict, "a proposal from a fork is not rebased by the original");

    // Erin pushes more to the fork; the proposal follows the branch.
    File.WriteAllText(Path.Combine(forkClone, "fix.txt"), "from the fork, revised\n");
    await Git(forkClone, ["commit", "-am", "Revise the fix"]);
    await Git(forkClone, ["push", "origin", "fix"]);
    using (HttpResponseMessage merged = await ApiPut(address, upstream + "/merge_requests/" + iid + "/merge", aliceApi, new { should_remove_source_branch = true }))
    {
        using JsonDocument json = JsonDocument.Parse(await merged.Content.ReadAsStreamAsync());
        Check(merged.IsSuccessStatusCode && json.RootElement.GetProperty("merge_request").GetProperty("state").GetString() == "merged" &&
            !json.RootElement.GetProperty("source_branch_removed").GetBoolean(),
            "the maintainer merges the fork's branch as it now stands, and the fork's branch is not theirs to remove");
    }
    await Git(clone, ["fetch", "origin"]);
    await Git(clone, ["checkout", "-B", "verify", "origin/main"]);
    Check(File.ReadAllText(Path.Combine(clone, "fix.txt")).Contains("revised", StringComparison.Ordinal), "the original has the fork's latest work");
    Check((await Git(forkClone, ["ls-remote", "origin", "refs/heads/fix"])).Contains("refs/heads/fix", StringComparison.Ordinal), "the fork's branch is untouched");
    Check((await RunGit(clone, ["fsck", "--strict", "--no-progress"])).ExitCode == 0, "the mirrored objects pass git fsck");

    if (!wasPublic)
    {
        await StopServer();
        publicBrowsing = false;
        address = await StartServer();
    }
    return address;
}

/// <summary>
/// Git LFS, driven by the real git-lfs client: a tracked file is pushed as a pointer plus an object, a
/// fresh clone gets the content back, and the raw route serves the content the pointer stands for.
/// </summary>
async Task CheckLargeFiles(string address, AuthenticatedIdentity alice)
{
    if ((await RunGit(root, ["lfs", "version"])).ExitCode != 0)
    {
        Console.WriteLine("SKIP: git-lfs is not installed, so the LFS transfer is exercised over HTTP only.");
    }
    long projectId;
    string aliceApi;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo created = await store.CreateProjectWithRepositoryAsync(alice, "heavy", "Heavy",
            (project, cancellation) => { cancellation.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; });
        projectId = created.Id;
        aliceApi = (await store.CreateTokenAsync(alice, "heavy-api", TokenScope.Api | TokenScope.WriteRepository, DateTimeOffset.UtcNow.AddHours(1))).Secret;
    }
    string remote = AuthenticatedUrl(address, "alice", aliceApi, "alice/heavy.git");
    byte[] payload = new byte[300 * 1024];
    RandomNumberGenerator.Fill(payload);
    string oid = Convert.ToHexStringLower(SHA256.HashData(payload));

    // The batch API by hand first, so a refusal is explained by the protocol rather than by the client.
    using (HttpResponseMessage batch = await ApiPostJson(address, "/alice/heavy.git/info/lfs/objects/batch", null,
        "{\"operation\":\"download\",\"objects\":[{\"oid\":\"" + oid + "\",\"size\":" + payload.Length + "}]}"))
        Check(batch.StatusCode == HttpStatusCode.Unauthorized, "an LFS batch on a private repository asks for credentials");
    using (HttpResponseMessage batch = await LfsBatch(address, "/alice/heavy.git/info/lfs/objects/batch", aliceApi, "upload", oid, payload.Length))
    {
        using JsonDocument json = JsonDocument.Parse(await batch.Content.ReadAsStreamAsync());
        JsonElement entry = json.RootElement.GetProperty("objects")[0];
        Check(batch.IsSuccessStatusCode && batch.Content.Headers.ContentType?.MediaType == "application/vnd.git-lfs+json" &&
            entry.GetProperty("actions").GetProperty("upload").GetProperty("href").GetString()!.EndsWith("/info/lfs/objects/" + oid, StringComparison.Ordinal),
            "an upload batch offers an upload action for an object the repository lacks");
    }
    using (HttpResponseMessage wrong = await LfsTransfer(address, "/alice/heavy.git/info/lfs/objects/" + oid, aliceApi, payload[..1000]))
        Check(wrong.StatusCode == HttpStatusCode.UnprocessableContent || wrong.StatusCode == HttpStatusCode.BadRequest,
            "bytes that do not hash to their oid are refused and not kept");
    using (HttpResponseMessage stored = await LfsTransfer(address, "/alice/heavy.git/info/lfs/objects/" + oid, aliceApi, payload))
        Check(stored.IsSuccessStatusCode, "the object is stored once its hash and size match");
    using (HttpResponseMessage batch = await LfsBatch(address, "/alice/heavy.git/info/lfs/objects/batch", aliceApi, "upload", oid, payload.Length))
    {
        using JsonDocument json = JsonDocument.Parse(await batch.Content.ReadAsStreamAsync());
        Check(batch.IsSuccessStatusCode && !json.RootElement.GetProperty("objects")[0].TryGetProperty("actions", out _),
            "an object already stored is offered no upload action");
    }
    using (HttpResponseMessage download = await LfsTransfer(address, "/alice/heavy.git/info/lfs/objects/" + oid, aliceApi, null))
        Check(download.IsSuccessStatusCode && (await download.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(payload),
            "the object downloads byte for byte");

    // Now the real client, end to end.
    if ((await RunGit(root, ["lfs", "version"])).ExitCode == 0)
    {
        string work = Path.Combine(root, "heavy-work");
        Directory.CreateDirectory(work);
        await Git(work, ["init", "--initial-branch=main"]);
        await Git(work, ["lfs", "install", "--local"]);
        await Git(work, ["lfs", "track", "*.bin"]);
        byte[] big = new byte[1024 * 1024];
        RandomNumberGenerator.Fill(big);
        File.WriteAllBytes(Path.Combine(work, "model.bin"), big);
        File.WriteAllText(Path.Combine(work, "readme.txt"), "with a large file\n");
        await Git(work, ["add", "."]);
        await Git(work, ["commit", "-m", "Add a large file"]);
        await Git(work, ["remote", "add", "origin", remote]);
        await Git(work, ["push", "origin", "main"]);
        string bigOid = Convert.ToHexStringLower(SHA256.HashData(big));
        Check(File.Exists(Path.Combine(repositoriesRoot, (await StorageIdOf(projectId))!, "lfs", bigOid[..2], bigOid[2..4], bigOid)),
            "git-lfs pushed the object beside the repository, content-addressed");

        string fresh = Path.Combine(root, "heavy-fresh");
        await Git(root, ["clone", remote, "heavy-fresh"]);
        // The suite runs Git with a private, empty global config, so the clone has no LFS filter until it is
        // installed there; a developer with `git lfs install` behind them gets this during the clone itself.
        await Git(fresh, ["lfs", "install", "--local"]);
        (int pullCode, string pullOut, string pullErr) = await RunGit(fresh, ["lfs", "pull"]);
        bool restored = File.ReadAllBytes(Path.Combine(fresh, "model.bin")).AsSpan().SequenceEqual(big);
        Check(restored, restored ? "a fresh clone gets the large file's content back through LFS"
            : "a fresh clone gets the large file's content back through LFS; git lfs pull exit " + pullCode + ": " + Redact(pullOut + pullErr));

        using (HttpResponseMessage raw = await ApiGet(address, "/api/v4/projects/" + projectId + "/repository/files/model.bin/raw", aliceApi))
            Check(raw.IsSuccessStatusCode && (await raw.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(big) &&
                raw.Headers.Contains("X-Graft-Lfs-Oid"),
                "the raw route serves the content a pointer stands for");
        using (HttpResponseMessage file = await ApiGet(address, "/api/v4/projects/" + projectId + "/repository/files/model.bin?ref=main", aliceApi))
        {
            using JsonDocument json = JsonDocument.Parse(await file.Content.ReadAsStreamAsync());
            Check(file.IsSuccessStatusCode && json.RootElement.GetProperty("size").GetInt64() < 200,
                "the file API still describes the pointer, which is what is in the tree");
        }
    }
}

async Task<HttpResponseMessage> LfsBatch(string address, string path, string? token, string operation, string oid, long size)
{
    HttpClientHandler handler = new() { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseProxy = false };
    HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };
    HttpRequestMessage request = new(HttpMethod.Post, path)
    {
        Content = new StringContent("{\"operation\":\"" + operation + "\",\"transfers\":[\"basic\"],\"objects\":[{\"oid\":\"" + oid + "\",\"size\":" + size + "}]}",
            Encoding.UTF8, "application/vnd.git-lfs+json")
    };
    if (token is not null) request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:" + token)));
    HttpResponseMessage response = await client.SendAsync(request);
    request.Dispose();
    client.Dispose();
    handler.Dispose();
    return response;
}

/// <summary>The basic transfer by hand, with the Basic credential git-lfs itself sends: a PUT of the bytes, or a GET when there are none.</summary>
async Task<HttpResponseMessage> LfsTransfer(string address, string path, string token, byte[]? content)
{
    HttpClientHandler handler = new() { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseProxy = false };
    HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };
    HttpRequestMessage request = new(content is null ? HttpMethod.Get : HttpMethod.Put, path);
    if (content is not null)
    {
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
    }
    request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:" + token)));
    HttpResponseMessage response = await client.SendAsync(request);
    request.Dispose();
    client.Dispose();
    handler.Dispose();
    return response;
}

async Task<string?> StorageIdOf(long projectId)
{
    await using GraftStore store = await GraftStore.OpenAsync(databasePath);
    return (await store.GetReadyProjectAsync(projectId))?.Project.StorageId;
}

/// <summary>
/// The Git engine beyond the bounded subset: a repository directory Git itself can open, a legacy directory
/// converted on first use, fetch negotiation that sends only what the client lacks, delta compression in
/// the packs Graft writes, protocol version 2, and housekeeping that packs everything with an index Git
/// agrees with byte for byte.
/// </summary>
async Task CheckGitEngine(string address, AuthenticatedIdentity alice)
{
    GitIdentity who = new("Graft Fixture", "fixture@example.invalid", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).ToOffset(TimeSpan.Zero));

    // A directory in the format Graft used before: raw content behind a type byte, and a reference snapshot.
    Repository authored = new();
    authored.CommitFiles("main", [new("readme.txt", "legacy layout\n"u8.ToArray()), new("a/b.txt", "nested\n"u8.ToArray())], who, "First");
    AuthoredCommit second = authored.CommitFiles("main", [new("readme.txt", "legacy layout, revised\n"u8.ToArray())], who, "Second");
    string legacy = Path.Combine(root, "legacy-layout");
    foreach ((string id, GitObject obj) in authored.Objects)
    {
        string path = Path.Combine(legacy, "objects", id[..2], id[2..]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream output = new(path, FileMode.CreateNew);
        output.WriteByte((byte)obj.Type);
        output.Write(obj.Data);
    }
    File.WriteAllText(Path.Combine(legacy, "refs.snapshot"), second.CommitId + " refs/heads/main\n", Encoding.ASCII);
    Repository migrated = new(new DurableRepositoryStorage(legacy));
    Check(migrated.Refs["refs/heads/main"] == second.CommitId && migrated.Objects.Count == authored.Objects.Count,
        "a repository in the previous layout opens and is converted");
    Check(!File.Exists(Path.Combine(legacy, "refs.snapshot")) && File.Exists(Path.Combine(legacy, "packed-refs")) &&
        File.ReadAllText(Path.Combine(legacy, "HEAD")).Trim() == "ref: refs/heads/main",
        "the conversion leaves packed-refs and HEAD where Git looks for them");
    await Git(legacy, ["fsck", "--full", "--strict"]);
    Check((await Git(legacy, ["rev-parse", "refs/heads/main"])).Trim() == second.CommitId, "Git itself opens the converted directory and reads its references");
    Check((await Git(legacy, ["cat-file", "-p", "refs/heads/main:readme.txt"])) == "legacy layout, revised\n", "Git reads a converted loose object");
    Check(new Repository(new DurableRepositoryStorage(legacy)).Objects.Count == authored.Objects.Count, "opening a converted directory again changes nothing");
    Check(migrated.Objects[second.CommitId].Data.AsSpan().SequenceEqual(authored.Objects[second.CommitId].Data), "a converted object reads back byte for byte");

    // A project pushed by a real client, with a file that grows so that its versions delta against each other.
    long projectId;
    string apiToken;
    string gitToken;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo created = await store.CreateProjectWithRepositoryAsync(alice, "engine", "Engine",
            (project, cancellation) => { cancellation.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; });
        projectId = created.Id;
        apiToken = (await store.CreateTokenAsync(alice, "engine-api", TokenScope.Api | TokenScope.ReadRepository | TokenScope.WriteRepository, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        gitToken = (await store.CreateTokenAsync(alice, "engine-git", TokenScope.ReadRepository | TokenScope.WriteRepository, DateTimeOffset.UtcNow.AddHours(1))).Secret;
    }
    string remote = AuthenticatedUrl(address, "alice", gitToken, "alice/engine.git");
    string repo = Path.Combine(repositoriesRoot, (await StorageIdOf(projectId))!);
    string work = Path.Combine(root, "engine-work");
    Directory.CreateDirectory(work);
    await Git(work, ["init", "--initial-branch=main"]);
    StringBuilder text = new();
    for (int line = 0; line < 120; line++) text.Append("line ").Append(line).Append(": the quick brown fox jumps over the lazy dog, again and again\n");
    List<string> commits = [];
    for (int round = 0; round < 4; round++)
    {
        text.Append("appended in round ").Append(round).Append('\n');
        File.WriteAllText(Path.Combine(work, "story.txt"), text.ToString());
        File.WriteAllText(Path.Combine(work, "round" + round + ".txt"), "round " + round + "\n");
        await Git(work, ["add", "."]);
        await Git(work, ["commit", "-m", "Round " + round]);
        commits.Add((await Git(work, ["rev-parse", "HEAD"])).Trim());
    }
    await Git(work, ["remote", "add", "origin", remote]);
    await Git(work, ["push", "origin", "main"]);
    string tip = commits[^1];
    await Git(repo, ["fsck", "--full", "--strict"]);
    Check((await Git(repo, ["symbolic-ref", "HEAD"])).Trim() == "refs/heads/main", "a pushed repository is a bare Git repository whose HEAD names the default branch");
    Check((await Git(repo, ["rev-parse", "refs/heads/main"])).Trim() == tip, "Git reads the pushed reference from packed-refs");
    Check((await Git(repo, ["cat-file", "-p", tip + ":story.txt"])) == text.ToString(), "Git reads a pushed loose object");

    // Negotiation by hand: a client that already has the first commit is sent only what came after it.
    int expectedNew = (await Git(work, ["rev-list", "--objects", commits[0] + ".." + tip])).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    byte[] request;
    using (MemoryStream building = new())
    {
        Packet.Write(building, "want " + tip + " multi_ack_detailed ofs-delta agent=graft-suite\n");
        Packet.Flush(building);
        Packet.Write(building, "have " + commits[0] + "\n");
        Packet.Write(building, "done\n");
        request = building.ToArray();
    }
    using (HttpResponseMessage response = await GitPost(address, "/alice/engine.git/git-upload-pack", gitToken, request, encoding: null))
    {
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        Cursor cursor = new(body);
        string first = cursor.Packet() ?? "";
        string final = cursor.Packet() ?? "";
        Check(response.IsSuccessStatusCode && first == "ACK " + commits[0] + " common\n" && final == "ACK " + commits[0] + "\n",
            "a have the repository knows is acknowledged as common and the final acknowledgment names it");
        PackResult pack = Pack.Read(body[(int)cursor.Position..], new Dictionary<string, GitObject>(StringComparer.Ordinal));
        Check(pack.Objects.Count == expectedNew, "the pack carries exactly the objects the client lacks, " + expectedNew + " of them");
        Check(pack.OffsetDeltas > 0, "the pack Graft writes is delta-compressed, with offset deltas because the client offered them");
        Check(pack.Objects.ContainsKey(tip) && !pack.Objects.ContainsKey(commits[0]), "what the client has is left out");
    }
    using (MemoryStream building = new())
    {
        Packet.Write(building, "want " + tip + " multi_ack_detailed ofs-delta\n");
        Packet.Flush(building);
        Packet.Write(building, "have " + commits[1] + "\n");
        Packet.Write(building, "have " + Limits.Zero[..39] + "1\n");
        Packet.Flush(building);
        request = building.ToArray();
    }
    using (HttpResponseMessage response = await GitPost(address, "/alice/engine.git/git-upload-pack", gitToken, request, encoding: null))
    {
        string body = Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync());
        Check(body.Contains("ACK " + commits[1] + " common\n", StringComparison.Ordinal) && body.Contains("ACK " + commits[1] + " ready\n", StringComparison.Ordinal) &&
            body.EndsWith("0008NAK\n", StringComparison.Ordinal),
            "a negotiation round acknowledges the common commit, says the pack is ready, and ends with NAK as upload-pack does");
    }
    using (MemoryStream building = new())
    {
        Packet.Write(building, "want " + tip + " multi_ack_detailed\n");
        Packet.Flush(building);
        Packet.Write(building, "have " + Limits.Zero[..39] + "1\n");
        Packet.Flush(building);
        request = building.ToArray();
    }
    using (HttpResponseMessage response = await GitPost(address, "/alice/engine.git/git-upload-pack", gitToken, request, encoding: null))
        Check(Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync()) == "0008NAK\n", "a have the repository does not know earns a bare NAK");

    // Protocol version 2 by hand, then through Git with its packets traced.
    using (HttpResponseMessage advertisement = await GitRequest(address, HttpMethod.Get, "/alice/engine.git/info/refs?service=git-upload-pack", gitToken, null, null, ("Git-Protocol", "version=2")))
    {
        string body = Encoding.ASCII.GetString(await advertisement.Content.ReadAsByteArrayAsync());
        Check(advertisement.IsSuccessStatusCode && body.Contains("000eversion 2\n", StringComparison.Ordinal) && body.Contains("ls-refs=unborn\n", StringComparison.Ordinal) &&
            body.Contains("fetch\n", StringComparison.Ordinal) && body.Contains("object-format=sha1\n", StringComparison.Ordinal),
            "a client asking for protocol version 2 is given the version 2 capability advertisement");
    }
    using (HttpResponseMessage advertisement = await GitRequest(address, HttpMethod.Get, "/alice/engine.git/info/refs?service=git-receive-pack", gitToken, null, null, ("Git-Protocol", "version=2")))
        Check(Encoding.ASCII.GetString(await advertisement.Content.ReadAsByteArrayAsync()).Contains(tip + " refs/heads/main", StringComparison.Ordinal),
            "a push still receives the version 0 advertisement, which is the only one a push has");
    using (MemoryStream building = new())
    {
        Packet.Write(building, "command=ls-refs\n");
        Packet.Write(building, "agent=graft-suite\n");
        Packet.Write(building, "object-format=sha1\n");
        Packet.Delimiter(building);
        Packet.Write(building, "peel\n");
        Packet.Write(building, "symrefs\n");
        Packet.Write(building, "ref-prefix HEAD\n");
        Packet.Write(building, "ref-prefix refs/heads/\n");
        Packet.Flush(building);
        request = building.ToArray();
    }
    using (HttpResponseMessage response = await GitRequest(address, HttpMethod.Post, "/alice/engine.git/git-upload-pack", gitToken, request, "application/x-git-upload-pack-request", ("Git-Protocol", "version=2")))
    {
        string body = Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync());
        Check(response.IsSuccessStatusCode && body.Contains(tip + " HEAD symref-target:refs/heads/main\n", StringComparison.Ordinal) &&
            body.Contains(tip + " refs/heads/main\n", StringComparison.Ordinal) && body.EndsWith("0000", StringComparison.Ordinal),
            "ls-refs lists the references under the prefixes asked for, with HEAD's target");
    }
    using (MemoryStream building = new())
    {
        Packet.Write(building, "command=fetch\n");
        Packet.Write(building, "object-format=sha1\n");
        Packet.Delimiter(building);
        Packet.Write(building, "thin-pack\n");
        Packet.Write(building, "ofs-delta\n");
        Packet.Write(building, "want " + tip + "\n");
        Packet.Write(building, "have " + commits[0] + "\n");
        Packet.Write(building, "done\n");
        Packet.Flush(building);
        request = building.ToArray();
    }
    using (HttpResponseMessage response = await GitRequest(address, HttpMethod.Post, "/alice/engine.git/git-upload-pack", gitToken, request, "application/x-git-upload-pack-request", ("Git-Protocol", "version=2")))
    {
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        Cursor cursor = new(body);
        (PacketKind kind, string? section) = cursor.Line();
        Check(response.IsSuccessStatusCode && kind == PacketKind.Data && section == "packfile\n", "a version 2 fetch with done answers with the packfile section at once");
        using MemoryStream packData = new();
        while (true)
        {
            string header = Encoding.ASCII.GetString(cursor.Take(4));
            int length = Convert.ToInt32(header, 16);
            if (length == 0) break;
            byte[] payload = cursor.Take(length - 4);
            Check(payload[0] == 1, "pack data travels on sideband one");
            packData.Write(payload, 1, payload.Length - 1);
        }
        Check(cursor.Remaining == 0, "the version 2 response ends with a flush");
        PackResult pack = Pack.Read(packData.ToArray(), new Dictionary<string, GitObject>(StringComparer.Ordinal));
        Check(pack.Objects.Count == expectedNew, "the version 2 pack carries exactly what the client lacks");
    }
    using (MemoryStream building = new())
    {
        Packet.Write(building, "command=fetch\n");
        Packet.Delimiter(building);
        Packet.Write(building, "want " + tip + "\n");
        Packet.Write(building, "have " + commits[2] + "\n");
        Packet.Flush(building);
        request = building.ToArray();
    }
    using (HttpResponseMessage response = await GitRequest(address, HttpMethod.Post, "/alice/engine.git/git-upload-pack", gitToken, request, "application/x-git-upload-pack-request", ("Git-Protocol", "version=2")))
    {
        string body = Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync());
        Check(body.StartsWith("0014acknowledgments\n" + "0031ACK " + commits[2] + "\n" + "000aready\n0001", StringComparison.Ordinal) && body.Contains("000dpackfile\n", StringComparison.Ordinal),
            "a version 2 fetch without done acknowledges the common commit, says ready, and sends the pack in the same response");
    }
    using (MemoryStream building = new())
    {
        Packet.Write(building, "command=fetch\n");
        Packet.Delimiter(building);
        Packet.Write(building, "want " + tip + "\n");
        Packet.Write(building, "deepen 1\n");
        Packet.Write(building, "done\n");
        Packet.Flush(building);
        request = building.ToArray();
    }
    using (HttpResponseMessage response = await GitRequest(address, HttpMethod.Post, "/alice/engine.git/git-upload-pack", gitToken, request, "application/x-git-upload-pack-request", ("Git-Protocol", "version=2")))
        Check(response.StatusCode == HttpStatusCode.BadRequest && (await response.Content.ReadAsStringAsync()).Contains("fetch-unsupported: deepen", StringComparison.Ordinal),
            "a shallow fetch is refused by name rather than answered with a full history");

    string traced = Path.Combine(root, "engine-v2-clone");
    (int cloneCode, _, string cloneTrace) = await RunGit(root, ["-c", "protocol.version=2", "clone", remote, "engine-v2-clone"], new Dictionary<string, string> { ["GIT_TRACE_PACKET"] = "1" });
    Check(cloneCode == 0 && cloneTrace.Contains("command=ls-refs", StringComparison.Ordinal) && cloneTrace.Contains("command=fetch", StringComparison.Ordinal),
        "Git clones over protocol version 2 when it asks for it, listing references and fetching as two commands");
    Check(File.ReadAllText(Path.Combine(traced, "story.txt")) == text.ToString(), "the version 2 clone has the content");
    await Git(traced, ["fsck", "--full", "--strict"]);
    text.Append("appended after the clone\n");
    File.WriteAllText(Path.Combine(work, "story.txt"), text.ToString());
    await Git(work, ["commit", "-am", "After the clone"]);
    await Git(work, ["push", "origin", "main"]);
    (int fetchCode, _, string fetchTrace) = await RunGit(traced, ["-c", "protocol.version=2", "pull", "--ff-only"], new Dictionary<string, string> { ["GIT_TRACE_PACKET"] = "1" });
    Check(fetchCode == 0 && fetchTrace.Contains("have " + tip, StringComparison.Ordinal) && File.ReadAllText(Path.Combine(traced, "story.txt")) == text.ToString(),
        "an incremental fetch over version 2 tells the server what it has and receives the rest");
    tip = (await Git(work, ["rev-parse", "HEAD"])).Trim();

    // Housekeeping: everything into one pack with an index, and Git agrees with the index byte for byte.
    using (HttpResponseMessage refused = await ApiPostJson(address, "/api/v4/projects/" + projectId + "/housekeeping", gitToken, "{}"))
        Check(refused.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, "housekeeping needs the api scope, like every other project setting, and a token without it learns nothing");
    int looseBefore = Directory.EnumerateDirectories(Path.Combine(repo, "objects")).Where(d => Path.GetFileName(d).Length == 2).Sum(d => Directory.EnumerateFiles(d).Count());
    Check(looseBefore > 0 && Directory.EnumerateFiles(Path.Combine(repo, "objects", "pack")).Count() == 0, "before housekeeping every object is loose and there is no pack");
    using (HttpResponseMessage packed = await ApiPostJson(address, "/api/v4/projects/" + projectId + "/housekeeping", apiToken, "{}"))
    {
        using JsonDocument json = JsonDocument.Parse(await packed.Content.ReadAsStreamAsync());
        Check(packed.StatusCode == HttpStatusCode.Created && json.RootElement.GetProperty("objects_packed").GetInt32() == looseBefore &&
            json.RootElement.GetProperty("loose_objects_removed").GetInt32() == looseBefore,
            "housekeeping packs every object and says so");
    }
    string[] packFiles = [.. Directory.EnumerateFiles(Path.Combine(repo, "objects", "pack"))];
    string? packPath = packFiles.FirstOrDefault(path => path.EndsWith(".pack", StringComparison.Ordinal));
    Check(packFiles.Length == 2 && packPath is not null && File.Exists(Path.ChangeExtension(packPath, ".idx")), "one pack and its index remain");
    int looseAfter = Directory.EnumerateDirectories(Path.Combine(repo, "objects")).Where(d => Path.GetFileName(d).Length == 2).Sum(d => Directory.EnumerateFiles(d).Count());
    Check(looseAfter == 0, "no loose objects remain after housekeeping");
    await Git(repo, ["fsck", "--full", "--strict"]);
    string verify = await Git(repo, ["verify-pack", "-v", Path.ChangeExtension(packPath!, ".idx")]);
    Check(verify.Split('\n').Any(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 7 } fields && fields[1] == "blob"),
        "Git verifies the pack and finds blobs stored as deltas in it");
    string indexCheck = Path.Combine(root, "engine-index-check");
    Directory.CreateDirectory(indexCheck);
    File.Copy(packPath!, Path.Combine(indexCheck, "check.pack"));
    await Git(indexCheck, ["index-pack", "--strict", "check.pack"]);
    Check(File.ReadAllBytes(Path.Combine(indexCheck, "check.idx")).AsSpan().SequenceEqual(File.ReadAllBytes(Path.ChangeExtension(packPath!, ".idx"))),
        "the index Graft wrote is byte for byte the index Git writes for the same pack");
    Check((await Git(repo, ["cat-file", "-p", tip + ":story.txt"])) == text.ToString(), "Git reads a packed object from the repository Graft keeps");

    // Reading and writing go on across the pack: the API, a clone, and a push that leaves new loose objects beside it.
    using (HttpResponseMessage file = await ApiGet(address, "/api/v4/projects/" + projectId + "/repository/files/story.txt/raw?ref=main", apiToken))
        Check(file.IsSuccessStatusCode && Encoding.UTF8.GetString(await file.Content.ReadAsByteArrayAsync()) == text.ToString(), "the API reads a file from the pack");
    string afterPack = Path.Combine(root, "engine-after-pack");
    await Git(root, ["clone", remote, "engine-after-pack"]);
    Check(File.ReadAllText(Path.Combine(afterPack, "story.txt")) == text.ToString(), "a clone is served from the pack");
    await Git(afterPack, ["fsck", "--full", "--strict"]);
    File.WriteAllText(Path.Combine(afterPack, "after.txt"), "after the pack\n");
    await Git(afterPack, ["add", "."]);
    await Git(afterPack, ["commit", "-m", "After the pack"]);
    await Git(afterPack, ["push", "origin", "main"]);
    await Git(repo, ["fsck", "--full", "--strict"]);
    Check(Directory.EnumerateDirectories(Path.Combine(repo, "objects")).Where(d => Path.GetFileName(d).Length == 2).Sum(d => Directory.EnumerateFiles(d).Count()) == 3,
        "a push after housekeeping leaves its three new objects loose beside the pack");
    await Git(traced, ["pull", "--ff-only"]);
    Check(File.Exists(Path.Combine(traced, "after.txt")), "a fetch across the pack and the loose objects brings the new commit");

    // Automatic housekeeping at the end of a push that leaves more loose objects than the threshold.
    Repository many = new();
    for (int batch = 0; batch < 11; batch++)
        many.CommitFiles("main", [.. Enumerable.Range(0, 100).Select(n => new FileChange("files/" + batch + "/" + n + ".txt", Encoding.ASCII.GetBytes("file " + batch + "-" + n + "\n")))], who, "Batch " + batch);
    string automatic = Path.Combine(root, "engine-automatic");
    Repository receiving = new(new DurableRepositoryStorage(automatic));
    receiving.Receive(Push((Limits.Zero, many.Refs["refs/heads/main"], "refs/heads/main"), Pack.Write(many.Objects.Values)));
    Check(many.Objects.Count > Repository.AutoRepackLooseObjects && receiving.LooseObjectCount == 0 &&
        Directory.EnumerateFiles(Path.Combine(automatic, "objects", "pack"), "*.idx").Count() == 1,
        "a push that leaves more than " + Repository.AutoRepackLooseObjects + " loose objects is packed before it is answered");
    Check(receiving.Objects.Count == many.Objects.Count && receiving.Objects[many.Refs["refs/heads/main"]].Data.AsSpan().SequenceEqual(many.Objects[many.Refs["refs/heads/main"]].Data),
        "the repository reads the same objects from the pack it just wrote");
    await Git(automatic, ["fsck", "--full", "--strict"]);
    Check(new Repository(new DurableRepositoryStorage(automatic)).Objects.Count == many.Objects.Count, "a repository held in a pack opens again with everything");
}

async Task<HttpResponseMessage> GitRequest(string address, HttpMethod method, string path, string token, byte[]? body, string? contentType, params (string Name, string Value)[] headers)
{
    HttpClientHandler handler = new() { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseProxy = false };
    HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(60) };
    HttpRequestMessage request = new(method, path);
    if (body is not null)
    {
        request.Content = new ByteArrayContent(body);
        if (contentType is not null) request.Content.Headers.ContentType = new(contentType);
    }
    foreach ((string name, string value) in headers) request.Headers.Add(name, value);
    request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:" + token)));
    HttpResponseMessage response = await client.SendAsync(request);
    request.Dispose();
    client.Dispose();
    handler.Dispose();
    return response;
}

/// <summary>
/// Package feeds and the container registry, over the protocols their clients speak: a generic file, a
/// NuGet v3 push and the feed that serves it, and a Docker v2 push and pull.
/// </summary>
async Task CheckPackages(string address, AuthenticatedIdentity alice, string reporterUsername)
{
    long projectId;
    string aliceApi;
    string reporterApi;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo created = await store.CreateProjectWithRepositoryAsync(alice, "crate", "Crate",
            (project, cancellation) => { cancellation.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; });
        projectId = created.Id;
        UserAccount reporter = Required(await store.FindActiveUserAsync(reporterUsername), "reporter account");
        await store.SetMembershipAsync(alice, projectId, reporter.Id, ProjectRole.Reporter);
        aliceApi = (await store.CreateTokenAsync(alice, "crate-api", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        reporterApi = (await store.CreateTokenAsync(new(reporter.Id, reporter.Username, false, IdentityKind.BrowserSession), "crate-reporter", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
    }
    string prefix = "/api/v4/projects/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Generic packages.
    byte[] build = Encoding.UTF8.GetBytes("a build artefact\n");
    using (HttpResponseMessage refused = await ApiPutBytes(address, prefix + "/packages/generic/tool/1.0.0/tool.bin", reporterApi, build, "application/octet-stream"))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "publishing a package needs the permission to push");
    using (HttpResponseMessage stored = await ApiPutBytes(address, prefix + "/packages/generic/tool/1.0.0/tool.bin", aliceApi, build, "application/octet-stream"))
        Check(stored.StatusCode == HttpStatusCode.Created, "a generic package file is published under a name and a version");
    using (HttpResponseMessage fetched = await ApiGet(address, prefix + "/packages/generic/tool/1.0.0/tool.bin", reporterApi))
        Check(fetched.IsSuccessStatusCode && (await fetched.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(build) &&
            fetched.Content.Headers.ContentDisposition?.DispositionType == "attachment", "a reader downloads it byte for byte, as an attachment");
    using (HttpResponseMessage challenge = await ApiGet(address, prefix + "/packages/generic/tool/1.0.0/tool.bin", null))
        Check(challenge.StatusCode == HttpStatusCode.Unauthorized && challenge.Headers.WwwAuthenticate.Any(h => h.Scheme == "Basic"),
            "an anonymous package client is challenged with Basic, which is what package clients answer");
    using (HttpResponseMessage listing = await ApiGet(address, prefix + "/packages", reporterApi))
    {
        using JsonDocument json = JsonDocument.Parse(await listing.Content.ReadAsStreamAsync());
        Check(listing.IsSuccessStatusCode && json.RootElement.GetArrayLength() == 1 &&
            json.RootElement[0].GetProperty("package_type").GetString() == "generic" &&
            json.RootElement[0].GetProperty("package_files")[0].GetProperty("file_sha256").GetString() == Convert.ToHexStringLower(SHA256.HashData(build)),
            "the package listing names the file and its hash");
    }

    // NuGet: a package built by hand, pushed as the client pushes it, and read back through the feed.
    string nupkg = Path.Combine(root, "Widgets.Core.1.2.3.nupkg");
    using (FileStream zip = new(nupkg, FileMode.Create))
    using (System.IO.Compression.ZipArchive archive = new(zip, System.IO.Compression.ZipArchiveMode.Create))
    {
        using (StreamWriter spec = new(archive.CreateEntry("Widgets.Core.nuspec").Open()))
            spec.Write("<?xml version=\"1.0\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata><id>Widgets.Core</id><version>1.2.3</version><authors>Alice</authors><description>Widgets for everyone.</description><dependencies><group targetFramework=\"net10.0\"><dependency id=\"Newtonsoft.Json\" version=\"13.0.3\" /></group></dependencies></metadata></package>");
        using (StreamWriter lib = new(archive.CreateEntry("lib/net10.0/Widgets.Core.dll").Open())) lib.Write("not really a dll");
    }
    using (HttpResponseMessage index = await ApiGet(address, prefix + "/packages/nuget/index.json", aliceApi))
    {
        using JsonDocument json = JsonDocument.Parse(await index.Content.ReadAsStreamAsync());
        Check(index.IsSuccessStatusCode && json.RootElement.GetProperty("resources").EnumerateArray().Any(r => r.GetProperty("@type").GetString() == "PackageBaseAddress/3.0.0"),
            "the NuGet service index names the feed's resources");
    }
    using (HttpResponseMessage pushed = await NuGetPushPackage(address, prefix + "/packages/nuget", aliceApi, nupkg))
        Check(pushed.StatusCode == HttpStatusCode.Created, "a NuGet package is pushed as the client pushes it, with the token as the API key" +
            (pushed.StatusCode == HttpStatusCode.Created ? "" : " (got " + (int)pushed.StatusCode + ": " + Redact(await pushed.Content.ReadAsStringAsync()) + ")"));
    using (HttpResponseMessage versions = await ApiGet(address, prefix + "/packages/nuget/download/widgets.core/index.json", reporterApi))
    {
        using JsonDocument json = JsonDocument.Parse(await versions.Content.ReadAsStreamAsync());
        Check(versions.IsSuccessStatusCode && json.RootElement.GetProperty("versions")[0].GetString() == "1.2.3", "the flat container lists the version, by lowercase id");
    }
    using (HttpResponseMessage content = await ApiGet(address, prefix + "/packages/nuget/download/widgets.core/1.2.3/widgets.core.1.2.3.nupkg", reporterApi))
        Check(content.IsSuccessStatusCode && (await content.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(File.ReadAllBytes(nupkg)), "the package downloads byte for byte");
    using (HttpResponseMessage spec = await ApiGet(address, prefix + "/packages/nuget/download/widgets.core/1.2.3/widgets.core.nuspec", reporterApi))
        Check(spec.IsSuccessStatusCode && (await spec.Content.ReadAsStringAsync()).Contains("<id>Widgets.Core</id>", StringComparison.Ordinal), "the nuspec is read out of the package");
    using (HttpResponseMessage registration = await ApiGet(address, prefix + "/packages/nuget/metadata/widgets.core/index.json", reporterApi))
    {
        using JsonDocument json = JsonDocument.Parse(await registration.Content.ReadAsStreamAsync());
        JsonElement entry = json.RootElement.GetProperty("items")[0].GetProperty("items")[0].GetProperty("catalogEntry");
        Check(registration.IsSuccessStatusCode && entry.GetProperty("id").GetString() == "Widgets.Core" &&
            entry.GetProperty("description").GetString() == "Widgets for everyone." &&
            entry.GetProperty("dependencyGroups")[0].GetProperty("dependencies")[0].GetProperty("id").GetString() == "Newtonsoft.Json",
            "the registration carries the package's description and dependencies");
    }
    using (HttpResponseMessage search = await ApiGet(address, prefix + "/packages/nuget/query?q=widget", reporterApi))
    {
        using JsonDocument json = JsonDocument.Parse(await search.Content.ReadAsStreamAsync());
        Check(search.IsSuccessStatusCode && json.RootElement.GetProperty("totalHits").GetInt32() == 1, "the search service finds the package");
    }
    using (HttpResponseMessage junk = await NuGetPushPackage(address, prefix + "/packages/nuget", aliceApi, nupkg + ".missing"))
        Check(junk.StatusCode == HttpStatusCode.BadRequest, "a push of something that is not a package is refused");

    // The container registry, as a Docker client speaks to it.
    byte[] layer = Encoding.UTF8.GetBytes("layer bytes " + new string('x', 5000));
    string layerDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(layer));
    string image = "/v2/alice/crate/app";
    using (HttpResponseMessage ping = await ApiGet(address, "/v2/", null))
        Check(ping.StatusCode == HttpStatusCode.Unauthorized && ping.Headers.WwwAuthenticate.Any(h => h.Scheme == "Basic"), "the registry challenges an anonymous client with Basic");
    using (HttpResponseMessage ping = await RegistryRequest(address, HttpMethod.Get, "/v2/", aliceApi, null, null))
        Check(ping.IsSuccessStatusCode && ping.Headers.Contains("Docker-Distribution-Api-Version"), "an identified client is told the API version");
    using (HttpResponseMessage missing = await RegistryRequest(address, HttpMethod.Head, image + "/blobs/" + layerDigest, aliceApi, null, null))
        Check(missing.StatusCode == HttpStatusCode.NotFound, "a blob not yet pushed is unknown");
    string? uploadLocation;
    using (HttpResponseMessage started = await RegistryRequest(address, HttpMethod.Post, image + "/blobs/uploads/", aliceApi, null, null))
    {
        uploadLocation = started.Headers.Location?.ToString();
        Check(started.StatusCode == HttpStatusCode.Accepted && uploadLocation is { Length: > 0 } && started.Headers.Contains("Docker-Upload-UUID"), "a blob upload is started");
    }
    using (HttpResponseMessage patched = await RegistryRequest(address, HttpMethod.Patch, uploadLocation!, aliceApi, layer[..3000], "application/octet-stream"))
        Check(patched.StatusCode == HttpStatusCode.Accepted && patched.Headers.TryGetValues("Range", out IEnumerable<string>? range) && range.First() == "0-2999", "a chunk is appended and the range reported");
    using (HttpResponseMessage completed = await RegistryRequest(address, HttpMethod.Put, uploadLocation + "?digest=" + layerDigest, aliceApi, layer[3000..], "application/octet-stream"))
        Check(completed.StatusCode == HttpStatusCode.Created && completed.Headers.TryGetValues("Docker-Content-Digest", out IEnumerable<string>? d) && d.First() == layerDigest, "the upload completes under its digest");
    using (HttpResponseMessage wrong = await RegistryRequest(address, HttpMethod.Post, image + "/blobs/uploads/?digest=sha256:" + new string('0', 64), aliceApi, layer, "application/octet-stream"))
        Check(wrong.StatusCode == HttpStatusCode.BadRequest, "a blob whose bytes do not hash to the digest is refused");
    using (HttpResponseMessage head = await RegistryRequest(address, HttpMethod.Head, image + "/blobs/" + layerDigest, aliceApi, null, null))
    {
        long? length = head.Content.Headers.ContentLength;
        bool there = head.IsSuccessStatusCode && length == layer.Length;
        Check(there, there ? "the blob is there, with its length" : "the blob is there, with its length (got " + (int)head.StatusCode + ", length " + (length?.ToString() ?? "none") + ", headers: " + head.Headers + head.Content.Headers + ")");
    }
    string manifest = "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.docker.distribution.manifest.v2+json\",\"config\":{\"mediaType\":\"application/vnd.docker.container.image.v1+json\",\"size\":" + layer.Length + ",\"digest\":\"" + layerDigest + "\"},\"layers\":[{\"mediaType\":\"application/vnd.docker.image.rootfs.diff.tar.gzip\",\"size\":" + layer.Length + ",\"digest\":\"" + layerDigest + "\"}]}";
    byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest);
    string manifestDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
    using (HttpResponseMessage refused = await RegistryRequest(address, HttpMethod.Put, image + "/manifests/latest", reporterApi, manifestBytes, "application/vnd.docker.distribution.manifest.v2+json", username: reporterUsername))
        Check(refused.StatusCode == HttpStatusCode.Forbidden, "a Reporter cannot push an image" + (refused.StatusCode == HttpStatusCode.Forbidden ? "" : " (got " + (int)refused.StatusCode + ": " + Redact(await refused.Content.ReadAsStringAsync()) + " " + refused.Headers + ")"));
    using (HttpResponseMessage pushed = await RegistryRequest(address, HttpMethod.Put, image + "/manifests/latest", aliceApi, manifestBytes, "application/vnd.docker.distribution.manifest.v2+json"))
        Check(pushed.StatusCode == HttpStatusCode.Created && pushed.Headers.TryGetValues("Docker-Content-Digest", out IEnumerable<string>? md) && md.First() == manifestDigest, "a manifest is pushed under a tag");
    using (HttpResponseMessage pulled = await RegistryRequest(address, HttpMethod.Get, image + "/manifests/latest", reporterApi, null, null, username: reporterUsername))
        Check(pulled.IsSuccessStatusCode && pulled.Content.Headers.ContentType?.MediaType == "application/vnd.docker.distribution.manifest.v2+json" &&
            (await pulled.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(manifestBytes), "a reader pulls the manifest by tag, with the type it was pushed as");
    using (HttpResponseMessage byDigest = await RegistryRequest(address, HttpMethod.Get, image + "/manifests/" + manifestDigest, reporterApi, null, null, username: reporterUsername))
        Check(byDigest.IsSuccessStatusCode, "and by digest");
    using (HttpResponseMessage pulledBlob = await RegistryRequest(address, HttpMethod.Get, image + "/blobs/" + layerDigest, reporterApi, null, null, username: reporterUsername))
        Check(pulledBlob.IsSuccessStatusCode && (await pulledBlob.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(layer), "and the layer byte for byte");
    using (HttpResponseMessage tags = await RegistryRequest(address, HttpMethod.Get, image + "/tags/list", reporterApi, null, null, username: reporterUsername))
    {
        using JsonDocument json = JsonDocument.Parse(await tags.Content.ReadAsStreamAsync());
        Check(tags.IsSuccessStatusCode && json.RootElement.GetProperty("name").GetString() == "alice/crate/app" && json.RootElement.GetProperty("tags")[0].GetString() == "latest", "the tag list names the image and its tag");
    }
    using (HttpResponseMessage images = await ApiGet(address, prefix + "/registry/repositories", reporterApi))
    {
        using JsonDocument json = JsonDocument.Parse(await images.Content.ReadAsStreamAsync());
        Check(images.IsSuccessStatusCode && json.RootElement[0].GetProperty("path").GetString() == "alice/crate/app" && json.RootElement[0].GetProperty("tags_count").GetInt32() == 1,
            "the project lists its images in GitLab's shape");
    }
    using (HttpResponseMessage untagged = await RegistryRequest(address, HttpMethod.Delete, image + "/manifests/latest", aliceApi, null, null))
        Check(untagged.StatusCode == HttpStatusCode.Accepted, "a tag is removed");
    using (HttpResponseMessage gone = await RegistryRequest(address, HttpMethod.Get, image + "/manifests/latest", aliceApi, null, null))
        Check(gone.StatusCode == HttpStatusCode.NotFound, "and the tag is gone while the manifest stays reachable by digest");
}

async Task<HttpResponseMessage> NuGetPushPackage(string address, string path, string token, string file)
{
    HttpClientHandler handler = new() { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseProxy = false };
    HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(30) };
    MultipartFormDataContent form = new();
    byte[] bytes = File.Exists(file) ? File.ReadAllBytes(file) : Encoding.UTF8.GetBytes("not a package");
    form.Add(new ByteArrayContent(bytes), "package", "package.nupkg");
    HttpRequestMessage request = new(HttpMethod.Put, path) { Content = form };
    request.Headers.Add("X-NuGet-ApiKey", token);
    HttpResponseMessage response = await client.SendAsync(request);
    request.Dispose();
    client.Dispose();
    handler.Dispose();
    return response;
}

async Task<HttpResponseMessage> RegistryRequest(string address, HttpMethod method, string path, string? token, byte[]? body, string? contentType, string username = "alice")
{
    HttpClientHandler handler = new() { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseProxy = false };
    HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(30) };
    HttpRequestMessage request = new(method, path);
    if (body is not null)
    {
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType ?? "application/octet-stream");
    }
    if (token is not null) request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + token)));
    HttpResponseMessage response = await client.SendAsync(request);
    request.Dispose();
    client.Dispose();
    handler.Dispose();
    return response;
}

/// <summary>
/// Webhooks, against a receiver this suite runs. Proves the delivery arrives, that it is signed with the
/// hook's own secret, and that a server without the operator's switch has no hook routes at all.
/// </summary>
async Task<string> CheckWebhooks(string address, AuthenticatedIdentity alice)
{
    long projectId;
    string apiToken;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo created = await store.CreateProjectWithRepositoryAsync(alice, "notified", "Notified",
            (project, cancellation) => { cancellation.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; });
        projectId = created.Id;
        apiToken = (await store.CreateTokenAsync(alice, "hook-api", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
    }
    string prefix = "/api/v4/projects/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // With the operator's switch off, which is how every other check in this suite runs.
    using (HttpResponseMessage absent = await ApiGet(address, prefix + "/hooks", apiToken))
        Check(absent.StatusCode == HttpStatusCode.NotFound,
            "a server started without the switch has no webhook routes rather than refusing ones it has");

    await StopServer();
    webhooksEnabled = true;
    address = await StartServer();
    Check(serverLines.Any(line => line.StartsWith("WEBHOOKS: on", StringComparison.Ordinal)),
        "a server with outbound notification says so at startup");

    // A receiver of our own, so the delivery is observed arriving rather than assumed.
    const string secret = "a-shared-signing-secret";
    using HttpListener receiver = new();
    int port = FreeLoopbackPort();
    string hookUrl = "http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/graft";
    receiver.Prefixes.Add("http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/");
    receiver.Start();
    TaskCompletionSource<(string Event, string Signature, string Body)> arrived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    _ = Task.Run(async () =>
    {
        try
        {
            HttpListenerContext received = await receiver.GetContextAsync();
            using StreamReader reader = new(received.Request.InputStream);
            string body = await reader.ReadToEndAsync();
            string eventName = received.Request.Headers["X-Graft-Event"] ?? "";
            string signature = received.Request.Headers["X-Graft-Signature"] ?? "";
            // Answered before the test is told, so the sender sees its 200 rather than a timeout caused by
            // this listener being stopped while the assertions run.
            received.Response.StatusCode = 200;
            received.Response.Close();
            arrived.TrySetResult((eventName, signature, body));
        }
        catch (Exception error) { arrived.TrySetException(error); }
    });

    using (HttpResponseMessage shortSecret = await ApiPost(address, prefix + "/hooks", apiToken,
        new { url = hookUrl, secret = "tooshort", events = new[] { "issue" } }))
        Check(shortSecret.StatusCode == HttpStatusCode.BadRequest, "a signing secret shorter than 16 characters is refused");
    using (HttpResponseMessage scheme = await ApiPost(address, prefix + "/hooks", apiToken,
        new { url = "file:///etc/passwd", secret, events = new[] { "issue" } }))
        Check(scheme.StatusCode == HttpStatusCode.BadRequest, "a hook address must be http or https");
    using (HttpResponseMessage unknown = await ApiPost(address, prefix + "/hooks", apiToken,
        new { url = hookUrl, secret, events = new[] { "everything" } }))
        Check(unknown.StatusCode == HttpStatusCode.BadRequest, "an event name nothing will ever send is refused when the hook is made");

    long hookId;
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/hooks", apiToken,
        new { url = hookUrl, secret, events = new[] { "issue", "push" } }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        hookId = json.RootElement.GetProperty("id").GetInt64();
        string payload = json.RootElement.ToString();
        Check(created.StatusCode == HttpStatusCode.Created &&
            json.RootElement.GetProperty("events").GetArrayLength() == 2 &&
            !payload.Contains(secret, StringComparison.Ordinal),
            "a hook is registered and its signing secret is never reported back");
    }

    using (HttpResponseMessage opened = await ApiPost(address, prefix + "/issues", apiToken, new { title = "Tell somebody" }))
        Check(opened.StatusCode == HttpStatusCode.Created, "an issue is opened to trigger a delivery");

    (string deliveredEvent, string signature, string deliveredBody) = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
    Check(deliveredEvent == "issue", "the delivery names the event that caused it");
    Check(signature == Signature(secret, deliveredBody),
        "the delivery is signed with an HMAC of its exact body under the hook's own secret");
    using (JsonDocument json = JsonDocument.Parse(deliveredBody))
    {
        Check(json.RootElement.GetProperty("action").GetString() == "opened" &&
            json.RootElement.GetProperty("issue").GetProperty("title").GetString() == "Tell somebody" &&
            json.RootElement.GetProperty("project").GetProperty("path").GetString() == "alice/notified",
            "the payload says what happened, to what, and in which project");
        Check(!deliveredBody.Contains(secret, StringComparison.Ordinal) &&
            !deliveredBody.Contains(apiToken, StringComparison.Ordinal),
            "and carries no credential of any kind");
    }
    // The outcome is recorded against the hook, so a failing receiver is visible without reading a log.
    // Polled, because the sender records it after answering the request that caused it: the whole point of
    // the queue is that nothing waits for the receiver.
    int? recorded = null;
    for (int attempt = 0; attempt < 40 && recorded is null; attempt++)
    {
        using HttpResponseMessage listed = await ApiGet(address, prefix + "/hooks", apiToken);
        using JsonDocument json = JsonDocument.Parse(await listed.Content.ReadAsStreamAsync());
        JsonElement status = json.RootElement[0].GetProperty("last_status");
        if (status.ValueKind == JsonValueKind.Number) recorded = status.GetInt32();
        else await Task.Delay(250);
    }
    Check(recorded == 200, "the last delivery's outcome is recorded on the hook");
    receiver.Stop();
    using (HttpResponseMessage removed = await ApiDelete(address, prefix + "/hooks/" + hookId, apiToken))
        Check(removed.StatusCode == HttpStatusCode.NoContent, "a hook is removed");

    // Back to the posture every other check runs under, and the caller takes the new address: a restart
    // moves the port.
    await StopServer();
    webhooksEnabled = false;
    return await StartServer();
}

/// <summary>The signature a receiver is expected to compute, written out here rather than shared with the
/// server, so the assertion checks the format rather than agreeing with itself.</summary>
static string Signature(string secret, string body) =>
    "sha256=" + Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

static int FreeLoopbackPort()
{
    using System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0);
    probe.Start();
    int port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
}

/// <summary>
/// Getting things out of Graft without cloning: tags, one file as bytes, and the whole tree as an archive.
/// Also the two defects this batch fixed, which were both about a page reporting the wrong thing.
/// </summary>
async Task CheckFetching(string address, AuthenticatedIdentity alice, string strangerUsername)
{
    long projectId;
    string apiToken;
    string readApi;
    string strangerApi;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo created = await store.CreateProjectWithRepositoryAsync(alice, "fetchable", "Fetchable",
            (project, cancellation) => { cancellation.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; });
        projectId = created.Id;
        UserAccount stranger = Required(await store.FindActiveUserAsync(strangerUsername), "stranger account");
        apiToken = (await store.CreateTokenAsync(alice, "fetch-api", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        readApi = (await store.CreateTokenAsync(alice, "fetch-read", TokenScope.ReadApi, DateTimeOffset.UtcNow.AddHours(1))).Secret;
        strangerApi = (await store.CreateTokenAsync(new(stranger.Id, stranger.Username, false, IdentityKind.BrowserSession),
            "fetch-stranger", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
    }
    string prefix = "/api/v4/projects/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // An empty repository is a state, not a refusal. This was the defect: the browse endpoint answered 404
    // when there was no tree, and the page reported that as a permission problem, so a project somebody had
    // just created looked like one they could not see.
    using (HttpResponseMessage empty = await ApiGet(address, "/api/browser/projects/alice/fetchable", null))
        Check(empty.StatusCode == HttpStatusCode.NotFound, "an empty private repository is still concealed from a stranger");

    string clone = Path.Combine(root, "fetch-clone");
    Directory.CreateDirectory(clone);
    await Git(clone, ["init", "--initial-branch=main"]);
    File.WriteAllText(Path.Combine(clone, "notes.txt"), "first line\nsecond line\n");
    Directory.CreateDirectory(Path.Combine(clone, "bin"));
    File.WriteAllBytes(Path.Combine(clone, "bin", "blob.dat"), [0, 1, 2, 0, 255, 7]);
    File.WriteAllText(Path.Combine(clone, "run.sh"), "#!/bin/sh\necho hello\n");
    await Git(clone, ["add", "."]);
    await Git(clone, ["update-index", "--chmod=+x", "run.sh"]);
    await Git(clone, ["commit", "-m", "Something to fetch"]);
    await Git(clone, ["remote", "add", "origin", AuthenticatedUrl(address, "alice", apiToken, "alice/fetchable.git")]);
    await Git(clone, ["push", "origin", "main"]);
    string head = (await Git(clone, ["rev-parse", "HEAD"])).Trim();

    // Tags.
    using (HttpResponseMessage none = await ApiGet(address, prefix + "/repository/tags", readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await none.Content.ReadAsStreamAsync());
        Check(none.IsSuccessStatusCode && json.RootElement.GetArrayLength() == 0, "a repository with no tags lists none");
    }
    using (HttpResponseMessage refused = await ApiPost(address, prefix + "/repository/tags", readApi,
        new { tag_name = "v0", @ref = "main" }))
        Check(refused.StatusCode == HttpStatusCode.NotFound, "a read-only token cannot create a tag");
    using (HttpResponseMessage created = await ApiPost(address, prefix + "/repository/tags", apiToken,
        new { tag_name = "v1.0.0", @ref = "main" }))
    {
        using JsonDocument json = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
        Check(created.StatusCode == HttpStatusCode.Created &&
            json.RootElement.GetProperty("name").GetString() == "v1.0.0" &&
            json.RootElement.GetProperty("commit").GetProperty("id").GetString() == head,
            "a tag is created at a named revision and names the commit it resolves to");
    }
    using (HttpResponseMessage again = await ApiPost(address, prefix + "/repository/tags", apiToken,
        new { tag_name = "v1.0.0", @ref = "main" }))
        Check(again.StatusCode == HttpStatusCode.Conflict, "creating a tag that already exists is refused");
    using (HttpResponseMessage nowhere = await ApiPost(address, prefix + "/repository/tags", apiToken,
        new { tag_name = "v9", @ref = "no-such-thing" }))
        Check(nowhere.StatusCode == HttpStatusCode.BadRequest, "a tag cannot name a revision that does not resolve");

    // A tag pushed by a client, including an annotated one, appears in the same listing.
    await Git(clone, ["tag", "-a", "v1.1.0", "-m", "Annotated by a client"]);
    await Git(clone, ["push", "origin", "v1.1.0"]);
    using (HttpResponseMessage listed = await ApiGet(address, prefix + "/repository/tags", readApi))
    {
        using JsonDocument json = JsonDocument.Parse(await listed.Content.ReadAsStreamAsync());
        JsonElement annotated = json.RootElement.EnumerateArray().Single(tag => tag.GetProperty("name").GetString() == "v1.1.0");
        Check(listed.IsSuccessStatusCode && json.RootElement.GetArrayLength() == 2,
            "a tag pushed by a client is listed beside one created through the API");
        // The reference points at the tag object and the commit is what it resolves to, which is the whole
        // reason both are reported rather than one.
        Check(annotated.GetProperty("target").GetString() != annotated.GetProperty("commit").GetProperty("id").GetString() &&
            annotated.GetProperty("commit").GetProperty("id").GetString() == head,
            "an annotated tag reports the tag object it points at and the commit that resolves to");
    }
    using (HttpResponseMessage deleted = await ApiDelete(address, prefix + "/repository/tags/v1.0.0", apiToken))
        Check(deleted.StatusCode == HttpStatusCode.NoContent, "a tag is deleted");
    using (HttpResponseMessage gone = await ApiDelete(address, prefix + "/repository/tags/v1.0.0", apiToken))
        Check(gone.StatusCode == HttpStatusCode.NotFound, "deleting a tag that is not there is a 404");

    // One file, as bytes.
    using (HttpResponseMessage raw = await ApiGet(address, prefix + "/repository/files/notes.txt/raw?ref=main", readApi))
    {
        byte[] body = await raw.Content.ReadAsByteArrayAsync();
        Check(raw.IsSuccessStatusCode && Encoding.UTF8.GetString(body) == "first line\nsecond line\n",
            "a file is served as its exact bytes");
        // The point of this route's content type: repository bytes must never reach this origin as a
        // document, because this origin holds the session cookie and the API.
        Check(raw.Content.Headers.ContentType?.MediaType == "application/octet-stream",
            "a raw file is always an unknown type, never the type its content looks like");
        Check(raw.Content.Headers.ContentDisposition?.DispositionType == "attachment",
            "and is always an attachment, so a browser saves it rather than rendering it");
        Check(raw.Headers.TryGetValues("X-Content-Type-Options", out IEnumerable<string>? sniff) &&
            sniff.Contains("nosniff"), "and the browser is told not to decide otherwise from the content");
    }
    using (HttpResponseMessage binary = await ApiGet(address,
        prefix + "/repository/files/" + Uri.EscapeDataString("bin/blob.dat") + "/raw?ref=main", readApi))
    {
        byte[] body = await binary.Content.ReadAsByteArrayAsync();
        Check(binary.IsSuccessStatusCode && body.SequenceEqual<byte>([0, 1, 2, 0, 255, 7]),
            "a binary file comes back byte for byte, which nothing else in the API could do");
    }
    using (HttpResponseMessage missing = await ApiGet(address, prefix + "/repository/files/absent.txt/raw?ref=main", readApi))
        Check(missing.StatusCode == HttpStatusCode.NotFound, "a file that is not there is a 404");
    // The browser's own route, because the GitLab-shaped one answers a token and would refuse a session.
    // A page that offered a download which then refused the reader would be worse than no download.
    using (HttpResponseMessage browserRaw = await ApiGet(address,
        "/api/projects/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        "/raw/bin/blob.dat?ref=main", null))
        Check(browserRaw.StatusCode == HttpStatusCode.NotFound,
            "the browser's own raw route conceals a private project from a stranger too");
    using (HttpResponseMessage refused = await ApiGet(address, prefix + "/repository/files/notes.txt/raw?ref=main", null))
        Check(refused.StatusCode == HttpStatusCode.NotFound, "a private project's files are concealed from a stranger");

    // The whole tree, as one file.
    byte[] zip;
    using (HttpResponseMessage archive = await ApiGet(address, prefix + "/repository/archive.zip?sha=main", readApi))
    {
        zip = await archive.Content.ReadAsByteArrayAsync();
        Check(archive.IsSuccessStatusCode && archive.Content.Headers.ContentType?.MediaType == "application/octet-stream" &&
            archive.Content.Headers.ContentDisposition?.DispositionType == "attachment",
            "an archive is served as an attachment of unknown type too");
    }
    using (MemoryStream reading = new(zip))
    using (System.IO.Compression.ZipArchive opened = new(reading, System.IO.Compression.ZipArchiveMode.Read))
    {
        string[] names = [.. opened.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal)];
        string expectedPrefix = "fetchable-" + head[..8] + "/";
        Check(names.Length == 3 && names.All(name => name.StartsWith(expectedPrefix, StringComparison.Ordinal)),
            "the zip holds every file under one directory named for the project and the commit");
        System.IO.Compression.ZipArchiveEntry notes = opened.Entries.Single(entry => entry.FullName.EndsWith("notes.txt", StringComparison.Ordinal));
        using StreamReader reader = new(notes.Open());
        Check(await reader.ReadToEndAsync() == "first line\nsecond line\n", "and its contents are the file's exact bytes");
    }

    byte[] tarball;
    using (HttpResponseMessage archive = await ApiGet(address, prefix + "/repository/archive.tar.gz?sha=main", readApi))
        tarball = await archive.Content.ReadAsByteArrayAsync();
    using (MemoryStream reading = new(tarball))
    using (System.IO.Compression.GZipStream decompressed = new(reading, System.IO.Compression.CompressionMode.Decompress))
    using (System.Formats.Tar.TarReader tar = new(decompressed))
    {
        int entries = 0;
        bool executable = false;
        while (await tar.GetNextEntryAsync() is System.Formats.Tar.TarEntry entry)
        {
            entries++;
            if (entry.Name.EndsWith("run.sh", StringComparison.Ordinal))
                executable = entry.Mode.HasFlag(UnixFileMode.UserExecute);
        }
        Check(entries == 3, "the tarball holds the same three files");
        // The one permission bit Git keeps. A script that unpacked without it would not run.
        Check(executable, "and an executable file unpacks executable");
    }
    using (HttpResponseMessage nowhere = await ApiGet(address, prefix + "/repository/archive.zip?sha=no-such-revision", readApi))
        Check(nowhere.StatusCode == HttpStatusCode.NotFound, "an archive of a revision that does not resolve is a 404");
    using (HttpResponseMessage refused = await ApiGet(address, prefix + "/repository/archive.zip?sha=main", strangerApi))
        Check(refused.StatusCode == HttpStatusCode.NotFound, "an archive of a private project is concealed from a stranger");
    // The default extension is the tarball, so a caller that names no format still gets a real archive.
    using (HttpResponseMessage plain = await ApiGet(address, prefix + "/repository/archive?sha=main", readApi))
        Check(plain.IsSuccessStatusCode && (await plain.Content.ReadAsByteArrayAsync()).Length > 0,
            "the archive route with no extension answers with a tarball");

    // A tag is a revision like any other, which is what makes it useful to download by.
    using (HttpResponseMessage byTag = await ApiGet(address, prefix + "/repository/archive.zip?sha=v1.1.0", readApi))
        Check(byTag.IsSuccessStatusCode, "an archive can be taken at a tag");

    // The premise the commit page's fix rests on. It used to resolve a project through the caller's own
    // listing, which returns only what they own or belong to, so a commit link into a public project failed
    // for everybody else even though the commit was readable. The browser repository endpoint answers for
    // any reader the project is visible to, and these two assertions are the difference between them.
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
        await store.SetProjectVisibilityAsync(alice, projectId, ProjectVisibility.Public);
    using (HttpResponseMessage listing = await ApiGet(address, "/api/v4/projects", strangerApi))
    {
        using JsonDocument json = JsonDocument.Parse(await listing.Content.ReadAsStreamAsync());
        Check(listing.IsSuccessStatusCode && json.RootElement.EnumerateArray()
            .All(item => item.GetProperty("id").GetInt64() != projectId),
            "a public project a reader does not belong to is absent from their own project listing");
    }
    using (HttpResponseMessage resolved = await ApiGet(address, "/api/browser/projects/alice/fetchable", null))
    {
        using JsonDocument json = JsonDocument.Parse(await resolved.Content.ReadAsStreamAsync());
        Check(resolved.IsSuccessStatusCode && json.RootElement.GetProperty("project").GetProperty("id").GetInt64() == projectId,
            "and is still resolvable by path through the endpoint every page now uses");
    }
    using (HttpResponseMessage anonymous = await ApiGet(address, prefix + "/repository/files/notes.txt/raw?ref=main", null))
        Check(anonymous.IsSuccessStatusCode, "a public project's files can be downloaded with no credential");
    using (HttpResponseMessage viaBrowser = await ApiGet(address,
        "/api/projects/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        "/raw/notes.txt?ref=main", null))
    {
        byte[] body = await viaBrowser.Content.ReadAsByteArrayAsync();
        Check(viaBrowser.IsSuccessStatusCode && Encoding.UTF8.GetString(body) == "first line\nsecond line\n",
            "and through the route the page's download button actually uses");
    }
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
        await store.SetProjectVisibilityAsync(alice, projectId, ProjectVisibility.Private);
}

/// <summary>
/// The empty-repository page, which used to report a 404 from the browse endpoint and which the browser
/// then described as a permission problem.
/// </summary>
async Task CheckEmptyRepository(string address, AuthenticatedIdentity alice, string ownerPassword)
{
    long projectId;
    string apiToken;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo created = await store.CreateProjectWithRepositoryAsync(alice, "brand-new", "Brand new",
            (project, cancellation) => { cancellation.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; });
        projectId = created.Id;
        apiToken = (await store.CreateTokenAsync(alice, "empty-api", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1))).Secret;
    }

    CookieContainer cookies = new();
    using HttpClientHandler handler = new()
    {
        CookieContainer = cookies,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        UseProxy = false
    };
    using HttpClient browser = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) };
    using (HttpRequestMessage login = new(HttpMethod.Post, "/api/login"))
    {
        login.Headers.Add("Origin", address);
        login.Content = JsonContent.Create(new { username = "alice", password = ownerPassword });
        using HttpResponseMessage response = await browser.SendAsync(login);
        Check(response.IsSuccessStatusCode, "the owner signs in to look at an empty repository");
    }

    using (HttpResponseMessage page = await browser.GetAsync("/api/browser/projects/alice/brand-new"))
    {
        using JsonDocument json = JsonDocument.Parse(await page.Content.ReadAsStreamAsync());
        Check(page.IsSuccessStatusCode, "an empty repository answers with a page rather than a 404");
        Check(json.RootElement.GetProperty("branches").GetArrayLength() == 0 &&
            json.RootElement.GetProperty("commits").GetArrayLength() == 0 &&
            json.RootElement.GetProperty("tree").GetArrayLength() == 0 &&
            json.RootElement.GetProperty("readme").ValueKind == JsonValueKind.Null,
            "and reports itself empty rather than describing a tree it does not have");
        Check(json.RootElement.GetProperty("can_push").GetBoolean() &&
            json.RootElement.GetProperty("project").GetProperty("http_url_to_repo").GetString()!.EndsWith(".git", StringComparison.Ordinal),
            "and still carries what somebody needs to push the first commit");
    }
    // Still concealed from everyone it should be. Being empty is not a reason to be visible.
    using (HttpResponseMessage stranger = await ApiGet(address, "/api/browser/projects/alice/brand-new", null))
        Check(stranger.StatusCode == HttpStatusCode.NotFound, "an empty private repository is still a 404 for a stranger");
    using (HttpResponseMessage archive = await ApiGet(address,
        "/api/v4/projects/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        "/repository/archive.zip", apiToken))
        Check(archive.StatusCode == HttpStatusCode.NotFound, "an empty repository has no archive to take");
}

/// <summary>
/// Git compresses a fetch negotiation body once it passes about a kilobyte and offers no setting to stop
/// it, so a server that refuses Content-Encoding refuses exactly the fetches that need several rounds of
/// negotiation. That is what happens once two histories have diverged, which is why a clone of a new
/// repository worked and a pull against a busy one did not.
/// </summary>
async Task CheckCompressedGitRequests(string address, AuthenticatedIdentity alice)
{
    long projectId;
    string pushToken;
    await using (GraftStore store = await GraftStore.OpenAsync(databasePath))
    {
        ProjectInfo created = await store.CreateProjectWithRepositoryAsync(alice, "compressed", "Compressed requests",
            (project, cancellation) => { cancellation.ThrowIfCancellationRequested(); _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, project.StorageId)); return Task.CompletedTask; });
        projectId = created.Id;
        pushToken = (await store.CreateTokenAsync(alice, "compressed-git",
            TokenScope.ReadRepository | TokenScope.WriteRepository, DateTimeOffset.UtcNow.AddHours(1))).Secret;
    }
    _ = projectId;

    string clone = Path.Combine(root, "compressed-source");
    Directory.CreateDirectory(clone);
    await Git(clone, ["init", "--initial-branch=main"]);
    File.WriteAllText(Path.Combine(clone, "one.txt"), "one\n");
    await Git(clone, ["add", "."]);
    await Git(clone, ["commit", "-m", "One"]);
    await Git(clone, ["remote", "add", "origin", AuthenticatedUrl(address, "alice", pushToken, "alice/compressed.git")]);
    await Git(clone, ["push", "origin", "main"]);
    string head = (await Git(clone, ["rev-parse", "HEAD"])).Trim();

    // A real negotiation body: one want, a flush, and done. Sent twice, once as it stands and once gzipped,
    // because the useful assertion is not that gzip is accepted but that it changes nothing.
    byte[] request;
    using (MemoryStream building = new())
    {
        Packet.Write(building, "want " + head + "\n");
        Packet.Flush(building);
        Packet.Write(building, "done\n");
        request = building.ToArray();
    }
    string path = "/alice/compressed.git/git-upload-pack";

    byte[] plainBody;
    using (HttpResponseMessage plain = await GitPost(address, path, pushToken, request, encoding: null))
    {
        plainBody = await plain.Content.ReadAsByteArrayAsync();
        Check(plain.IsSuccessStatusCode && plainBody.Length > 0, "an uncompressed negotiation request is answered");
    }
    using (HttpResponseMessage compressed = await GitPost(address, path, pushToken, Gzip(request), encoding: "gzip"))
    {
        byte[] body = await compressed.Content.ReadAsByteArrayAsync();
        Check(compressed.IsSuccessStatusCode, "a gzipped negotiation request is answered rather than refused");
        Check(body.SequenceEqual(plainBody),
            "and answers exactly what the same request answered uncompressed, so compression changes nothing");
    }

    // The ceiling is applied to what comes out of the decompressor, not to what arrived, which is what lets
    // a compressed body be accepted at all. Twenty mebibytes of zeros is a few kilobytes on the wire and is
    // refused the moment it passes the limit while expanding.
    byte[] bomb = Gzip(new byte[20 * 1024 * 1024]);
    Check(bomb.Length < 128 * 1024, "the oversized body really is small while compressed");
    using (HttpResponseMessage refused = await GitPost(address, path, pushToken, bomb, encoding: "gzip"))
    {
        string message = await refused.Content.ReadAsStringAsync();
        Check(refused.StatusCode == HttpStatusCode.BadRequest && message.Contains("request-limit", StringComparison.Ordinal),
            "a small body that expands past the limit is still refused, and says which limit");
    }

    using (HttpResponseMessage refused = await GitPost(address, path, pushToken, request, encoding: "br"))
    {
        string message = await refused.Content.ReadAsStringAsync();
        Check(refused.StatusCode == HttpStatusCode.BadRequest && message.Contains("content-encoding", StringComparison.Ordinal),
            "an encoding Git never sends is still refused by name");
    }

    using (HttpResponseMessage refused = await GitPost(address, path, pushToken, request, encoding: "gzip"))
        Check(refused.StatusCode == HttpStatusCode.BadRequest,
            "a body that claims to be gzip and is not is refused rather than read as rubbish");

    // The whole point, end to end: a fetch that has to negotiate over a history long enough that Git
    // compresses the request. Enough commits that the client's "have" lines pass a kilobyte on their own.
    string other = Path.Combine(root, "compressed-other");
    await Git(root, ["clone", AuthenticatedUrl(address, "alice", pushToken, "alice/compressed.git"), "compressed-other"]);
    for (int commit = 0; commit < 60; commit++)
    {
        File.WriteAllText(Path.Combine(clone, "one.txt"), "one " + commit.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        await Git(clone, ["commit", "-am", "Change " + commit.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
    }
    await Git(clone, ["push", "origin", "main"]);
    for (int commit = 0; commit < 60; commit++)
    {
        File.WriteAllText(Path.Combine(other, "mine.txt"), "mine " + commit.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        await Git(other, ["add", "."]);
        await Git(other, ["commit", "-m", "Mine " + commit.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
    }
    // Diverged on both sides, which is what makes Git send rounds of "have" lines rather than one small
    // request. This is the case that failed before.
    await Git(other, ["fetch", "origin"]);
    Check((await Git(other, ["rev-parse", "origin/main"])).Trim() ==
        (await Git(clone, ["rev-parse", "HEAD"])).Trim(),
        "a real fetch against a diverged history negotiates and completes");
}

static byte[] Gzip(byte[] content)
{
    using MemoryStream output = new();
    using (System.IO.Compression.GZipStream compressing =
        new(output, System.IO.Compression.CompressionLevel.SmallestSize, true))
        compressing.Write(content);
    return output.ToArray();
}

/// <summary>
/// Posts a Git request body directly, so the Content-Encoding header can be set to things a real client
/// would not send. Basic authentication, as the Git transport uses.
/// </summary>
async Task<HttpResponseMessage> GitPost(string address, string path, string token, byte[] body, string? encoding)
{
    HttpClientHandler handler = new()
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        UseProxy = false
    };
    HttpClient client = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(60) };
    HttpRequestMessage request = new(HttpMethod.Post, path) { Content = new ByteArrayContent(body) };
    request.Content.Headers.ContentType = new("application/x-git-upload-pack-request");
    if (encoding is not null) request.Content.Headers.ContentEncoding.Add(encoding);
    request.Headers.Authorization = new("Basic",
        Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:" + token)));
    HttpResponseMessage response = await client.SendAsync(request);
    request.Dispose();
    client.Dispose();
    handler.Dispose();
    return response;
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
static string Redact(string message) => System.Text.RegularExpressions.Regex.Replace(message, "graft_pat_[A-Za-z0-9_-]+", "graft_pat_REDACTED");
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
