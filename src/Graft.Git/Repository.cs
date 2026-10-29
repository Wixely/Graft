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
        string? head,
        CancellationToken cancellationToken);
    /// <summary>Writes every object into one pack and removes what it replaces; see <see cref="DurableRepositoryStorage.Repack"/>.</summary>
    RepackResult Repack(IEnumerable<PackEntry> entries, CancellationToken cancellationToken);
    int LooseObjectCount { get; }
}

// Access must be serialized by the host. A persistence implementation commits validated
// objects and refs before the new dictionaries become visible to readers.
public sealed class Repository
{
    /// <summary>
    /// How many loose objects a push may leave behind before the repository is repacked on the spot, the
    /// way Git runs <c>gc --auto</c> at the end of a receive. Git's own default is 6,700; this is lower
    /// because every loose object is hashed when the repository is opened, and a pack is not.
    /// </summary>
    public const int AutoRepackLooseObjects = 1000;
    private const string Agent = "agent=graft";

    private readonly IRepositoryPersistence? persistence;
    public Dictionary<string, GitObject> Objects { get; private set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Refs { get; private set; } = new(StringComparer.Ordinal);
    public int OffsetDeltas { get; private set; }
    public int ReferenceDeltas { get; private set; }

    /// <summary>The number of loose objects in storage, or zero for a repository held only in memory.</summary>
    public int LooseObjectCount => persistence?.LooseObjectCount ?? 0;

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
    public string? DefaultBranch => HeadOf(Refs, PreferredDefaultBranch);

    /// <summary>The default-branch rule applied to any set of references; what <c>HEAD</c> on disk is written from.</summary>
    public static string? HeadOf(IReadOnlyDictionary<string, string> refs, string? preferred)
    {
        if (preferred is { Length: > 0 } && refs.ContainsKey("refs/heads/" + preferred)) return preferred;
        if (refs.ContainsKey("refs/heads/main")) return "main";
        if (refs.ContainsKey("refs/heads/master")) return "master";
        return refs.Keys
            .Where(name => name.StartsWith("refs/heads/", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => name[11..])
            .FirstOrDefault();
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
        persistence?.Commit(objects, refs, HeadOf(refs, PreferredDefaultBranch), cancellationToken);
        Objects = objects;
        Refs = refs;
    }

    /// <summary>
    /// Brings a commit from another repository into this one and points a server-owned reference at it:
    /// every object reachable from <paramref name="commitId"/> that this repository lacks is copied, the
    /// batch is validated the way a pushed one is, and the reference is written. This is how a proposal
    /// from a fork becomes something the target repository can analyse, diff and merge without ever
    /// reading the fork at merge time.
    /// </summary>
    /// <returns>True when anything changed; false when the reference already pointed at the commit.</returns>
    public bool Mirror(Repository from, string commitId, string referenceName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(from);
        Limits.Require(Limits.IsId(commitId) && Limits.IsRef(referenceName) && !Limits.IsClientRef(referenceName), "mirror-target");
        Limits.Require(from.Objects.TryGetValue(commitId, out GitObject? tip) && tip.Type == 1, "mirror-commit");
        if (Refs.GetValueOrDefault(referenceName) == commitId && Objects.ContainsKey(commitId)) return false;

        using TemporaryStorage staging = new();
        Dictionary<string, GitObject> staged = new(Objects, StringComparer.Ordinal);
        foreach (string id in Graph.Walk(from.Objects, [commitId], cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (staged.ContainsKey(id)) continue;
            // A copy of the bytes into this repository's own staging, never a reference to the other
            // repository's file: the durable commit moves staged files into place, and moving the fork's
            // object out from under it would be the one way this could damage the fork.
            staged[id] = from.Objects[id].CopyInto(staging.DirectoryPath);
        }
        Limits.Require(staged.Count <= Limits.ObjectCount, "repository-object-count");
        Limits.Require(staged.Values.Sum(value => (long)value.Size) <= Limits.ExpandedBytes, "repository-budget");
        Graph.Walk(staged, [commitId], cancellationToken);
        Dictionary<string, string> refs = new(Refs, StringComparer.Ordinal) { [referenceName] = commitId };
        Limits.Require(refs.Count <= Limits.RefCount, "ref-count");
        persistence?.Commit(staged, refs, HeadOf(refs, PreferredDefaultBranch), cancellationToken);
        Objects = staged;
        Refs = refs;
        return true;
    }

    /// <summary>Removes a server-owned reference, leaving its objects. Nothing to do when it is absent.</summary>
    public void DropServerRef(string referenceName, CancellationToken cancellationToken = default)
    {
        Limits.Require(Limits.IsRef(referenceName) && !Limits.IsClientRef(referenceName), "mirror-target");
        if (!Refs.ContainsKey(referenceName)) return;
        Dictionary<string, string> refs = new(Refs, StringComparer.Ordinal);
        refs.Remove(referenceName);
        persistence?.Commit(new Dictionary<string, GitObject>(Objects, StringComparer.Ordinal), refs, HeadOf(refs, PreferredDefaultBranch), cancellationToken);
        Refs = refs;
    }

    /// <summary>
    /// Writes every object into a single pack with delta compression and removes the loose files and
    /// older packs it replaces. Nothing is pruned: an object nothing points at is kept, because a comment
    /// or a proposal may still name the commit it belongs to. Paths are recovered from the trees so that
    /// versions of one file are packed beside each other, which is where deltas are found.
    /// </summary>
    public RepackResult Repack(CancellationToken cancellationToken = default)
    {
        if (persistence is null) return new(0, 0, 0);
        Dictionary<string, string?> named = Graph.WalkNamed(Objects, Refs.Values, null, cancellationToken);
        List<PackEntry> entries = new(Objects.Count);
        foreach ((string id, GitObject obj) in Objects)
            entries.Add(new(obj, named.GetValueOrDefault(id)));
        return persistence.Repack(entries, cancellationToken);
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
            ? "report-status ofs-delta " + Agent
            : "multi_ack_detailed ofs-delta include-tag " + Agent + (head is null ? "" : " symref=HEAD:refs/heads/" + head);
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
        foreach (var pair in Refs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Ref(pair.Value, pair.Key);
            // A tag that is an object is advertised with what it resolves to, as Git does.
            if (!receive && Peel(pair.Value) is { } peeled) Ref(peeled, pair.Key + "^{}");
        }
        if (first) Ref(Limits.Zero, "capabilities^{}");
        Packet.Flush(output);
        return output.ToArray();
    }

    /// <summary>
    /// The protocol version 2 capability advertisement, sent in place of the reference list when a client
    /// asks for version 2. References are then fetched with <c>ls-refs</c>.
    /// </summary>
    public byte[] AdvertiseV2(string service)
    {
        Limits.Require(service == "git-upload-pack", "service");
        using MemoryStream output = new();
        Packet.Write(output, "# service=" + service + "\n");
        Packet.Flush(output);
        Packet.Write(output, "version 2\n");
        Packet.Write(output, Agent + "\n");
        Packet.Write(output, "ls-refs=unborn\n");
        Packet.Write(output, "fetch\n");
        Packet.Write(output, "server-option\n");
        Packet.Write(output, "object-format=sha1\n");
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
                    Limits.Require(cap is "report-status" or "ofs-delta" || cap.StartsWith("agent=", StringComparison.Ordinal), "unsupported-capability");
                    requested.Add(cap);
                }
            var parts = sections[0].Split(' ');
            Limits.Require(parts.Length == 3 && Limits.IsId(parts[0]) && Limits.IsId(parts[1]) && Limits.IsClientRef(parts[2]), "ref-command");
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
        persistence?.Commit(stagedObjects, stagedRefs, HeadOf(stagedRefs, PreferredDefaultBranch), cancellationToken);
        Objects = stagedObjects;
        Refs = stagedRefs;
        OffsetDeltas += pack.OffsetDeltas;
        ReferenceDeltas += pack.ReferenceDeltas;
        // Housekeeping at the end of a receive, as Git does it: the client waits a moment longer and the
        // repository opens quickly next time.
        if (persistence is not null && persistence.LooseObjectCount >= AutoRepackLooseObjects) Repack(cancellationToken);
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

    /// <summary>
    /// Protocol version 0 <c>upload-pack</c> over stateless HTTP. The client names what it wants and what
    /// it has; every have the repository recognises is acknowledged as common, the client is told when the
    /// common set is enough to stop, and the pack it finally receives carries only what lies between.
    /// </summary>
    public void Upload(Stream body, Stream output, CancellationToken cancellationToken = default)
    {
        var cursor = new Cursor(body, body.Position, body.Length);
        var wants = new HashSet<string>(StringComparer.Ordinal);
        var haves = new List<string>();
        var capabilities = new HashSet<string>(StringComparer.Ordinal);
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
                Limits.Require(Refs.Values.Contains(parts[1], StringComparer.Ordinal) || Refs.Values.Any(id => Peel(id) == parts[1]), "unadvertised-want");
                foreach (var capability in parts.Skip(2))
                {
                    Limits.Require(capability is "ofs-delta" or "multi_ack_detailed" or "multi_ack" or "include-tag" ||
                        capability.StartsWith("agent=", StringComparison.Ordinal), "unsupported-capability");
                    capabilities.Add(capability);
                }
                wants.Add(parts[1]);
                Limits.Require(wants.Count <= Limits.RefCount, "want-count");
            }
            else if (parts[0] == "have")
            {
                Limits.Require(negotiation && parts.Length == 2 && Limits.IsId(parts[1]), "have");
                Limits.Require(haves.Count < Limits.ObjectCount, "have-count");
                haves.Add(parts[1]);
            }
            else if (parts[0] == "done")
            {
                Limits.Require(parts.Length == 1 && cursor.Remaining == 0, "done");
                done = true;
            }
            else throw new GitInputException("unsupported-upload-command");
        }
        Limits.Require(wants.Count > 0, "missing-want");

        List<string> common = [.. haves.Where(id => Objects.TryGetValue(id, out GitObject? have) && have.Type == 1).Distinct(StringComparer.Ordinal)];
        bool detailed = capabilities.Contains("multi_ack_detailed");
        if (!done)
        {
            // One round of negotiation, answered the way upload-pack answers it: each common have is
            // acknowledged, "ready" says the pack could be built now, and the round ends with NAK.
            if (detailed)
            {
                foreach (string id in common) Packet.Write(output, "ACK " + id + " common\n");
                if (common.Count > 0 && Graph.AllReach(Objects, wants, common, cancellationToken))
                    Packet.Write(output, "ACK " + common[^1] + " ready\n");
            }
            Packet.Write(output, "NAK\n");
            return;
        }
        if (detailed) foreach (string id in common) Packet.Write(output, "ACK " + id + " common\n");
        if (common.Count > 0 && (detailed || capabilities.Contains("multi_ack"))) Packet.Write(output, "ACK " + common[^1] + "\n");
        else Packet.Write(output, "NAK\n");
        WritePack(output, wants, common, capabilities.Contains("ofs-delta"), capabilities.Contains("include-tag"), cancellationToken);
    }

    /// <summary>
    /// Protocol version 2 over stateless HTTP: one command per request. <c>ls-refs</c> lists references
    /// under the prefixes asked for; <c>fetch</c> negotiates and sends a pack in sideband packets.
    /// </summary>
    public void ServeV2(Stream body, Stream output, CancellationToken cancellationToken = default)
    {
        Cursor cursor = new(body, body.Position, body.Length);
        string? command = null;
        List<string> arguments = [];
        bool inArguments = false;
        while (cursor.Remaining > 0)
        {
            (PacketKind kind, string? line) = cursor.Line();
            if (kind == PacketKind.Flush) break;
            if (kind == PacketKind.Delimiter) { Limits.Require(command is not null && !inArguments, "v2-delimiter"); inArguments = true; continue; }
            Limits.Require(kind == PacketKind.Data, "v2-packet");
            string text = line!.TrimEnd('\n');
            if (!inArguments)
            {
                if (text.StartsWith("command=", StringComparison.Ordinal)) { Limits.Require(command is null, "v2-command"); command = text[8..]; }
                else if (text.StartsWith("agent=", StringComparison.Ordinal) || text == "object-format=sha1") { }
                else if (text.StartsWith("server-option=", StringComparison.Ordinal)) { }
                else throw new GitInputException("v2-capability: " + text);
            }
            else arguments.Add(text);
        }
        Limits.Require(command is not null, "v2-command");
        switch (command)
        {
            case "ls-refs": LsRefs(arguments, output); break;
            case "fetch": FetchV2(arguments, output, cancellationToken); break;
            default: throw new GitInputException("v2-command: " + command);
        }
    }

    private void LsRefs(List<string> arguments, Stream output)
    {
        bool symrefs = false;
        bool peel = false;
        bool unborn = false;
        List<string> prefixes = [];
        foreach (string argument in arguments)
        {
            if (argument == "symrefs") symrefs = true;
            else if (argument == "peel") peel = true;
            else if (argument == "unborn") unborn = true;
            else if (argument.StartsWith("ref-prefix ", StringComparison.Ordinal)) prefixes.Add(argument[11..]);
            else throw new GitInputException("ls-refs-argument");
        }
        bool Wanted(string name) => prefixes.Count == 0 || prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
        string? head = DefaultBranch;
        if (Wanted("HEAD"))
        {
            if (head is not null && Refs.TryGetValue("refs/heads/" + head, out string? headId))
                Packet.Write(output, headId + " HEAD" + (symrefs ? " symref-target:refs/heads/" + head : "") + "\n");
            else if (unborn && head is null && Refs.Count == 0)
                Packet.Write(output, "unborn HEAD symref-target:refs/heads/main\n");
        }
        foreach ((string name, string id) in Refs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!Wanted(name)) continue;
            string line = id + " " + name;
            if (peel && Peel(id) is { } peeled) line += " peeled:" + peeled;
            Packet.Write(output, line + "\n");
        }
        Packet.Flush(output);
    }

