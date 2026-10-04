using System.Globalization;
using System.Text;

namespace Graft.Git;

public sealed record GitIdentity(string Name, string Email, DateTimeOffset When);
public sealed record GitCommit(
    string Id,
    string TreeId,
    IReadOnlyList<string> ParentIds,
    string Title,
    string Message,
    GitIdentity Author,
    GitIdentity Committer);
public sealed record GitTreeEntry(string Id, string Name, string Path, string Type, string Mode);
public sealed record GitFile(string BlobId, string CommitId, string Path, string Mode, byte[] Content);

public static class RepositoryView
{
    public static string? ResolveCommit(Repository repository, string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        string? id = revision switch
        {
            // HEAD follows whatever the repository considers its default branch, which is not always "main".
            "HEAD" => repository.DefaultBranch is { } head
                ? repository.Refs.GetValueOrDefault("refs/heads/" + head)
                : null,
            _ when Limits.IsId(revision) => revision,
            _ => repository.Refs.GetValueOrDefault("refs/heads/" + revision)
                ?? repository.Refs.GetValueOrDefault("refs/tags/" + revision)
        };
        if (id is null || !repository.Objects.TryGetValue(id, out GitObject? value)) return null;
        for (int depth = 0; value.Type == 4 && depth < 16; depth++)
        {
            (string target, int type) = Graph.Links(value).Single();
            if (!repository.Objects.TryGetValue(target, out value) || value.Type != type) return null;
            id = target;
        }
        return value.Type == 1 ? id : null;
    }

    public static IReadOnlyList<(string Name, string Id)> Branches(Repository repository) => repository.Refs
        .Where(pair => pair.Key.StartsWith("refs/heads/", StringComparison.Ordinal))
        .Select(pair => (pair.Key[11..], pair.Value))
        .OrderBy(pair => pair.Item1, StringComparer.Ordinal)
        .ToArray();

    public static GitCommit ReadCommit(Repository repository, string id)
    {
        Limits.Require(repository.Objects.TryGetValue(id, out GitObject? value) && value.Type == 1, "commit");
        int separator = value!.Data.AsSpan().IndexOf("\n\n"u8);
        Limits.Require(separator >= 0, "commit-headers");
        string headers = StrictUtf8(value.Data.AsSpan(0, separator));
        string message = StrictUtf8(value.Data.AsSpan(separator + 2));
        string[] lines = headers.Split('\n');
        string tree = Value(lines, "tree ");
        List<string> parents = lines.Where(line => line.StartsWith("parent ", StringComparison.Ordinal))
            .Select(line => line[7..]).ToList();
        Limits.Require(Limits.IsId(tree) && parents.All(Limits.IsId), "commit-link");
        GitIdentity author = ParseIdentity(Value(lines, "author "));
        GitIdentity committer = ParseIdentity(Value(lines, "committer "));
        string title = message.Split('\n', 2)[0];
        return new(id, tree, parents, title, message, author, committer);
    }

    public static IReadOnlyList<GitCommit> Commits(Repository repository, string revision, int skip, int take)
    {
        string? root = ResolveCommit(repository, revision);
        if (root is null) return [];
        HashSet<string> visited = [];
        Stack<string> pending = new();
        pending.Push(root);
        List<GitCommit> commits = [];
        while (pending.TryPop(out string? id))
        {
            if (!visited.Add(id)) continue;
            Limits.Require(visited.Count <= Limits.ObjectCount, "history-budget");
            GitCommit commit = ReadCommit(repository, id);
            commits.Add(commit);
            foreach (string parent in commit.ParentIds) pending.Push(parent);
        }
        return commits.OrderByDescending(commit => commit.Committer.When)
            .ThenBy(commit => commit.Id, StringComparer.Ordinal)
            .Skip(skip).Take(take).ToArray();
    }

    public static IReadOnlyList<GitTreeEntry>? Tree(Repository repository, string revision, string path)
    {
        string? commitId = ResolveCommit(repository, revision);
        if (commitId is null) return null;
        string treeId = ReadCommit(repository, commitId).TreeId;
        string normalized = NormalizePath(path);
        if (normalized.Length > 0)
        {
            foreach (string segment in normalized.Split('/'))
            {
                GitTreeEntry? entry = ReadTree(repository, treeId, "").SingleOrDefault(item => item.Name == segment);
                if (entry is null || entry.Type != "tree") return null;
                treeId = entry.Id;
            }
        }
        return ReadTree(repository, treeId, normalized);
    }

