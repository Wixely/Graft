namespace Graft.Git;

/// <summary>
/// One line of a diff. <see cref="Kind"/> is <c>' '</c> for context, <c>'+'</c> for an addition and
/// <c>'-'</c> for a removal. <see cref="NoNewline"/> marks a line that ends its file without a newline,
/// which a unified diff reports beneath the line rather than as a line of its own.
/// </summary>
public sealed record DiffLine(char Kind, string Text, int? OldLine, int? NewLine, bool NoNewline = false);

/// <summary>A run of changed lines with its surrounding context, numbered as a unified diff numbers it.</summary>
public sealed record DiffHunk(int OldStart, int OldLines, int NewStart, int NewLines, IReadOnlyList<DiffLine> Lines);

/// <summary>
/// What happened to one path between two trees. <see cref="Hunks"/> is empty when the change carries no
/// text to show, which is the case for a binary file, a file too large to diff, and a mode-only change.
/// </summary>
public sealed record DiffFile(
    string Path,
    string Status,
    string? OldMode,
    string? NewMode,
    string? OldBlobId,
    string? NewBlobId,
    bool IsBinary,
    bool TooLarge,
    int Added,
    int Removed,
    IReadOnlyList<DiffHunk> Hunks);

/// <summary>A commit and what it changed against one parent.</summary>
public sealed record CommitDiff(
    GitCommit Commit,
    string? ParentId,
    IReadOnlyList<DiffFile> Files,
    bool Truncated);

/// <summary>
/// Compares two trees and produces a unified diff.
///
/// <para>
/// A forge has to answer "what did this commit change", and that question is separate from browsing a tree:
/// browsing reads one revision, this reads two and reports the difference. The comparison is done on the
/// object model rather than by shelling out, so it holds wherever the rest of the engine does.
/// </para>
///
/// <para>
/// The line comparison is Myers' greedy algorithm, the one Git uses by default, so the edit script is
/// minimal and in the ordinary case the hunks are the ones <c>git diff</c> shows. Common leading and
/// trailing lines are trimmed before it runs, which is what keeps a one-line edit in a large file cheap.
/// </para>
///
/// <para>
/// Where this can still differ from Git is placement, not content. When a change sits next to lines
/// identical to the ones it touches, several equally minimal scripts exist and Git runs a further pass
/// (<c>xdl_change_compact</c>) that slides each group to a preferred position among them. This does not
/// implement that pass, so a diff of such a region can show the same edit one line above or below where
/// Git would put it. Both are correct and both reconstruct the new file exactly, which is the property the
/// acceptance suite asserts for every file it compares; the suite also asserts hunk-for-hunk equality with
/// Git across the shapes where no such ambiguity arises.
/// </para>
/// </summary>
public static class RepositoryDiff
{
    /// <summary>Lines of unchanged content kept either side of a change, matching <c>git diff</c>'s default.</summary>
    public const int ContextLines = 3;

    /// <summary>Most files reported for one commit. Beyond this the diff is marked truncated.</summary>
    public const int MaximumFiles = 300;

    /// <summary>Largest file compared line by line. A larger one is reported as changed without hunks.</summary>
    public const int MaximumFileBytes = 1024 * 1024;

    /// <summary>Most lines compared in one file, which bounds the cost of a pathological input.</summary>
    public const int MaximumFileLines = 50_000;