    private void FetchV2(List<string> arguments, Stream output, CancellationToken cancellationToken)
    {
        HashSet<string> wants = new(StringComparer.Ordinal);
        List<string> haves = [];
        List<(string Name, string Id)> wantedRefs = [];
        bool done = false;
        bool offsetDeltas = false;
        bool includeTag = false;
        foreach (string argument in arguments)
        {
            string[] parts = argument.Split(' ');
            switch (parts[0])
            {
                case "want":
                    Limits.Require(parts.Length == 2 && Limits.IsId(parts[1]), "want");
                    Limits.Require(Refs.Values.Contains(parts[1], StringComparer.Ordinal) || Refs.Values.Any(id => Peel(id) == parts[1]), "unadvertised-want");
                    wants.Add(parts[1]);
                    break;
                case "want-ref":
                    Limits.Require(parts.Length == 2 && Refs.ContainsKey(parts[1]), "want-ref");
                    wants.Add(Refs[parts[1]]);
                    wantedRefs.Add((parts[1], Refs[parts[1]]));
                    break;
                case "have":
                    Limits.Require(parts.Length == 2 && Limits.IsId(parts[1]), "have");
                    Limits.Require(haves.Count < Limits.ObjectCount, "have-count");
                    haves.Add(parts[1]);
                    break;
                case "done": done = true; break;
                case "ofs-delta": offsetDeltas = true; break;
                case "include-tag": includeTag = true; break;
                case "thin-pack": case "no-progress": case "wait-for-done": break;
                case "filter": case "deepen": case "deepen-since": case "deepen-not": case "deepen-relative": case "shallow":
                    throw new GitInputException("fetch-unsupported: " + parts[0]);
                default: throw new GitInputException("fetch-argument: " + parts[0]);
            }
            Limits.Require(wants.Count <= Limits.RefCount, "want-count");
        }
        Limits.Require(wants.Count > 0, "missing-want");
        List<string> common = [.. haves.Where(id => Objects.TryGetValue(id, out GitObject? have) && have.Type == 1).Distinct(StringComparer.Ordinal)];
        if (!done)
        {
            Packet.Write(output, "acknowledgments\n");
            if (common.Count == 0) Packet.Write(output, "NAK\n");
            else foreach (string id in common) Packet.Write(output, "ACK " + id + "\n");
            if (common.Count == 0 || !Graph.AllReach(Objects, wants, common, cancellationToken))
            {
                Packet.Flush(output);
                return;
            }
            Packet.Write(output, "ready\n");
            Packet.Delimiter(output);
        }
        if (wantedRefs.Count > 0)
        {
            Packet.Write(output, "wanted-refs\n");
            foreach ((string name, string id) in wantedRefs) Packet.Write(output, id + " " + name + "\n");
            Packet.Delimiter(output);
        }
        Packet.Write(output, "packfile\n");
        using (SidebandStream sideband = new(output))
            WritePack(sideband, wants, common, offsetDeltas, includeTag, cancellationToken);
        Packet.Flush(output);
    }

