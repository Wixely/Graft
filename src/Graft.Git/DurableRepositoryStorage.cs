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
        foreach (string path in Directory.EnumerateFiles(objectsRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(objectsRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            string[] parts = relative.Split('/');
            Limits.Require(parts.Length == 2 && parts[0].Length == 2 && parts[1].Length == 38, "stored-object-path");
            string id = parts[0] + parts[1];
            Limits.Require(Limits.IsId(id), "stored-object-id");
            byte[] encoded = File.ReadAllBytes(path);
            Limits.Require(encoded.Length >= 1 && encoded[0] is >= 1 and <= 4, "stored-object-type");
            GitObject value = new(encoded[0], encoded.AsSpan(1).ToArray());
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
            if (File.Exists(finalPath)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            string temporaryPath = Path.Combine(stagingRoot, "object-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream output = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.WriteByte((byte)value.Type);
                    output.Write(value.Data);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, finalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                File.Delete(temporaryPath);
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
