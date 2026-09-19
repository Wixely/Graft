using System.Diagnostics;
using System.Runtime.CompilerServices;
using Dapper;
using DnaX.Data.Migrations;
using DnaX.Data.Migrations.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

[module: DapperAot]

Console.WriteLine("Graft storage migration probe v1 (2026-09-19)");
Console.WriteLine($"MODE: {(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT")}");

var failures = 0;
var root = Path.Combine(Path.GetTempPath(), "graft-storage-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    await Check("fresh schema, repeat no-op and restart", () => FreshNoOpAndRestart(Path.Combine(root, "fresh.db")));
    await Check("v1 to v2 data preservation", () => UpgradePreservesData(Path.Combine(root, "upgrade.db")));
    await Check("altered checksum refusal", () => AlteredChecksumIsRefused(Path.Combine(root, "drift.db")));
    await Check("future version refusal", () => FutureVersionIsRefused(Path.Combine(root, "future.db")));
    await Check("concurrent migration lock timeout", () => LockTimeoutIsBounded(Path.Combine(root, "locked.db")));
    await Check("failure rollback and clean restart", () => FailureRollsBackAndRestartSucceeds(Path.Combine(root, "failure.db")));
}
finally
{
    SqliteConnection.ClearAllPools();
    try { Directory.Delete(root, recursive: true); }
    catch { Console.WriteLine("WARN: temporary probe directory could not be removed."); }
}

Console.WriteLine($"RESULT: {(failures == 0 ? "PASS" : "FAIL")}; failed checks: {failures}");
var platform = OperatingSystem.IsWindows() ? "Windows" : "Linux";
Console.WriteLine($"LIMITS: local {platform} execution only; no production schema, backup/restore, power-loss or required-host validation.");
return failures == 0 ? 0 : 1;

async Task Check(string name, Func<Task> action)
{
    Console.WriteLine("CHECK: " + name);
    try
    {
        await action();
        Console.WriteLine("PASS: " + name);
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL: {name}; {ex.GetType().Name}; HResult=0x{ex.HResult:X8}");
    }
}

static async Task FreshNoOpAndRestart(string path)
{
    await using (ServiceProvider services = CreateServices(path, Schema.V2))
    {
        IDnaXDatabaseMigrator migrator = services.GetRequiredService<IDnaXDatabaseMigrator>();
        DnaXMigrationResult first = await migrator.MigrateAsync("Graft");
        DnaXMigrationResult second = await migrator.MigrateAsync("Graft");
        Require(first.AppliedMigrations.Count == 2);
        Require(second.AppliedMigrations.Count == 0 && second.Status.State == DnaXMigrationState.Current);
        await using SqliteConnection db = await Open(path);
        Require(await db.ExecuteAsync("INSERT INTO Projects (Id, Name) VALUES (@Id, @Name);", new ProjectParameters(7, "preserved")) == 1);
    }

    await using ServiceProvider restarted = CreateServices(path, Schema.V2);
    DnaXMigrationStatus status = await restarted.GetRequiredService<IDnaXDatabaseMigrator>().GetStatusAsync("Graft");
    await using SqliteConnection reopened = await Open(path);
    ProjectRow project = await reopened.QuerySingleAsync<ProjectRow>("SELECT Id, Name, Description FROM Projects WHERE Id = @Id;", new ProjectId(7));
    Require(status.State == DnaXMigrationState.Current && project.Name == "preserved" && project.Description == "");
}

static async Task UpgradePreservesData(string path)
{
    await using (ServiceProvider v1 = CreateServices(path, Schema.V1))
    {
        await v1.GetRequiredService<IDnaXDatabaseMigrator>().MigrateAsync("Graft");
        await using SqliteConnection db = await Open(path);
        await db.ExecuteAsync("INSERT INTO Projects (Id, Name) VALUES (@Id, @Name);", new ProjectParameters(11, "upgrade"));
    }

    await using (ServiceProvider v2 = CreateServices(path, Schema.V2))
    {
        DnaXMigrationResult result = await v2.GetRequiredService<IDnaXDatabaseMigrator>().MigrateAsync("Graft");
        Require(result.AppliedMigrations.Count == 1);
    }

    await using SqliteConnection reopened = await Open(path);
    ProjectRow row = await reopened.QuerySingleAsync<ProjectRow>("SELECT Id, Name, Description FROM Projects WHERE Id = @Id;", new ProjectId(11));
    Require(row.Name == "upgrade" && row.Description == "");
    Require(await reopened.QuerySingleAsync<long>("SELECT COUNT(*) FROM \"__DnaXMigrations\";") == 2);
}

static async Task AlteredChecksumIsRefused(string path)
{
    await using ServiceProvider original = CreateServices(path, Schema.V2);
    await original.GetRequiredService<IDnaXDatabaseMigrator>().MigrateAsync("Graft");
    await using (SqliteConnection db = await Open(path))
    {
        await db.ExecuteAsync("UPDATE \"__DnaXMigrations\" SET \"Checksum\" = @Checksum WHERE \"Version\" = 1;", new LedgerChecksum("sha256:" + new string('0', 64)));
    }

    await using ServiceProvider restarted = CreateServices(path, Schema.V2);
    IDnaXDatabaseMigrator migrator = restarted.GetRequiredService<IDnaXDatabaseMigrator>();
    Require((await migrator.GetStatusAsync("Graft")).State == DnaXMigrationState.Drifted);
    await RequireThrows<DnaXMigrationValidationException>(() => migrator.MigrateAsync("Graft").AsTask());
}

