using System.Text;

namespace Graft.Git;

public sealed record RepositoryState(
    Dictionary<string, GitObject> Objects,
    Dictionary<string, string> Refs);

public interface IRepositoryPersistence
{
    RepositoryState Load();
    void Commit(
        IReadOnlyDictionary<string, GitObject> objects,
        IReadOnlyDictionary<string, string> refs,
        CancellationToken cancellationToken);
}

// Access must be serialized by the host. A persistence implementation commits validated
// objects and refs before the new dictionaries become visible to readers.
public sealed class Repository
{
    private readonly IRepositoryPersistence? persistence;
    public Dictionary<string, GitObject> Objects { get; private set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Refs { get; private set; } = new(StringComparer.Ordinal);
    public int OffsetDeltas { get; private set; }
    public int ReferenceDeltas { get; private set; }

    /// <summary>
    /// The branch an application has chosen as this repository's default, or null to let the repository
    /// decide. The engine does not persist this; the host sets it from wherever it stores project settings.
    /// </summary>
    public string? PreferredDefaultBranch { get; set; }

    /// <summary>
    /// The branch a clone should check out and a browser should open. A repository that was never told
    /// otherwise falls back to convention rather than to a single hardcoded name: plenty of repositories
    /// use <c>master</c>, and an imported one may use neither that nor <c>main</c>. Null only when the
    /// repository has no branches at all.
    /// </summary>
    public string? DefaultBranch
    {
        get
        {
            if (PreferredDefaultBranch is { Length: > 0 } preferred &&
                Refs.ContainsKey("refs/heads/" + preferred)) return preferred;
            if (Refs.ContainsKey("refs/heads/main")) return "main";
            if (Refs.ContainsKey("refs/heads/master")) return "master";
            return Refs.Keys
                .Where(name => name.StartsWith("refs/heads/", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => name[11..])
                .FirstOrDefault();
        }
    }

    public Repository(IRepositoryPersistence? persistence = null)
    {
        this.persistence = persistence;
        if (persistence is null) return;
        RepositoryState state = persistence.Load();
        Limits.Require(state.Objects.Count <= Limits.ObjectCount, "repository-object-count");
        Limits.Require(state.Objects.Values.Sum(value => (long)value.Size) <= Limits.ExpandedBytes, "repository-budget");
        Limits.Require(state.Refs.Count <= Limits.RefCount, "ref-count");
        foreach ((string name, string id) in state.Refs)
        {
            Limits.Require(Limits.IsRef(name) && Limits.IsId(id), "stored-ref");
            Limits.Require(state.Objects.ContainsKey(id), "stored-ref-object");
        }
        Graph.Walk(state.Objects, state.Refs.Values, CancellationToken.None);
        Objects = state.Objects;
        Refs = state.Refs;
    }

    /// <summary>
    /// Publishes an already-validated batch through the same durable path a push uses: objects and refs are
    /// committed to storage before either becomes visible to readers. Callers own the validation; see
    /// <see cref="RepositoryAuthor"/> for the server-authored case.
    /// </summary>
    internal void Apply(
        Dictionary<string, GitObject> objects,
        Dictionary<string, string> refs,
        CancellationToken cancellationToken)
    {
        persistence?.Commit(objects, refs, cancellationToken);
        Objects = objects;
        Refs = refs;
    }

    public byte[] Advertise(string service)
    {
        var receive = service == "git-receive-pack";
        Limits.Require(receive || service == "git-upload-pack", "service");
        // A clone follows the advertised symref to decide what to check out. Pointing it at a branch that
        // does not exist leaves the client complaining about a dangling remote HEAD, so this advertises the
        // repository's actual default rather than assuming one name.
        var head = DefaultBranch;
        var capabilities = receive
            ? "report-status ofs-delta"
            : "ofs-delta" + (head is null ? "" : " symref=HEAD:refs/heads/" + head);
        using var output = new MemoryStream();
        Packet.Write(output, "# service=" + service + "\n");
        Packet.Flush(output);
        var first = true;
        void Ref(string id, string name)
        {
            Packet.Write(output, id + " " + name + (first ? "\0" + capabilities : "") + "\n");
            first = false;
        }
        if (!receive && head is not null && Refs.TryGetValue("refs/heads/" + head, out var headId)) Ref(headId, "HEAD");
        foreach (var pair in Refs.OrderBy(p => p.Key, StringComparer.Ordinal)) Ref(pair.Value, pair.Key);
        if (first) Ref(Limits.Zero, "capabilities^{}");
        Packet.Flush(output);
        return output.ToArray();
    }

    public byte[] Receive(byte[] body, CancellationToken cancellationToken = default)
    {
        using MemoryStream input = new(body, false);
        return Receive(input, cancellationToken);
    }

    public byte[] Receive(Stream body, CancellationToken cancellationToken = default)
    {
        using TemporaryStorage? staging = persistence is null ? null : new();
        var cursor = new Cursor(body, body.Position, body.Length);
        var commands = new List<(string Old, string New, string Name)>();
        var requested = new HashSet<string>(StringComparer.Ordinal);
        string? packet;
        while ((packet = cursor.Packet()) is not null)
        {
            Limits.Require(commands.Count < Limits.RefCount, "ref-count");
            var sections = packet.TrimEnd('\n').Split('\0');
            Limits.Require(sections.Length <= 2 && (commands.Count == 0 || sections.Length == 1), "command-capabilities");
            if (sections.Length == 2)
                foreach (var cap in sections[1].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    Limits.Require(cap is "report-status" or "ofs-delta", "unsupported-capability");
                    requested.Add(cap);
                }
            var parts = sections[0].Split(' ');
            Limits.Require(parts.Length == 3 && Limits.IsId(parts[0]) && Limits.IsId(parts[1]) && Limits.IsRef(parts[2]), "ref-command");
            Limits.Require(parts[1] != Limits.Zero, "deletion-unsupported");
            Limits.Require(commands.All(c => c.Name != parts[2]), "duplicate-ref");
            Limits.Require(Refs.GetValueOrDefault(parts[2], Limits.Zero) == parts[0], "stale-ref");
            commands.Add((parts[0], parts[1], parts[2]));
        }
        Limits.Require(commands.Count > 0, "empty-update");
        var pack = cursor.Remaining == 0
            ? new PackResult(new(StringComparer.Ordinal), 0, 0)
            : Pack.Read(body, Objects, cancellationToken, staging?.DirectoryPath);
        var stagedObjects = new Dictionary<string, GitObject>(Objects, StringComparer.Ordinal);
        foreach (var pair in pack.Objects) stagedObjects[pair.Key] = pair.Value;
        Limits.Require(stagedObjects.Count <= Limits.ObjectCount && stagedObjects.Values.Sum(obj => (long)obj.Size) <= Limits.ExpandedBytes, "repository-budget");
        // Validate even unreachable submitted objects before making any of the batch visible.
        Graph.Walk(stagedObjects, pack.Objects.Keys, cancellationToken);
        var stagedRefs = new Dictionary<string, string>(Refs, StringComparer.Ordinal);
        foreach (var command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Limits.Require(stagedObjects.ContainsKey(command.New), "missing-ref-object");
            Graph.Walk(stagedObjects, [command.New], cancellationToken);
            if (command.Name.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                Limits.Require(stagedObjects[command.New].Type == 1, "branch-not-commit");
                if (command.Old != Limits.Zero)
                    Limits.Require(Graph.IsAncestor(stagedObjects, command.Old, command.New, cancellationToken), "non-fast-forward");
            }
            else Limits.Require(command.Old == Limits.Zero || command.Old == command.New, "tag-replacement");
            stagedRefs[command.Name] = command.New;
        }
        Limits.Require(stagedRefs.Count <= Limits.RefCount, "ref-count");
        foreach (var name in stagedRefs.Keys)
            Limits.Require(!stagedRefs.Keys.Any(other => other.StartsWith(name + "/", StringComparison.Ordinal)), "ref-prefix-conflict");
        cancellationToken.ThrowIfCancellationRequested();
        persistence?.Commit(stagedObjects, stagedRefs, cancellationToken);
        Objects = stagedObjects;
        Refs = stagedRefs;
        OffsetDeltas += pack.OffsetDeltas;
        ReferenceDeltas += pack.ReferenceDeltas;
        using var output = new MemoryStream();
        if (requested.Contains("report-status"))
        {
            Packet.Write(output, "unpack ok\n");
            foreach (var command in commands) Packet.Write(output, "ok " + command.Name + "\n");
            Packet.Flush(output);
        }
        return output.ToArray();
    }

    public byte[] Upload(byte[] body, CancellationToken cancellationToken = default)
    {
        using MemoryStream input = new(body, false);
        using MemoryStream output = new();
        Upload(input, output, cancellationToken);
        return output.ToArray();
    }

    public void Upload(Stream body, Stream output, CancellationToken cancellationToken = default)
    {
        var cursor = new Cursor(body, body.Position, body.Length);
        var wants = new HashSet<string>(StringComparer.Ordinal);
        var negotiation = false;
        var done = false;
        while (cursor.Remaining > 0)
        {
            var packet = cursor.Packet();
            if (packet is null) { negotiation = true; continue; }
            var parts = packet.TrimEnd('\n').Split(' ');
            if (parts[0] == "want")
            {
                Limits.Require(!negotiation && parts.Length >= 2 && Limits.IsId(parts[1]), "want");
                Limits.Require(Refs.Values.Contains(parts[1], StringComparer.Ordinal), "unadvertised-want");
                foreach (var capability in parts.Skip(2))
                    Limits.Require(capability == "ofs-delta", "unsupported-capability");
                wants.Add(parts[1]);
                Limits.Require(wants.Count <= Limits.RefCount, "want-count");
            }
            else if (parts[0] == "have")
                Limits.Require(negotiation && parts.Length == 2 && Limits.IsId(parts[1]), "have");
            else if (parts[0] == "done")
            {
                Limits.Require(parts.Length == 1 && cursor.Remaining == 0, "done");
                done = true;
            }
            else throw new GitInputException("unsupported-upload-command");
        }
        Limits.Require(wants.Count > 0, "missing-want");
        // No multi_ack/side-band/thin-pack capability is advertised. Stateless rounds
        // return NAK; after done send a complete (not delta-compressed) reachable pack.
        Packet.Write(output, "NAK\n");
        if (done)
        {
            var reachable = Graph.Walk(Objects, wants, cancellationToken);
            Pack.Write(output, reachable.Order(StringComparer.Ordinal).Select(id => Objects[id]), cancellationToken);
        }
    }
}

public static class Graph
{
    public static HashSet<string> Walk(IReadOnlyDictionary<string, GitObject> objects, IEnumerable<string> roots, CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(string Id, int Type)>(roots.Select(id => (id, 0)));
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Limits.Require(objects.TryGetValue(item.Id, out var obj), "missing-object");
            Limits.Require(item.Type == 0 || obj!.Type == item.Type, "linked-object-type");
            if (!visited.Add(item.Id)) continue;
            Limits.Require(visited.Count <= Limits.ObjectCount, "graph-budget");
            foreach (var link in Links(obj!)) pending.Push(link);
        }
        return visited;
    }

    public static bool IsAncestor(IReadOnlyDictionary<string, GitObject> objects, string ancestor, string child, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(child);
        while (pending.TryPop(out var id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (id == ancestor) return true;
            if (!seen.Add(id)) continue;
            foreach (var link in Links(objects[id]).Where(l => l.Type == 1)) pending.Push(link.Id);
        }
        return false;
    }

    /// <summary>
    /// The best common ancestor of two commits, or null when they share none.
    ///
    /// <para>
    /// Walks back from the first commit collecting everything reachable, then walks back from the second
    /// and returns the first commit it meets that the first could also reach. "First it meets" is by
    /// breadth, so the answer is the nearest common ancestor along the second commit's history rather than
    /// an arbitrary one. Where the graph has several equally near ancestors, Git would recursively merge
    /// them into a virtual base and this returns one of them instead; the difference can only show up in a
    /// criss-cross history, and the result is still an ancestor of both.
    /// </para>
    /// </summary>
    public static string? MergeBase(IReadOnlyDictionary<string, GitObject> objects, string first, string second,
        CancellationToken cancellationToken = default)
    {
        HashSet<string> reachable = Ancestors(objects, first, cancellationToken);
        HashSet<string> seen = new(StringComparer.Ordinal);
        Queue<string> pending = new();
        pending.Enqueue(second);
        while (pending.TryDequeue(out string? id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(id)) continue;
            Limits.Require(seen.Count <= Limits.ObjectCount, "merge-base-budget");
            if (reachable.Contains(id)) return id;
            if (!objects.TryGetValue(id, out GitObject? commit) || commit.Type != 1) continue;
            foreach ((string parent, int type) in Links(commit))
                if (type == 1) pending.Enqueue(parent);
        }
        return null;
    }

    /// <summary>A commit and every commit it can reach, following parents only.</summary>
    public static HashSet<string> Ancestors(IReadOnlyDictionary<string, GitObject> objects, string commitId,
        CancellationToken cancellationToken = default)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        Stack<string> pending = new();
        pending.Push(commitId);
        while (pending.TryPop(out string? id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(id)) continue;
            Limits.Require(seen.Count <= Limits.ObjectCount, "ancestry-budget");
            if (!objects.TryGetValue(id, out GitObject? commit) || commit.Type != 1) continue;
            foreach ((string parent, int type) in Links(commit))
                if (type == 1) pending.Push(parent);
        }
        return seen;
    }

    public static List<(string Id, int Type)> Links(GitObject obj)
    {
        var links = new List<(string, int)>();
        if (obj.Type == 3) return links;
        if (obj.Type == 2)
        {
            byte[] data = obj.Data;
            var cursor = new Cursor(data);
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (cursor.Remaining > 0)
            {
                var mode = new StringBuilder();
                byte value;
                while ((value = cursor.Byte()) != (byte)' ')
                {
                    Limits.Require(mode.Length < 6 && value is >= (byte)'0' and <= (byte)'7', "tree-mode");
                    mode.Append((char)value);
                }
                var type = mode.ToString() switch { "40000" or "040000" => 2, "100644" or "100755" or "120000" => 3, "160000" => 0, _ => -1 };
                Limits.Require(type >= 0, "tree-mode");
                var start = cursor.Position;
                while (cursor.Byte() != 0) { }
                var name = data.AsSpan((int)start, (int)(cursor.Position - start - 1));
                Limits.Require(name.Length > 0 && !name.Contains((byte)'/') && !name.SequenceEqual("."u8) && !name.SequenceEqual(".."u8), "tree-name");
                Limits.Require(names.Add(Convert.ToHexString(name)), "tree-duplicate-name");
                var id = Convert.ToHexStringLower(cursor.Take(20));
                // Gitlinks reference another repository; never follow them here.
                if (type != 0) links.Add((id, type));
            }
            return links;
        }
        var headerEnd = obj.Data.AsSpan().IndexOf("\n\n"u8);
        Limits.Require(headerEnd >= 0, "object-headers");
        var lines = Encoding.UTF8.GetString(obj.Data.AsSpan(0, headerEnd)).Split('\n');
        string Id(string line, string prefix)
        {
            Limits.Require(line.StartsWith(prefix, StringComparison.Ordinal) && Limits.IsId(line[prefix.Length..]), "object-link");
            return line[prefix.Length..];
        }
        if (obj.Type == 1)
        {
            Limits.Require(lines.Count(l => l.StartsWith("tree ", StringComparison.Ordinal)) == 1, "commit-tree");
            links.Add((Id(lines[0], "tree "), 2));
            foreach (var line in lines.Where(l => l.StartsWith("parent ", StringComparison.Ordinal))) links.Add((Id(line, "parent "), 1));
        }
        else
        {
            Limits.Require(obj.Type == 4 && lines.Length >= 3, "tag-headers");
            var id = Id(lines[0], "object ");
            var type = lines[1] switch { "type commit" => 1, "type tree" => 2, "type blob" => 3, "type tag" => 4, _ => 0 };
            Limits.Require(type != 0 && lines[2].StartsWith("tag ", StringComparison.Ordinal), "tag-type");
            links.Add((id, type));
        }
        return links;
    }
}