    public static GitFile? File(Repository repository, string revision, string path)
    {
        string? commitId = ResolveCommit(repository, revision);
        if (commitId is null) return null;
        string normalized = NormalizePath(path);
        if (normalized.Length == 0) return null;
        string treeId = ReadCommit(repository, commitId).TreeId;
        string[] segments = normalized.Split('/');
        GitTreeEntry? entry = null;
        string parent = "";
        for (int index = 0; index < segments.Length; index++)
        {
            entry = ReadTree(repository, treeId, parent).SingleOrDefault(item => item.Name == segments[index]);
            if (entry is null) return null;
            if (index < segments.Length - 1)
            {
                if (entry.Type != "tree") return null;
                treeId = entry.Id;
                parent = entry.Path;
            }
        }
        if (entry is null || entry.Type != "blob" || !repository.Objects.TryGetValue(entry.Id, out GitObject? blob) || blob.Type != 3)
            return null;
        return new(entry.Id, commitId, normalized, entry.Mode, blob!.Data);
    }

    private static IReadOnlyList<GitTreeEntry> ReadTree(Repository repository, string treeId, string parent)
    {
        Limits.Require(repository.Objects.TryGetValue(treeId, out GitObject? tree) && tree.Type == 2, "tree");
        Cursor cursor = new(tree!.Data);
        List<GitTreeEntry> entries = [];
        while (cursor.Remaining > 0)
        {
            StringBuilder mode = new();
            byte value;
            while ((value = cursor.Byte()) != (byte)' ') mode.Append((char)value);
            int start = cursor.Position;
            while (cursor.Byte() != 0) { }
            string name = StrictUtf8(tree.Data.AsSpan(start, cursor.Position - start - 1));
            string id = Convert.ToHexStringLower(cursor.Take(20));
            string type = mode.ToString() switch
            {
                "40000" or "040000" => "tree",
                "100644" or "100755" or "120000" => "blob",
                "160000" => "commit",
                _ => throw new GitInputException("tree-mode")
            };
            entries.Add(new(id, name, parent.Length == 0 ? name : parent + "/" + name, type, mode.ToString()));
        }
        return entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
    }

    private static string NormalizePath(string path)
    {
        if (path.Length == 0) return "";
        Limits.Require(path.Length <= 1024 && path[0] != '/' && path[^1] != '/', "path");
        string[] parts = path.Split('/');
        Limits.Require(parts.All(part => part.Length > 0 && part is not "." and not ".."), "path");
        return string.Join('/', parts);
    }

    private static string Value(string[] lines, string prefix)
    {
        string[] matches = lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        Limits.Require(matches.Length == 1, "commit-header");
        return matches[0][prefix.Length..];
    }

    private static GitIdentity ParseIdentity(string value)
    {
        int emailStart = value.LastIndexOf(" <", StringComparison.Ordinal);
        int emailEnd = value.LastIndexOf("> ", StringComparison.Ordinal);
        Limits.Require(emailStart > 0 && emailEnd > emailStart, "identity");
        string[] time = value[(emailEnd + 2)..].Split(' ');
        long seconds = 0;
        int hours = 0;
        int minutes = 0;
        Limits.Require(time.Length == 2 && long.TryParse(time[0], CultureInfo.InvariantCulture, out seconds) &&
            time[1].Length == 5 && (time[1][0] is '+' or '-') && int.TryParse(time[1].AsSpan(1, 2), out hours) &&
            int.TryParse(time[1].AsSpan(3, 2), out minutes) && hours <= 14 && minutes <= 59, "identity-time");
        TimeSpan offset = new(hours, minutes, 0);
        if (time[1][0] == '-') offset = -offset;
        return new(value[..emailStart], value[(emailStart + 2)..emailEnd], DateTimeOffset.FromUnixTimeSeconds(seconds).ToOffset(offset));
    }

    private static string StrictUtf8(ReadOnlySpan<byte> bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new GitInputException("utf8"); }
    }
}
