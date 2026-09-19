using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Graft.Operations;

public sealed record BackupFile(string Path, long Length, string Sha256);
public sealed record BackupManifest(
    int FormatVersion,
    string Application,
    DateTimeOffset CreatedAtUtc,
    string DatabasePath,
    string RepositoriesPath,
    string[] Directories,
    BackupFile[] Files);

public static class BackupManager
{
    private const string ManifestName = "manifest.json";
    private const string DatabaseName = "database.sqlite";
    private const string RepositoriesName = "repositories";

    public static async Task<BackupManifest> CreateAsync(
        string databasePath,
        string repositoriesPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        string database = ExistingFile(databasePath, nameof(databasePath));
        string repositories = ExistingDirectory(repositoriesPath, nameof(repositoriesPath));
        string output = NewPath(outputPath, nameof(outputPath));
        RequireSeparate(database, repositories, output);
        using MaintenanceLease lease = MaintenanceLease.Acquire(database, repositories);

        string staging = output + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);
            string databaseCopy = Path.Combine(staging, DatabaseName);
            await SnapshotDatabase(database, databaseCopy, cancellationToken);
            string repositoriesCopy = Path.Combine(staging, RepositoriesName);
            Directory.CreateDirectory(repositoriesCopy);
            CopyTree(repositories, repositoriesCopy, cancellationToken);

