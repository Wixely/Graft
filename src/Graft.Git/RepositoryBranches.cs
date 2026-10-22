namespace Graft.Git;

/// <summary>
/// Creating and deleting branches without a push.
///
/// <para>
/// Until now a branch appeared by being pushed to, or as a side effect of committing a file, and could only
/// be removed by pushing a deletion. Both of those need a Git client, which made a branch something you
/// could not manage from the forge you were looking at.
/// </para>
///
/// <para>
/// Neither operation writes an object. Creating a branch points a new reference at a commit the repository
/// already holds, and deleting one removes a reference; the objects beneath it stay, which is exactly how
/// Git behaves and is why deleting a branch is not a way to lose work that another branch can still reach.
/// </para>
/// </summary>
public static class RepositoryBranches
{
    /// <summary>
    /// Points a new branch at whatever <paramref name="revision"/> resolves to: a branch name, a tag, or a
    /// commit id. Refuses a name that already exists rather than moving it, because moving a branch is a
    /// different act with different consequences and should not be reachable by a creation that was meant
    /// to fail.
    /// </summary>
    public static string CreateBranch(
        this Repository repository,
        string name,
        string revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        string reference = "refs/heads/" + name;
        Limits.Require(Limits.IsRef(reference), "branch");
        Limits.Require(!repository.Refs.ContainsKey(reference), "branch-exists");

        string? commitId = RepositoryView.ResolveCommit(repository, revision);
        Limits.Require(commitId is not null, "branch-revision");

        Dictionary<string, string> staged = new(repository.Refs, StringComparer.Ordinal) { [reference] = commitId! };
        Limits.Require(staged.Count <= Limits.RefCount, "ref-count");
        // Git cannot hold both `refs/heads/a` and `refs/heads/a/b`, because the first is a file where the
        // second needs a directory. A forge that accepted one would produce a repository no client could
        // check out.
        RequireNoPrefixConflict(staged);

        repository.Apply(new(repository.Objects, StringComparer.Ordinal), staged, cancellationToken);
        return commitId!;
    }

    /// <summary>
    /// Removes a branch. Refuses the repository's effective default branch, because that is the branch a
    /// clone checks out and a browser opens, and a repository whose HEAD names nothing is one that several
    /// clients handle badly and none handle well. Change the default first, then delete.
    /// </summary>
    public static void DeleteBranch(
        this Repository repository,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        string reference = "refs/heads/" + name;
        Limits.Require(Limits.IsRef(reference), "branch");
        Limits.Require(repository.Refs.ContainsKey(reference), "branch-missing");
        Limits.Require(repository.DefaultBranch != name, "branch-is-default");

        Dictionary<string, string> staged = new(repository.Refs, StringComparer.Ordinal);
        staged.Remove(reference);
        repository.Apply(new(repository.Objects, StringComparer.Ordinal), staged, cancellationToken);
    }

    /// <summary>
    /// Points a tag at a commit. Lightweight only: the reference names the commit directly rather than an
    /// annotated tag object, because an annotated tag carries a message and a tagger and those belong to a
    /// release, which this is not. A tag pushed by a client keeps whatever form the client gave it.
    /// </summary>
    public static string CreateTag(
        this Repository repository,
        string name,
        string revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        string reference = "refs/tags/" + name;
        Limits.Require(Limits.IsRef(reference), "tag");
        Limits.Require(!repository.Refs.ContainsKey(reference), "tag-exists");

        string? commitId = RepositoryView.ResolveCommit(repository, revision);
        Limits.Require(commitId is not null, "tag-revision");

        Dictionary<string, string> staged = new(repository.Refs, StringComparer.Ordinal) { [reference] = commitId! };
        Limits.Require(staged.Count <= Limits.RefCount, "ref-count");
        RequireNoPrefixConflict(staged);

        repository.Apply(new(repository.Objects, StringComparer.Ordinal), staged, cancellationToken);
        return commitId!;
    }

    /// <summary>
    /// Removes a tag. Unlike a branch there is no default to protect, so this refuses only a name that is
    /// not there.
    /// </summary>
    public static void DeleteTag(
        this Repository repository,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        string reference = "refs/tags/" + name;
        Limits.Require(Limits.IsRef(reference), "tag");
        Limits.Require(repository.Refs.ContainsKey(reference), "tag-missing");

        Dictionary<string, string> staged = new(repository.Refs, StringComparer.Ordinal);
        staged.Remove(reference);
        repository.Apply(new(repository.Objects, StringComparer.Ordinal), staged, cancellationToken);
    }

    private static void RequireNoPrefixConflict(Dictionary<string, string> refs)
    {
        foreach (string name in refs.Keys)
            Limits.Require(!refs.Keys.Any(other => other.StartsWith(name + "/", StringComparison.Ordinal)),
                "ref-prefix-conflict");
    }
}