    private void WritePack(Stream output, HashSet<string> wants, List<string> common, bool offsetDeltas, bool includeTag, CancellationToken cancellationToken)
    {
        HashSet<string>? exclude = common.Count == 0 ? null : Graph.Walk(Objects, common, cancellationToken);
        Dictionary<string, string?> named = Graph.WalkNamed(Objects, wants, exclude, cancellationToken);
        if (includeTag)
        {
            // Tags whose target is going out travel with it, so a clone that asked for branches alone still
            // learns the tags that name commits it received.
            foreach ((string name, string id) in Refs)
            {
                if (!name.StartsWith("refs/tags/", StringComparison.Ordinal) || named.ContainsKey(id)) continue;
                if (Objects.TryGetValue(id, out GitObject? tag) && tag.Type == 4 && Peel(id) is { } peeled && named.ContainsKey(peeled))
                    named[id] = null;
            }
        }
        PackBuilder.Write(output, named.Select(pair => new PackEntry(Objects[pair.Key], pair.Value)), offsetDeltas, deltify: true, cancellationToken);
    }

    /// <summary>What a tag object ultimately points at, or null when the id is not a tag object.</summary>
    private string? Peel(string id)
    {
        if (!Objects.TryGetValue(id, out GitObject? value) || value.Type != 4) return null;
        string current = id;
        for (int depth = 0; depth < 16 && value is { Type: 4 }; depth++)
        {
            List<(string Id, int Type)> links = Graph.Links(value);
            if (links.Count != 1) return null;
            current = links[0].Id;
            if (!Objects.TryGetValue(current, out value)) return null;
        }
        return value is { Type: not 4 } ? current : null;
    }

