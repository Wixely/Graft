using System.Text;

namespace Graft.Git;

/// <summary>
/// A repository on disk, laid out as a bare Git repository: loose objects under <c>objects/xx/</c> as
/// zlib streams, packs with their indexes under <c>objects/pack/</c>, references in <c>packed-refs</c>,
/// <c>HEAD</c> naming the default branch and a minimal <c>config</c>. Git itself can open the directory,
/// which is the test of the format. Graft is the only writer; a reference written loose under
/// <c>refs/</c> by something else is honoured when loading, as Git would honour it.
///
/// <para>
/// Publication is in two steps that the observer can see: every new object is made durable first, then
/// the reference file is replaced as a whole. A failure between the two leaves objects nothing points at,
/// which is harmless and which a later repack sweeps into the pack with everything else.
/// </para>
///
/// <para>
/// A directory in the format Graft used before this one, a type byte in front of raw content and a
/// <c>refs.snapshot</c> file, is converted the first time it is opened. Each file is rewritten in place
/// and recognised by its first bytes, so an interrupted conversion simply resumes.
/// </para>
/// </summary>
public sealed class DurableRepositoryStorage : IRepositoryPersistence, IDisposable
{
    private const string PackedRefsName = "packed-refs";
    private const string PackedRefsBackupName = "packed-refs.backup";
    private const string PackedRefsHeader = "# pack-refs with: peeled fully-peeled sorted";
    private const string LegacyRefsName = "refs.snapshot";
    private const string LegacyRefsBackupName = "refs.snapshot.backup";
    private readonly string root;
    private readonly string objectsRoot;
    private readonly string packRoot;
    private readonly string stagingRoot;
    private readonly string refsPath;
    private readonly string refsBackupPath;
    private readonly Action<DurableCommitPhase>? commitObserver;
    private readonly List<PackFile> packs = [];
    private IReadOnlyDictionary<string, GitObject> known = new Dictionary<string, GitObject>(StringComparer.Ordinal);

    public DurableRepositoryStorage(string repositoryRoot) : this(repositoryRoot, null) { }

    internal DurableRepositoryStorage(string repositoryRoot, Action<DurableCommitPhase>? commitObserver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        root = Path.GetFullPath(repositoryRoot);
        objectsRoot = Path.Combine(root, "objects");
        packRoot = Path.Combine(objectsRoot, "pack");
        stagingRoot = Path.Combine(root, "staging");
        refsPath = Path.Combine(root, PackedRefsName);
        refsBackupPath = Path.Combine(root, PackedRefsBackupName);
        this.commitObserver = commitObserver;
        Directory.CreateDirectory(objectsRoot);
        Directory.CreateDirectory(packRoot);
        Directory.CreateDirectory(Path.Combine(objectsRoot, "info"));
        Directory.CreateDirectory(Path.Combine(root, "refs", "heads"));
        Directory.CreateDirectory(Path.Combine(root, "refs", "tags"));
        Directory.CreateDirectory(stagingRoot);
        WriteSkeleton();
        RecoverRefSnapshot();
        Migrate();
    }

    public string RootPath => root;

    /// <summary>How many objects sit loose rather than in a pack; what decides whether a repack is due.</summary>
    public int LooseObjectCount { get; private set; }

    /// <summary>The packs currently open, for the operator's benefit.</summary>
    public int PackCount => packs.Count;

