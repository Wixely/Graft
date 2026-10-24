namespace Graft.Git;

/// <summary>What merging one branch into another would do, or why it cannot be done here.</summary>
public enum MergeKind
{
    /// <summary>A branch the proposal names does not resolve, so there is nothing to say about it.</summary>
    Unresolved = 0,
    /// <summary>The target already contains the source. Nothing to do, and nothing to record.</summary>
    AlreadyMerged = 1,
    /// <summary>The target is an ancestor of the source, so the target reference simply moves forward and
    /// no merge commit is written. This is the common case for a branch nobody else pushed to.</summary>
    FastForward = 2,
    /// <summary>Both branches moved, and no file was changed on both sides. The result is a merge commit
    /// with two parents whose tree takes each side's changes.</summary>
    ThreeWay = 3,
    /// <summary>Both branches changed the same file. Graft does not merge file contents; see
    /// <see cref="MergeAnalysis.Conflicts"/> for the paths and the remarks on this type for why.</summary>
    Conflict = 4
}

/// <summary>
/// What a merge would do, computed without writing anything.
/// </summary>
public sealed record MergeAnalysis(
    MergeKind Kind,
    string? BaseCommitId,
    string? SourceCommitId,
    string? TargetCommitId,
    IReadOnlyList<string> Conflicts)
{
    public bool CanMerge => Kind is MergeKind.FastForward or MergeKind.ThreeWay;
}

/// <summary>What a merge actually did.</summary>
public sealed record MergeOutcome(MergeKind Kind, string CommitId, string TargetBranch);

/// <summary>
/// Merges one branch into another.
///
/// <para>
/// Two cases are handled and a third is refused, deliberately. A fast-forward moves the target reference.
/// A three-way merge writes a merge commit whose tree takes each side's changes, where the two sides
/// changed different files. Where both sides changed the same file, this refuses and names the file.
/// </para>
///
/// <para>
/// <b>Why it refuses rather than merging the file.</b> Resolving that case means merging line runs within a
/// file, which is a second diff algorithm with its own conflict markers, its own notion of a half-merged
/// working state, and no way to show the result to somebody before it is committed. A forge that produced
/// a silently wrong merge would be worse than one that says it cannot: the author can merge locally, where
/// they have the tools and can see what they are resolving, and push. So the boundary is drawn at file
/// granularity and reported precisely, rather than guessed at.
/// </para>
///
/// <para>
/// Unrelated histories merge rather than refuse, with an empty base. Every path present on both sides then
/// counts as changed on both sides, so anything but a genuinely disjoint pair of trees reports a conflict,
/// which is the conservative reading.
/// </para>
/// </summary>
public static class RepositoryMerge
{
    public static MergeAnalysis Analyse(
        Repository repository,
        string sourceBranch,
        string targetBranch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        string? source = repository.Refs.GetValueOrDefault("refs/heads/" + sourceBranch);
        string? target = repository.Refs.GetValueOrDefault("refs/heads/" + targetBranch);
        if (source is null || target is null) return new(MergeKind.Unresolved, null, source, target, []);
        if (!repository.Objects.TryGetValue(source, out GitObject? sourceCommit) || sourceCommit.Type != 1 ||
            !repository.Objects.TryGetValue(target, out GitObject? targetCommit) || targetCommit.Type != 1)
            return new(MergeKind.Unresolved, null, source, target, []);

        // Asked in this order because the answers overlap: a source already contained in the target is
        // "already merged" even though the target is also trivially reachable from nothing.
        if (Graph.IsAncestor(repository.Objects, source, target, cancellationToken))
            return new(MergeKind.AlreadyMerged, source, source, target, []);
        if (Graph.IsAncestor(repository.Objects, target, source, cancellationToken))
            return new(MergeKind.FastForward, target, source, target, []);

        string? mergeBase = Graph.MergeBase(repository.Objects, source, target, cancellationToken);
        Dictionary<string, (string Id, string Mode)> baseFiles = mergeBase is null
            ? new(StringComparer.Ordinal)
            : RepositoryDiff.Flatten(repository, RepositoryView.ReadCommit(repository, mergeBase).TreeId, "");
        Dictionary<string, (string Id, string Mode)> sourceFiles =
            RepositoryDiff.Flatten(repository, RepositoryView.ReadCommit(repository, source).TreeId, "");
        Dictionary<string, (string Id, string Mode)> targetFiles =
            RepositoryDiff.Flatten(repository, RepositoryView.ReadCommit(repository, target).TreeId, "");

        List<string> conflicts = [];
        foreach (string path in Paths(baseFiles, sourceFiles, targetFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Resolve(path, baseFiles, sourceFiles, targetFiles) is null) conflicts.Add(path);
        }
        conflicts.Sort(StringComparer.Ordinal);
        return conflicts.Count > 0
            ? new(MergeKind.Conflict, mergeBase, source, target, conflicts)
            : new(MergeKind.ThreeWay, mergeBase, source, target, []);
    }

