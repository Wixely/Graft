using System.Text;

namespace Graft.Git;

/// <summary>One line of one file that contains what was looked for.</summary>
public sealed record BlobMatch(string Path, string BlobId, int Line, string Text);

/// <summary>
/// Searching a repository's content without an index.
///
/// <para>
/// Graft keeps no search index; a search reads the tree at a revision and looks at every text file in it,
/// bounded by file size, by the number of files it will read and by the number of matches it will return.
/// That is slow in principle and fast enough in practice for the repositories this engine is sized for,
/// and it has the property an index does not, which is that it is never stale: it searches what is there.
/// A match is a case-insensitive substring; there are no patterns, because a pattern language is where a
/// search becomes a way to make a server work hard.
/// </para>
/// </summary>
public static class RepositorySearch
{
    private const int MaximumFileBytes = 1024 * 1024;
    private const int MaximumFilesRead = 20_000;
    private const int MaximumLineLength = 400;

    public static IReadOnlyList<BlobMatch> Blobs(Repository repository, string revision, string query, int maximumMatches,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(query);
        string needle = query.Trim();
        if (needle.Length is 0 or > 200 || maximumMatches < 1) return [];
        IReadOnlyList<GitTreeEntry>? files = RepositoryView.FlatTree(repository, revision);
        if (files is null) return [];

        List<BlobMatch> matches = [];
        int read = 0;
        UTF8Encoding strict = new(false, true);
        foreach (GitTreeEntry entry in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count >= maximumMatches || read >= MaximumFilesRead) break;
            if (!repository.Objects.TryGetValue(entry.Id, out GitObject? blob) || blob.Type != 3 || blob.Size > MaximumFileBytes) continue;
            read++;
            byte[] bytes = blob.Data;
            if (Array.IndexOf(bytes, (byte)0) >= 0) continue;
            string text;
            try { text = strict.GetString(bytes); }
            catch (DecoderFallbackException) { continue; }
            if (!text.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            int line = 1;
            foreach (string row in text.Split('\n'))
            {
                if (row.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    string shown = row.TrimEnd('\r');
                    matches.Add(new(entry.Path, entry.Id, line, shown.Length > MaximumLineLength ? shown[..MaximumLineLength] : shown));
                    if (matches.Count >= maximumMatches) break;
                }
                line++;
            }
        }
        return matches;
    }

    /// <summary>Commits reachable from a revision whose message contains the query, newest first.</summary>
    public static IReadOnlyList<GitCommit> Commits(Repository repository, string revision, string query, int maximumMatches,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(query);
        string needle = query.Trim();
        if (needle.Length is 0 or > 200 || maximumMatches < 1) return [];
        List<GitCommit> matches = [];
        foreach (GitCommit commit in RepositoryView.Commits(repository, revision, 0, 5000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (commit.Message.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                commit.Author.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(commit);
                if (matches.Count >= maximumMatches) break;
            }
        }
        return matches;
    }
}
