using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Graft.Core;
using Graft.Git;
using Graft.Operations;
using Graft.Storage;
using Microsoft.Data.Sqlite;

if (args.Length != 2 || !File.Exists(args[0]) || !File.Exists(args[1]))
{
    Console.Error.WriteLine("Usage: Graft.Operations.Acceptance <Graft.Server DLL or executable> <Graft.Admin DLL or executable>");
    return 2;
}

string serverPath = Path.GetFullPath(args[0]);
string adminPath = Path.GetFullPath(args[1]);
string root = Path.Combine(Path.GetTempPath(), "graft-mvp07-" + Guid.NewGuid().ToString("N"));
string activeDatabase = Path.Combine(root, "active", "graft.db");
string activeRepositories = Path.Combine(root, "active", "repositories");
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
    string bootstrapDatabase = Path.Combine(root, "bootstrap", "graft.db");
    string bootstrapRepositories = Path.Combine(root, "bootstrap", "repositories");
    string bootstrapPassword = Password();
    string bootstrapPasswordFile = Path.Combine(root, "bootstrap.password");
    File.WriteAllText(bootstrapPasswordFile, bootstrapPassword + Environment.NewLine, new UTF8Encoding(false));
    _ = await StartServer(bootstrapDatabase, bootstrapRepositories);
    await Throws<InvalidOperationException>(() => Admin(["bootstrap", "--database", bootstrapDatabase,
        "--repositories", bootstrapRepositories, "--username", "operator", "--password-file", bootstrapPasswordFile]),
        "running server blocks CLI bootstrap");
    await StopServer();
    await Admin(["bootstrap", "--database", bootstrapDatabase, "--repositories", bootstrapRepositories,
        "--username", "operator", "--password-file", bootstrapPasswordFile]);
    await using (GraftStore bootstrapped = await GraftStore.OpenAsync(bootstrapDatabase))
    {
        SessionGrant? session = await bootstrapped.LoginAsync("operator", bootstrapPassword);
        AuthenticatedIdentity identity = Required(session is null ? null : await bootstrapped.ResolveSessionAsync(session.Secret),
            "CLI bootstrap administrator login");
        Check(identity.IsAdministrator, "CLI bootstrap grants administrator role");
        Check(!await bootstrapped.ContainsStoredSecretAsync(bootstrapPassword), "CLI bootstrap hashes password at rest");
    }
    await Throws<InvalidOperationException>(() => Admin(["bootstrap", "--database", bootstrapDatabase,
        "--repositories", bootstrapRepositories, "--username", "second", "--password-file", bootstrapPasswordFile]),
        "CLI bootstrap is one-use");

    string adminPassword = Password();
    string alicePassword = Password();
    TokenGrant importToken;
    ProjectInfo project;
    await using (GraftStore fixture = await GraftStore.OpenAsync(activeDatabase))
    {
        await fixture.BootstrapAdministratorAsync("admin", adminPassword);
        SessionGrant adminSession = Required(await fixture.LoginAsync("admin", adminPassword), "admin login");
        AuthenticatedIdentity admin = Required(await fixture.ResolveSessionAsync(adminSession.Secret), "admin identity");
        UserAccount alice = await fixture.CreateUserAsync(admin, "alice", alicePassword);
        SessionGrant aliceSession = Required(await fixture.LoginAsync(alice.Username, alicePassword), "alice login");
        AuthenticatedIdentity aliceIdentity = Required(await fixture.ResolveSessionAsync(aliceSession.Secret), "alice identity");
        project = await fixture.CreateProjectWithRepositoryAsync(aliceIdentity, "imported", "Synthetic import",
            (created, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = new DurableRepositoryStorage(Path.Combine(activeRepositories, created.StorageId));
                return Task.CompletedTask;
            });
        importToken = await fixture.CreateTokenAsync(aliceIdentity, "import-token",
            TokenScope.ReadRepository | TokenScope.WriteRepository, DateTimeOffset.UtcNow.AddHours(1));
    }

    string source = Path.Combine(root, "source");
    Directory.CreateDirectory(source);
    await Git(source, ["init", "--initial-branch=main"]);
    File.WriteAllText(Path.Combine(source, "README.md"), "synthetic GitLab repository\n");
    Directory.CreateDirectory(Path.Combine(source, "src"));
    File.WriteAllBytes(Path.Combine(source, "src", "payload.bin"), [0, 1, 2, 0, 255]);
    await Git(source, ["add", "."]);
    await Git(source, ["commit", "-m", "Initial import fixture"]);
    await Git(source, ["branch", "feature"]);
    await Git(source, ["tag", "-a", "v1.0", "-m", "Synthetic release"]);
    string sourceHashBefore = HashTree(source);
    string bundle = Path.Combine(root, "source.bundle");
    await Git(source, ["bundle", "create", bundle, "--all"]);
    GitBundleInventory inventory = GitBundleInventory.Read(bundle);
    Check(inventory.Format == "v2" && inventory.Sha256.Length == 64, "bundle inventory and digest");
    Check(inventory.Refs.Any(item => item.Name == "refs/heads/main") &&
        inventory.Refs.Any(item => item.Name == "refs/heads/feature") &&
        inventory.Refs.Any(item => item.Name == "refs/tags/v1.0"), "bundle inventory includes branches and tag");
    Check(inventory.Excluded.Length == 8 && inventory.Excluded.Any(item => item.Area == "collaboration") &&
        inventory.Excluded.Any(item => item.Area == "ci-cd"), "repository-only loss report is explicit");
    string inventoryReport = Path.Combine(root, "import-inventory.json");
    await Admin(["inspect-bundle", "--input", bundle, "--output", inventoryReport]);
    using (JsonDocument report = JsonDocument.Parse(File.ReadAllText(inventoryReport)))
        Check(report.RootElement.GetProperty("refs").GetArrayLength() == inventory.Refs.Length &&
            report.RootElement.GetProperty("excluded").GetArrayLength() == inventory.Excluded.Length,
            "admin CLI writes import inventory and loss report");

    string address = await StartServer(activeDatabase, activeRepositories);
    await Throws<InvalidOperationException>(() => BackupManager.CreateAsync(activeDatabase, activeRepositories,
        Path.Combine(root, "must-not-exist")), "running server blocks backup");
    string target = AuthenticatedUrl(address, "alice", importToken.Secret, "alice/imported.git");
    string mirror = Path.Combine(root, "mirror.git");
    await Git(root, ["clone", "--mirror", source, mirror]);
    await Git(mirror, ["remote", "add", "graft", target]);
    await PushRepository(mirror);
    Dictionary<string, string> firstImport = ParseRefs(await Git(root, ["ls-remote", target]));
    AssertImportedRefs(inventory, firstImport, "initial import");
    await PushRepository(mirror);
    Dictionary<string, string> repeatedImport = ParseRefs(await Git(root, ["ls-remote", target]));
    AssertImportedRefs(inventory, repeatedImport, "repeat import");
    Check(firstImport.OrderBy(item => item.Key).SequenceEqual(repeatedImport.OrderBy(item => item.Key)),
        "repeat import leaves target refs unchanged");
    Check(HashTree(source) == sourceHashBefore && (await Git(source, ["status", "--porcelain=v1"])).Length == 0,
        "source repository remains unchanged");
    await StopServer();

    string backup = Path.Combine(root, "backup");
    await Admin(["backup", "--database", activeDatabase, "--repositories", activeRepositories, "--output", backup]);
    BackupManifest manifest = await BackupManager.VerifyAsync(backup);
    Check(manifest.FormatVersion == 1 && manifest.Files.Any(file => file.Path == "database.sqlite"),
        "backup manifest inventories database");
    Check(manifest.Files.Any(file => file.Path.StartsWith("repositories/", StringComparison.Ordinal)),
        "backup manifest inventories repository content");
    await Admin(["verify", "--input", backup]);
    BackupManifest verified = await BackupManager.VerifyAsync(backup);
    Check(verified.Files.Length == manifest.Files.Length, "backup hashes and SQLite integrity verify");

    string undeclared = Path.Combine(root, "undeclared-backup");
    CopyTree(backup, undeclared);
    Directory.CreateDirectory(Path.Combine(undeclared, "unexpected-empty-directory"));
    await Throws<InvalidDataException>(() => BackupManager.VerifyAsync(undeclared), "undeclared backup path is rejected");

    string tampered = Path.Combine(root, "tampered-backup");
    CopyTree(backup, tampered);
    string tamperedFile = Directory.EnumerateFiles(Path.Combine(tampered, "repositories"), "*", SearchOption.AllDirectories).First();
    File.AppendAllText(tamperedFile, "tampered");
    await Throws<InvalidDataException>(() => BackupManager.VerifyAsync(tampered), "tampered backup is rejected");

    string candidateDatabase = Path.Combine(root, "candidate", "graft.db");
    string candidateRepositories = Path.Combine(root, "candidate", "repositories");
    await Admin(["restore", "--input", backup, "--database", candidateDatabase, "--repositories", candidateRepositories]);
    await VerifyRestored(candidateDatabase, candidateRepositories, importToken.Secret, project, firstImport, "restored candidate");
    await Throws<IOException>(() => BackupManager.RestoreAsync(backup, candidateDatabase, candidateRepositories),
        "restore refuses to overwrite existing data");

    await using (SqliteConnection damaged = new($"Data Source={candidateDatabase};Pooling=False"))
    {
        await damaged.OpenAsync();
        await using SqliteCommand command = damaged.CreateCommand();
        command.CommandText = "DROP TABLE ProjectMemberships;";
        await command.ExecuteNonQueryAsync();
    }
    await Throws<SqliteException>(async () =>
    {
        await using GraftStore broken = await GraftStore.OpenAsync(candidateDatabase);
        AuthenticatedIdentity identity = Required(await broken.ResolveTokenAsync(importToken.Secret), "broken token identity");
        _ = await broken.GetPermissionAsync(identity, project.Id, ProjectAction.BrowseRepository);
    }, "synthetic incompatible upgrade fails preflight");

    string rollbackDatabase = Path.Combine(root, "rollback", "graft.db");
    string rollbackRepositories = Path.Combine(root, "rollback", "repositories");
    await Admin(["restore", "--input", backup, "--database", rollbackDatabase, "--repositories", rollbackRepositories]);
    await VerifyRestored(rollbackDatabase, rollbackRepositories, importToken.Secret, project, firstImport, "rollback restore");

    address = await StartServer(rollbackDatabase, rollbackRepositories);
    string restoredUrl = AuthenticatedUrl(address, "alice", importToken.Secret, "alice/imported.git");
    await Git(root, ["clone", "--no-checkout", restoredUrl, "restored-clone"]);
    await Git(Path.Combine(root, "restored-clone"), ["fsck", "--full", "--strict"]);
    Check(true, "restored server clone passes strict fsck");
    await StopServer();

    Console.WriteLine($"RESULT: PASS; assertions: {assertions}");
    Console.WriteLine("BOUNDARY: synthetic repository-only import over Git HTTPS; quiesced local backup and fresh-target restore.");
    return 0;
}
finally
{
    await StopServer();
    DeleteTree(root);
}