static async Task FutureVersionIsRefused(string path)
{
    await using ServiceProvider original = CreateServices(path, Schema.V2);
    await original.GetRequiredService<IDnaXDatabaseMigrator>().MigrateAsync("Graft");
    await using (SqliteConnection db = await Open(path))
    {
        await db.ExecuteAsync("""
            INSERT INTO "__DnaXMigrations"
                ("Version", "Id", "Name", "Checksum", "ApplicationVersion", "AppliedAtUtc")
            VALUES (3, 'future', 'Future', @Checksum, 'future', @AppliedAtUtc);
            """, new FutureLedger("sha256:" + new string('0', 64), DateTimeOffset.UtcNow.ToString("O")));
    }

    await using ServiceProvider restarted = CreateServices(path, Schema.V2);
    IDnaXDatabaseMigrator migrator = restarted.GetRequiredService<IDnaXDatabaseMigrator>();
    Require((await migrator.GetStatusAsync("Graft")).State == DnaXMigrationState.UnsupportedFuture);
    await RequireThrows<DnaXMigrationValidationException>(() => migrator.MigrateAsync("Graft").AsTask());
}

static async Task LockTimeoutIsBounded(string path)
{
    await using ServiceProvider services = CreateServices(path, Schema.V2, TimeSpan.FromMilliseconds(200));
    IDnaXDatabaseMigrator migrator = services.GetRequiredService<IDnaXDatabaseMigrator>();
    await migrator.GetStatusAsync("Graft");

    await using SqliteConnection blocker = await Open(path);
    await using SqliteTransaction transaction = blocker.BeginTransaction(deferred: false);
    Stopwatch elapsed = Stopwatch.StartNew();
    await RequireThrows<TimeoutException>(() => migrator.MigrateAsync("Graft").AsTask());
    elapsed.Stop();
    Require(elapsed.Elapsed >= TimeSpan.FromMilliseconds(150) && elapsed.Elapsed < TimeSpan.FromSeconds(3));
    await transaction.RollbackAsync();
}

static async Task FailureRollsBackAndRestartSucceeds(string path)
{
    await using (ServiceProvider broken = CreateServices(path, Schema.BrokenV2))
    {
        await RequireThrows<DnaXMigrationFailedException>(() => broken.GetRequiredService<IDnaXDatabaseMigrator>().MigrateAsync("Graft").AsTask());
    }

    await using (SqliteConnection db = await Open(path))
    {
        long artifacts = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('Projects', '__DnaXMigrations');");
        Require(artifacts == 0);
    }

    await using ServiceProvider restarted = CreateServices(path, Schema.V2);
    DnaXMigrationResult recovered = await restarted.GetRequiredService<IDnaXDatabaseMigrator>().MigrateAsync("Graft");
    Require(recovered.AppliedMigrations.Count == 2 && recovered.Status.State == DnaXMigrationState.Current);
}

static ServiceProvider CreateServices(string path, DnaXMigrationManifest manifest, TimeSpan? lockTimeout = null)
{
    ServiceCollection services = new();
    services.AddLogging();
    services.AddDnaXDataMigrations("Graft", options =>
    {
        options.ConnectionFactory = _ => new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        options.Manifest = manifest;
        options.ApplicationVersion = "mvp-03";
        options.UseSqlite(sqlite =>
        {
            sqlite.LockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5);
            sqlite.LockRetryDelay = TimeSpan.FromMilliseconds(20);
        });
    });
    return services.BuildServiceProvider();
}

static async Task<SqliteConnection> Open(string path)
{
    SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
    await connection.OpenAsync();
    return connection;
}

static async Task RequireThrows<TException>(Func<Task> action) where TException : Exception
{
    try { await action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void Require(bool condition)
{
    if (!condition) throw new InvalidOperationException("Probe assertion failed.");
}

static class Schema
{
    private const string CreateProjects = """
        CREATE TABLE Projects (
            Id INTEGER NOT NULL PRIMARY KEY,
            Name TEXT NOT NULL
        );
        """;

    public static DnaXMigrationManifest V1 { get; } = new(1,
    [
        DnaXMigration.Sql(1, "create-projects", "Create projects", CreateProjects)
    ]);

    public static DnaXMigrationManifest V2 { get; } = new(2,
    [
        DnaXMigration.Sql(1, "create-projects", "Create projects", CreateProjects),
        DnaXMigration.Sql(2, "add-project-description", "Add project description",
            "ALTER TABLE Projects ADD COLUMN Description TEXT NOT NULL DEFAULT '';")
    ]);

    public static DnaXMigrationManifest BrokenV2 { get; } = new(2,
    [
        DnaXMigration.Sql(1, "create-projects", "Create projects", CreateProjects),
        DnaXMigration.Sql(2, "broken-migration", "Broken migration", "CREATE TABLE Broken (;")
    ]);
}

public sealed record ProjectParameters(long Id, string Name);
public sealed record ProjectId(long Id);
public sealed record LedgerChecksum(string Checksum);
public sealed record FutureLedger(string Checksum, string AppliedAtUtc);
public sealed class ProjectRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}