    /// <summary>
    /// Diffs a commit against a parent. <paramref name="parentIndex"/> selects which parent of a merge to
    /// compare against; a commit with no parents is compared against an empty tree, so an initial commit
    /// reads as every file being added rather than as nothing having happened.
    /// </summary>
    public static CommitDiff ForCommit(Repository repository, string commitId, int parentIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(repository);
        GitCommit commit = RepositoryView.ReadCommit(repository, commitId);
        string? parentId = parentIndex >= 0 && parentIndex < commit.ParentIds.Count ? commit.ParentIds[parentIndex] : null;
        string? parentTree = parentId is null ? null : RepositoryView.ReadCommit(repository, parentId).TreeId;

        Dictionary<string, (string Id, string Mode)> before = parentTree is null ? [] : Flatten(repository, parentTree, "");
        Dictionary<string, (string Id, string Mode)> after = Flatten(repository, commit.TreeId, "");

        List<string> paths = before.Keys.Union(after.Keys, StringComparer.Ordinal).ToList();
        paths.Sort(StringComparer.Ordinal);

        List<DiffFile> files = [];
        bool truncated = false;
        foreach (string path in paths)
        {
            bool inBefore = before.TryGetValue(path, out (string Id, string Mode) old);
            bool inAfter = after.TryGetValue(path, out (string Id, string Mode) current);
            if (inBefore && inAfter && old.Id == current.Id && old.Mode == current.Mode) continue;
            if (files.Count == MaximumFiles) { truncated = true; break; }

            string status = !inBefore ? "added" : !inAfter ? "deleted" : "modified";
            files.Add(Compare(repository, path, status, inBefore ? old : null, inAfter ? current : null));
        }

        return new(commit, parentId, files, truncated);
    }

    /// <summary>Diffs one path between two trees, or reports why it could not be shown as text.</summary>
    private static DiffFile Compare(Repository repository, string path, string status,
        (string Id, string Mode)? before, (string Id, string Mode)? after)
    {
        byte[] oldBytes = before is null ? [] : Content(repository, before.Value.Id);
        byte[] newBytes = after is null ? [] : Content(repository, after.Value.Id);

        if (IsBinary(oldBytes) || IsBinary(newBytes))
            return new(path, status, before?.Mode, after?.Mode, before?.Id, after?.Id, true, false, 0, 0, []);
        if (oldBytes.Length > MaximumFileBytes || newBytes.Length > MaximumFileBytes)
            return new(path, status, before?.Mode, after?.Mode, before?.Id, after?.Id, false, true, 0, 0, []);

        FileLines oldLines = Lines(oldBytes);
        FileLines newLines = Lines(newBytes);
        if (oldLines.Texts.Length > MaximumFileLines || newLines.Texts.Length > MaximumFileLines)
            return new(path, status, before?.Mode, after?.Mode, before?.Id, after?.Id, false, true, 0, 0, []);

        IReadOnlyList<DiffHunk> hunks = Hunks(oldLines, newLines);
        int added = hunks.Sum(hunk => hunk.Lines.Count(line => line.Kind == '+'));
        int removed = hunks.Sum(hunk => hunk.Lines.Count(line => line.Kind == '-'));
        return new(path, status, before?.Mode, after?.Mode, before?.Id, after?.Id, false, false, added, removed, hunks);
    }

    /// <summary>
    /// Every blob reachable from a tree, keyed by its full path. Comparing two of these turns a recursive
    /// tree comparison into a set comparison, and because an unchanged directory keeps its object id, the
    /// walk could be pruned later without changing the result.
    /// </summary>
    private static Dictionary<string, (string Id, string Mode)> Flatten(Repository repository, string treeId, string parent)
    {
        Dictionary<string, (string Id, string Mode)> entries = new(StringComparer.Ordinal);
        foreach (GitTreeEntry entry in RepositoryView.ReadTree(repository, treeId, parent))
        {
            if (entry.Type == "tree")
            {
                foreach (KeyValuePair<string, (string Id, string Mode)> nested in Flatten(repository, entry.Id, entry.Path))
                    entries[nested.Key] = nested.Value;
            }
            // A submodule is a commit reference rather than content this engine holds, so it is never read
            // as a blob; it simply does not appear among the files whose text can be compared.
            else if (entry.Type == "blob") entries[entry.Path] = (entry.Id, entry.Mode);
        }
        return entries;
    }

    private static byte[] Content(Repository repository, string blobId)
    {
        Limits.Require(repository.Objects.TryGetValue(blobId, out GitObject? blob) && blob.Type == 3, "blob");
        return blob!.Data;
    }

    /// <summary>
    /// Git treats a file as binary when a NUL byte appears early in it, and so does this. Sniffing a prefix
    /// rather than the whole file is what keeps the check cheap on a large file.
    /// </summary>
    private static bool IsBinary(byte[] content) =>
        content.AsSpan(0, Math.Min(content.Length, 8000)).IndexOf((byte)0) >= 0;

