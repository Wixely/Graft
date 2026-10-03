using System.Globalization;
using System.Text;

namespace Graft.Git;

/// <summary>One file to write into a commit. The content is the exact blob body.</summary>
public sealed record FileChange(string Path, byte[] Content, bool Executable = false);

/// <summary>What an authored commit produced.</summary>
public sealed record AuthoredCommit(string CommitId, string TreeId, string Branch, bool CreatedBranch);

/// <summary>
/// Writes Git objects on the server.
///
/// <para>
/// Every other write path in Graft ingests a pack a client already built, so until now nothing here
/// constructed objects itself. Committing a file from the browser does: it writes a blob, rewrites the trees
/// along that path, builds a commit and advances the branch. The resulting objects are ordinary Git objects,
/// byte-identical to what a client would have pushed, so the repository stays consistent with its own
/// transport and a later clone reproduces them exactly.
/// </para>
/// </summary>
public static class RepositoryAuthor
{
    private const int MaximumChanges = 100;
    private const int MaximumPathLength = 1024;
    private const int MaximumMessageLength = 10_000;
    private const string TreeMode = "40000";
    private const string RegularFileMode = "100644";
    private const string ExecutableFileMode = "100755";

    /// <summary>
    /// Commits <paramref name="changes"/> onto <paramref name="branch"/>, creating the branch when the
    /// repository is empty. Adds or replaces files; it does not delete, and it never rewrites history.
    /// </summary>
    public static AuthoredCommit CommitFiles(
        this Repository repository,
        string branch,
        IReadOnlyList<FileChange> changes,
        GitIdentity author,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(changes);

        string reference = "refs/heads/" + branch;
        Limits.Require(Limits.IsRef(reference), "branch");
        Limits.Require(changes.Count is > 0 and <= MaximumChanges, "change-count");

        string body = NormalizeMessage(message);
        string identity = FormatIdentity(author);

        // Validate every path before writing anything, so a rejected batch leaves no partial state.
        string[] paths = [.. changes.Select(change => ValidatePath(change.Path))];
        Limits.Require(paths.Distinct(StringComparer.Ordinal).Count() == paths.Length, "duplicate-path");
        foreach (FileChange change in changes)
        {
            ArgumentNullException.ThrowIfNull(change.Content);
            Limits.Require(change.Content.Length <= Limits.ObjectBytes, "blob-size");
        }

        Dictionary<string, GitObject> written = new(StringComparer.Ordinal);
        string? parent = repository.Refs.GetValueOrDefault(reference);
        if (parent is not null)
            Limits.Require(repository.Objects.TryGetValue(parent, out GitObject? tip) && tip.Type == 1, "branch-tip");

        string? parentTree = parent is null ? null : RepositoryView.ReadCommit(repository, parent).TreeId;
        Node root = Load(repository, parentTree);

        for (int index = 0; index < changes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] segments = paths[index].Split('/');
            Node node = root;
            for (int depth = 0; depth < segments.Length - 1; depth++)
                node = node.Descend(repository, segments[depth]);
            string name = segments[^1];
            Limits.Require(!node.Subtrees.ContainsKey(name) && !node.PendingSubtrees.ContainsKey(name), "path-is-directory");
            GitObject blob = new(3, changes[index].Content);
            written[blob.Id] = blob;
            node.Leaves[name] = (changes[index].Executable ? ExecutableFileMode : RegularFileMode, blob.Id);
        }

        string treeId = Serialize(root, written, cancellationToken);
        GitObject commit = BuildCommit(treeId, parent, identity, body);
        written[commit.Id] = commit;

        Dictionary<string, GitObject> stagedObjects = new(repository.Objects, StringComparer.Ordinal);
        foreach ((string id, GitObject value) in written) stagedObjects[id] = value;
        Limits.Require(stagedObjects.Count <= Limits.ObjectCount, "repository-object-count");
        Limits.Require(stagedObjects.Values.Sum(value => (long)value.Data.Length) <= Limits.ExpandedBytes, "repository-budget");

        // The same reachability validation a pushed batch gets, before anything becomes visible.
        Graph.Walk(stagedObjects, [commit.Id], cancellationToken);
        if (parent is not null)
            Limits.Require(Graph.IsAncestor(stagedObjects, parent, commit.Id, cancellationToken), "non-fast-forward");

        Dictionary<string, string> stagedRefs = new(repository.Refs, StringComparer.Ordinal) { [reference] = commit.Id };
        Limits.Require(stagedRefs.Count <= Limits.RefCount, "ref-count");
        foreach (string name in stagedRefs.Keys)
            Limits.Require(!stagedRefs.Keys.Any(other => other.StartsWith(name + "/", StringComparison.Ordinal)), "ref-prefix-conflict");

