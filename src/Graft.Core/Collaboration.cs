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
    long Comments = 0,
    /// <summary>Everyone who has approved, stale approvals included. The count a project requires is
    /// satisfied only by the ones whose <see cref="Approval.Current"/> is true.</summary>
    IReadOnlyList<Approval>? Approvals = null,
    /// <summary>How many current approvals this project requires before a merge. Zero means none.</summary>
    int ApprovalsRequired = 0);

/// <summary>
/// One comment. The same shape on an issue and on a merge request, because it is the same thing.
///
/// <para>
/// The four anchor fields are null together for a comment on the whole thing, which is what every comment
/// written before anchors existed is. When they are set, the comment was written against one line of one
/// file as that file looked at <see cref="AnchorCommitSha"/>. That commit is kept rather than discarded
/// because the branch moves: a comment that silently re-anchored to whatever now occupies the same line
/// number would be worse than one that says the code has changed since.
/// </para>
/// </summary>
public sealed record Comment(
    long Id,
    long AuthorUserId,
    string AuthorUsername,
    string Body,
    DateTimeOffset CreatedAtUtc,
    string? Path = null,
    int? Line = null,
    /// <summary><c>old</c> or <c>new</c>: which side of the diff the line is on. A removed line can only be
    /// commented on the old side and an added line only on the new, so this is not redundant with the
    /// line number.</summary>
    DiffSide? Side = null,
    string? AnchorCommitSha = null);

/// <summary>Which side of a diff a line belongs to.</summary>
public enum DiffSide
{
    Old = 0,
    New = 1
}

/// <summary>
/// One person's approval of a merge proposal, and the commit they approved.
///
/// <para>
/// <see cref="Current"/> is false when the source branch has moved since. A stale approval still exists
/// and is still shown, because somebody did look; it simply does not count towards the number a project
/// requires, since an approval of code that has since changed is not an approval of what would be merged.
/// </para>
/// </summary>
public sealed record Approval(
    long UserId,
    string Username,
    string SourceCommitSha,
    bool Current,
    DateTimeOffset CreatedAtUtc);

public sealed record IssuePage(IReadOnlyList<Issue> Issues, long Total);
public sealed record MergeProposalPage(IReadOnlyList<MergeProposal> Proposals, long Total);
