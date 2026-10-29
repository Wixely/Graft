using System.Diagnostics;
using System.Text;
using Graft.Core;
using Graft.Git;
using Graft.Storage;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: Graft.Browser.Fixture <Graft.Server DLL or executable>");
    return 2;
}

// This fixture builds a throwaway instance in a new temporary directory and destroys it on exit, so it
// defaults to credentials nobody has to type. GRAFT_BROWSER_FIXTURE_PASSWORD still overrides them. The
// default only works against a Debug server: Release keeps the 16-character minimum and would reject it.
const string DefaultUsername = "admin";
const string DefaultPassword = "admin";
string username = Environment.GetEnvironmentVariable("GRAFT_BROWSER_FIXTURE_USERNAME") ?? DefaultUsername;
string password = Environment.GetEnvironmentVariable("GRAFT_BROWSER_FIXTURE_PASSWORD") ?? DefaultPassword;
if (!PasswordPolicy.IsAcceptableLength(password))
{
    Console.Error.WriteLine($"GRAFT_BROWSER_FIXTURE_PASSWORD must be between {PasswordPolicy.MinimumLength} and " +
        $"{PasswordPolicy.MaximumLength} characters for this build.");
    return 2;
}
if (password == DefaultPassword && !PasswordPolicy.RelaxedForLocalDebug)
{
    Console.Error.WriteLine("This is a Release build, which requires a 16-character password. " +
        "Set GRAFT_BROWSER_FIXTURE_PASSWORD, or run the Debug server for local debugging.");
    return 2;
}

string serverPath = Path.GetFullPath(args[0]);
string root = Path.Combine(Path.GetTempPath(), "graft-browser-" + Guid.NewGuid().ToString("N"));
string databasePath = Path.Combine(root, "graft.db");
string repositoriesRoot = Path.Combine(root, "repositories");
string gitConfig = Path.Combine(root, "empty.gitconfig");
Directory.CreateDirectory(root);
File.WriteAllText(gitConfig, "");

// The port is chosen here rather than by the server, because the pages origin has to be named before the
// server starts and it has to carry the same port.
int port = FreePort();
string pagesOrigin = "https://localhost:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
Process? server = null;
Task? outputReader = null;
Task<string>? errorReader = null;

