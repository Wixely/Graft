using System.Globalization;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// What GitLab's clients read that Graft had nowhere to keep or no way to ask: stars, reactions on comments, comments on
/// snippets, the activity feed, files attached to Markdown, the user directory, and deleting an issue.
///
/// <para>
/// The user directory is the administrator's: anybody else is shown no one but themselves. The activity feed is derived
/// from issues, merge requests, comments and a record of pushes, never from a second copy of them, and leaves out
/// confidential issues and the comments on them for everyone, because who may read those is decided per issue.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    private const int MaximumSnippetNoteBytes = 64 * 1024;
    private const int ActivityWindow = 1000;

    // ---------------------------------------------------------------------------------------------------
    // Stars
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Stars a project the caller may read, or unstars it. Returns the project's star count afterwards.</summary>
    public async Task<long> SetStarAsync(AuthenticatedIdentity actor, long projectId, bool starred, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision read = await DecideAsync(db, transaction, actor, projectId, ProjectAction.ReadCollaboration, cancellationToken);
        if (!read.Allowed) throw new UnauthorizedAccessException(read.Explanation);
        AuthenticatedIdentity live = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        if (starred)
            await db.ExecuteAsync("INSERT OR IGNORE INTO ProjectStars (UserId, ProjectId, CreatedAtUtc) VALUES (@UserId, @ProjectId, @NowUtc);",
                new StarParameters(live.UserId, projectId, Now()), transaction);
        else
            await db.ExecuteAsync("DELETE FROM ProjectStars WHERE UserId = @UserId AND ProjectId = @ProjectId;",
                new StarParameters(live.UserId, projectId, ""), transaction);
        long count = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM ProjectStars WHERE ProjectId = @Id;", new IdParameters(projectId), transaction);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    public async Task<long> CountStarsAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        return await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM ProjectStars WHERE ProjectId = @Id;", new IdParameters(projectId));
    }

    /// <summary>The ids of the projects this person has starred, newest star first.</summary>
    public async Task<IReadOnlyList<long>> ListStarredProjectIdsAsync(AuthenticatedIdentity actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        IEnumerable<long> ids = await db.QueryAsync<long>("SELECT ProjectId FROM ProjectStars WHERE UserId = @Id ORDER BY CreatedAtUtc DESC;",
            new IdParameters(actor.UserId));
        return [.. ids];
    }

    // ---------------------------------------------------------------------------------------------------
    // Reactions
    // ---------------------------------------------------------------------------------------------------

    /// <summary>The reactions on a comment, for whoever may read the conversation. The caller checks the comment belongs where it was addressed.</summary>
    public async Task<IReadOnlyList<AwardEmoji>> ListAwardsAsync(AuthenticatedIdentity? identity, long projectId, long commentId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        IEnumerable<AwardRow> rows = await db.QueryAsync<AwardRow>(AwardSelect + " WHERE a.CommentId = @Id ORDER BY a.Id;", new IdParameters(commentId));
        return [.. rows.Select(ToAward)];
    }

    /// <summary>
    /// Adds the caller's reaction. A participant may react, as they may comment. The name is GitLab's: lower-case letters,
    /// digits, <c>_</c>, <c>+</c> and <c>-</c>. Reacting twice with the same name is a conflict.
    /// </summary>
    public async Task<AwardEmoji> AddAwardAsync(AuthenticatedIdentity actor, long projectId, long commentId, string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string clean = (name ?? "").Trim();
        if (clean.Length is 0 or > 64 || !clean.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '+' or '-'))
            throw new ArgumentException("name must be an emoji name such as thumbsup.", nameof(name));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity author = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);
        if (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Comments WHERE Id = @Id AND ProjectId = @ProjectId;",
                new CommentOwnerParameters(commentId, projectId), transaction) == 0)
            throw new CollaborationNotFoundException("Comment is unavailable.");
        long? id = await db.ExecuteScalarAsync<long?>("""
            INSERT OR IGNORE INTO AwardEmoji (CommentId, UserId, Name, CreatedAtUtc) VALUES (@CommentId, @UserId, @Name, @NowUtc)
            RETURNING Id;
            """, new AwardInsertParameters(commentId, author.UserId, clean, Now()), transaction);
        if (id is null) throw new CollaborationConflictException("You have already reacted with " + clean + ".");
        await transaction.CommitAsync(cancellationToken);
        AwardRow row = await db.QuerySingleAsync<AwardRow>(AwardSelect + " WHERE a.Id = @Id;", new IdParameters(id.Value));
        return ToAward(row);
    }

    /// <summary>Removes a reaction. Only the person who reacted, or an administrator, may. False when there is no such reaction on the comment.</summary>
    public async Task<bool> RemoveAwardAsync(AuthenticatedIdentity actor, long projectId, long commentId, long awardId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity live = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);
        AwardRow? row = await db.QuerySingleOrDefaultAsync<AwardRow>(AwardSelect + " WHERE a.Id = @Id;", new IdParameters(awardId), transaction);
        if (row is null || row.CommentId != commentId) return false;
        if (row.UserId != live.UserId && !live.IsAdministrator) throw new UnauthorizedAccessException("Only the person who reacted can remove the reaction.");
        await db.ExecuteAsync("DELETE FROM AwardEmoji WHERE Id = @Id;", new IdParameters(awardId), transaction);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // ---------------------------------------------------------------------------------------------------
    // Snippet comments
    // ---------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<SnippetNote>> ListSnippetNotesAsync(AuthenticatedIdentity? identity, long projectId, long snippetId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        await RequireSnippetAsync(db, null, projectId, snippetId);
        IEnumerable<SnippetNoteRow> rows = await db.QueryAsync<SnippetNoteRow>(SnippetNoteSelect + " WHERE n.SnippetId = @Id ORDER BY n.Id;",
            new IdParameters(snippetId));
        return [.. rows.Select(ToSnippetNote)];
    }

    public async Task<SnippetNote> AddSnippetNoteAsync(AuthenticatedIdentity actor, long projectId, long snippetId, string body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string clean = RequireDocument(body, MaximumSnippetNoteBytes, nameof(body));
        if (clean.Trim().Length == 0) throw new ArgumentException("body is required.", nameof(body));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity author = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);
        await RequireSnippetAsync(db, transaction, projectId, snippetId);
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO SnippetNotes (SnippetId, AuthorUserId, Body, CreatedAtUtc) VALUES (@SnippetId, @AuthorUserId, @Body, @NowUtc) RETURNING Id;
            """, new SnippetNoteInsertParameters(snippetId, author.UserId, clean, Now()), transaction);
        await transaction.CommitAsync(cancellationToken);
        return ToSnippetNote(await db.QuerySingleAsync<SnippetNoteRow>(SnippetNoteSelect + " WHERE n.Id = @Id;", new IdParameters(id)));
    }

    private static async Task RequireSnippetAsync(SqliteConnection db, SqliteTransaction? transaction, long projectId, long snippetId)
    {
        if (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Snippets WHERE Id = @Id AND ProjectId = @ProjectId;",
                new CommentOwnerParameters(snippetId, projectId), transaction) == 0)
            throw new CollaborationNotFoundException("Snippet is unavailable.");
    }

    // ---------------------------------------------------------------------------------------------------
    // Activity
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Records the branches a push changed, for the activity feed. Called after the push is durable.</summary>
    public async Task RecordPushAsync(long projectId, long? userId, IReadOnlyList<PushRecord> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0) return;
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        string now = Now();
        foreach (PushRecord change in changes)
            await db.ExecuteAsync("""
                INSERT INTO PushEvents (ProjectId, UserId, Ref, BeforeSha, AfterSha, CommitCount, CommitTitle, CreatedAtUtc)
                VALUES (@ProjectId, @UserId, @Ref, @BeforeSha, @AfterSha, @CommitCount, @CommitTitle, @NowUtc);
                """, new PushInsertParameters(projectId, userId, change.Ref, change.BeforeSha, change.AfterSha, change.CommitCount,
                    change.CommitTitle is null ? null : change.CommitTitle.Length > 200 ? change.CommitTitle[..200] : change.CommitTitle, now), transaction);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>A project's activity, newest first, for whoever may read its conversation.</summary>
    public async Task<(IReadOnlyList<ActivityEvent> Events, long Total)> ListProjectActivityAsync(AuthenticatedIdentity? identity, long projectId,
        int skip, int take, CancellationToken cancellationToken = default)
    {
        if (skip < 0 || take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        IEnumerable<ActivityRow> rows = await db.QueryAsync<ActivityRow>(ActivitySelect + " WHERE e.ProjectId = @Id ORDER BY e.At DESC, e.Kind DESC LIMIT @Take OFFSET @Skip;",
            new ScopedPageParameters(projectId, take, skip));
        long total = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM (" + ActivityUnion + ") e WHERE e.ProjectId = @Id;", new IdParameters(projectId));
        return ([.. rows.Select(ToActivity)], total);
    }

    /// <summary>
    /// What one person did, newest first, limited to projects the viewer may read the conversation of. A person's own
    /// activity is theirs to read and an administrator reads anybody's; anybody else is told there is no such person,
    /// as the directory does. The newest thousand entries are considered.
    /// </summary>
    public async Task<(IReadOnlyList<ActivityEvent> Events, long Total)?> ListUserActivityAsync(AuthenticatedIdentity viewer, long userId,
        int skip, int take, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (skip < 0 || take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity live = await RequireLiveIdentityAsync(db, null, viewer, cancellationToken);
        if (live.UserId != userId && !live.IsAdministrator) return null;
        IEnumerable<ActivityRow> rows = await db.QueryAsync<ActivityRow>(ActivitySelect + " WHERE e.UserId = @Id ORDER BY e.At DESC, e.Kind DESC LIMIT @Take OFFSET 0;",
            new ScopedPageParameters(userId, ActivityWindow, 0));
        Dictionary<long, bool> readable = [];
        List<ActivityEvent> visible = [];
        foreach (ActivityRow row in rows)
        {
            if (!readable.TryGetValue(row.ProjectId, out bool allowed))
                readable[row.ProjectId] = allowed = (await DecideAsync(db, null, live, row.ProjectId, ProjectAction.ReadCollaboration, cancellationToken)).Allowed;
            if (allowed) visible.Add(ToActivity(row));
        }
        return ([.. visible.Skip(skip).Take(take)], visible.Count);
    }

    // ---------------------------------------------------------------------------------------------------
    // Uploads
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Records a file the server has already stored for a project, for a participant. Returns the record.</summary>
    public async Task<ProjectUpload> RecordUploadAsync(AuthenticatedIdentity actor, long projectId, string secret, string fileName, string contentType,
        long bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity author = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);
        string now = Now();
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO ProjectUploads (ProjectId, Secret, FileName, ContentType, Bytes, UploadedByUserId, CreatedAtUtc)
            VALUES (@ProjectId, @Secret, @FileName, @ContentType, @Bytes, @UserId, @NowUtc) RETURNING Id;
            """, new UploadInsertParameters(projectId, secret, fileName, contentType, bytes, author.UserId, now), transaction);
        await AuditAsync(db, transaction, author.UserId, "upload-file", "project", projectId.ToString(CultureInfo.InvariantCulture),
            $"File '{fileName}' ({bytes} bytes) attached.", now);
        await transaction.CommitAsync(cancellationToken);
        return new(id, projectId, secret, fileName, contentType, bytes, Instant(now));
    }

    /// <summary>A project's upload by its secret and name, for whoever may read the conversation; null when there is none.</summary>
    public async Task<ProjectUpload?> FindUploadAsync(AuthenticatedIdentity? identity, long projectId, string secret, string fileName,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        PermissionDecision read = await DecideAsync(db, null, identity, projectId, ProjectAction.ReadCollaboration, cancellationToken);
        if (!read.Allowed) return null;
        UploadRow? row = await db.QuerySingleOrDefaultAsync<UploadRow>("""
            SELECT Id, ProjectId, Secret, FileName, ContentType, Bytes, CreatedAtUtc FROM ProjectUploads
            WHERE ProjectId = @ProjectId AND Secret = @Secret AND FileName = @FileName;
            """, new UploadLookupParameters(projectId, secret, fileName));
        return row is null ? null : new(row.Id, row.ProjectId, row.Secret, row.FileName, row.ContentType, row.Bytes, Instant(row.CreatedAtUtc));
    }

    // ---------------------------------------------------------------------------------------------------
    // The user directory
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The directory: every active account, optionally matched by name, for an administrator, and nobody for anyone
    /// else. Graft does not let its users enumerate one another.
    /// </summary>
    public async Task<IReadOnlyList<UserAccount>> SearchDirectoryAsync(AuthenticatedIdentity viewer, string? search, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity live = await RequireLiveIdentityAsync(db, null, viewer, cancellationToken);
        if (!live.IsAdministrator) return [];
        string pattern = "%" + (search ?? "").Trim().ToLowerInvariant().Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        IEnumerable<UserListRow> rows = await db.QueryAsync<UserListRow>("""
            SELECT Id, Username, IsAdministrator, Disabled, Email FROM Users
            WHERE Disabled = 0 AND NormalizedUsername LIKE @Pattern ESCAPE '\'
            ORDER BY NormalizedUsername LIMIT 100;
            """, new PatternParameters(pattern));
        return [.. rows.Select(row => new UserAccount(row.Id, row.Username, row.IsAdministrator != 0, false, row.Email))];
    }

    /// <summary>One account by id: the viewer's own, or anybody's for an administrator; null otherwise, as for no such account.</summary>
    public async Task<UserAccount?> FindDirectoryUserAsync(AuthenticatedIdentity viewer, long userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity live = await RequireLiveIdentityAsync(db, null, viewer, cancellationToken);
        if (live.UserId != userId && !live.IsAdministrator) return null;
        UserListRow? row = await db.QuerySingleOrDefaultAsync<UserListRow>(
            "SELECT Id, Username, IsAdministrator, Disabled, Email FROM Users WHERE Id = @Id AND Disabled = 0;", new IdParameters(userId));
        return row is null ? null : new(row.Id, row.Username, row.IsAdministrator != 0, false, row.Email);
    }

    /// <summary>The ids of the projects in a person's own namespace, newest first. The caller decides which of them the viewer may see.</summary>
    public async Task<IReadOnlyList<long>> ListPersonalProjectIdsAsync(long ownerUserId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        IEnumerable<long> ids = await db.QueryAsync<long>("SELECT Id FROM Projects WHERE OwnerUserId = @Id AND GroupId IS NULL ORDER BY Id DESC;",
            new IdParameters(ownerUserId));
        return [.. ids];
    }

    // ---------------------------------------------------------------------------------------------------
    // Deleting an issue
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Deletes an issue with its comments, reactions, labels, people, time and notifications. The project's owner or an
    /// administrator may, as for deleting the project: GitLab gives this to the Owner role only.
    /// </summary>
    public async Task DeleteIssueAsync(AuthenticatedIdentity actor, long projectId, long iid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity live = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        long? owner = await db.ExecuteScalarAsync<long?>("SELECT OwnerUserId FROM Projects WHERE Id = @Id;", new IdParameters(projectId), transaction);
        if (owner is null) throw new UnauthorizedAccessException("Project is unavailable.");
        PermissionDecision read = await DecideAsync(db, transaction, live, projectId, ProjectAction.ReadCollaboration, cancellationToken);
        if (!read.Allowed) throw new UnauthorizedAccessException(read.Explanation);
        long issueId = await ResolveSubjectAsync(db, transaction, projectId, CommentSubject.Issue, iid);
        if (!live.IsAdministrator && live.UserId != owner) throw new InvalidOperationException("Only the project owner or an administrator can delete an issue.");
        IssueSubjectParameters subject = new("issue", issueId);
        await db.ExecuteAsync("DELETE FROM AwardEmoji WHERE CommentId IN (SELECT Id FROM Comments WHERE SubjectType = @SubjectType AND SubjectId = @SubjectId);", subject, transaction);
        await db.ExecuteAsync("UPDATE Comments SET ParentCommentId = NULL WHERE SubjectType = @SubjectType AND SubjectId = @SubjectId;", subject, transaction);
        await db.ExecuteAsync("DELETE FROM Comments WHERE SubjectType = @SubjectType AND SubjectId = @SubjectId;", subject, transaction);
        await db.ExecuteAsync("DELETE FROM SubjectLabels WHERE SubjectType = @SubjectType AND SubjectId = @SubjectId;", subject, transaction);
        await db.ExecuteAsync("DELETE FROM SubjectPeople WHERE SubjectType = @SubjectType AND SubjectId = @SubjectId;", subject, transaction);
        await db.ExecuteAsync("DELETE FROM TimeEntries WHERE SubjectType = @SubjectType AND SubjectId = @SubjectId;", subject, transaction);
        await db.ExecuteAsync("DELETE FROM Notifications WHERE ProjectId = @ProjectId AND SubjectType = 'issue' AND SubjectIid = @Iid;",
            new IssueNotificationParameters(projectId, iid), transaction);
        await db.ExecuteAsync("DELETE FROM Issues WHERE Id = @Id;", new IdParameters(issueId), transaction);
        await AuditAsync(db, transaction, live.UserId, "delete-issue", "issue", issueId.ToString(CultureInfo.InvariantCulture),
            $"Issue #{iid} deleted.", Now());
        await transaction.CommitAsync(cancellationToken);
    }

    // ---------------------------------------------------------------------------------------------------
    // Rows
    // ---------------------------------------------------------------------------------------------------

    private const string AwardSelect = """
        SELECT a.Id, a.CommentId, a.Name, a.UserId, u.Username, a.CreatedAtUtc FROM AwardEmoji a JOIN Users u ON u.Id = a.UserId
        """;
    private const string SnippetNoteSelect = """
        SELECT n.Id, n.SnippetId, n.AuthorUserId, u.Username AS AuthorUsername, n.Body, n.CreatedAtUtc FROM SnippetNotes n JOIN Users u ON u.Id = n.AuthorUserId
        """;

    // Every kind of activity in one shape. Confidential issues, and comments on them, are left out for everyone.
    private const string ActivityUnion = """
        SELECT 1 AS Kind, i.Id AS SourceId, i.ProjectId, i.AuthorUserId AS UserId, i.CreatedAtUtc AS At, i.Id AS TargetId, i.Iid AS TargetIid,
               i.Title AS TargetTitle, NULL AS SubjectType, NULL AS Body, NULL AS Ref, NULL AS BeforeSha, NULL AS AfterSha, 0 AS CommitCount, NULL AS CommitTitle
        FROM Issues i WHERE i.Confidential = 0
        UNION ALL
        SELECT 2, m.Id, m.ProjectId, m.AuthorUserId, m.CreatedAtUtc, m.Id, m.Iid, m.Title, NULL, NULL, NULL, NULL, NULL, 0, NULL
        FROM MergeRequests m
        UNION ALL
        SELECT 3, m.Id, m.ProjectId, m.MergedByUserId, m.ClosedAtUtc, m.Id, m.Iid, m.Title, NULL, NULL, NULL, NULL, NULL, 0, NULL
        FROM MergeRequests m WHERE m.State = 'merged' AND m.MergedByUserId IS NOT NULL AND m.ClosedAtUtc IS NOT NULL
        UNION ALL
        SELECT 4, c.Id, c.ProjectId, c.AuthorUserId, c.CreatedAtUtc, COALESCE(i.Id, r.Id), COALESCE(i.Iid, r.Iid), COALESCE(i.Title, r.Title),
               c.SubjectType, c.Body, NULL, NULL, NULL, 0, NULL
        FROM Comments c
        LEFT JOIN Issues i ON c.SubjectType = 'issue' AND i.Id = c.SubjectId
        LEFT JOIN MergeRequests r ON c.SubjectType = 'merge_request' AND r.Id = c.SubjectId
        WHERE (c.SubjectType = 'issue' AND i.Id IS NOT NULL AND i.Confidential = 0) OR (c.SubjectType = 'merge_request' AND r.Id IS NOT NULL)
        UNION ALL
        SELECT 5, p.Id, p.ProjectId, p.UserId, p.CreatedAtUtc, NULL, NULL, NULL, NULL, NULL, p.Ref, p.BeforeSha, p.AfterSha, p.CommitCount, p.CommitTitle
        FROM PushEvents p
        """;
    private const string ActivitySelect = """
        SELECT e.Kind, e.SourceId, e.ProjectId, e.UserId, u.Username, e.At, e.TargetId, e.TargetIid, e.TargetTitle, e.SubjectType, e.Body,
               e.Ref, e.BeforeSha, e.AfterSha, e.CommitCount, e.CommitTitle
        FROM (
        """ + ActivityUnion + """
        ) e
        LEFT JOIN Users u ON u.Id = e.UserId
        """;

    private static AwardEmoji ToAward(AwardRow row) => new(row.Id, row.CommentId, row.Name, row.UserId, row.Username, Instant(row.CreatedAtUtc));
    private static SnippetNote ToSnippetNote(SnippetNoteRow row) =>
        new(row.Id, row.SnippetId, row.AuthorUserId, row.AuthorUsername, row.Body, Instant(row.CreatedAtUtc));
    private static ActivityEvent ToActivity(ActivityRow row) => new(row.SourceId * 8 + row.Kind, (ActivityKind)row.Kind, row.ProjectId, row.UserId,
        row.Username, Instant(row.At), row.TargetId, row.TargetIid, row.TargetTitle, row.SubjectType, row.Body, row.Ref, row.BeforeSha, row.AfterSha,
        (int)row.CommitCount, row.CommitTitle);
}