    /// <summary>
    /// Copies one repository's storage into a new directory, file by file: every object, loose or packed,
    /// the client references and the default branch. The source is only read. This is what forking does;
    /// the result opens as an ordinary repository that happens to start with everything the original had.
    /// </summary>
    public static void Copy(string sourceRoot, string destinationRoot, CancellationToken cancellationToken = default)
    {
        string source = Path.GetFullPath(sourceRoot);
        string destination = Path.GetFullPath(destinationRoot);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("The fork's directory is not empty.");
        // Opening the source first converts a legacy layout, so what is copied is always the current format.
        using (DurableRepositoryStorage opened = new(source)) opened.Dispose();
        Directory.CreateDirectory(Path.Combine(destination, "objects", "pack"));
        Directory.CreateDirectory(Path.Combine(destination, "staging"));
        string sourceObjects = Path.Combine(source, "objects");
        if (Directory.Exists(sourceObjects))
            foreach (string path in Directory.EnumerateFiles(sourceObjects, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(sourceObjects, path);
                string target = Path.Combine(destination, "objects", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target, overwrite: false);
            }
        LargeFileStore.Copy(source, destination, cancellationToken);
        Dictionary<string, string> refs = ReadRefs(source);
        Dictionary<string, string> kept = new(StringComparer.Ordinal);
        // Only client references travel. A proposal reference belongs to the repository it was made in.
        foreach ((string name, string id) in refs) if (Limits.IsClientRef(name)) kept[name] = id;
        using DurableRepositoryStorage fork = new(destination);
        RepositoryState state = fork.Load();
        fork.Commit(state.Objects, kept, Repository.HeadOf(kept, null), cancellationToken);
    }

    public RepositoryState Load()
    {
        foreach (PackFile pack in packs) pack.Dispose();
        packs.Clear();
        Dictionary<string, GitObject> objects = new(StringComparer.Ordinal);
        long total = 0;
        void Count(int size)
        {
            Limits.Require((total += size) <= Limits.ExpandedBytes && objects.Count < Limits.ObjectCount, "repository-budget");
        }

        // Packs first: an object that is both packed and loose is read from the pack, and the loose copy is
        // a leftover a repack removes.
        foreach (string packPath in Directory.EnumerateFiles(packRoot, "*.pack").OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!File.Exists(Path.ChangeExtension(packPath, ".idx"))) continue;
            PackFile pack = PackFile.Open(packPath);
            packs.Add(pack);
            foreach ((string id, long offset) in pack.Entries().OrderBy(entry => entry.Offset))
            {
                if (objects.ContainsKey(id)) continue;
                (int type, int size) = pack.Describe(offset);
                Count(size);
                objects.Add(id, new GitObject(type, size, pack, offset));
            }
        }

        LooseObjectCount = 0;
        foreach (string path in LooseFiles())
        {
            string relative = Path.GetRelativePath(objectsRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            string[] parts = relative.Split('/');
            Limits.Require(parts.Length == 2 && parts[0].Length == 2 && parts[1].Length == 38, "stored-object-path");
            string id = parts[0] + parts[1];
            Limits.Require(Limits.IsId(id), "stored-object-id");
            LooseObjectCount++;
            if (objects.ContainsKey(id)) continue;
            (int type, int size) = LooseObject.ReadHeader(path);
            Count(size);
            GitObject value = new(type, size, path);
            Limits.Require(value.Id == id, "stored-object-hash");
            objects.Add(id, value);
        }

        known = objects;
        return new(objects, ReadRefs(root));
    }

    public void Commit(
        IReadOnlyDictionary<string, GitObject> objects,
        IReadOnlyDictionary<string, string> refs,
        string? head,
        CancellationToken cancellationToken)
    {
        foreach ((string id, GitObject value) in objects.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value.PackSource is { } pack && packs.Contains(pack)) continue;
            if (known.TryGetValue(id, out GitObject? existing) && ReferenceEquals(existing, value) && value.StoredPath == ObjectPath(id)) continue;
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
                        LooseObjectCount++;
                        continue;
                    }
                    catch (IOException) when (!File.Exists(finalPath)) { }
                }
                using (FileStream output = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (Stream source = value.OpenRead()) LooseObject.Write(output, value.Type, value.Size, source, cancellationToken);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, finalPath, overwrite: false);
                value.StoredAt(finalPath);
                LooseObjectCount++;
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
        known = objects;