    /// <summary>
    /// Merges, or throws <see cref="GitInputException"/> when <see cref="Analyse"/> says it cannot. Callers
    /// are expected to analyse first and report that answer; this re-analyses anyway, because the branch
    /// could have moved in between and a merge decided from a stale answer is the one way this could
    /// silently lose a commit.
    /// </summary>
    public static MergeOutcome Merge(
        Repository repository,
        string sourceBranch,
        string targetBranch,
        GitIdentity author,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(author);
        string targetReference = "refs/heads/" + targetBranch;
        Limits.Require(Limits.IsRef("refs/heads/" + sourceBranch) && Limits.IsRef(targetReference), "branch");

        MergeAnalysis analysis = Analyse(repository, sourceBranch, targetBranch, cancellationToken);
        Limits.Require(analysis.CanMerge, analysis.Kind switch
        {
            MergeKind.AlreadyMerged => "already-merged",
            MergeKind.Conflict => "merge-conflict",
            _ => "merge-unresolved"
        });
        string source = analysis.SourceCommitId!;
        string target = analysis.TargetCommitId!;

        if (analysis.Kind == MergeKind.FastForward)
        {
            // No new object: the reference moves to a commit the repository already holds, which is exactly
            // what a client's fast-forward push would have done.
            Dictionary<string, string> advanced = new(repository.Refs, StringComparer.Ordinal) { [targetReference] = source };
            repository.Apply(new(repository.Objects, StringComparer.Ordinal), advanced, cancellationToken);
            return new(MergeKind.FastForward, source, targetBranch);
        }

        Dictionary<string, (string Id, string Mode)> baseFiles = analysis.BaseCommitId is null
            ? new(StringComparer.Ordinal)
            : RepositoryDiff.Flatten(repository, RepositoryView.ReadCommit(repository, analysis.BaseCommitId).TreeId, "");
        Dictionary<string, (string Id, string Mode)> sourceFiles =
            RepositoryDiff.Flatten(repository, RepositoryView.ReadCommit(repository, source).TreeId, "");
        Dictionary<string, (string Id, string Mode)> targetFiles =
            RepositoryDiff.Flatten(repository, RepositoryView.ReadCommit(repository, target).TreeId, "");

        Dictionary<string, (string Mode, string Id)> merged = new(StringComparer.Ordinal);
        foreach (string path in Paths(baseFiles, sourceFiles, targetFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string Id, string Mode)? resolved = Resolve(path, baseFiles, sourceFiles, targetFiles);
            Limits.Require(resolved is not null, "merge-conflict");
            if (resolved is (string id, string mode)) merged[path] = (mode, id);
        }

        Dictionary<string, GitObject> written = new(StringComparer.Ordinal);
        string treeId = RepositoryAuthor.WriteTree(merged, written, cancellationToken);
        // The target first, so the merge reads as the target branch advancing and `diff HEAD^` shows what
        // the source brought in. Git's own merge orders the parents the same way.
        GitObject commit = RepositoryAuthor.BuildCommit(treeId, [target, source], author, message);
        written[commit.Id] = commit;

        Dictionary<string, GitObject> staged = new(repository.Objects, StringComparer.Ordinal);
        foreach ((string id, GitObject value) in written) staged[id] = value;
        Limits.Require(staged.Count <= Limits.ObjectCount, "repository-object-count");
        Limits.Require(staged.Values.Sum(value => (long)value.Size) <= Limits.ExpandedBytes, "repository-budget");

        // The same checks a pushed batch gets. Both parents must remain reachable, which is what makes this
        // a merge rather than a rewrite of either branch.
        Graph.Walk(staged, [commit.Id], cancellationToken);
        Limits.Require(Graph.IsAncestor(staged, target, commit.Id, cancellationToken), "non-fast-forward");
        Limits.Require(Graph.IsAncestor(staged, source, commit.Id, cancellationToken), "merge-parent-unreachable");

        Dictionary<string, string> refs = new(repository.Refs, StringComparer.Ordinal) { [targetReference] = commit.Id };
        Limits.Require(refs.Count <= Limits.RefCount, "ref-count");
        repository.Apply(staged, refs, cancellationToken);
        return new(MergeKind.ThreeWay, commit.Id, targetBranch);
    }

    /// <summary>
    /// What one path becomes, or null when the two sides disagree about it. Null is the conflict: both
    /// changed it, and to different things.
    ///
    /// <para>
    /// Absence is a value here. A path present in the base and gone from one side was deleted by that side,
    /// and a deletion is a change like any other, so a file one side deleted and the other edited conflicts
    /// rather than quietly going or quietly staying.
    /// </para>
    /// </summary>
    private static (string Id, string Mode)? Resolve(
        string path,
        Dictionary<string, (string Id, string Mode)> baseFiles,
        Dictionary<string, (string Id, string Mode)> sourceFiles,
        Dictionary<string, (string Id, string Mode)> targetFiles)
    {
        (string Id, string Mode)? original = baseFiles.TryGetValue(path, out (string Id, string Mode) baseEntry) ? baseEntry : null;
        (string Id, string Mode)? source = sourceFiles.TryGetValue(path, out (string Id, string Mode) sourceEntry) ? sourceEntry : null;
        (string Id, string Mode)? target = targetFiles.TryGetValue(path, out (string Id, string Mode) targetEntry) ? targetEntry : null;

        bool sourceChanged = source != original;
        bool targetChanged = target != original;
        if (!sourceChanged) return target;
        if (!targetChanged) return source;
        return source == target ? source : null;
    }

    private static IEnumerable<string> Paths(
        Dictionary<string, (string Id, string Mode)> baseFiles,
        Dictionary<string, (string Id, string Mode)> sourceFiles,
        Dictionary<string, (string Id, string Mode)> targetFiles) =>
        baseFiles.Keys
            .Union(sourceFiles.Keys, StringComparer.Ordinal)
            .Union(targetFiles.Keys, StringComparer.Ordinal);
}
