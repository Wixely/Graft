using System.IO.Compression;
using Graft.Core;

namespace Graft.Server;

/// <summary>Why an uploaded page artifact was refused, with the status the caller should see.</summary>
public sealed class PageArtifactException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// The files of a project's page artifacts (ADR 0051), on disk beside its repository under
/// <c>page-artifacts/&lt;name&gt;/&lt;generation&gt;/</c>. An upload is one zip archive: it is staged, unpacked
/// into a new generation and only then named by the database, so what is served is always one whole build and
/// never part of two.
///
/// <para>
/// The archive is somebody else's bytes. Every entry's path is checked before anything is written, the bytes
/// are counted as they are unpacked rather than believed from the archive's own header, and nothing in it can
/// name a place outside its own directory.
/// </para>
/// </summary>
public sealed class PageArtifactFiles(string repositoryRoot)
{
    private readonly string root = Path.Combine(Path.GetFullPath(repositoryRoot), "page-artifacts");

    private string ArtifactDirectory(string name) => Path.Combine(root, name);
    private string GenerationDirectory(string name, string generation) => Path.Combine(root, name, generation);

    /// <summary>
    /// Stages the archive, unpacks it into a new generation and returns what it held. Throws
    /// <see cref="PageArtifactException"/> for anything refused; nothing is left behind when it does.
    /// </summary>
    public async Task<(string Generation, int Files, long Bytes)> ReceiveAsync(string name, Stream archive, long maximumBytes, CancellationToken cancellationToken)
    {
        if (!PageArtifacts.IsName(name)) throw new PageArtifactException(400, "A page artifact's name is up to 40 lower-case letters, digits, '-' and '_', starting with a letter or digit.");
        string generation = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(root, ".incoming");
        Directory.CreateDirectory(staging);
        string zipPath = Path.Combine(staging, generation + ".zip");
        string unpacking = Path.Combine(staging, generation);
        try
        {
            await using (FileStream output = new(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[256 * 1024];
                long written = 0;
                int count;
                while ((count = await archive.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    written += count;
                    if (written > maximumBytes) throw TooLarge(maximumBytes);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                if (written == 0) throw new PageArtifactException(400, "The upload is empty. A page artifact is uploaded as one zip archive.");
            }

            (int files, long bytes) = await UnpackAsync(zipPath, unpacking, maximumBytes, cancellationToken);
            Directory.CreateDirectory(ArtifactDirectory(name));
            Directory.Move(unpacking, GenerationDirectory(name, generation));
            return (generation, files, bytes);
        }
        finally
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            if (Directory.Exists(unpacking)) Directory.Delete(unpacking, recursive: true);
        }
    }

    private static PageArtifactException TooLarge(long maximumBytes) =>
        new(413, "The page artifact is larger than this server's limit of " + (maximumBytes / (1024 * 1024)) + " MiB.");

    private static async Task<(int Files, long Bytes)> UnpackAsync(string zipPath, string destination, long maximumBytes, CancellationToken cancellationToken)
    {
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException) { throw new PageArtifactException(400, "The upload is not a zip archive. A page artifact is uploaded as one zip archive."); }
        using (zip)
        {
            // The paths first, all of them, so a refused archive writes nothing. Windows tools have written
            // backslashes into archives for years; inside a zip that is only ever a separator.
            List<(ZipArchiveEntry Entry, string[] Segments)> entries = [];
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string path = entry.FullName.Replace('\\', '/');
                if (path.EndsWith('/')) continue;
                string[]? segments = PageArtifacts.Segments(path);
                if (segments is null)
                    throw new PageArtifactException(400, "The archive holds a path that cannot be served: " + Printable(entry.FullName));
                entries.Add((entry, segments));
                if (entries.Count > PageArtifacts.MaximumFiles)
                    throw new PageArtifactException(413, "A page artifact holds at most " + PageArtifacts.MaximumFiles + " files.");
            }
            if (entries.Count == 0) throw new PageArtifactException(400, "The archive holds no files.");

            // An archive made by zipping a folder holds that folder as its only top-level entry. The folder is
            // packaging, not content: the artifact's own name is already the directory the files answer under.
            int skip = entries.All(entry => entry.Segments.Length > 1 &&
                string.Equals(entry.Segments[0], entries[0].Segments[0], StringComparison.OrdinalIgnoreCase)) ? 1 : 0;

            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach ((_, string[] segments) in entries)
                if (!seen.Add(string.Join('/', segments.Skip(skip))))
                    throw new PageArtifactException(400, "The archive holds the same path twice, or two that differ only by case: " + string.Join('/', segments.Skip(skip)));
            // A file and a directory of one name cannot both exist once unpacked.
            foreach ((_, string[] segments) in entries)
                for (int depth = skip + 1; depth < segments.Length; depth++)
                    if (seen.Contains(string.Join('/', segments[skip..depth])))
                        throw new PageArtifactException(400, "The archive uses one path as both a file and a folder: " + string.Join('/', segments[skip..depth]));

            Directory.CreateDirectory(destination);
            string boundary = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            long total = 0;
            byte[] buffer = new byte[256 * 1024];
            foreach ((ZipArchiveEntry entry, string[] segments) in entries)
            {
                string target = Path.GetFullPath(Path.Combine([destination, .. segments.Skip(skip)]));
                if (!target.StartsWith(boundary, StringComparison.Ordinal))
                    throw new PageArtifactException(400, "The archive holds a path that cannot be served: " + Printable(entry.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try
                {
                    await using Stream source = entry.Open();
                    await using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
                    int count;
                    while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        // Counted here, not read from the entry: the header's length is the archive's claim.
                        total += count;
                        if (total > maximumBytes) throw TooLarge(maximumBytes);
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    }
                }
                catch (InvalidDataException) { throw new PageArtifactException(400, "The archive is damaged at " + Printable(entry.FullName) + "."); }
            }
            return (entries.Count, total);
        }
    }

    private static string Printable(string value) =>
        new([.. value.Take(120).Select(character => char.IsControl(character) ? '?' : character)]);

    /// <summary>
    /// The file a path inside an artifact names, or null. The path is checked by the same rule that admitted
    /// it at upload, and the result is confirmed to be inside the generation before it is returned.
    /// </summary>
    public string? Resolve(string name, string generation, string path)
    {
        if (!PageArtifacts.IsName(name) || generation.Length != 32 || !generation.All(char.IsAsciiHexDigitLower)) return null;
        string[]? segments = PageArtifacts.Segments(path);
        if (segments is null) return null;
        string directory = GenerationDirectory(name, generation);
        string target = Path.GetFullPath(Path.Combine([directory, .. segments]));
        if (!target.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        return File.Exists(target) ? target : null;
    }

    /// <summary>The files of one generation, by path, up to a limit.</summary>
    public IReadOnlyList<(string Path, long Size)> List(string name, string generation, int limit, out bool truncated)
    {
        truncated = false;
        string directory = GenerationDirectory(name, generation);
        if (!Directory.Exists(directory)) return [];
        List<(string, long)> files = [];
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (files.Count == limit) { truncated = true; break; }
            files.Add((Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/'), new FileInfo(file).Length));
        }
        return files;
    }

    /// <summary>
    /// Removes one generation. A file a reader still has open can refuse on Windows; what is left is unnamed by
    /// the database and so unreachable, and goes when the artifact or the project is deleted.
    /// </summary>
    public void DeleteGeneration(string name, string generation)
    {
        if (!PageArtifacts.IsName(name) || generation.Length != 32 || !generation.All(char.IsAsciiHexDigitLower)) return;
        TryDelete(GenerationDirectory(name, generation));
    }

    /// <summary>Removes an artifact and every generation of it.</summary>
    public void DeleteArtifact(string name)
    {
        if (PageArtifacts.IsName(name)) TryDelete(ArtifactDirectory(name));
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