    /// <summary>Packs pack data into sideband-64k packets on band 1, as protocol version 2 requires.</summary>
    private sealed class SidebandStream(Stream inner) : Stream
    {
        private const int Payload = 65515;
        private readonly byte[] buffer = new byte[Payload];
        private int filled;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        private long position;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Write(byte[] source, int offset, int count) => Write(source.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> source)
        {
            position += source.Length;
            while (source.Length > 0)
            {
                int take = Math.Min(Payload - filled, source.Length);
                source[..take].CopyTo(buffer.AsSpan(filled));
                filled += take;
                source = source[take..];
                if (filled == Payload) Emit();
            }
        }
        public override void WriteByte(byte value) => Write(new ReadOnlySpan<byte>(in value));
        private void Emit()
        {
            if (filled == 0) return;
            Packet.WriteBand(inner, 1, buffer.AsSpan(0, filled));
            filled = 0;
        }
        public override void Flush() { }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Emit();
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

public static class Graph
{
    public static HashSet<string> Walk(IReadOnlyDictionary<string, GitObject> objects, IEnumerable<string> roots, CancellationToken cancellationToken) =>
        Walk(objects, roots, null, cancellationToken);

    /// <summary>
    /// Everything reachable from <paramref name="roots"/>, not descending into anything in
    /// <paramref name="stop"/>. When the stop set is itself closed under reachability, the result is
    /// exactly what the roots reach that the stop set does not, which is what a fetch has to send.
    /// </summary>
    public static HashSet<string> Walk(IReadOnlyDictionary<string, GitObject> objects, IEnumerable<string> roots, HashSet<string>? stop, CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(string Id, int Type)>(roots.Select(id => (id, 0)));
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Limits.Require(objects.TryGetValue(item.Id, out var obj), "missing-object");
            Limits.Require(item.Type == 0 || obj!.Type == item.Type, "linked-object-type");
            if (stop is not null && stop.Contains(item.Id)) continue;
            if (!visited.Add(item.Id)) continue;
            Limits.Require(visited.Count <= Limits.ObjectCount, "graph-budget");
            foreach (var link in Links(obj!)) pending.Push(link);
        }
        return visited;
    }