try
{
    TokenGrant token;
    await using (GraftStore fixture = await GraftStore.OpenAsync(databasePath))
    {
        UserAccount user = await fixture.BootstrapAdministratorAsync(username, password);
        SessionGrant session = (await fixture.LoginAsync(user.Username, password))!;
        AuthenticatedIdentity identity = (await fixture.ResolveSessionAsync(session.Secret))!;
        ProjectInfo project = await fixture.CreateProjectWithRepositoryAsync(identity, "seeded", "Seeded repository",
            (created, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, created.StorageId));
                return Task.CompletedTask;
            });
        token = await fixture.CreateTokenAsync(identity, "browser-fixture", TokenScope.WriteRepository,
            DateTimeOffset.UtcNow.AddHours(2));

        // Three more projects, one for each way a project can be published, so the public browser has
        // something to list and the difference between them is visible rather than described.
        ProjectInfo open = await fixture.CreateProjectWithRepositoryAsync(identity, "open", "Open source",
            (created, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, created.StorageId));
                return Task.CompletedTask;
            }, "Readable by anyone, and listed for everyone.");
        ProjectInfo unlisted = await fixture.CreateProjectWithRepositoryAsync(identity, "unlisted", "Unlisted project",
            (created, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, created.StorageId));
                return Task.CompletedTask;
            }, "Reachable only by its path.");
        ProjectInfo card = await fixture.CreateProjectWithRepositoryAsync(identity, "page-only", "Page only",
            (created, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = new DurableRepositoryStorage(Path.Combine(repositoriesRoot, created.StorageId));
                return Task.CompletedTask;
            }, "A published page over a private repository.");
        await fixture.SetProjectVisibilityAsync(identity, open.Id, ProjectVisibility.Public);
        await fixture.SetProjectVisibilityAsync(identity, unlisted.Id, ProjectVisibility.Unlisted);
        await fixture.SetPagePublishedAsync(identity, card.Id, true);
    }

    string address = await StartServer();
    string source = Path.Combine(root, "source");
    Directory.CreateDirectory(source);
    await Git(source, ["init", "--initial-branch=main"]);
    File.WriteAllText(Path.Combine(source, "readme.txt"), "Graft browser fixture\n");
    File.WriteAllText(Path.Combine(source, "hostile.html"), "<script>throw new Error('must remain text')</script>\n");
    File.WriteAllBytes(Path.Combine(source, "binary.dat"), [0, 1, 2, 0, 255]);
    // A README exercising every piece of syntax the renderer supports, with a real image beside it so the
    // relative-image path is shown resolving out of the repository rather than being described.
    File.WriteAllBytes(Path.Combine(source, "README.md"), Fixture("fixture/README.md"));
    Directory.CreateDirectory(Path.Combine(source, "docs"));
    File.WriteAllBytes(Path.Combine(source, "docs", "graft-readme-blue.png"), Fixture("fixture/graft-readme-blue.png"));
    await Git(source, ["add", "."]);
    await Git(source, ["commit", "-m", "Seed browser fixture"]);
    await Git(source, ["remote", "add", "origin", AuthenticatedUrl(address, username, token.Secret, username + "/seeded.git")]);
    await Git(source, ["push", "origin", "main"]);

    // The page-only project gets the committed sample pushed into it, so the fixture shows a real curated
    // page and a real site rather than an empty page that is technically correct and demonstrates nothing.
    string sample = Path.Combine(root, "page-sample");
    Directory.CreateDirectory(Path.Combine(sample, ".graft"));
    Directory.CreateDirectory(Path.Combine(sample, "pages"));
    Directory.CreateDirectory(Path.Combine(sample, "docs"));
    Directory.CreateDirectory(Path.Combine(sample, "src"));
    await Git(sample, ["init", "--initial-branch=main"]);
    File.WriteAllBytes(Path.Combine(sample, ".graft", "project.json"), Fixture("sample/project.json"));
    File.WriteAllBytes(Path.Combine(sample, "README.md"), Fixture("sample/README.md"));
    File.WriteAllBytes(Path.Combine(sample, "compose.yaml"), Fixture("sample/compose.yaml"));
    File.WriteAllBytes(Path.Combine(sample, "docs", "guide.md"), Fixture("sample/docs-guide.md"));
    File.WriteAllBytes(Path.Combine(sample, "pages", "index.html"), Fixture("sample/pages-index.html"));
    File.WriteAllBytes(Path.Combine(sample, "pages", "about.html"), Fixture("sample/pages-about.html"));
    File.WriteAllBytes(Path.Combine(sample, "pages", "style.css"), Fixture("sample/pages-style.css"));
    File.WriteAllBytes(Path.Combine(sample, "pages", "lantern.svg"), Fixture("sample/pages-lantern.svg"));
    // Present so that the allowlist can be seen refusing something, rather than only admitting things.
    File.WriteAllText(Path.Combine(sample, "src", "Secrets.cs"),
        "// If this appears on the page or the site, the allowlist failed.\nconst string Key = \"never-served\";\n");
    await Git(sample, ["add", "."]);
    await Git(sample, ["commit", "-m", "A site in the pages folder, and a page config"]);
    await Git(sample, ["push", AuthenticatedUrl(address, username, token.Secret, username + "/page-only.git"), "main"]);

    // A second branch to propose, so the merge request below is a real proposal over real commits rather
    // than a row describing branches that do not exist.
    await Git(source, ["checkout", "-b", "lantern"]);
    File.WriteAllText(Path.Combine(source, "lantern.txt"), "A lantern, added on a branch.\n");
    await Git(source, ["add", "."]);
    await Git(source, ["commit", "-m", "Add a lantern on a branch"]);
    await Git(source, ["push", "origin", "lantern"]);

    // Conversation to look at. Seeded through the store rather than the API because the fixture already
    // holds an identity and this needs no HTTP.
    await using (GraftStore fixture = await GraftStore.OpenAsync(databasePath))
    {
        SessionGrant session = (await fixture.LoginAsync(username, password))!;
        AuthenticatedIdentity identity = (await fixture.ResolveSessionAsync(session.Secret))!;
        ProjectInfo seeded = (await fixture.FindReadyProjectAsync(username, "seeded"))!;
        Issue first = await fixture.CreateIssueAsync(identity, seeded.Id, "The lantern flickers",
            "It dims every few seconds, and only after the room has been dark for a while.\n\nSteps:\n1. Turn it on.\n2. Wait.");
        await fixture.AddCommentAsync(identity, seeded.Id, CommentSubject.Issue, first.Iid,
            "Reproduced. It looks like the wick, not the oil.");
        Issue second = await fixture.CreateIssueAsync(identity, seeded.Id, "Document the oil ratio");
        await fixture.UpdateIssueAsync(identity, seeded.Id, second.Iid, null, null, CollaborationState.Closed);
        await fixture.CreateMergeProposalAsync(identity, seeded.Id, "lantern", "main",
            "Add a lantern", "Adds one file and changes nothing else, so it should merge cleanly.");
    }

    // Echo the credentials only when they are the throwaway defaults, so a supplied password is never logged.
    Console.WriteLine(password == DefaultPassword
        ? $"FIXTURE: username={username}; password={password}; project={username}/seeded"
        : $"FIXTURE: username={username}; password=(from GRAFT_BROWSER_FIXTURE_PASSWORD); project={username}/seeded");
    // Both halves of the project page, printed so a developer does not have to work out either address.
    // The page is on the forge; the site is on the pages origin, which is the same listener under a name a
    // browser treats as a different origin.
    // The token too, under the same rule: it is a throwaway on a disposable fixture, and a developer driving the
    // API or a package client by hand needs one.
    if (password == DefaultPassword) Console.WriteLine("TOKEN: " + token.Secret);
    Console.WriteLine("PAGE: " + address + "/-/p/page-only");
    Console.WriteLine("SITE: " + pagesOrigin + "/page-only/");
    Console.WriteLine("ISSUES: " + address + "/" + username + "/seeded/-/issues");
    Console.WriteLine("MERGE: " + address + "/" + username + "/seeded/-/merge-requests");
    Console.WriteLine("READY: " + address + "/projects");
    Console.WriteLine("Press Enter to stop and remove the disposable fixture.");
    await Console.In.ReadLineAsync();
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("RESULT: FAIL: " + error.GetType().Name + ": " + Redact(error.Message));
    return 1;
}
finally
{
    if (server is not null)
    {
        if (!server.HasExited) server.Kill(entireProcessTree: true);
        await server.WaitForExitAsync();
        if (outputReader is not null) await outputReader;
        if (errorReader is not null)
        {
            string errors = await errorReader;
            if (!string.IsNullOrWhiteSpace(errors)) Console.Error.WriteLine("WARN: server wrote an error.");
        }
        server.Dispose();
    }
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try
    {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
    catch (IOException) { Console.Error.WriteLine("WARN: temporary fixture cleanup failed."); }
}

async Task<string> StartServer()
{
    TaskCompletionSource<string> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    ProcessStartInfo start = new(serverPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : serverPath)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = root
    };
    if (serverPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(serverPath);
    start.ArgumentList.Add("--database");
    start.ArgumentList.Add(databasePath);
    start.ArgumentList.Add("--repositories");
    start.ArgumentList.Add(repositoriesRoot);
    // Publishing is on in the fixture so the public browser and the visibility controls can be looked at.
    // It is off by default in the product, and a fixture is not a deployment.
    start.ArgumentList.Add("--public-browsing");
    start.ArgumentList.Add("true");
    // The forge listens on a port this fixture chose, so it can also name a pages origin on the same port.
    start.ArgumentList.Add("--listen");
    start.ArgumentList.Add("https://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    // `localhost` and `127.0.0.1` are the same listener and are covered by the same generated certificate,
    // but they are different origins to a browser, which is the only property a project site depends on.
    // So a developer gets a working pages origin with no hosts file to edit and no second certificate.
    start.ArgumentList.Add("--pages-origin");
    start.ArgumentList.Add(pagesOrigin);
    server = Process.Start(start)!;
    Process active = server;
    // Whatever the server prints after it is ready is forwarded under a prefix, so a developer driving the
    // fixture by hand sees what the server logged about the request they just made.
    errorReader = Task.Run(async () =>
    {
        StringBuilder all = new();
        while (await active.StandardError.ReadLineAsync() is string line) { all.AppendLine(line); Console.Error.WriteLine("SERVER: " + line); }
        return all.ToString();
    });
    outputReader = Task.Run(async () =>
    {
        while (await active.StandardOutput.ReadLineAsync() is string line)
        {
            if (line.StartsWith("READY: ", StringComparison.Ordinal)) ready.TrySetResult(line[7..]);
            else if (ready.Task.IsCompleted) Console.WriteLine("SERVER: " + line);
        }
    });
    return await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
}

async Task Git(string directory, string[] arguments)
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
        _ = await output;
        _ = await errors;
        if (process.ExitCode != 0) throw new InvalidOperationException("Git " + arguments[0] + " failed.");
    }
    finally
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
    }
}

/// <summary>Reads one of the seeded fixture files embedded in this executable.</summary>
static byte[] Fixture(string name)
{
    using Stream stream = typeof(Program).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException("The fixture resource " + name + " was not embedded in this build.");
    using MemoryStream copy = new();
    stream.CopyTo(copy);
    return copy.ToArray();
}

static string AuthenticatedUrl(string address, string username, string token, string path) =>
    address.Replace("https://", "https://" + Uri.EscapeDataString(username) + ":" + Uri.EscapeDataString(token) + "@", StringComparison.Ordinal) + "/" + path;
static string Redact(string message) => message.Contains("graft_pat_", StringComparison.Ordinal) ? "credential-bearing operation failed" : message;

/// <summary>A free loopback port, so the fixture can name the pages origin before the server starts.</summary>
static int FreePort()
{
    using System.Net.Sockets.Socket probe = new(System.Net.Sockets.AddressFamily.InterNetwork,
        System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
    probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
    return ((System.Net.IPEndPoint)probe.LocalEndPoint!).Port;
}
