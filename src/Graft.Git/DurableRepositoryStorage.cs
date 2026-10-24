using System.Text;

namespace Graft.Git;

public sealed class DurableRepositoryStorage : IRepositoryPersistence
{
    private const string RefsFileName = "refs.snapshot";
    private const string RefsBackupName = "refs.snapshot.backup";
    private readonly string root;
    private readonly string objectsRoot;
    private readonly string stagingRoot;
    private readonly string refsPath;
    private readonly string refsBackupPath;
    private readonly Action<DurableCommitPhase>? commitObserver;

    public DurableRepositoryStorage(string repositoryRoot) : this(repositoryRoot, null) { }

    internal DurableRepositoryStorage(string repositoryRoot, Action<DurableCommitPhase>? commitObserver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        root = Path.GetFullPath(repositoryRoot);
        objectsRoot = Path.Combine(root, "objects");
        stagingRoot = Path.Combine(root, "staging");
        refsPath = Path.Combine(root, RefsFileName);
        refsBackupPath = Path.Combine(root, RefsBackupName);
        this.commitObserver = commitObserver;
        Directory.CreateDirectory(objectsRoot);
        Directory.CreateDirectory(stagingRoot);
        RecoverRefSnapshot();
    }

    public RepositoryState Load()
    {
        Dictionary<string, GitObject> objects = new(StringComparer.Ordinal);
        long total = 0;
        foreach (string path in Directory.EnumerateFiles(objectsRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(objectsRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            string[] parts = relative.Split('/');
            Limits.Require(parts.Length == 2 && parts[0].Length == 2 && parts[1].Length == 38, "stored-object-path");
            string id = parts[0] + parts[1];
            Limits.Require(Limits.IsId(id), "stored-object-id");
            using FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Limits.Require(source.Length is >= 1 and <= Limits.ObjectBytes + 1, "stored-object-size");
            int type = source.ReadByte();
            Limits.Require(type is >= 1 and <= 4, "stored-object-type");
            int size = (int)source.Length - 1;
            Limits.Require((total += size) <= Limits.ExpandedBytes && objects.Count < Limits.ObjectCount, "repository-budget");
            GitObject value = new(type, size, path);
            Limits.Require(value.Id == id, "stored-object-hash");
            Limits.Require(objects.TryAdd(id, value), "stored-object-duplicate");
        }

        Dictionary<string, string> refs = new(StringComparer.Ordinal);
        if (File.Exists(refsPath))
        {
            foreach (string line in File.ReadAllLines(refsPath, Encoding.ASCII))
            {
                string[] parts = line.Split(' ');
                Limits.Require(parts.Length == 2 && Limits.IsId(parts[0]) && Limits.IsRef(parts[1]), "stored-ref");
                Limits.Require(refs.TryAdd(parts[1], parts[0]), "stored-ref-duplicate");
            }
        }
        return new(objects, refs);
    }

    public void Commit(
        IReadOnlyDictionary<string, GitObject> objects,
        IReadOnlyDictionary<string, string> refs,
        CancellationToken cancellationToken)
    {
        foreach ((string id, GitObject value) in objects.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Limits.Require(Limits.IsId(id) && value.Id == id, "object-hash");
            string finalPath = ObjectPath(id);
            if (File.Exists(finalPath)) { value.StoredAt(finalPath); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            string temporaryPath = Path.Combine(stagingRoot, "object-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                if (value.StoredPath is { } stored)
                {
                    using (FileStream source = new(stored, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        source.Flush(flushToDisk: true);
                    try
                    {
                        File.Move(stored, finalPath);
                        value.StoredAt(finalPath);
                        continue;
                    }
                    catch (IOException) when (!File.Exists(finalPath)) { }
                }
                using (FileStream output = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.WriteByte((byte)value.Type);
                    using Stream source = value.OpenRead();
                    source.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, finalPath, overwrite: false);
                value.StoredAt(finalPath);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                File.Delete(temporaryPath);
                value.StoredAt(finalPath);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        commitObserver?.Invoke(DurableCommitPhase.ObjectsFlushed);
        cancellationToken.ThrowIfCancellationRequested();
        string refsTemporaryPath = Path.Combine(root, ".refs-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream output = new(refsTemporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new(output, Encoding.ASCII, leaveOpen: true))
            {
                writer.NewLine = "\n";
                foreach ((string name, string id) in refs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    Limits.Require(Limits.IsRef(name) && Limits.IsId(id), "ref-snapshot");
                    writer.Write(id);
                    writer.Write(' ');
                    writer.WriteLine(name);
                }
                writer.Flush();
                output.Flush(flushToDisk: true);
            }

            commitObserver?.Invoke(DurableCommitPhase.RefsFlushed);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(refsPath))
            {
                File.Replace(refsTemporaryPath, refsPath, refsBackupPath, ignoreMetadataErrors: true);
                if (File.Exists(refsBackupPath)) File.Delete(refsBackupPath);
            }
            else
            {
                File.Move(refsTemporaryPath, refsPath);
            }
        }
        finally
        {
            if (File.Exists(refsTemporaryPath)) File.Delete(refsTemporaryPath);
        }
    }

    private string ObjectPath(string id) => Path.Combine(objectsRoot, id[..2], id[2..]);

    private void RecoverRefSnapshot()
    {
        if (!File.Exists(refsPath) && File.Exists(refsBackupPath))
            File.Move(refsBackupPath, refsPath);
        else if (File.Exists(refsPath) && File.Exists(refsBackupPath))
            File.Delete(refsBackupPath);
    }
}

internal enum DurableCommitPhase
{
    ObjectsFlushed,
    RefsFlushed
}