    /// <summary>
    /// The same walk, remembering the path each tree and blob was first seen at. Commits and tags have no
    /// path. The names let a pack writer place versions of one file side by side.
    /// </summary>
    public static Dictionary<string, string?> WalkNamed(IReadOnlyDictionary<string, GitObject> objects, IEnumerable<string> roots, HashSet<string>? stop, CancellationToken cancellationToken)
    {
        Dictionary<string, string?> visited = new(StringComparer.Ordinal);
        Stack<(string Id, string? Path)> pending = new(roots.Select(id => (id, (string?)null)));
        while (pending.TryPop(out (string Id, string? Path) item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Limits.Require(objects.TryGetValue(item.Id, out GitObject? obj), "missing-object");
            if (stop is not null && stop.Contains(item.Id)) continue;
            if (!visited.TryAdd(item.Id, item.Path)) continue;
            Limits.Require(visited.Count <= Limits.ObjectCount, "graph-budget");
            if (obj!.Type == 2)
            {
                string prefix = item.Path is null ? "" : item.Path + "/";
                foreach ((string name, string id, int type) in TreeEntries(obj))
                    if (type != 0) pending.Push((id, prefix + name));
            }
            else foreach ((string id, int type) in Links(obj)) pending.Push((id, type == 2 ? "" : null));
        }
        return visited;
    }

    /// <summary>True when every want can reach, through parents, at least one commit in <paramref name="common"/>.</summary>
    public static bool AllReach(IReadOnlyDictionary<string, GitObject> objects, IEnumerable<string> wants, IReadOnlyCollection<string> common, CancellationToken cancellationToken)
    {
        HashSet<string> commonSet = new(common, StringComparer.Ordinal);
        foreach (string want in wants)
        {
            string start = want;
            if (objects.TryGetValue(start, out GitObject? value) && value.Type == 4)
            {
                for (int depth = 0; depth < 16 && value is { Type: 4 }; depth++)
                {
                    List<(string Id, int Type)> links = Links(value);
                    if (links.Count != 1) return false;
                    start = links[0].Id;
                    if (!objects.TryGetValue(start, out value)) return false;
                }
            }
            if (value is not { Type: 1 }) return false;
            HashSet<string> seen = new(StringComparer.Ordinal);
            Queue<string> pending = new();
            pending.Enqueue(start);
            bool reached = false;
            while (pending.TryDequeue(out string? id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(id)) continue;
                if (commonSet.Contains(id)) { reached = true; break; }
                Limits.Require(seen.Count <= Limits.ObjectCount, "ancestry-budget");
                if (!objects.TryGetValue(id, out GitObject? commit) || commit.Type != 1) continue;
                foreach ((string parent, int type) in Links(commit)) if (type == 1) pending.Enqueue(parent);
            }
            if (!reached) return false;
        }
        return true;
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

    /// <summary>A tree's entries in order: name, id and the kind of object (2 tree, 3 blob, 0 gitlink).</summary>
    public static List<(string Name, string Id, int Type)> TreeEntries(GitObject tree)
    {
        Limits.Require(tree.Type == 2, "tree");
        List<(string, string, int)> entries = [];
        byte[] data = tree.Data;
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
            entries.Add((Encoding.UTF8.GetString(name), id, type));
        }
        return entries;
    }

    public static List<(string Id, int Type)> Links(GitObject obj)
    {
        var links = new List<(string, int)>();
        if (obj.Type == 3) return links;
        if (obj.Type == 2)
        {
            // Gitlinks reference another repository; never follow them here.
            foreach ((string _, string id, int type) in TreeEntries(obj)) if (type != 0) links.Add((id, type));
            return links;
        }
        byte[] data = obj.Data;
        var headerEnd = data.AsSpan().IndexOf("\n\n"u8);
        Limits.Require(headerEnd >= 0, "object-headers");
        var lines = Encoding.UTF8.GetString(data.AsSpan(0, headerEnd)).Split('\n');
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