internal sealed record StarParameters(long UserId, long ProjectId, string NowUtc);
internal sealed record AwardInsertParameters(long CommentId, long UserId, string Name, string NowUtc);
internal sealed record AwardRow(long Id, long CommentId, string Name, long UserId, string Username, string CreatedAtUtc);
internal sealed record SnippetNoteInsertParameters(long SnippetId, long AuthorUserId, string Body, string NowUtc);
internal sealed record SnippetNoteRow(long Id, long SnippetId, long AuthorUserId, string AuthorUsername, string Body, string CreatedAtUtc);
internal sealed record PushInsertParameters(long ProjectId, long? UserId, string Ref, string? BeforeSha, string? AfterSha, int CommitCount, string? CommitTitle, string NowUtc);
internal sealed record ActivityRow(long Kind, long SourceId, long ProjectId, long? UserId, string? Username, string At, long? TargetId, long? TargetIid,
    string? TargetTitle, string? SubjectType, string? Body, string? Ref, string? BeforeSha, string? AfterSha, long CommitCount, string? CommitTitle);
internal sealed record UploadInsertParameters(long ProjectId, string Secret, string FileName, string ContentType, long Bytes, long UserId, string NowUtc);
internal sealed record UploadLookupParameters(long ProjectId, string Secret, string FileName);
internal sealed record UploadRow(long Id, long ProjectId, string Secret, string FileName, string ContentType, long Bytes, string CreatedAtUtc);
internal sealed record PatternParameters(string Pattern);
internal sealed record IssueSubjectParameters(string SubjectType, long SubjectId);
internal sealed record IssueNotificationParameters(long ProjectId, long Iid);