        commitObserver?.Invoke(DurableCommitPhase.ObjectsFlushed);
        cancellationToken.ThrowIfCancellationRequested();
        WriteRefs(refs, objects, cancellationToken);
        WriteHead(head);
    }

    /// <summary>
    /// Writes every object into one new pack with its index, then removes the loose files and the packs
    /// that preceded it. Objects are re-pointed at the new pack as it is adopted, so the dictionary a
    /// repository holds keeps working without being rebuilt. The new pack is complete and indexed before
    /// anything old is deleted; a crash in between leaves duplicates, which loading tolerates.
    /// </summary>
    public RepackResult Repack(IEnumerable<PackEntry> entries, CancellationToken cancellationToken)
    {
        PackEntry[] items = [.. entries];
        if (items.Length == 0)
        {
            RemoveAllPacks();
            return new(0, 0, 0);
        }
        string temporaryPack = Path.Combine(stagingRoot, "pack-" + Guid.NewGuid().ToString("N") + ".pack");
        string temporaryIndex = Path.ChangeExtension(temporaryPack, ".idx");
        List<PackPlacement> placements;
        byte[] checksum;
        try
        {
            using (FileStream output = new(temporaryPack, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 256 * 1024))
            {
                placements = PackBuilder.Write(output, items, offsetDeltas: true, deltify: true, cancellationToken);
                output.Flush(flushToDisk: true);
                checksum = new byte[20];
                output.Position = output.Length - 20;
                output.ReadExactly(checksum);
            }
            using (FileStream output = new(temporaryIndex, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024))
            {
                PackBuilder.WriteIndex(output, placements, checksum);
                output.Flush(flushToDisk: true);
            }
            string name = "pack-" + Convert.ToHexStringLower(checksum);
            string finalPack = Path.Combine(packRoot, name + ".pack");
            string finalIndex = Path.Combine(packRoot, name + ".idx");
            if (File.Exists(finalPack) && File.Exists(finalIndex))
            {
                // The same objects packed the same way: the pack already here is this one. Nothing to adopt.
                File.Delete(temporaryPack);
                File.Delete(temporaryIndex);
            }
            else
            {
                if (File.Exists(finalIndex)) File.Delete(finalIndex);
                File.Move(temporaryPack, finalPack, overwrite: true);
                File.Move(temporaryIndex, finalIndex, overwrite: false);
            }
            PackFile adopted = PackFile.Open(finalPack, cancellationToken);
            Dictionary<string, long> offsets = placements.ToDictionary(placement => placement.Id, placement => placement.Offset, StringComparer.Ordinal);
            int loose = 0;
            foreach (PackEntry entry in items)
            {
                string id = entry.Object.Id;
                if (entry.Object.StoredPath is not null) loose++;
                entry.Object.StoredIn(adopted, offsets[id]);
            }
            PackFile[] previous = [.. packs];
            packs.Clear();
            packs.Add(adopted);
            foreach (PackFile old in previous)
            {
                old.Dispose();
                if (old.PackPath != adopted.PackPath)
                {
                    File.Delete(old.IndexPath);
                    File.Delete(old.PackPath);
                }
            }
            int removed = 0;
            foreach (string path in LooseFiles().ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(path);
                removed++;
            }
            LooseObjectCount = 0;
            return new(items.Length, removed, previous.Length);
        }
        finally
        {
            if (File.Exists(temporaryPack)) File.Delete(temporaryPack);
            if (File.Exists(temporaryIndex)) File.Delete(temporaryIndex);
        }
    }

    public void Dispose()
    {
        foreach (PackFile pack in packs) pack.Dispose();
        packs.Clear();
    }

    private void RemoveAllPacks()
    {
        foreach (PackFile pack in packs)
        {
            pack.Dispose();
            File.Delete(pack.IndexPath);
            File.Delete(pack.PackPath);
        }
        packs.Clear();
    }

    private IEnumerable<string> LooseFiles() => Directory.EnumerateDirectories(objectsRoot)
        .Where(directory => Path.GetFileName(directory).Length == 2)
        .OrderBy(directory => directory, StringComparer.Ordinal)
        .SelectMany(directory => Directory.EnumerateFiles(directory).OrderBy(path => path, StringComparer.Ordinal));

    private string ObjectPath(string id) => Path.Combine(objectsRoot, id[..2], id[2..]);

    private void WriteSkeleton()
    {
        string config = Path.Combine(root, "config");
        if (!File.Exists(config))
            File.WriteAllText(config, "[core]\n\trepositoryformatversion = 0\n\tfilemode = false\n\tbare = true\n", Encoding.ASCII);
        string head = Path.Combine(root, "HEAD");
        if (!File.Exists(head)) File.WriteAllText(head, "ref: refs/heads/main\n", Encoding.ASCII);
    }

    private void WriteHead(string? branch)
    {
        if (branch is null) return;
        string content = "ref: refs/heads/" + branch + "\n";
        string head = Path.Combine(root, "HEAD");
        if (File.Exists(head) && File.ReadAllText(head, Encoding.ASCII) == content) return;
        string temporary = Path.Combine(root, ".HEAD-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, content, Encoding.ASCII);
            File.Move(temporary, head, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void WriteRefs(IReadOnlyDictionary<string, string> refs, IReadOnlyDictionary<string, GitObject> objects, CancellationToken cancellationToken)
    {
        string refsTemporaryPath = Path.Combine(root, ".packed-refs-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream output = new(refsTemporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new(output, Encoding.ASCII, leaveOpen: true))
            {
                writer.NewLine = "\n";
                writer.WriteLine(PackedRefsHeader);
                foreach ((string name, string id) in refs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    Limits.Require(Limits.IsRef(name) && Limits.IsId(id), "ref-snapshot");
                    writer.Write(id);
                    writer.Write(' ');
                    writer.WriteLine(name);
                    // "fully-peeled" promises a peeled line under every reference to a tag object, so a
                    // reader knows a reference without one points at something that is not a tag.
                    string? peeled = Peel(objects, id);
                    if (peeled is not null) writer.WriteLine("^" + peeled);
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
            // Loose references, which only something other than Graft writes, would shadow the packed file.
            string refsRoot = Path.Combine(root, "refs");
            foreach (string loose in Directory.EnumerateFiles(refsRoot, "*", SearchOption.AllDirectories).ToArray())
                File.Delete(loose);
        }
        finally
        {
            if (File.Exists(refsTemporaryPath)) File.Delete(refsTemporaryPath);
        }
    }

    private static string? Peel(IReadOnlyDictionary<string, GitObject> objects, string id)
    {
        if (!objects.TryGetValue(id, out GitObject? value) || value.Type != 4) return null;
        string current = id;
        for (int depth = 0; depth < 16 && value is { Type: 4 }; depth++)
        {
            List<(string Id, int Type)> links = Graph.Links(value);
            if (links.Count != 1) return null;
            current = links[0].Id;
            objects.TryGetValue(current, out value);
        }
        return value is { Type: not 4 } ? current : null;
    }

    private static Dictionary<string, string> ReadRefs(string root)
    {
        Dictionary<string, string> refs = new(StringComparer.Ordinal);
        string packed = Path.Combine(root, PackedRefsName);
        if (File.Exists(packed))
        {
            foreach (string line in File.ReadAllLines(packed, Encoding.ASCII))
            {
                if (line.Length == 0 || line[0] == '#' || line[0] == '^') continue;
                string[] parts = line.Split(' ');
                Limits.Require(parts.Length == 2 && Limits.IsId(parts[0]) && Limits.IsRef(parts[1]), "stored-ref");
                Limits.Require(refs.TryAdd(parts[1], parts[0]), "stored-ref-duplicate");
            }
        }
        string refsRoot = Path.Combine(root, "refs");
        if (Directory.Exists(refsRoot))
        {
            foreach (string path in Directory.EnumerateFiles(refsRoot, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (name.EndsWith(".lock", StringComparison.Ordinal)) continue;
                string id = File.ReadAllText(path, Encoding.ASCII).Trim();
                Limits.Require(Limits.IsRef(name) && Limits.IsId(id), "stored-ref");
                refs[name] = id;
            }
        }
        return refs;
    }

    private void RecoverRefSnapshot()
    {
        if (!File.Exists(refsPath) && File.Exists(refsBackupPath))
            File.Move(refsBackupPath, refsPath);
        else if (File.Exists(refsPath) && File.Exists(refsBackupPath))
            File.Delete(refsBackupPath);
        string legacy = Path.Combine(root, LegacyRefsName);
        string legacyBackup = Path.Combine(root, LegacyRefsBackupName);
        if (!File.Exists(legacy) && File.Exists(legacyBackup)) File.Move(legacyBackup, legacy);
        else if (File.Exists(legacy) && File.Exists(legacyBackup)) File.Delete(legacyBackup);
    }

    /// <summary>Converts the format Graft used before: raw content behind a type byte, and a reference snapshot.</summary>
    private void Migrate()
    {
        foreach (string path in LooseFiles().ToArray())
        {
            if (LooseObject.LooksLikeZlib(path)) continue;
            string id = Path.GetFileName(Path.GetDirectoryName(path)!) + Path.GetFileName(path);
            Limits.Require(Limits.IsId(id), "stored-object-id");
            int type;
            int size;
            using (FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                type = source.ReadByte();
                Limits.Require(type is >= 1 and <= 4 && source.Length - 1 <= Limits.ObjectBytes, "stored-object-type");
                size = (int)(source.Length - 1);
            }
            string temporary = Path.Combine(stagingRoot, "migrate-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    source.Position = 1;
                    LooseObject.Write(output, type, size, source);
                    output.Flush(flushToDisk: true);
                }
                GitObject converted = new(type, size, temporary);
                Limits.Require(converted.Id == id, "stored-object-hash");
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        string legacy = Path.Combine(root, LegacyRefsName);
        if (File.Exists(legacy))
        {
            Dictionary<string, string> refs = new(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(legacy, Encoding.ASCII))
            {
                string[] parts = line.Split(' ');
                Limits.Require(parts.Length == 2 && Limits.IsId(parts[0]) && Limits.IsRef(parts[1]), "stored-ref");
                Limits.Require(refs.TryAdd(parts[1], parts[0]), "stored-ref-duplicate");
            }
            // Peel lines need the objects, so the snapshot is converted through an ordinary load and write.
            RepositoryState state = Load();
            WriteRefs(refs, state.Objects, CancellationToken.None);
            WriteHead(Repository.HeadOf(refs, null));
            File.Delete(legacy);
        }
    }
}

/// <summary>What a repack did: how many objects went into the pack, how many loose files and old packs it removed.</summary>
public sealed record RepackResult(int Packed, int LooseRemoved, int PacksRemoved);

internal enum DurableCommitPhase
{
    ObjectsFlushed,
    RefsFlushed
}
