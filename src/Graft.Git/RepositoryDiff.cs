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
/// A minimal script is not by itself enough to agree with Git. Where a change sits beside lines identical
/// to the ones it touches, several equally minimal scripts exist, so after the comparison each group of
/// changed lines is slid as far down its file as the content allows, and then pulled back up to sit against
/// whatever the other file changes in the same place. Those two passes are what turn a correct diff into
/// the same diff a reader sees from <c>git diff</c>.
/// </para>
///
/// <para>
/// Measured against real Git over generated file pairs built from a deliberately repetitive alphabet, the
/// two passes agree with Git on 99% of cases once Git's indent heuristic is turned off, which is the
/// like-for-like comparison because this does not implement that heuristic. Against Git's default output
/// agreement is about 95%, and the difference between those two figures is the heuristic: a scoring pass
/// Git has applied since 2.14 that prefers to split a run of changes at a line whose indentation makes the
/// result easier to read. Implementing it is recorded as follow-on work.
/// </para>
///
/// <para>
/// Both figures are reproducible rather than asserted: <c>tests/Graft.Diff.Fuzz</c> generates the pairs and
/// compares every one against <c>git diff</c>.
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
    /// Compares two files' content directly, for a caller that already holds the bytes rather than a
    /// repository. The same comparison the commit view uses.
    /// </summary>
    public static IReadOnlyList<DiffHunk> CompareText(byte[] before, byte[] after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return Hunks(Lines(before), Lines(after));
    }

    /// <summary>
    /// The edit script: every line of both sides in order, each marked as context, addition or removal.
    ///
    /// <para>
    /// The comparison is done as two arrays of changed-or-not, one per file, rather than as an interleaved
    /// list. That is what lets a group of changes be slid to a different position afterwards, which is the
    /// step that makes the output agree with Git where several equally minimal scripts exist.
    /// </para>
    /// </summary>
    private static List<DiffLine> Script(FileLines oldLines, FileLines newLines)
    {
        string[] oldKeys = oldLines.Keys;
        string[] newKeys = newLines.Keys;
        bool[] oldChanged = new bool[oldKeys.Length];
        bool[] newChanged = new bool[newKeys.Length];

        int prefix = 0;
        while (prefix < oldKeys.Length && prefix < newKeys.Length &&
            string.Equals(oldKeys[prefix], newKeys[prefix], StringComparison.Ordinal)) prefix++;

        int suffix = 0;
        while (suffix < oldKeys.Length - prefix && suffix < newKeys.Length - prefix &&
            string.Equals(oldKeys[^(suffix + 1)], newKeys[^(suffix + 1)], StringComparison.Ordinal)) suffix++;

        Myers(oldKeys[prefix..(oldKeys.Length - suffix)], newKeys[prefix..(newKeys.Length - suffix)],
            oldChanged, newChanged, prefix);

        // Trimming the common ends above fixes those lines as context, but sliding below works on the whole
        // arrays, so a group is still free to move across the boundary if that is where it belongs.
        Compact(oldKeys, oldChanged);
        Compact(newKeys, newChanged);
        while (Align(oldKeys, oldChanged, newKeys, newChanged)) { }

        List<DiffLine> script = [];
        int i = 0;
        int j = 0;
        while (i < oldKeys.Length || j < newKeys.Length)
        {
            if (i < oldKeys.Length && oldChanged[i]) { script.Add(new('-', oldLines.Texts[i], i + 1, null)); i++; }
            else if (j < newKeys.Length && newChanged[j]) { script.Add(new('+', newLines.Texts[j], null, j + 1)); j++; }
            else if (i < oldKeys.Length && j < newKeys.Length)
            { script.Add(new(' ', oldLines.Texts[i], i + 1, j + 1)); i++; j++; }
            else break;
        }

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
    /// Slides each group of changed lines as far down its file as it can legally go, joining it to the next
    /// group when they meet.
    ///
    /// <para>
    /// A group can move whenever the line just past it is identical to the line at its other edge: deleting
    /// the first of two identical lines and deleting the second produce the same file. Git normalises that
    /// freedom so a diff does not wander, and this reproduces the normalisation.
    /// </para>
    /// </summary>
    private static void Compact(string[] keys, bool[] changed)
    {
        int length = changed.Length;
        int index = 0;
        while (true)
        {
            while (index < length && !changed[index]) index++;
            if (index >= length) break;
            int start = index;
            while (index < length && changed[index]) index++;
            int end = index;

            bool joined;
            do
            {
                joined = false;
                while (start > 0 && string.Equals(keys[start - 1], keys[end - 1], StringComparison.Ordinal))
                {
                    changed[--start] = true;
                    changed[--end] = false;
                    if (start > 0 && changed[start - 1])
                    {
                        while (start > 0 && changed[start - 1]) start--;
                        joined = true;
                    }
                }
                while (end < length && string.Equals(keys[end], keys[start], StringComparison.Ordinal))
                {
                    changed[start++] = false;
                    changed[end++] = true;
                    if (end < length && changed[end])
                    {
                        while (end < length && changed[end]) end++;
                        joined = true;
                    }
                }
            } while (joined);

            index = end;
        }
    }

    /// <summary>
    /// Pulls a group up so it sits against the change the other file makes in the same place, and reports
    /// whether anything moved.
    ///
    /// <para>
    /// Sliding leaves every group as low as it can go, which can leave a deletion a line below the insertion
    /// that replaces it. That reads as two unrelated edits rather than one substitution, and it is not what
    /// Git shows. Where the two are separated only by context, and the lower one can legally move up to meet
    /// the other, it does.
    /// </para>
    /// </summary>
    private static bool Align(string[] oldKeys, bool[] oldChanged, string[] newKeys, bool[] newChanged)
    {
        int i = 0;
        int j = 0;
        int previousSide = 0;
        int context = 0;
        while (i < oldChanged.Length || j < newChanged.Length)
        {
            if (i < oldChanged.Length && oldChanged[i])
            {
                int start = i;
                while (i < oldChanged.Length && oldChanged[i]) i++;
                if (previousSide == 2 && context > 0 && SlideUp(oldKeys, oldChanged, start, i, context)) return true;
                previousSide = 1;
                context = 0;
            }
            else if (j < newChanged.Length && newChanged[j])
            {
                int start = j;
                while (j < newChanged.Length && newChanged[j]) j++;
                if (previousSide == 1 && context > 0 && SlideUp(newKeys, newChanged, start, j, context)) return true;
                previousSide = 2;
                context = 0;
            }
            else if (i < oldChanged.Length && j < newChanged.Length) { i++; j++; context++; }
            else break;
        }
        return false;
    }

    /// <summary>
    /// Moves a group up by exactly <paramref name="steps"/> lines, or not at all.
    ///
    /// <para>
    /// All or nothing matters. Moving part of the way leaves the two groups still apart, so nothing has been
    /// joined, and the group has been dragged off the position sliding chose for it. A partial move made the
    /// output disagree with Git on edits that were never related in the first place: a deletion near the top
    /// of a file would tug an unrelated insertion several lines up towards it.
    /// </para>
    /// </summary>
    private static bool SlideUp(string[] keys, bool[] changed, int start, int end, int steps)
    {
        for (int step = 1; step <= steps; step++)
        {
            if (start - step < 0 || changed[start - step]) return false;
            if (!string.Equals(keys[start - step], keys[end - step], StringComparison.Ordinal)) return false;
        }
        for (int step = 0; step < steps; step++)
        {
            changed[--start] = true;
            changed[--end] = false;
        }
        return steps > 0;
    }

    /// <summary>
    /// Myers' greedy shortest-edit-script algorithm. It walks diagonals of the edit graph, recording where
    /// each step reached, then backtracks through that trace to mark the lines each file changed. The script
    /// is minimal, which is the starting point the sliding above then normalises.
    /// </summary>
    private static void Myers(string[] oldKeys, string[] newKeys, bool[] oldChanged, bool[] newChanged, int offset)
    {
        int n = oldKeys.Length;
        int m = newKeys.Length;
        if (n == 0 && m == 0) return;

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
                    Backtrack(trace, n, m, d, max, offset, oldChanged, newChanged);
                    return;
                }
            }
        }
    }

    /// <summary>Walks the recorded trace backwards, marking the line each step added or removed.</summary>
    private static void Backtrack(List<int[]> trace, int n, int m, int d, int max, int offset,
        bool[] oldChanged, bool[] newChanged)
    {
        int x = n;
        int y = m;

        for (int step = d; step > 0; step--)
        {
            int[] v = trace[step];
            int k = x - y;
            int previousK = k == -step || (k != step && v[k - 1 + max] < v[k + 1 + max]) ? k + 1 : k - 1;
            int previousX = v[previousK + max];
            int previousY = previousX - previousK;

            while (x > previousX && y > previousY) { x--; y--; }
            if (previousX < x) oldChanged[offset + x - 1] = true;
            else if (previousY < y) newChanged[offset + y - 1] = true;
            x = previousX;
            y = previousY;
        }
    }
}