async Task VerifyRestored(string database, string repositories, string secret, ProjectInfo expectedProject,
    Dictionary<string, string> expectedRefs, string label)
{
    await using GraftStore restored = await GraftStore.OpenAsync(database);
    AuthenticatedIdentity identity = Required(await restored.ResolveTokenAsync(secret), label + " token");
    ProjectInfo? actualProject = await restored.FindReadyProjectAsync("alice", "imported");
    Check(actualProject?.StorageId == expectedProject.StorageId, label + " project metadata");
    PermissionDecision permission = await restored.GetPermissionAsync(identity, expectedProject.Id, ProjectAction.PushRepository);
    Check(permission.Allowed, label + " permissions");
    Repository repository = new(new DurableRepositoryStorage(Path.Combine(repositories, expectedProject.StorageId)));
    foreach ((string name, string id) in expectedRefs.Where(item => item.Key.StartsWith("refs/", StringComparison.Ordinal)))
        Check(repository.Refs.GetValueOrDefault(name) == id, label + " ref " + name);
}

async Task<string> StartServer(string database, string repositories)
{
    TaskCompletionSource<string> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    serverLines = new();
    ProcessStartInfo start = ServerStart(database, repositories);
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

ProcessStartInfo ServerStart(string database, string repositories)
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
    start.ArgumentList.Add(database);
    start.ArgumentList.Add("--repositories");
    start.ArgumentList.Add(repositories);
    return start;
}

