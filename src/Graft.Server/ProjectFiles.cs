using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace Graft.Server;

/// <summary>
/// Files a project keeps beside its repository that are not Git objects: package files and container
/// image blobs. Everything is written to a temporary file while it is hashed and moved into place only
/// once the hash is known, so nothing half-written is ever served.
/// </summary>
public sealed class ProjectFiles(string repositoryRoot)
{
    public const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumBlobBytes = 8L * 1024 * 1024 * 1024;

    private readonly string root = Path.GetFullPath(repositoryRoot);

    /// <summary>A package file's path: type, name and version are segments that were validated by the store.</summary>
    public string PackagePath(string packageType, string name, string version, string fileName) =>
        Path.Combine(root, "packages", packageType, name.ToLowerInvariant().Replace('/', Path.DirectorySeparatorChar), version.ToLowerInvariant(), fileName);

    public string BlobPath(string digestHex) => Path.Combine(root, "registry", "blobs", "sha256", digestHex[..2], digestHex);
    public string ManifestPath(string digestHex) => Path.Combine(root, "registry", "manifests", "sha256", digestHex[..2], digestHex);
    public string UploadPath(string uploadId) => Path.Combine(root, "registry", "uploads", uploadId);

    /// <summary>Writes a stream to a path, hashing it, bounded. Returns the hash and the size.</summary>
    public async Task<(string Sha256, long Size)> WriteAsync(string destination, Stream content, long maximumBytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            long written = 0;
            string hex;
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[256 * 1024];
                int count;
                while ((count = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    written += count;
                    if (written > maximumBytes) throw new InvalidDataException("too-large");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                hex = Convert.ToHexStringLower(hash.GetHashAndReset());
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
            return (hex, written);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Appends a stream to an in-progress upload and returns the new length.</summary>
    public async Task<long> AppendUploadAsync(string uploadId, Stream content, CancellationToken cancellationToken)
    {
        string path = UploadPath(uploadId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream output = new(path, FileMode.Append, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        byte[] buffer = new byte[256 * 1024];
        int count;
        while ((count = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + count > MaximumBlobBytes) throw new InvalidDataException("too-large");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
        return output.Length;
    }

    /// <summary>Hashes a finished upload and moves it into the blob store under its digest, or refuses it.</summary>
    public async Task<(string Sha256, long Size)> CompleteUploadAsync(string uploadId, CancellationToken cancellationToken)
    {
        string path = UploadPath(uploadId);
        if (!File.Exists(path)) throw new FileNotFoundException("upload", path);
        string hex;
        long size;
        using (FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
        {
            size = input.Length;
            hex = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
        }
        string destination = BlobPath(hex);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)) File.Delete(path);
        else File.Move(path, destination);
        return (hex, size);
    }

    public void AbandonUpload(string uploadId)
    {
        string path = UploadPath(uploadId);
        if (File.Exists(path)) File.Delete(path);
    }

    public static bool IsUploadId(string value) => value.Length == 32 && value.All(char.IsAsciiHexDigitLower);

    public void DeletePackageDirectory(string packageType, string name, string version)
    {
        string directory = Path.GetDirectoryName(PackagePath(packageType, name, version, "x"))!;
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

/// <summary>What a .nuspec says about its package: enough for a listing and a registration.</summary>
public sealed record NuGetManifest(string Id, string Version, string Description, string Authors, IReadOnlyList<(string? Framework, IReadOnlyList<(string Id, string Range)> Dependencies)> DependencyGroups)
{
    /// <summary>Reads the .nuspec from a package, or returns null for anything that is not a package.</summary>
    public static NuGetManifest? Read(string nupkgPath)
    {
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(nupkgPath);
            ZipArchiveEntry? spec = zip.Entries.FirstOrDefault(entry =>
                !entry.FullName.Contains('/') && entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            if (spec is null || spec.Length > 1024 * 1024) return null;
            using Stream stream = spec.Open();
            XDocument document = XDocument.Load(stream);
            XElement? metadata = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "metadata");
            if (metadata is null) return null;
            string? Value(string name) => metadata.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim();
            string? id = Value("id");
            string? version = Value("version");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version)) return null;
            List<(string?, IReadOnlyList<(string, string)>)> groups = [];
            XElement? dependencies = metadata.Elements().FirstOrDefault(element => element.Name.LocalName == "dependencies");
            if (dependencies is not null)
            {
                List<(string, string)> ungrouped = [.. dependencies.Elements().Where(element => element.Name.LocalName == "dependency")
                    .Select(element => ((string?)element.Attribute("id") ?? "", (string?)element.Attribute("version") ?? ""))];
                if (ungrouped.Count > 0) groups.Add((null, ungrouped));
                foreach (XElement group in dependencies.Elements().Where(element => element.Name.LocalName == "group"))
                    groups.Add(((string?)group.Attribute("targetFramework"),
                        [.. group.Elements().Where(element => element.Name.LocalName == "dependency")
                            .Select(element => ((string?)element.Attribute("id") ?? "", (string?)element.Attribute("version") ?? ""))]));
            }
            return new(id, version, Value("description") ?? "", Value("authors") ?? "", groups);
        }
        catch (InvalidDataException) { return null; }
        catch (System.Xml.XmlException) { return null; }
        catch (IOException) { return null; }
    }
}