        cancellationToken.ThrowIfCancellationRequested();
        repository.Apply(stagedObjects, stagedRefs, cancellationToken);
        return new(commit.Id, treeId, branch, parent is null);
    }

    /// <summary>
    /// A directory being rewritten. Subtrees the commit does not touch stay unloaded and are re-emitted by
    /// their existing id, so untouched directories keep their exact bytes and are never re-serialized.
    /// </summary>
    private sealed class Node
    {
        public readonly Dictionary<string, (string Mode, string Id)> Leaves = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Node> Subtrees = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> PendingSubtrees = new(StringComparer.Ordinal);

        public Node Descend(Repository repository, string name)
        {
            Limits.Require(!Leaves.ContainsKey(name), "path-is-file");
            if (Subtrees.TryGetValue(name, out Node? loaded)) return loaded;
            Node child = PendingSubtrees.TryGetValue(name, out string? id) ? Load(repository, id) : new Node();
            PendingSubtrees.Remove(name);
            Subtrees[name] = child;
            return child;
        }
    }

    private static Node Load(Repository repository, string? treeId)
    {
        Node node = new();
        if (treeId is null) return node;
        Limits.Require(repository.Objects.TryGetValue(treeId, out GitObject? tree) && tree.Type == 2, "tree");
        Cursor cursor = new(tree!.Data);
        while (cursor.Remaining > 0)
        {
            StringBuilder mode = new();
            byte value;
            while ((value = cursor.Byte()) != (byte)' ') mode.Append((char)value);
            int start = cursor.Position;
            while (cursor.Byte() != 0) { }
            string name = Utf8(tree.Data.AsSpan(start, cursor.Position - start - 1));
            string id = Convert.ToHexStringLower(cursor.Take(20));
            if (mode.ToString() is "40000" or "040000") node.PendingSubtrees[name] = id;
            else node.Leaves[name] = (mode.ToString(), id);
        }
        return node;
    }

    private static string Serialize(Node node, Dictionary<string, GitObject> written, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<(string Name, bool IsTree, string Mode, string Id)> entries = [];
        foreach ((string name, (string mode, string id)) in node.Leaves) entries.Add((name, false, mode, id));
        foreach ((string name, string id) in node.PendingSubtrees) entries.Add((name, true, TreeMode, id));
        foreach ((string name, Node child) in node.Subtrees)
            entries.Add((name, true, TreeMode, Serialize(child, written, cancellationToken)));

        entries.Sort(static (left, right) => CompareNames(left.Name, left.IsTree, right.Name, right.IsTree));
        using MemoryStream output = new();
        foreach ((string name, _, string mode, string id) in entries)
        {
            output.Write(Encoding.ASCII.GetBytes(mode + " "));
            output.Write(Encoding.UTF8.GetBytes(name));
            output.WriteByte(0);
            output.Write(Convert.FromHexString(id));
        }
        GitObject tree = new(2, output.ToArray());
        written[tree.Id] = tree;
        return tree.Id;
    }

    /// <summary>
    /// Git's canonical tree ordering: names compare as raw bytes, but a directory sorts as though its name
    /// ended in a slash. Emitting entries in any other order produces a tree that real Git reports as
    /// unsorted, so this is what keeps authored objects interchangeable with pushed ones.
    /// </summary>
    private static int CompareNames(string leftName, bool leftIsTree, string rightName, bool rightIsTree)
    {
        byte[] left = Encoding.UTF8.GetBytes(leftIsTree ? leftName + "/" : leftName);
        byte[] right = Encoding.UTF8.GetBytes(rightIsTree ? rightName + "/" : rightName);
        int shared = Math.Min(left.Length, right.Length);
        for (int index = 0; index < shared; index++)
        {
            int difference = left[index].CompareTo(right[index]);
            if (difference != 0) return difference;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static GitObject BuildCommit(string treeId, string? parent, string identity, string message)
    {
        StringBuilder headers = new();
        headers.Append("tree ").Append(treeId).Append('\n');
        if (parent is not null) headers.Append("parent ").Append(parent).Append('\n');
        headers.Append("author ").Append(identity).Append('\n');
        headers.Append("committer ").Append(identity).Append('\n');
        headers.Append('\n').Append(message);
        return new(1, Encoding.UTF8.GetBytes(headers.ToString()));
    }

    /// <summary>Formats an identity as <c>Name &lt;email&gt; seconds +HHMM</c>, which the reader parses back.</summary>
    private static string FormatIdentity(GitIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string name = identity.Name.Trim();
        string email = identity.Email.Trim();
        Limits.Require(name.Length is > 0 and <= 100 && !name.Any(IsForbiddenInIdentity), "author-name");
        Limits.Require(email.Length is > 0 and <= 200 && !email.Any(IsForbiddenInIdentity), "author-email");
        TimeSpan offset = identity.When.Offset;
        Limits.Require(Math.Abs(offset.TotalHours) <= 14, "author-offset");
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        return string.Create(CultureInfo.InvariantCulture,
            $"{name} <{email}> {identity.When.ToUnixTimeSeconds()} {sign}{Math.Abs(offset.Hours):D2}{Math.Abs(offset.Minutes):D2}");
    }

    private static bool IsForbiddenInIdentity(char value) => value is '<' or '>' or '\n' or '\0';

    private static string NormalizeMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string normalized = message.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        Limits.Require(normalized.Length is > 0 and <= MaximumMessageLength, "message");
        Limits.Require(!normalized.Contains('\0', StringComparison.Ordinal), "message");
        return normalized + "\n";
    }

    /// <summary>
    /// Rejects anything that must never reach a tree. Beyond Git's own rules this also refuses control
    /// characters and backslashes, which are legal to Git on Linux but turn into a different path, or an
    /// invalid one, when a Windows client checks the repository out.
    /// </summary>
    private static string ValidatePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Limits.Require(path.Length is > 0 and <= MaximumPathLength, "path");
        Limits.Require(path[0] != '/' && path[^1] != '/', "path");
        string[] segments = path.Split('/');
        foreach (string segment in segments)
        {
            Limits.Require(segment.Length > 0, "path");
            Limits.Require(segment is not "." and not "..", "path-traversal");
            Limits.Require(!segment.Equals(".git", StringComparison.OrdinalIgnoreCase), "path-dot-git");
            Limits.Require(!segment.Any(value => value < 0x20 || value == 0x7f || value == '\\'), "path-character");
        }
        return path;
    }

    private static string Utf8(ReadOnlySpan<byte> bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new GitInputException("utf8"); }
    }
}