async Task<string> Git(string directory, string[] arguments)
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
        "core.autocrlf=false", "http.proxy=", "http.sslVerify=false", "protocol.file.allow=always"
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
        string error = await errors;
        if (process.ExitCode != 0) throw new InvalidOperationException("Git " + arguments[0] + " failed: " + error);
        return await output;
    }
    finally
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
    }
}

async Task Admin(string[] arguments)
{
    bool dll = adminPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    ProcessStartInfo start = new(dll ? "dotnet" : adminPath)
    {
        WorkingDirectory = root,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    if (dll) start.ArgumentList.Add(adminPath);
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using Process process = Process.Start(start)!;
    Task<string> output = process.StandardOutput.ReadToEndAsync();
    Task<string> errors = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
    string error = await errors;
    if (process.ExitCode != 0) throw new InvalidOperationException("Graft.Admin " + arguments[0] + " failed: " + error);
    _ = await output;
    assertions++;
}

async Task PushRepository(string mirror)
{
    await Git(mirror, ["push", "graft", "--all"]);
    await Git(mirror, ["push", "graft", "--tags"]);
}

void AssertImportedRefs(GitBundleInventory inventory, Dictionary<string, string> targetRefs, string label)
{
    foreach (ImportRef expected in inventory.Refs.Where(item => item.Name.StartsWith("refs/", StringComparison.Ordinal)))
        Check(targetRefs.GetValueOrDefault(expected.Name) == expected.ObjectId, label + " preserves " + expected.Name);
}

static Dictionary<string, string> ParseRefs(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
    .Select(line => line.TrimEnd('\r').Split('\t'))
    .Where(parts => parts.Length == 2 && parts[1].StartsWith("refs/", StringComparison.Ordinal) && !parts[1].EndsWith("^{}", StringComparison.Ordinal))
    .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);

static string AuthenticatedUrl(string address, string username, string token, string path)
{
    Uri uri = new(address);
    return $"{uri.Scheme}://{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(token)}@{uri.Authority}/{path}";
}

static string HashTree(string path)
{
    using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .OrderBy(item => Path.GetRelativePath(path, item), StringComparer.Ordinal))
    {
        byte[] name = Encoding.UTF8.GetBytes(Path.GetRelativePath(path, file).Replace('\\', '/'));
        hash.AppendData(name);
        hash.AppendData([0]);
        using FileStream input = File.OpenRead(file);
        byte[] buffer = new byte[81920];
        int count;
        while ((count = input.Read(buffer)) > 0) hash.AppendData(buffer.AsSpan(0, count));
    }
    return Convert.ToHexStringLower(hash.GetHashAndReset());
}

static void CopyTree(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        string target = Path.Combine(destination, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
}

static void DeleteTree(string path)
{
    if (!Directory.Exists(path)) return;
    foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        File.SetAttributes(file, FileAttributes.Normal);
    foreach (string directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
        File.SetAttributes(directory, FileAttributes.Directory);
    Directory.Delete(path, recursive: true);
}

async Task Throws<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { assertions++; return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + name);
}

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("Assertion failed: " + name);
    assertions++;
}

static T Required<T>(T? value, string name) where T : class =>
    value ?? throw new InvalidOperationException("Missing fixture value: " + name);

static string Password() => "P!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
