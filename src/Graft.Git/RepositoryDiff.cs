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
/// changed lines is moved to where Git would put it. That normalisation is a port of Git's own
/// <c>xdl_change_compact</c>, including the indent heuristic Git has applied by default since 2.14, which
/// chooses among the legal positions of a group by scoring the indentation around each candidate split.
/// </para>
///
/// <para>
/// Measured against real Git over generated file pairs built from a deliberately repetitive alphabet, this
/// agrees with <c>git diff</c> at its default settings on about 99% of cases. What remains is not placement:
/// Git's divide-and-conquer search sometimes settles on a different script of the same length, such as
/// matching a different one of two repeated lines, and no amount of sliding turns one into the other.
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
        (IReadOnlyList<DiffFile> files, bool truncated) = BetweenTrees(repository, parentTree, commit.TreeId);
        return new(commit, parentId, files, truncated);
    }

    /// <summary>
    /// Compares two commits directly, which is the question a merge proposal asks: not "what did this
    /// commit change" but "what does this branch contain that that one does not". A null
    /// <paramref name="beforeCommitId"/> compares against nothing, so every file reads as added.
    /// </summary>
    public static (IReadOnlyList<DiffFile> Files, bool Truncated) BetweenCommits(
        Repository repository, string? beforeCommitId, string afterCommitId)
    {
        ArgumentNullException.ThrowIfNull(repository);
        string? beforeTree = beforeCommitId is null ? null : RepositoryView.ReadCommit(repository, beforeCommitId).TreeId;
        return BetweenTrees(repository, beforeTree, RepositoryView.ReadCommit(repository, afterCommitId).TreeId);
    }

    private static (IReadOnlyList<DiffFile> Files, bool Truncated) BetweenTrees(
        Repository repository, string? beforeTreeId, string afterTreeId)
    {
        Dictionary<string, (string Id, string Mode)> before = beforeTreeId is null ? [] : Flatten(repository, beforeTreeId, "");
        Dictionary<string, (string Id, string Mode)> after = Flatten(repository, afterTreeId, "");

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

        return (files, truncated);
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
    internal static Dictionary<string, (string Id, string Mode)> Flatten(Repository repository, string treeId, string parent)
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

        // Trimming the common ends above fixes those lines as context, but compaction below works on the
        // whole arrays, so a group is still free to move across the boundary if that is where it belongs.
        // Each side is compacted against the other in turn, in the order Git does it.
        Compact(oldKeys, oldChanged, Indents(oldLines.Texts), newChanged);
        Compact(newKeys, newChanged, Indents(newLines.Texts), oldChanged);

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
    /// Normalises where each group of changed lines sits, following Git's <c>xdl_change_compact</c> step
    /// for step so that the output is the one <c>git diff</c> shows.
    ///
    /// <para>
    /// A group can move whenever the line just past it is identical to the line at its other edge: deleting
    /// the first of two identical lines and deleting the second produce the same file. For each group this
    /// slides it as far up as it can go and then as far down, merging whatever other groups it bumps into.
    /// If nothing moved, the group stays. If at some position on the way it sat level with a change in the
    /// other file, it is brought back to the lowest such position, so a substitution reads as one edit rather
    /// than a deletion a few lines from its insertion. Otherwise the indent heuristic chooses among the
    /// positions, which is what Git has done by default since 2.14.
    /// </para>
    ///
    /// <para>
    /// The two sides are walked in lockstep as sequences of groups, where a group also exists, empty, between
    /// every two unchanged lines. That is what lets "the change the other file makes in the same place" be
    /// found by counting rather than by searching, and it is why each slide here moves a cursor on the other
    /// side too.
    /// </para>
    /// </summary>
    private static void Compact(string[] keys, bool[] changed, int[] indents, bool[] otherChanged)
    {
        Group g = Group.First(changed);
        Group go = Group.First(otherChanged);

        while (true)
        {
            if (g.End != g.Start)
            {
                int groupSize;
                int earliestEnd;
                int endMatchingOther;
                do
                {
                    groupSize = g.End - g.Start;
                    endMatchingOther = -1;

                    while (g.SlideUp(keys, changed))
                        if (!go.Previous(otherChanged)) throw Desynchronised();
                    earliestEnd = g.End;
                    if (go.End > go.Start) endMatchingOther = g.End;

                    while (g.SlideDown(keys, changed))
                    {
                        if (!go.Next(otherChanged)) throw Desynchronised();
                        if (go.End > go.Start) endMatchingOther = g.End;
                    }
                } while (groupSize != g.End - g.Start);

                if (g.End == earliestEnd)
                {
                    // Nowhere to move.
                }
                else if (endMatchingOther != -1)
                {
                    while (go.End == go.Start)
                    {
                        if (!g.SlideUp(keys, changed)) throw Desynchronised();
                        if (!go.Previous(otherChanged)) throw Desynchronised();
                    }
                }
                else
                {
                    // Every position the group may occupy implies two splits, one above it and one below.
                    // Each split is scored on its own, the two are summed, and the lowest sum wins, with the
                    // lower position taking a tie. Candidates are bounded to the group's own length plus one
                    // and to a fixed distance, exactly as Git bounds them.
                    int shift = Math.Max(earliestEnd, Math.Max(g.End - groupSize - 1, g.End - IndentHeuristicMaxSliding));
                    int bestShift = -1;
                    SplitScore best = default;
                    for (; shift <= g.End; shift++)
                    {
                        SplitScore score = default;
                        score.Add(MeasureSplit(indents, shift));
                        score.Add(MeasureSplit(indents, shift - groupSize));
                        if (bestShift == -1 || score.CompareTo(best) <= 0)
                        {
                            best = score;
                            bestShift = shift;
                        }
                    }

                    while (g.End > bestShift)
                    {
                        if (!g.SlideUp(keys, changed)) throw Desynchronised();
                        if (!go.Previous(otherChanged)) throw Desynchronised();
                    }
                }
            }

            if (!g.Next(changed)) break;
            if (!go.Next(otherChanged)) throw Desynchronised();
        }

        if (go.Next(otherChanged)) throw Desynchronised();
    }

    private static InvalidOperationException Desynchronised() =>
        new("The two sides of the diff lost step while normalising group positions.");

    /// <summary>
    /// A run of changed lines on one side, or the empty run between two unchanged lines. <see cref="Start"/>
    /// is the first changed line, <see cref="End"/> the first unchanged line after it; they are equal for an
    /// empty group.
    /// </summary>
    private struct Group
    {
        public int Start;
        public int End;

        public static Group First(bool[] changed)
        {
            Group g = default;
            while (g.End < changed.Length && changed[g.End]) g.End++;
            return g;
        }

        /// <summary>Moves to the next group, which may be empty. False once already at the end of the file.</summary>
        public bool Next(bool[] changed)
        {
            if (End == changed.Length) return false;
            Start = End + 1;
            for (End = Start; End < changed.Length && changed[End]; End++) { }
            return true;
        }

        /// <summary>Moves to the previous group, which may be empty. False once already at the start.</summary>
        public bool Previous(bool[] changed)
        {
            if (Start == 0) return false;
            End = Start - 1;
            for (Start = End; Start > 0 && changed[Start - 1]; Start--) { }
            return true;
        }

        /// <summary>Slides the group one line down if the content allows, absorbing any group it meets.</summary>
        public bool SlideDown(string[] keys, bool[] changed)
        {
            if (End >= keys.Length || !string.Equals(keys[Start], keys[End], StringComparison.Ordinal)) return false;
            changed[Start++] = false;
            changed[End++] = true;
            while (End < changed.Length && changed[End]) End++;
            return true;
        }

        /// <summary>Slides the group one line up if the content allows, absorbing any group it meets.</summary>
        public bool SlideUp(string[] keys, bool[] changed)
        {
            if (Start <= 0 || !string.Equals(keys[Start - 1], keys[End - 1], StringComparison.Ordinal)) return false;
            changed[--Start] = true;
            changed[--End] = false;
            while (Start > 0 && changed[Start - 1]) Start--;
            return true;
        }
    }

    // The weights of Git's indent heuristic, kept with Git's names so the two can be read side by side.
    private const int IndentWeight = 60;
    private const int IndentHeuristicMaxSliding = 100;
    private const int MaxIndent = 200;
    private const int MaxBlanks = 20;
    private const int StartOfFilePenalty = 1;
    private const int EndOfFilePenalty = 21;
    private const int TotalBlankWeight = -30;
    private const int PostBlankWeight = 6;
    private const int RelativeIndentPenalty = -4;
    private const int RelativeIndentWithBlankPenalty = 10;
    private const int RelativeOutdentPenalty = 24;
    private const int RelativeOutdentWithBlankPenalty = 17;
    private const int RelativeDedentPenalty = 23;
    private const int RelativeDedentWithBlankPenalty = 17;

    /// <summary>
    /// Each line's indentation in columns, a tab counting to the next multiple of eight, or -1 for a line
    /// that is blank or whitespace only. Computed from the text rather than the comparison key, which may
    /// carry the no-newline marker.
    /// </summary>
    private static int[] Indents(string[] texts)
    {
        int[] indents = new int[texts.Length];
        for (int index = 0; index < texts.Length; index++)
        {
            string text = texts[index];
            int indent = 0;
            indents[index] = -1;
            for (int position = 0; position < text.Length; position++)
            {
                char c = text[position];
                if (c is not (' ' or '\t' or '\r' or '\n' or '\f' or '\v')) { indents[index] = indent; break; }
                if (c == ' ') indent++;
                else if (c == '\t') indent += 8 - indent % 8;
                if (indent >= MaxIndent) { indents[index] = MaxIndent; break; }
            }
        }
        return indents;
    }

    /// <summary>What a hypothetical split just above line <c>split</c> looks like to the heuristic.</summary>
    private readonly record struct SplitMeasurement(
        bool EndOfFile, int Indent, int PreBlank, int PreIndent, int PostBlank, int PostIndent);

    private static SplitMeasurement MeasureSplit(int[] indents, int split)
    {
        bool endOfFile = split >= indents.Length;
        int indent = endOfFile ? -1 : indents[split];

        int preBlank = 0;
        int preIndent = -1;
        for (int i = split - 1; i >= 0; i--)
        {
            preIndent = indents[i];
            if (preIndent != -1) break;
            preBlank++;
            if (preBlank == MaxBlanks) { preIndent = 0; break; }
        }

        int postBlank = 0;
        int postIndent = -1;
        for (int i = split + 1; i < indents.Length; i++)
        {
            postIndent = indents[i];
            if (postIndent != -1) break;
            postBlank++;
            if (postBlank == MaxBlanks) { postIndent = 0; break; }
        }

        return new(endOfFile, indent, preBlank, preIndent, postBlank, postIndent);
    }

    /// <summary>The accumulated score of the two splits a group position implies. Lower is better.</summary>
    private struct SplitScore
    {
        public int EffectiveIndent;
        public int Penalty;

        public void Add(SplitMeasurement m)
        {
            if (m.PreIndent == -1 && m.PreBlank == 0) Penalty += StartOfFilePenalty;
            if (m.EndOfFile) Penalty += EndOfFilePenalty;

            int postBlank = m.Indent == -1 ? 1 + m.PostBlank : 0;
            int totalBlank = m.PreBlank + postBlank;
            Penalty += TotalBlankWeight * totalBlank;
            Penalty += PostBlankWeight * postBlank;

            int indent = m.Indent != -1 ? m.Indent : m.PostIndent;
            bool anyBlanks = totalBlank != 0;
            EffectiveIndent += indent;

            if (indent == -1 || m.PreIndent == -1)
            {
                // Nothing to compare against.
            }
            else if (indent > m.PreIndent)
            {
                Penalty += anyBlanks ? RelativeIndentWithBlankPenalty : RelativeIndentPenalty;
            }
            else if (indent < m.PreIndent)
            {
                // Indented less than its predecessor: either the end of a block, or the start of a new one
                // whose own body follows. What comes next tells the two apart.
                if (m.PostIndent != -1 && m.PostIndent > indent)
                    Penalty += anyBlanks ? RelativeOutdentWithBlankPenalty : RelativeOutdentPenalty;
                else
                    Penalty += anyBlanks ? RelativeDedentWithBlankPenalty : RelativeDedentPenalty;
            }
        }

        public int CompareTo(SplitScore other)
        {
            int indents = (EffectiveIndent > other.EffectiveIndent ? 1 : 0) - (EffectiveIndent < other.EffectiveIndent ? 1 : 0);
            return IndentWeight * indents + (Penalty - other.Penalty);
        }
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
