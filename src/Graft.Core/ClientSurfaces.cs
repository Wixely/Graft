namespace Graft.Core;

/// <summary>A reaction on a comment: one person, one emoji name, once.</summary>
public sealed record AwardEmoji(long Id, long CommentId, string Name, long UserId, string Username, DateTimeOffset CreatedAtUtc);

/// <summary>A comment on a snippet. Snippet comments are flat: no threads, no resolving.</summary>
public sealed record SnippetNote(long Id, long SnippetId, long AuthorUserId, string AuthorUsername, string Body, DateTimeOffset CreatedAtUtc);

/// <summary>One branch a push changed, as the activity feed records it.</summary>
public sealed record PushRecord(string Ref, string? BeforeSha, string? AfterSha, int CommitCount, string? CommitTitle);

/// <summary>What an activity entry is about.</summary>
public enum ActivityKind
{
    IssueOpened = 1,
    MergeRequestOpened = 2,
    MergeRequestMerged = 3,
    Commented = 4,
    Pushed = 5
}

/// <summary>
/// One entry in an activity feed, derived from what Graft already keeps: issues and merge requests opened, merges,
/// comments, and pushes. <see cref="Id"/> is unique across kinds.
/// </summary>
public sealed record ActivityEvent(
    long Id,
    ActivityKind Kind,
    long ProjectId,
    long? AuthorUserId,
    string? AuthorUsername,
    DateTimeOffset CreatedAtUtc,
    long? TargetId,
    long? TargetIid,
    string? TargetTitle,
    /// <summary>For a comment: <c>issue</c> or <c>merge_request</c>.</summary>
    string? SubjectType,
    string? Body,
    string? Ref,
    string? BeforeSha,
    string? AfterSha,
    int CommitCount,
    string? CommitTitle);

/// <summary>A file attached to a project's Markdown, addressed by an unguessable secret and its name.</summary>
public sealed record ProjectUpload(long Id, long ProjectId, string Secret, string FileName, string ContentType, long Bytes, DateTimeOffset CreatedAtUtc);
