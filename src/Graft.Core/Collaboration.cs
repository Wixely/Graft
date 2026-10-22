namespace Graft.Core;

/// <summary>
/// Where an issue or a merge request has got to.
///
/// <para>
/// One enum for both, because the two share every state but one and a reader comparing them should not have
/// to map between two vocabularies. <see cref="Merged"/> is reachable only by a merge request; an issue is
/// opened or closed and nothing else.
/// </para>
/// </summary>
public enum CollaborationState
{
    /// <summary>Spelled GitLab's way in the API, as <c>opened</c> rather than <c>open</c>.</summary>
    Opened = 0,
    Closed = 1,
    /// <summary>A merge request whose source reached its target. Terminal: a merged request cannot reopen,
    /// because reopening it would propose work the target already contains.</summary>
    Merged = 2
}

/// <summary>Which kind of thing a comment is attached to.</summary>
public enum CommentSubject
{
    Issue = 0,
    MergeRequest = 1
}

/// <summary>
/// An issue. <see cref="Iid"/> is the number people use, counted from one within its project; <see cref="Id"/>
/// is the row and is never shown. Both are carried because the API addresses issues by the first and
/// comments join on the second.
/// </summary>
public sealed record Issue(
    long Id,
    long ProjectId,
    long Iid,
    long AuthorUserId,
    string AuthorUsername,
    string Title,
    string Description,
    CollaborationState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    long Comments = 0);

/// <summary>
/// A proposal to bring one branch into another.
///
/// <para>
/// The branches are stored as names rather than as commit ids, so the proposal follows the branch: pushing
/// to the source updates what is being proposed, which is what an author expects and what makes review of
/// an evolving branch possible. It also means a proposal can outlive the branch it names, and the Git
/// engine, not the database, is what can say whether it still resolves.
/// </para>
/// </summary>
public sealed record MergeProposal(
    long Id,
    long ProjectId,
    long Iid,
    long AuthorUserId,
    string AuthorUsername,
    string Title,
    string Description,
    string SourceBranch,
    string TargetBranch,
    CollaborationState State,
    string? MergeCommitSha,
    string? MergedByUsername,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    long Comments = 0);

/// <summary>One comment. The same shape on an issue and on a merge request, because it is the same thing.</summary>
public sealed record Comment(
    long Id,
    long AuthorUserId,
    string AuthorUsername,
    string Body,
    DateTimeOffset CreatedAtUtc);

public sealed record IssuePage(IReadOnlyList<Issue> Issues, long Total);
public sealed record MergeProposalPage(IReadOnlyList<MergeProposal> Proposals, long Total);
