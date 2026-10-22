using System.Globalization;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// The conversation half of the store: issues, merge proposals and the comments on both.
///
/// <para>
/// Separate file, same class, because these share the connection handling, the permission resolution and
/// the audit writer with everything else and splitting them into a second type would mean duplicating all
/// three. Nothing here is reachable without a permission decision from the same
/// <see cref="ProjectAuthorization"/> that guards the repository.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    /// <summary>GitLab's own ceiling for an issue title, so a client that validates against GitLab's rules
    /// and one that validates against Graft's agree.</summary>
    private const int MaximumTitleLength = 255;

    /// <summary>Generous for prose and far below anything that threatens a row. A body larger than this is
    /// a file, and files belong in the repository.</summary>
    private const int MaximumBodyLength = 50_000;

    public async Task<Issue> CreateIssueAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string title,
        string description = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string cleanTitle = RequireTitle(title);
        string cleanDescription = RequireBody(description, nameof(description), allowEmpty: true);

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity author = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);

        string now = Now();
        long iid = await NextNumberAsync(db, transaction, CommentSubject.Issue, projectId);
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO Issues (ProjectId, Iid, AuthorUserId, Title, Description, State, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@ProjectId, @Iid, @AuthorUserId, @Title, @Description, 'opened', @CreatedAtUtc, @CreatedAtUtc)
            RETURNING Id;
            """, new IssueInsertParameters(projectId, iid, author.UserId, cleanTitle, cleanDescription, now), transaction);
        await AuditAsync(db, transaction, author.UserId, "open-issue", "issue",
            Reference(projectId, iid), "Issue opened.", now);
        await transaction.CommitAsync(cancellationToken);
        return new(id, projectId, iid, author.UserId, author.Username, cleanTitle, cleanDescription,
            CollaborationState.Opened, Instant(now), Instant(now), null);
    }

    /// <summary>
    /// Lists a project's issues, newest first. <paramref name="state"/> null means both.
    /// </summary>
    public async Task<IssuePage> ListIssuesAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        CollaborationState? state,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0 || take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);

        CollaborationListParameters parameters = new(projectId, StateName(state), state is null ? 1 : 0, take, skip);
        IEnumerable<IssueRow> rows = await db.QueryAsync<IssueRow>("""
            SELECT i.Id, i.ProjectId, i.Iid, i.AuthorUserId, u.Username AS AuthorUsername, i.Title, i.Description,
                   i.State, i.CreatedAtUtc, i.UpdatedAtUtc, i.ClosedAtUtc,
                   (SELECT COUNT(*) FROM Comments c WHERE c.SubjectType = 'issue' AND c.SubjectId = i.Id) AS Comments
            FROM Issues i
            JOIN Users u ON u.Id = i.AuthorUserId
            WHERE i.ProjectId = @ProjectId AND (@AnyState = 1 OR i.State = @State)
            ORDER BY i.Iid DESC
            LIMIT @Take OFFSET @Skip;
            """, parameters);
        long total = await db.QuerySingleAsync<long>("""
            SELECT COUNT(*) FROM Issues i
            WHERE i.ProjectId = @ProjectId AND (@AnyState = 1 OR i.State = @State);
            """, parameters);
        return new([.. rows.Select(ToIssue)], total);
    }

    public async Task<Issue?> FindIssueAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        long iid,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        IssueRow? row = await db.QuerySingleOrDefaultAsync<IssueRow>("""
            SELECT i.Id, i.ProjectId, i.Iid, i.AuthorUserId, u.Username AS AuthorUsername, i.Title, i.Description,
                   i.State, i.CreatedAtUtc, i.UpdatedAtUtc, i.ClosedAtUtc,
                   (SELECT COUNT(*) FROM Comments c WHERE c.SubjectType = 'issue' AND c.SubjectId = i.Id) AS Comments
            FROM Issues i
            JOIN Users u ON u.Id = i.AuthorUserId
            WHERE i.ProjectId = @ProjectId AND i.Iid = @Iid;
            """, new CollaborationItemParameters(projectId, iid));
        return row is null ? null : ToIssue(row);
    }

    /// <summary>
    /// Edits an issue. A null argument leaves that field alone, so closing an issue does not require
    /// resending its text.
    ///
    /// <para>
    /// The author may edit their own; a Maintainer may edit anyone's. That split is deliberate: a Reporter
    /// who can file an issue should not be able to rewrite somebody else's words, and a project needs
    /// somebody who can close what the author has abandoned.
    /// </para>
    /// </summary>
    public async Task<Issue> UpdateIssueAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long iid,
        string? title,
        string? description,
        CollaborationState? state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (state is CollaborationState.Merged) throw new ArgumentException("An issue cannot be merged.", nameof(state));
        string? cleanTitle = title is null ? null : RequireTitle(title);
        string? cleanDescription = description is null ? null : RequireBody(description, nameof(description), allowEmpty: true);

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity editor = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);
        CollaborationOwnerRow? existing = await db.QuerySingleOrDefaultAsync<CollaborationOwnerRow>(
            "SELECT Id, AuthorUserId, State FROM Issues WHERE ProjectId = @ProjectId AND Iid = @Iid;",
            new CollaborationItemParameters(projectId, iid), transaction);
        if (existing is null) throw new CollaborationNotFoundException("Issue is unavailable.");
        await RequireEditorAsync(db, transaction, editor, projectId, existing.AuthorUserId,
            textChanged: cleanTitle is not null || cleanDescription is not null, cancellationToken);

        string now = Now();
        string nextState = state is null ? existing.State : StateName(state)!;
        await db.ExecuteAsync("""
            UPDATE Issues
            SET Title = COALESCE(@Title, Title),
                Description = COALESCE(@Description, Description),
                State = @State,
                ClosedAtUtc = CASE WHEN @State = 'closed' THEN COALESCE(ClosedAtUtc, @Now) ELSE NULL END,
                UpdatedAtUtc = @Now
            WHERE Id = @Id;
            """, new IssueUpdateParameters(existing.Id, cleanTitle, cleanDescription, nextState, now), transaction);
        if (nextState != existing.State)
            await AuditAsync(db, transaction, editor.UserId, nextState == "closed" ? "close-issue" : "reopen-issue",
                "issue", Reference(projectId, iid), nextState == "closed" ? "Issue closed." : "Issue reopened.", now);
        await transaction.CommitAsync(cancellationToken);
        return await FindIssueAsync(actor, projectId, iid, cancellationToken)
            ?? throw new CollaborationNotFoundException("Issue is unavailable.");
    }

    public async Task<MergeProposal> CreateMergeProposalAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string sourceBranch,
        string targetBranch,
        string title,
        string description = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string source = RequireBranch(sourceBranch, nameof(sourceBranch));
        string target = RequireBranch(targetBranch, nameof(targetBranch));
        if (string.Equals(source, target, StringComparison.Ordinal))
            throw new ArgumentException("A merge proposal needs two different branches.", nameof(sourceBranch));
        string cleanTitle = RequireTitle(title);
        string cleanDescription = RequireBody(description, nameof(description), allowEmpty: true);

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity author = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);

        // One open proposal per pair of branches. A second would be two conversations about one change, and
        // whichever was merged would silently decide the other.
        long open = await db.QuerySingleAsync<long>("""
            SELECT COUNT(*) FROM MergeRequests
            WHERE ProjectId = @ProjectId AND SourceBranch = @Source AND TargetBranch = @Target AND State = 'opened';
            """, new MergeBranchParameters(projectId, source, target), transaction);
        if (open != 0)
            throw new CollaborationConflictException("An open merge proposal already exists for these branches.");

        string now = Now();
        long iid = await NextNumberAsync(db, transaction, CommentSubject.MergeRequest, projectId);
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO MergeRequests (ProjectId, Iid, AuthorUserId, Title, Description, SourceBranch, TargetBranch,
                                       State, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@ProjectId, @Iid, @AuthorUserId, @Title, @Description, @SourceBranch, @TargetBranch,
                    'opened', @CreatedAtUtc, @CreatedAtUtc)
            RETURNING Id;
            """, new MergeInsertParameters(projectId, iid, author.UserId, cleanTitle, cleanDescription, source, target, now),
            transaction);
        await AuditAsync(db, transaction, author.UserId, "open-merge-request", "merge_request",
            Reference(projectId, iid), $"Merge proposed from {source} into {target}.", now);
        await transaction.CommitAsync(cancellationToken);
        return new(id, projectId, iid, author.UserId, author.Username, cleanTitle, cleanDescription,
            source, target, CollaborationState.Opened, null, null, Instant(now), Instant(now), null);
    }

    public async Task<MergeProposalPage> ListMergeProposalsAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        CollaborationState? state,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0 || take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);

        CollaborationListParameters parameters = new(projectId, StateName(state), state is null ? 1 : 0, take, skip);
        IEnumerable<MergeRow> rows = await db.QueryAsync<MergeRow>("""
            SELECT m.Id, m.ProjectId, m.Iid, m.AuthorUserId, a.Username AS AuthorUsername, m.Title, m.Description,
                   m.SourceBranch, m.TargetBranch, m.State, m.MergeCommitSha, g.Username AS MergedByUsername,
                   m.CreatedAtUtc, m.UpdatedAtUtc, m.ClosedAtUtc,
                   (SELECT COUNT(*) FROM Comments c WHERE c.SubjectType = 'merge_request' AND c.SubjectId = m.Id) AS Comments
            FROM MergeRequests m
            JOIN Users a ON a.Id = m.AuthorUserId
            LEFT JOIN Users g ON g.Id = m.MergedByUserId
            WHERE m.ProjectId = @ProjectId AND (@AnyState = 1 OR m.State = @State)
            ORDER BY m.Iid DESC
            LIMIT @Take OFFSET @Skip;
            """, parameters);
        long total = await db.QuerySingleAsync<long>("""
            SELECT COUNT(*) FROM MergeRequests m
            WHERE m.ProjectId = @ProjectId AND (@AnyState = 1 OR m.State = @State);
            """, parameters);
        return new([.. rows.Select(ToProposal)], total);
    }

    public async Task<MergeProposal?> FindMergeProposalAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        long iid,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        MergeRow? row = await db.QuerySingleOrDefaultAsync<MergeRow>("""
            SELECT m.Id, m.ProjectId, m.Iid, m.AuthorUserId, a.Username AS AuthorUsername, m.Title, m.Description,
                   m.SourceBranch, m.TargetBranch, m.State, m.MergeCommitSha, g.Username AS MergedByUsername,
                   m.CreatedAtUtc, m.UpdatedAtUtc, m.ClosedAtUtc,
                   (SELECT COUNT(*) FROM Comments c WHERE c.SubjectType = 'merge_request' AND c.SubjectId = m.Id) AS Comments
            FROM MergeRequests m
            JOIN Users a ON a.Id = m.AuthorUserId
            LEFT JOIN Users g ON g.Id = m.MergedByUserId
            WHERE m.ProjectId = @ProjectId AND m.Iid = @Iid;
            """, new CollaborationItemParameters(projectId, iid));
        return row is null ? null : ToProposal(row);
    }

    /// <summary>
    /// Edits a merge proposal's text or closes and reopens it. The branches are fixed once proposed: a
    /// proposal that could be repointed would invalidate every comment made about it.
    /// </summary>
    public async Task<MergeProposal> UpdateMergeProposalAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long iid,
        string? title,
        string? description,
        CollaborationState? state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (state is CollaborationState.Merged)
            throw new ArgumentException("Merging is done by merging, not by setting a state.", nameof(state));
        string? cleanTitle = title is null ? null : RequireTitle(title);
        string? cleanDescription = description is null ? null : RequireBody(description, nameof(description), allowEmpty: true);

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity editor = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);
        CollaborationOwnerRow? existing = await db.QuerySingleOrDefaultAsync<CollaborationOwnerRow>(
            "SELECT Id, AuthorUserId, State FROM MergeRequests WHERE ProjectId = @ProjectId AND Iid = @Iid;",
            new CollaborationItemParameters(projectId, iid), transaction);
        if (existing is null) throw new CollaborationNotFoundException("Merge proposal is unavailable.");
        if (existing.State == "merged")
            throw new CollaborationConflictException("A merged proposal cannot be changed: its work is already in the target.");
        await RequireEditorAsync(db, transaction, editor, projectId, existing.AuthorUserId,
            textChanged: cleanTitle is not null || cleanDescription is not null, cancellationToken);

        string now = Now();
        string nextState = state is null ? existing.State : StateName(state)!;
        await db.ExecuteAsync("""
            UPDATE MergeRequests
            SET Title = COALESCE(@Title, Title),
                Description = COALESCE(@Description, Description),
                State = @State,
                ClosedAtUtc = CASE WHEN @State = 'closed' THEN COALESCE(ClosedAtUtc, @Now) ELSE NULL END,
                UpdatedAtUtc = @Now
            WHERE Id = @Id;
            """, new IssueUpdateParameters(existing.Id, cleanTitle, cleanDescription, nextState, now), transaction);
        if (nextState != existing.State)
            await AuditAsync(db, transaction, editor.UserId,
                nextState == "closed" ? "close-merge-request" : "reopen-merge-request", "merge_request",
                Reference(projectId, iid), nextState == "closed" ? "Merge proposal closed." : "Merge proposal reopened.", now);
        await transaction.CommitAsync(cancellationToken);
        return await FindMergeProposalAsync(actor, projectId, iid, cancellationToken)
            ?? throw new CollaborationNotFoundException("Merge proposal is unavailable.");
    }

    /// <summary>
    /// Records that a proposal has been merged, naming the commit that is now in the target branch.
    ///
    /// <para>
    /// The Git work happens before this and outside the store, which is the only possible order: the commit
    /// has to exist before a row can name it. So this is a record of something already done, and it fails
    /// only if the proposal moved underneath the caller. It requires the permission a push requires,
    /// because that is what merging is.
    /// </para>
    /// </summary>
    public async Task<MergeProposal> RecordMergeAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long iid,
        string mergeCommitSha,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(mergeCommitSha);
        if (mergeCommitSha.Length != 40 || !mergeCommitSha.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("A merge commit is a lowercase hexadecimal object id.", nameof(mergeCommitSha));

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId,
            ProjectAction.PushRepository, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);

        string now = Now();
        int changed = await db.ExecuteAsync("""
            UPDATE MergeRequests
            SET State = 'merged', MergeCommitSha = @Sha, MergedByUserId = @UserId, ClosedAtUtc = @Now, UpdatedAtUtc = @Now
            WHERE ProjectId = @ProjectId AND Iid = @Iid AND State = 'opened';
            """, new MergeRecordParameters(projectId, iid, mergeCommitSha, actor.UserId, now), transaction);
        if (changed != 1) throw new CollaborationConflictException("The proposal is no longer open.");
        await AuditAsync(db, transaction, actor.UserId, "merge-merge-request", "merge_request",
            Reference(projectId, iid), $"Merged as {mergeCommitSha}.", now);
        await transaction.CommitAsync(cancellationToken);
        return await FindMergeProposalAsync(actor, projectId, iid, cancellationToken)
            ?? throw new CollaborationNotFoundException("Merge proposal is unavailable.");
    }

    public async Task<Comment> AddCommentAsync(
        AuthenticatedIdentity actor,
        long projectId,
        CommentSubject subject,
        long iid,
        string body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string cleanBody = RequireBody(body, nameof(body), allowEmpty: false);

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity author = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);
        long subjectId = await ResolveSubjectAsync(db, transaction, projectId, subject, iid);

        string now = Now();
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO Comments (ProjectId, SubjectType, SubjectId, AuthorUserId, Body, CreatedAtUtc)
            VALUES (@ProjectId, @SubjectType, @SubjectId, @AuthorUserId, @Body, @CreatedAtUtc)
            RETURNING Id;
            """, new CommentInsertParameters(projectId, SubjectName(subject), subjectId, author.UserId, cleanBody, now),
            transaction);
        // The subject's own timestamp moves, so a list ordered by activity is possible later and a reader
        // can see that a closed issue is still being discussed.
        await db.ExecuteAsync(subject == CommentSubject.Issue
            ? "UPDATE Issues SET UpdatedAtUtc = @UpdatedAtUtc WHERE Id = @Id;"
            : "UPDATE MergeRequests SET UpdatedAtUtc = @UpdatedAtUtc WHERE Id = @Id;",
            new TouchParameters(subjectId, now), transaction);
        await transaction.CommitAsync(cancellationToken);
        return new(id, author.UserId, author.Username, cleanBody, Instant(now));
    }

    public async Task<IReadOnlyList<Comment>> ListCommentsAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        CommentSubject subject,
        long iid,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0 || take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        long subjectId = await ResolveSubjectAsync(db, null, projectId, subject, iid);
        IEnumerable<CommentRow> rows = await db.QueryAsync<CommentRow>("""
            SELECT c.Id, c.AuthorUserId, u.Username AS AuthorUsername, c.Body, c.CreatedAtUtc
            FROM Comments c
            JOIN Users u ON u.Id = c.AuthorUserId
            WHERE c.SubjectType = @SubjectType AND c.SubjectId = @SubjectId
            ORDER BY c.Id
            LIMIT @Take OFFSET @Skip;
            """, new CommentListParameters(SubjectName(subject), subjectId, take, skip));
        return [.. rows.Select(row => new Comment(row.Id, row.AuthorUserId, row.AuthorUsername, row.Body,
            Instant(row.CreatedAtUtc)))];
    }

    /// <summary>
    /// Deletes a comment. Its author, or a Maintainer, because a project with no way to remove abuse has no
    /// answer to it.
    /// </summary>
    public async Task DeleteCommentAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long commentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity editor = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);
        CommentOwnerRow? existing = await db.QuerySingleOrDefaultAsync<CommentOwnerRow>(
            "SELECT Id, AuthorUserId FROM Comments WHERE Id = @Id AND ProjectId = @ProjectId;",
            new CommentOwnerParameters(commentId, projectId), transaction);
        if (existing is null) throw new CollaborationNotFoundException("Comment is unavailable.");
        await RequireEditorAsync(db, transaction, editor, projectId, existing.AuthorUserId, textChanged: true, cancellationToken);

        await db.ExecuteAsync("DELETE FROM Comments WHERE Id = @Id;", new IdParameters(commentId), transaction);
        await AuditAsync(db, transaction, editor.UserId, "delete-comment", "comment",
            commentId.ToString(CultureInfo.InvariantCulture), "Comment deleted.", Now());
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The next public number for a project's issues or merge proposals.
    ///
    /// <para>
    /// Computed rather than stored, inside the immediate transaction that inserts the row, so two authors
    /// cannot be handed the same number: the second waits for the first to commit and then reads its row.
    /// Which table to count is chosen from an enum, so no caller can put a name in the statement.
    /// </para>
    /// </summary>
    private static async Task<long> NextNumberAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        CommentSubject kind,
        long projectId) =>
        await db.QuerySingleAsync<long>(
            kind == CommentSubject.Issue
                ? "SELECT COALESCE(MAX(Iid), 0) + 1 FROM Issues WHERE ProjectId = @Id;"
                : "SELECT COALESCE(MAX(Iid), 0) + 1 FROM MergeRequests WHERE ProjectId = @Id;",
            new IdParameters(projectId), transaction);

    /// <summary>Turns a public number into the row id comments hang from, refusing one that names nothing.</summary>
    private static async Task<long> ResolveSubjectAsync(
        SqliteConnection db,
        SqliteTransaction? transaction,
        long projectId,
        CommentSubject subject,
        long iid)
    {
        SubjectRow? row = await db.QuerySingleOrDefaultAsync<SubjectRow>(subject == CommentSubject.Issue
            ? "SELECT Id FROM Issues WHERE ProjectId = @ProjectId AND Iid = @Iid;"
            : "SELECT Id FROM MergeRequests WHERE ProjectId = @ProjectId AND Iid = @Iid;",
            new CollaborationItemParameters(projectId, iid), transaction);
        return row?.Id ?? throw new CollaborationNotFoundException(subject == CommentSubject.Issue
            ? "Issue is unavailable."
            : "Merge proposal is unavailable.");
    }

    private static async Task RequireReaderAsync(
        SqliteConnection db,
        SqliteTransaction? transaction,
        AuthenticatedIdentity? identity,
        long projectId,
        CancellationToken cancellationToken)
    {
        PermissionDecision permission = await DecideAsync(db, transaction, identity, projectId,
            ProjectAction.ReadCollaboration, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);
    }

    private static async Task<AuthenticatedIdentity> RequireParticipantAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        AuthenticatedIdentity actor,
        long projectId,
        CancellationToken cancellationToken)
    {
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId,
            ProjectAction.WriteCollaboration, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);
        return await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
    }

    /// <summary>
    /// Whether this caller may change somebody else's item. The author always may; anybody else needs the
    /// permission to manage the project.
    ///
    /// <para>
    /// <paramref name="textChanged"/> separates the two things an edit can be. Changing state is a project
    /// decision and a Maintainer makes it; changing words is authorship, and nobody but the author and a
    /// Maintainer rewrites them. Both land on the same rule here, and the parameter exists so the
    /// distinction is visible at the call sites rather than implied.
    /// </para>
    /// </summary>
    private static async Task RequireEditorAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        AuthenticatedIdentity editor,
        long projectId,
        long authorUserId,
        bool textChanged,
        CancellationToken cancellationToken)
    {
        if (editor.UserId == authorUserId) return;
        PermissionDecision permission = await DecideAsync(db, transaction, editor, projectId,
            ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed)
            throw new UnauthorizedAccessException(textChanged
                ? "Only the author or a Maintainer can change this."
                : "Only the author or a Maintainer can change its state.");
    }

    private static string Reference(long projectId, long iid) =>
        projectId.ToString(CultureInfo.InvariantCulture) + "#" + iid.ToString(CultureInfo.InvariantCulture);

    private static string? StateName(CollaborationState? state) => state switch
    {
        CollaborationState.Opened => "opened",
        CollaborationState.Closed => "closed",
        CollaborationState.Merged => "merged",
        _ => null
    };

    private static CollaborationState ParseState(string value) => value switch
    {
        "closed" => CollaborationState.Closed,
        "merged" => CollaborationState.Merged,
        _ => CollaborationState.Opened
    };

    private static string SubjectName(CommentSubject subject) =>
        subject == CommentSubject.Issue ? "issue" : "merge_request";

    private static string RequireTitle(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string trimmed = value.Trim();
        if (trimmed.Length > MaximumTitleLength)
            throw new ArgumentException($"A title is at most {MaximumTitleLength} characters.", nameof(value));
        // A title is one line. A newline in one would break every place it is shown on a single row, and
        // the description is where more than a line belongs.
        if (trimmed.Any(character => character is '\r' or '\n'))
            throw new ArgumentException("A title is a single line.", nameof(value));
        return trimmed;
    }

    private static string RequireBody(string value, string parameterName, bool allowEmpty)
    {
        value ??= "";
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A comment needs something in it.", parameterName);
        if (value.Length > MaximumBodyLength)
            throw new ArgumentException($"At most {MaximumBodyLength} characters.", parameterName);
        return value;
    }

    /// <summary>
    /// Bounds a branch name. This is not Git's rule set: the Git engine owns that, and the server checks the
    /// branch actually resolves before a proposal is stored. What is refused here is what would make a
    /// stored name unusable or ambiguous however valid Git thought it was.
    /// </summary>
    private static string RequireBranch(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 255) throw new ArgumentException("A branch name is at most 255 characters.", parameterName);
        if (value != value.Trim()) throw new ArgumentException("A branch name has no surrounding space.", parameterName);
        if (value.Any(character => char.IsControl(character) || character is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
            throw new ArgumentException("A branch name contains a character Git does not allow.", parameterName);
        if (value.StartsWith('-') || value.StartsWith('/') || value.EndsWith('/') || value.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("A branch name is malformed.", parameterName);
        return value;
    }

    private static Issue ToIssue(IssueRow row) => new(
        row.Id, row.ProjectId, row.Iid, row.AuthorUserId, row.AuthorUsername, row.Title, row.Description,
        ParseState(row.State), Instant(row.CreatedAtUtc), Instant(row.UpdatedAtUtc),
        row.ClosedAtUtc is null ? null : Instant(row.ClosedAtUtc), row.Comments);

    private static MergeProposal ToProposal(MergeRow row) => new(
        row.Id, row.ProjectId, row.Iid, row.AuthorUserId, row.AuthorUsername, row.Title, row.Description,
        row.SourceBranch, row.TargetBranch, ParseState(row.State), row.MergeCommitSha, row.MergedByUsername,
        Instant(row.CreatedAtUtc), Instant(row.UpdatedAtUtc),
        row.ClosedAtUtc is null ? null : Instant(row.ClosedAtUtc), row.Comments);
}

/// <summary>The issue, proposal or comment named does not exist in this project.</summary>
public sealed class CollaborationNotFoundException(string message) : Exception(message);

/// <summary>The operation contradicts the current state: a merged proposal, or a duplicate of an open one.</summary>
public sealed class CollaborationConflictException(string message) : Exception(message);

internal sealed record IssueInsertParameters(long ProjectId, long Iid, long AuthorUserId, string Title,
    string Description, string CreatedAtUtc);
internal sealed record MergeInsertParameters(long ProjectId, long Iid, long AuthorUserId, string Title,
    string Description, string SourceBranch, string TargetBranch, string CreatedAtUtc);
internal sealed record IssueUpdateParameters(long Id, string? Title, string? Description, string State, string Now);
internal sealed record MergeRecordParameters(long ProjectId, long Iid, string Sha, long UserId, string Now);
internal sealed record MergeBranchParameters(long ProjectId, string Source, string Target);
internal sealed record CollaborationItemParameters(long ProjectId, long Iid);
internal sealed record CollaborationListParameters(long ProjectId, string? State, int AnyState, int Take, int Skip);
internal sealed record CommentInsertParameters(long ProjectId, string SubjectType, long SubjectId, long AuthorUserId,
    string Body, string CreatedAtUtc);
internal sealed record CommentListParameters(string SubjectType, long SubjectId, int Take, int Skip);
internal sealed record CommentOwnerParameters(long Id, long ProjectId);
internal sealed record TouchParameters(long Id, string UpdatedAtUtc);
internal sealed record AuditQueryParameters(string? Action, int AnyAction, string? TargetType, int AnyTargetType,
    int Take, int Skip);

internal sealed class CollaborationOwnerRow
{
    public long Id { get; set; }
    public long AuthorUserId { get; set; }
    public string State { get; set; } = "";
}

internal sealed class CommentOwnerRow
{
    public long Id { get; set; }
    public long AuthorUserId { get; set; }
}

internal sealed class SubjectRow { public long Id { get; set; } }

internal sealed class IssueRow
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public long Iid { get; set; }
    public long AuthorUserId { get; set; }
    public string AuthorUsername { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string State { get; set; } = "opened";
    public string CreatedAtUtc { get; set; } = "";
    public string UpdatedAtUtc { get; set; } = "";
    public string? ClosedAtUtc { get; set; }
    public long Comments { get; set; }
}

internal sealed class MergeRow
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public long Iid { get; set; }
    public long AuthorUserId { get; set; }
    public string AuthorUsername { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string SourceBranch { get; set; } = "";
    public string TargetBranch { get; set; } = "";
    public string State { get; set; } = "opened";
    public string? MergeCommitSha { get; set; }
    public string? MergedByUsername { get; set; }
    public string CreatedAtUtc { get; set; } = "";
    public string UpdatedAtUtc { get; set; } = "";
    public string? ClosedAtUtc { get; set; }
    public long Comments { get; set; }
}

internal sealed class CommentRow
{
    public long Id { get; set; }
    public long AuthorUserId { get; set; }
    public string AuthorUsername { get; set; } = "";
    public string Body { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
}

internal sealed class AuditEventRow
{
    public long Id { get; set; }
    public long? ActorUserId { get; set; }
    public string? ActorUsername { get; set; }
    public string Action { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string? TargetId { get; set; }
    public string Detail { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
}
