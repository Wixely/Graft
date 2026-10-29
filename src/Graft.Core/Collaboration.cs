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
    long Comments = 0,
    IReadOnlyList<Label>? Labels = null,
    MilestoneReference? Milestone = null,
    IReadOnlyList<Person>? Assignees = null,
    /// <summary>A date with no time, because a deadline has no time zone.</summary>
    DateOnly? DueDate = null,
    /// <summary>Readable by members and the author only, however public the project is.</summary>
    bool Confidential = false,
    /// <summary>Seconds of work estimated, or zero. Spent time is a separate ledger.</summary>
    long TimeEstimateSeconds = 0,
    long TimeSpentSeconds = 0);

/// <summary>A project's label: a word and a colour, applied to issues and proposals alike.</summary>
public sealed record Label(long Id, string Name, string Color, string Description);

/// <summary>A milestone as an issue names it, by number and title; the full record is <see cref="Milestone"/>.</summary>
public sealed record MilestoneReference(long Id, long Iid, string Title);

/// <summary>Someone assigned to or asked to review a piece of work.</summary>
public sealed record Person(long UserId, string Username);

public enum MilestoneState
{
    Active = 0,
    Closed = 1
}

/// <summary>
/// A milestone: a named point to aim at, with the open and closed issues counted towards it. Numbered per
/// project like issues, because that is how people refer to them.
/// </summary>
public sealed record Milestone(
    long Id,
    long ProjectId,
    long Iid,
    string Title,
    string Description,
    MilestoneState State,
    DateOnly? DueDate,
    DateOnly? StartDate,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long OpenIssues = 0,
    long ClosedIssues = 0,
    long MergeRequests = 0);

/// <summary>A board: a name and the labels its lists are bound to, in order.</summary>
public sealed record Board(long Id, long ProjectId, string Name, IReadOnlyList<BoardList> Lists);
public sealed record BoardList(long Id, Label Label, int Position);

/// <summary>Which relation to a piece of work a person has.</summary>
public enum PersonKind
{
    Assignee = 0,
    Reviewer = 1
}

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
    int ApprovalsRequired = 0,
    IReadOnlyList<Label>? Labels = null,
    MilestoneReference? Milestone = null,
    IReadOnlyList<Person>? Assignees = null,
    IReadOnlyList<Person>? Reviewers = null,
    /// <summary>Marked as not ready. A draft cannot be merged, however mergeable the branches are.</summary>
    bool Draft = false,
    long TimeEstimateSeconds = 0,
    long TimeSpentSeconds = 0,
    /// <summary>The project the source branch lives in when it is not this one: a fork. Null otherwise.</summary>
    long? SourceProjectId = null,
    string? SourceProjectPath = null)
{
    /// <summary>Where the engine reads the source from: the branch itself, or the server-owned mirror of a
    /// fork's branch tip inside the target repository.</summary>
    public string SourceReference => SourceProjectId is null ? SourceBranch : "refs/merge-requests/" + Iid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/head";
    public bool FromFork => SourceProjectId is not null;
}

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
    string? AnchorCommitSha = null,
    /// <summary>The comment this one answers, or null for the first comment of a thread. Threads are one
    /// level deep: a reply to a reply is stored as a reply to the thread's first comment.</summary>
    long? ParentId = null,
    /// <summary>When the body was last changed after posting, or null if never. The earlier text is not
    /// kept; the fact that it changed is.</summary>
    DateTimeOffset? EditedAtUtc = null,
    /// <summary>Set on the first comment of a thread when the conversation was marked settled. Null on a
    /// reply, and null on an unresolved thread.</summary>
    DateTimeOffset? ResolvedAtUtc = null,
    string? ResolvedByUsername = null);

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

/// <summary>
/// An optional change to a field that can itself be null. <c>default</c> leaves the field alone;
/// <see cref="To"/> sets it, including to null. Exists so an update can clear a milestone or a due date
/// without a second parameter per field saying whether the first one meant anything.
/// </summary>
public readonly record struct Change<T>(bool Set, T? Value)
{
    public static Change<T> To(T? value) => new(true, value);
    public static Change<T> Unchanged => default;
}

/// <summary>
/// A release: the notes, links and files that sit on a tag. The tag is named rather than referenced,
/// because tags live in the repository; <see cref="TagExists"/> is filled in by whoever can see the
/// repository, and a release whose tag has gone still exists and says so.
/// </summary>
public sealed record Release(
    long Id,
    long ProjectId,
    string TagName,
    string Name,
    string Description,
    long AuthorUserId,
    string AuthorUsername,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ReleasedAtUtc,
    IReadOnlyList<ReleaseLink> Links,
    IReadOnlyList<ReleaseAsset> Assets,
    bool TagExists = true,
    string? CommitId = null);

public sealed record ReleaseLink(long Id, string Name, string Url, string LinkType);

/// <summary>An uploaded file on a release: its metadata. The bytes are read separately, on download.</summary>
public sealed record ReleaseAsset(long Id, string FileName, long Size, string Sha256, string UploadedByUsername, DateTimeOffset CreatedAtUtc);

/// <summary>A wiki page as it stands. <see cref="Slug"/> is its address, derived from the title when made.</summary>
public sealed record WikiPage(
    long Id,
    long ProjectId,
    string Slug,
    string Title,
    string Content,
    string CreatedByUsername,
    string UpdatedByUsername,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Versions = 0);

/// <summary>One saved state of a wiki page. The current state is also the newest version.</summary>
public sealed record WikiPageVersion(
    long Id,
    string Title,
    string Content,
    string Message,
    string AuthorUsername,
    DateTimeOffset CreatedAtUtc);

/// <summary>One file with a title, to paste and share. As visible as its project.</summary>
public sealed record Snippet(
    long Id,
    long ProjectId,
    string Title,
    string FileName,
    string Description,
    string Content,
    long AuthorUserId,
    string AuthorUsername,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>Time somebody spent on an issue or proposal, in seconds, on a day, with a word about what.</summary>
public sealed record TimeEntry(long Id, long UserId, string Username, long Seconds, DateOnly SpentOn, string Summary, DateTimeOffset CreatedAtUtc);

/// <summary>A package: a name and a version in one format, with its files. The bytes live beside the repository.</summary>
public sealed record Package(
    long Id,
    long ProjectId,
    string PackageType,
    string Name,
    string Version,
    string Description,
    string Authors,
    string CreatedByUsername,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<PackageFile> Files);

public sealed record PackageFile(long Id, string FileName, long Size, string Sha256, DateTimeOffset CreatedAtUtc);

/// <summary>An image name within a project, with its tags.</summary>
public sealed record ContainerRepository(long Id, long ProjectId, string Name, DateTimeOffset CreatedAtUtc, IReadOnlyList<ContainerTag> Tags);
public sealed record ContainerTag(string Tag, string Digest, string MediaType, long Size, DateTimeOffset UpdatedAtUtc);
public sealed record ContainerManifest(string Digest, string MediaType, long Size);
