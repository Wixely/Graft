using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Graft.Git;

/// <summary>What a Git LFS pointer file says: which object it stands for and how big that object is.</summary>
public sealed record LfsPointer(string Oid, long Size)
{
    /// <summary>
    /// Reads a pointer, or returns null for anything that is not one. The format is three lines in a fixed
    /// order; a file that merely mentions the spec is not a pointer, and a pointer is never larger than a
    /// few hundred bytes, so anything bigger is content.
    /// </summary>
    public static LfsPointer? TryParse(ReadOnlySpan<byte> content)
    {
        if (content.Length is 0 or > 1024) return null;
        if (!content.StartsWith("version https://git-lfs.github.com/spec/v1\n"u8)) return null;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(content); }
        catch (DecoderFallbackException) { return null; }
        string? oid = null;
        long? size = null;
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            if (line.StartsWith("oid sha256:", StringComparison.Ordinal))
            {
                string value = line["oid sha256:".Length..];
                if (!LargeFileStore.IsOid(value)) return null;
                oid = value;
            }
            else if (line.StartsWith("size ", StringComparison.Ordinal))
            {
                if (!long.TryParse(line[5..], NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)) return null;
                size = parsed;
            }
        }
        return oid is null || size is null ? null : new(oid, size.Value);
    }
}

/// <summary>
/// Where a repository's large files live: content-addressed by SHA-256 under <c>lfs/</c> beside the
/// repository's objects, so a backup of the repository directory carries them and a fork copies them.
///
/// <para>
/// An object is written to a temporary file while it is hashed, and only moved into place once the hash
/// matches the name it was offered under and the size matches what was declared. A client that lies about
/// either is refused and nothing is kept. Reads stream from disk; nothing here is held in memory, because a
/// large file is the one thing in a repository that is allowed to be large.
/// </para>
/// </summary>
public sealed class LargeFileStore
{
    public const long MaximumBytes = 4L * 1024 * 1024 * 1024;
    private readonly string root;

    public LargeFileStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        root = Path.Combine(Path.GetFullPath(repositoryRoot), "lfs");
    }

    public static bool IsOid(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public bool Exists(string oid) => IsOid(oid) && File.Exists(PathOf(oid));

    public long? Size(string oid)
    {
        if (!IsOid(oid)) return null;
        FileInfo file = new(PathOf(oid));
        return file.Exists ? file.Length : null;
    }

    public Stream OpenRead(string oid)
    {
        Limits.Require(IsOid(oid), "lfs-oid");
        return new FileStream(PathOf(oid), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
    }

    /// <summary>Stores a stream as <paramref name="oid"/>, refusing it unless its hash and size are what was declared.</summary>
    public async Task StoreAsync(string oid, long declaredSize, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        Limits.Require(IsOid(oid), "lfs-oid");
        Limits.Require(declaredSize is >= 0 and <= MaximumBytes, "lfs-size");
        string final = PathOf(oid);
        if (File.Exists(final)) { await content.CopyToAsync(Stream.Null, cancellationToken); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        Directory.CreateDirectory(Path.Combine(root, "incoming"));
        string temporary = Path.Combine(root, "incoming", Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            long written = 0;
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[256 * 1024];
                int count;
                while ((count = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    written += count;
                    Limits.Require(written <= declaredSize, "lfs-size-exceeded");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                Limits.Require(written == declaredSize, "lfs-size-mismatch");
                Limits.Require(Convert.ToHexStringLower(hash.GetHashAndReset()) == oid, "lfs-hash-mismatch");
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, final, overwrite: false); }
            catch (IOException) when (File.Exists(final)) { }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Copies every stored object into another store's directory. Forking.</summary>
    public static void Copy(string sourceRoot, string destinationRoot, CancellationToken cancellationToken = default)
    {
        string source = Path.Combine(Path.GetFullPath(sourceRoot), "lfs");
        if (!Directory.Exists(source)) return;
        string destination = Path.Combine(Path.GetFullPath(destinationRoot), "lfs");
        foreach (string path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, path);
            if (relative.StartsWith("incoming", StringComparison.Ordinal)) continue;
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target, overwrite: false);
        }
    }

    private string PathOf(string oid) => Path.Combine(root, oid[..2], oid[2..4], oid);
}