    /// <summary>
    /// A file's lines, alongside the keys they are compared by.
    ///
    /// <para>
    /// The two differ only for a file whose last line has no newline. Such a line is not the same line as one
    /// that does, and Git reports it as changed rather than as context, so the key for it carries a marker
    /// the text does not. Comparing by key and rendering by text is what keeps those two facts separate.
    /// </para>
    /// </summary>
    private sealed record FileLines(string[] Texts, string[] Keys, bool EndsWithNewline);

    /// <summary>
    /// Appended to the key of a last line that has no newline after it, so that such a line never compares
    /// equal to one that does. A NUL cannot occur in text reaching this comparison, because content
    /// carrying one is classified as binary and never compared line by line, which is what makes it a safe
    /// sentinel. It is built from a character value rather than written as an escape so that no copy of
    /// this file carries a raw control byte, which would make the source unreadable to ordinary tools.
    /// </summary>
    private static readonly string NoNewlineSentinel = new([(char)0, 'n', 'o', 'n', 'e', 'w', 'l', 'i', 'n', 'e']);

    private static FileLines Lines(byte[] content)
    {
        if (content.Length == 0) return new([], [], true);
        string text = System.Text.Encoding.UTF8.GetString(content);
        bool endsWithNewline = text.EndsWith('\n');
        if (endsWithNewline) text = text[..^1];
        string[] texts = text.Split('\n');
        string[] keys = (string[])texts.Clone();
        if (!endsWithNewline) keys[^1] += NoNewlineSentinel;
        return new(texts, keys, endsWithNewline);
    }

    /// <summary>Groups an edit script into hunks, each carrying up to <see cref="ContextLines"/> either side.</summary>
    private static IReadOnlyList<DiffHunk> Hunks(FileLines oldLines, FileLines newLines)
    {
        List<DiffLine> script = Script(oldLines, newLines);
        List<DiffHunk> hunks = [];
        int index = 0;
        while (index < script.Count)
        {
            if (script[index].Kind == ' ') { index++; continue; }

            // Walk back over the context this change is entitled to, then forward to the end of the run,
            // treating a short stretch of context between two changes as joining them into one hunk.
            int start = Math.Max(0, index - ContextLines);
            int end = index;
            int quiet = 0;
            while (end < script.Count && quiet <= ContextLines * 2)
            {
                if (script[end].Kind == ' ') quiet++; else quiet = 0;
                end++;
            }
            end -= Math.Max(0, quiet - ContextLines);

            List<DiffLine> lines = script.GetRange(start, end - start);
            int oldCount = lines.Count(line => line.Kind is ' ' or '-');
            int newCount = lines.Count(line => line.Kind is ' ' or '+');
            // A side contributing no lines is numbered by the line it would be inserted after, which is zero
            // at the start of a file. That is what a unified diff means by "-0,0".
            int oldStart = oldCount > 0
                ? lines.First(line => line.OldLine is not null).OldLine!.Value
                : Preceding(script, start, line => line.OldLine);
            int newStart = newCount > 0
                ? lines.First(line => line.NewLine is not null).NewLine!.Value
                : Preceding(script, start, line => line.NewLine);

            hunks.Add(new(oldStart, oldCount, newStart, newCount, lines));
            index = end;
        }
        return hunks;
    }

    private static int Preceding(List<DiffLine> script, int start, Func<DiffLine, int?> number)
    {
        for (int index = start - 1; index >= 0; index--)
            if (number(script[index]) is int value) return value;
        return 0;
    }