            BackupManifest manifest = BuildManifest(staging);
            await using (FileStream destination = new(Path.Combine(staging, ManifestName), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(destination, manifest, OperationsJson.Default.BackupManifest, cancellationToken);
                await destination.FlushAsync(cancellationToken);
                destination.Flush(flushToDisk: true);
            }
            Directory.Move(staging, output);
            return manifest;
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    public static async Task<BackupManifest> VerifyAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        string backup = ExistingDirectory(backupPath, nameof(backupPath));
        string manifestPath = Path.Combine(backup, ManifestName);
        await using FileStream input = new(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        BackupManifest manifest = await JsonSerializer.DeserializeAsync(input, OperationsJson.Default.BackupManifest, cancellationToken)
            ?? throw new InvalidDataException("Backup manifest is empty.");
        ValidateManifest(manifest);

        (string[] actualDirectories, string[] actualFiles) = InventoryTree(backup);
        if (!actualDirectories.Contains(RepositoriesName, StringComparer.Ordinal) ||
            actualDirectories.Any(path => path != RepositoriesName &&
                !path.StartsWith(RepositoriesName + "/", StringComparison.Ordinal)))
            throw new InvalidDataException("Backup contains an undeclared directory outside the repository tree.");
        HashSet<string> declared = manifest.Files.Select(file => NormalizeRelative(file.Path)).ToHashSet(StringComparer.Ordinal);
        string[] actual = actualFiles.Where(path => path != ManifestName).ToArray();
        if (!declared.SetEquals(actual)) throw new InvalidDataException("Backup file inventory does not match its manifest.");
        string[] repositoryDirectories = actualDirectories
            .Where(path => path.StartsWith(RepositoriesName + "/", StringComparison.Ordinal))
            .Select(path => path[(RepositoriesName.Length + 1)..]).ToArray();
        if (!manifest.Directories.ToHashSet(StringComparer.Ordinal).SetEquals(repositoryDirectories))
            throw new InvalidDataException("Backup directory inventory does not match its manifest.");
        foreach (BackupFile file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = SafeChild(backup, file.Path);
            FileInfo info = new(path);
            if (!info.Exists || info.Length != file.Length || !string.Equals(Hash(path), file.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("Backup file verification failed: " + file.Path);
        }
        await CheckDatabase(Path.Combine(backup, DatabaseName), cancellationToken);
        return manifest;
    }

    public static async Task<BackupManifest> RestoreAsync(
        string backupPath,
        string databasePath,
        string repositoriesPath,
        CancellationToken cancellationToken = default)
    {
        string backup = ExistingDirectory(backupPath, nameof(backupPath));
        string database = NewPath(databasePath, nameof(databasePath));
        string repositories = NewPath(repositoriesPath, nameof(repositoriesPath));
        RequireSeparate(database, repositories, backup);
        using MaintenanceLease lease = MaintenanceLease.Acquire(database, repositories);
        if (File.Exists(database) || Directory.Exists(database)) throw new IOException("Restore database target already exists.");
        if (File.Exists(repositories) || Directory.Exists(repositories)) throw new IOException("Restore repository target already exists.");
        BackupManifest manifest = await VerifyAsync(backup, cancellationToken);

        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        Directory.CreateDirectory(Path.GetDirectoryName(repositories)!);
        string databaseStage = database + ".restore-" + Guid.NewGuid().ToString("N");
        string repositoriesStage = repositories + ".restore-" + Guid.NewGuid().ToString("N");
        bool databasePublished = false;
        try
        {
            CopyFile(Path.Combine(backup, DatabaseName), databaseStage, cancellationToken);
            Directory.CreateDirectory(repositoriesStage);
            CopyTree(Path.Combine(backup, RepositoriesName), repositoriesStage, cancellationToken);
            await CheckDatabase(databaseStage, cancellationToken);
            File.Move(databaseStage, database);
            databasePublished = true;
            Directory.Move(repositoriesStage, repositories);
            return manifest;
        }
        catch
        {
            if (File.Exists(databaseStage)) File.Delete(databaseStage);
            if (Directory.Exists(repositoriesStage)) Directory.Delete(repositoriesStage, recursive: true);
            if (databasePublished && File.Exists(database)) File.Delete(database);
            throw;
        }
    }

    private static async Task SnapshotDatabase(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await CheckDatabase(sourcePath, cancellationToken);
        SqliteConnectionStringBuilder sourceBuilder = new() { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        SqliteConnectionStringBuilder destinationBuilder = new() { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using SqliteConnection source = new(sourceBuilder.ToString());
        await using SqliteConnection destination = new(destinationBuilder.ToString());
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
        await destination.CloseAsync();
        await CheckDatabase(destinationPath, cancellationToken);
    }

    private static async Task CheckDatabase(string path, CancellationToken cancellationToken)
    {
        SqliteConnectionStringBuilder builder = new() { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using SqliteConnection connection = new(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(result?.ToString(), "ok", StringComparison.Ordinal))
            throw new InvalidDataException("SQLite integrity check failed.");
    }

    private static BackupManifest BuildManifest(string root)
    {
        (string[] allDirectories, string[] allFiles) = InventoryTree(root);
        string[] directories = allDirectories.Where(path => path.StartsWith(RepositoriesName + "/", StringComparison.Ordinal))
            .Select(path => path[(RepositoriesName.Length + 1)..]).ToArray();
        BackupFile[] files = allFiles
            .Select(relative =>
            {
                string path = SafeChild(root, relative);
                return new BackupFile(relative, new FileInfo(path).Length, Hash(path));
            })
            .OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        return new(1, "Graft", DateTimeOffset.UtcNow, DatabaseName, RepositoriesName, directories, files);
    }

    private static void ValidateManifest(BackupManifest manifest)
    {
        if (manifest.FormatVersion != 1 || manifest.Application != "Graft" || manifest.DatabasePath != DatabaseName ||
            manifest.RepositoriesPath != RepositoriesName || manifest.Files.Length == 0)
            throw new InvalidDataException("Unsupported backup manifest.");
        if (manifest.Files.Select(file => NormalizeRelative(file.Path)).Distinct(StringComparer.Ordinal).Count() != manifest.Files.Length ||
            manifest.Directories.Select(NormalizeRelative).Distinct(StringComparer.Ordinal).Count() != manifest.Directories.Length)
            throw new InvalidDataException("Backup manifest contains duplicate paths.");
        if (!manifest.Files.Any(file => file.Path == DatabaseName)) throw new InvalidDataException("Backup database is missing.");
    }

    private static void CopyTree(string source, string destination, CancellationToken cancellationToken)
    {
        RejectLink(new DirectoryInfo(source).Attributes);
        Stack<(string Source, string Destination)> pending = new();
        pending.Push((source, destination));
        while (pending.TryPop(out (string Source, string Destination) current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string directory in Directory.EnumerateDirectories(current.Source, "*", SearchOption.TopDirectoryOnly))
            {
                RejectLink(File.GetAttributes(directory));
                string target = SafeChild(destination, Path.GetRelativePath(source, directory));
                Directory.CreateDirectory(target);
                pending.Push((directory, target));
            }
            foreach (string file in Directory.EnumerateFiles(current.Source, "*", SearchOption.TopDirectoryOnly))
            {
                RejectLink(File.GetAttributes(file));
                string target = SafeChild(destination, Path.GetRelativePath(source, file));
                CopyFile(file, target, cancellationToken);
            }
        }
    }

    private static void CopyFile(string source, string destination, CancellationToken cancellationToken)
    {
        using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
        byte[] buffer = new byte[81920];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, count);
        }
        output.Flush(flushToDisk: true);
    }

    private static (string[] Directories, string[] Files) InventoryTree(string root)
    {
        List<string> directories = [];
        List<string> files = [];
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.TryPop(out string? current))
        {
            foreach (string directory in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
            {
                RejectLink(File.GetAttributes(directory));
                directories.Add(NormalizeRelative(Path.GetRelativePath(root, directory)));
                pending.Push(directory);
            }
            foreach (string file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
            {
                RejectLink(File.GetAttributes(file));
                files.Add(NormalizeRelative(Path.GetRelativePath(root, file)));
            }
        }
        return (directories.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            files.OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    private static string Hash(string path)
    {
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    private static string ExistingFile(string path, string parameterName)
    {
        string full = Path.GetFullPath(path ?? throw new ArgumentNullException(parameterName));
        if (!File.Exists(full)) throw new FileNotFoundException("Required file was not found.", full);
        RejectLink(File.GetAttributes(full));
        return full;
    }

    private static string ExistingDirectory(string path, string parameterName)
    {
        string full = Path.GetFullPath(path ?? throw new ArgumentNullException(parameterName));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Required directory was not found.");
        RejectLink(File.GetAttributes(full));
        return full;
    }

    private static string NewPath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        return Path.GetFullPath(path);
    }

    private static string SafeChild(string root, string relative)
    {
        string normalized = NormalizeRelative(relative);
        string full = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, PathComparison)) throw new InvalidDataException("Path escapes its root.");
        return full;
    }

    private static string NormalizeRelative(string path)
    {
        string value = path.Replace('\\', '/');
        if (value.Length == 0 || value.StartsWith('/') || Path.IsPathRooted(value) ||
            value.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException("Invalid relative path.");
        return value;
    }

    private static void RequireSeparate(string first, string second, string third)
    {
        string[] paths = [first, second, third];
        for (int left = 0; left < paths.Length; left++)
        for (int right = left + 1; right < paths.Length; right++)
        {
            string a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths[left]));
            string b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths[right]));
            if (a.Equals(b, PathComparison) || IsChild(a, b) || IsChild(b, a))
                throw new ArgumentException("Database, repository and backup paths must not overlap.");
        }
    }

    private static bool IsChild(string candidate, string parent) =>
        candidate.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison);

    private static void RejectLink(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Symbolic links and reparse points are not supported in backups.");
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(GitBundleInventory))]
public partial class OperationsJson : JsonSerializerContext;
