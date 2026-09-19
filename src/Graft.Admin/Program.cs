using System.Text.Json;
using Graft.Operations;

if (args.Length == 0)
{
    Usage();
    return 2;
}

try
{
    Dictionary<string, string> options = Parse(args[1..]);
    switch (args[0])
    {
        case "backup":
        {
            RequireOnly(options, "database", "repositories", "output");
            BackupManifest manifest = await BackupManager.CreateAsync(
                Required(options, "database"), Required(options, "repositories"), Required(options, "output"));
            Console.WriteLine($"BACKUP: PASS; files={manifest.Files.Length}; directories={manifest.Directories.Length}");
            break;
        }
        case "verify":
        {
            RequireOnly(options, "input");
            BackupManifest manifest = await BackupManager.VerifyAsync(Required(options, "input"));
            Console.WriteLine($"VERIFY: PASS; files={manifest.Files.Length}; created={manifest.CreatedAtUtc:O}");
            break;
        }
        case "restore":
        {
            RequireOnly(options, "input", "database", "repositories");
            BackupManifest manifest = await BackupManager.RestoreAsync(
                Required(options, "input"), Required(options, "database"), Required(options, "repositories"));
            Console.WriteLine($"RESTORE: PASS; files={manifest.Files.Length}; directories={manifest.Directories.Length}");
            break;
        }
        case "inspect-bundle":
        {
            RequireOnly(options, "input", "output");
            GitBundleInventory inventory = GitBundleInventory.Read(Required(options, "input"));
            string output = Path.GetFullPath(Required(options, "output"));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using FileStream stream = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(stream, inventory, OperationsJson.Default.GitBundleInventory);
            await stream.FlushAsync();
            Console.WriteLine($"INVENTORY: PASS; refs={inventory.Refs.Length}; excluded={inventory.Excluded.Length}");
            break;
        }
        default:
            Usage();
            return 2;
    }
    return 0;
}
catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or InvalidOperationException or
    JsonException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("ERROR: " + error.Message);
    return 1;
}

static Dictionary<string, string> Parse(string[] values)
{
    if (values.Length % 2 != 0) throw new ArgumentException("Options must be --name value pairs.");
    Dictionary<string, string> result = new(StringComparer.Ordinal);
    for (int index = 0; index < values.Length; index += 2)
    {
        if (!values[index].StartsWith("--", StringComparison.Ordinal) || values[index].Length == 2 ||
            !result.TryAdd(values[index][2..], values[index + 1]))
            throw new ArgumentException("Options must be unique --name value pairs.");
    }
    return result;
}

static string Required(Dictionary<string, string> options, string name) =>
    options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException("Missing --" + name + ".");

static void RequireOnly(Dictionary<string, string> options, params string[] names)
{
    if (options.Count != names.Length || names.Any(name => !options.ContainsKey(name)))
        throw new ArgumentException("Missing or unsupported option.");
}

static void Usage() => Console.Error.WriteLine(
    "Usage: Graft.Admin backup --database <path> --repositories <path> --output <directory>\n" +
    "       Graft.Admin verify --input <backup-directory>\n" +
    "       Graft.Admin restore --input <backup-directory> --database <path> --repositories <path>\n" +
    "       Graft.Admin inspect-bundle --input <bundle> --output <report.json>");
