using System.Diagnostics;
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
    }

    string address = await StartServer();
    string source = Path.Combine(root, "source");
    Directory.CreateDirectory(source);
    await Git(source, ["init", "--initial-branch=main"]);
    File.WriteAllText(Path.Combine(source, "readme.txt"), "Graft browser fixture\n");
    File.WriteAllText(Path.Combine(source, "hostile.html"), "<script>throw new Error('must remain text')</script>\n");
    File.WriteAllBytes(Path.Combine(source, "binary.dat"), [0, 1, 2, 0, 255]);
    await Git(source, ["add", "."]);
    await Git(source, ["commit", "-m", "Seed browser fixture"]);
    await Git(source, ["remote", "add", "origin", AuthenticatedUrl(address, username, token.Secret, username + "/seeded.git")]);
    await Git(source, ["push", "origin", "main"]);

    // Echo the credentials only when they are the throwaway defaults, so a supplied password is never logged.
    Console.WriteLine(password == DefaultPassword
        ? $"FIXTURE: username={username}; password={password}; project={username}/seeded"
        : $"FIXTURE: username={username}; password=(from GRAFT_BROWSER_FIXTURE_PASSWORD); project={username}/seeded");
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
    server = Process.Start(start)!;
    Process active = server;
    errorReader = active.StandardError.ReadToEndAsync();
    outputReader = Task.Run(async () =>
    {
        while (await active.StandardOutput.ReadLineAsync() is string line)
        {
            if (line.StartsWith("READY: ", StringComparison.Ordinal)) ready.TrySetResult(line[7..]);
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

static string AuthenticatedUrl(string address, string username, string token, string path) =>
    address.Replace("https://", "https://" + Uri.EscapeDataString(username) + ":" + Uri.EscapeDataString(token) + "@", StringComparison.Ordinal) + "/" + path;
static string Redact(string message) => message.Contains("graft_pat_", StringComparison.Ordinal) ? "credential-bearing operation failed" : message;