    /// <summary>
    /// The edit script: every line of both sides in order, each marked as context, addition or removal.
    /// </summary>
    private static List<DiffLine> Script(FileLines oldLines, FileLines newLines)
    {
        List<DiffLine> script = [];
        string[] oldKeys = oldLines.Keys;
        string[] newKeys = newLines.Keys;

        int prefix = 0;
        while (prefix < oldKeys.Length && prefix < newKeys.Length &&
            string.Equals(oldKeys[prefix], newKeys[prefix], StringComparison.Ordinal)) prefix++;

        int suffix = 0;
        while (suffix < oldKeys.Length - prefix && suffix < newKeys.Length - prefix &&
            string.Equals(oldKeys[^(suffix + 1)], newKeys[^(suffix + 1)], StringComparison.Ordinal)) suffix++;

        for (int index = 0; index < prefix; index++)
            script.Add(new(' ', oldLines.Texts[index], index + 1, index + 1));

        script.AddRange(Myers(
            oldLines.Texts[prefix..(oldKeys.Length - suffix)], oldKeys[prefix..(oldKeys.Length - suffix)],
            newLines.Texts[prefix..(newKeys.Length - suffix)], newKeys[prefix..(newKeys.Length - suffix)],
            prefix));

        for (int index = 0; index < suffix; index++)
            script.Add(new(' ', oldLines.Texts[oldKeys.Length - suffix + index],
                oldKeys.Length - suffix + index + 1, newKeys.Length - suffix + index + 1));

        // Mark whichever lines end their file without a newline. A context line can only be last on both
        // sides at once, because the keys that made it context already encode the newline either way.
        for (int index = 0; index < script.Count; index++)
        {
            DiffLine line = script[index];
            bool lastOld = !oldLines.EndsWithNewline && line.Kind is ' ' or '-' && line.OldLine == oldKeys.Length;
            bool lastNew = !newLines.EndsWithNewline && line.Kind is ' ' or '+' && line.NewLine == newKeys.Length;
            if (lastOld || lastNew) script[index] = line with { NoNewline = true };
        }
        return script;
    }

    /// <summary>
    /// Myers' greedy shortest-edit-script algorithm. It walks diagonals of the edit graph, recording where
    /// each step reached, then backtracks through that trace to recover the edits. The result is a minimal
    /// script, which is what makes the output agree with Git rather than merely being correct.
    /// </summary>
    private static List<DiffLine> Myers(string[] oldTexts, string[] oldKeys, string[] newTexts, string[] newKeys, int offset)
    {
        int n = oldKeys.Length;
        int m = newKeys.Length;
        List<DiffLine> script = [];
        if (n == 0 && m == 0) return script;

        int max = n + m;
        int[] v = new int[2 * max + 1];
        List<int[]> trace = [];

        for (int d = 0; d <= max; d++)
        {
            trace.Add((int[])v.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[k - 1 + max] < v[k + 1 + max]) ? v[k + 1 + max] : v[k - 1 + max] + 1;
                int y = x - k;
                while (x < n && y < m && string.Equals(oldKeys[x], newKeys[y], StringComparison.Ordinal)) { x++; y++; }
                v[k + max] = x;
                if (x >= n && y >= m)
                {
                    Backtrack(trace, oldTexts, newTexts, n, m, d, max, offset, script);
                    return script;
                }
            }
        }
        return script;
    }

    /// <summary>Walks the recorded trace backwards, emitting the edits it implies in forward order.</summary>
    private static void Backtrack(List<int[]> trace, string[] oldTexts, string[] newTexts,
        int n, int m, int d, int max, int offset, List<DiffLine> script)
    {
        List<DiffLine> reversed = [];
        int x = n;
        int y = m;

        for (int step = d; step > 0; step--)
        {
            int[] v = trace[step];
            int k = x - y;
            int previousK = k == -step || (k != step && v[k - 1 + max] < v[k + 1 + max]) ? k + 1 : k - 1;
            int previousX = v[previousK + max];
            int previousY = previousX - previousK;

            while (x > previousX && y > previousY)
            {
                reversed.Add(new(' ', oldTexts[x - 1], offset + x, offset + y));
                x--; y--;
            }
            if (previousX < x) reversed.Add(new('-', oldTexts[x - 1], offset + x, null));
            else if (previousY < y) reversed.Add(new('+', newTexts[y - 1], null, offset + y));
            x = previousX;
            y = previousY;
        }

        while (x > 0 && y > 0)
        {
            reversed.Add(new(' ', oldTexts[x - 1], offset + x, offset + y));
            x--; y--;
        }

        reversed.Reverse();
        script.AddRange(reversed);
    }
}
