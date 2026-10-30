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

    public Task<Issue> CreateIssueAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string title,
        string description = "",
        CancellationToken cancellationToken = default) =>
        CreateIssueAsync(actor, projectId, title, description, null, cancellationToken);

    /// <summary>
    /// Opens an issue. <paramref name="changes"/> may carry labels, a milestone, assignees, a due date and
    /// confidentiality to set at the same time; where the author may not organise the project those are
    /// still applied, because the author may always triage their own issue.
    /// </summary>
    public async Task<Issue> CreateIssueAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string title,
        string description,
        IssueChanges? changes,
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
            INSERT INTO Issues (ProjectId, Iid, AuthorUserId, Title, Description, State, CreatedAtUtc, UpdatedAtUtc, DueDate, Confidential)
            VALUES (@ProjectId, @Iid, @AuthorUserId, @Title, @Description, 'opened', @CreatedAtUtc, @CreatedAtUtc, @DueDate, @Confidential)
            RETURNING Id;
            """, new IssueInsertParameters(projectId, iid, author.UserId, cleanTitle, cleanDescription, now,
                changes is { DueDate.Set: true } ? Day(changes.DueDate.Value) : null,
                changes?.Confidential == true ? 1 : 0), transaction);
        List<NotificationDelivery> deliveries = [];
        if (changes is not null)
            deliveries.AddRange(await NotifyAddedAsync(db, transaction, author.UserId, projectId, CommentSubject.Issue, id,
                await ApplyPlanningAsync(db, transaction, projectId, CommentSubject.Issue, id, changes.Labels, changes.AddLabels,
                    changes.RemoveLabels, changes.MilestoneId, changes.Assignees, null, now), now));
        await AuditAsync(db, transaction, author.UserId, "open-issue", "issue",
            Reference(projectId, iid), changes?.Confidential == true ? "Confidential issue opened." : "Issue opened.", now);
        await transaction.CommitAsync(cancellationToken);
        await RaiseAsync(deliveries);
        return (await FindIssueAsync(actor, projectId, iid, cancellationToken))!;
    }

    /// <summary>
    /// Lists a project's issues, newest first. <paramref name="state"/> null means both.
    /// </summary>
    public Task<IssuePage> ListIssuesAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        CollaborationState? state,
        int skip,
        int take,
        CancellationToken cancellationToken = default) =>
        ListIssuesAsync(identity, projectId, new IssueFilter(state), skip, take, cancellationToken);

    /// <summary>
    /// Lists a project's issues, newest first, narrowed by <paramref name="filter"/>.
    ///
    /// <para>
    /// A confidential issue is listed for members and for its author and for nobody else, whatever the
    /// project's visibility. The public grant a public project gives a stranger reaches the repository and
    /// the ordinary conversation; it does not reach the issues somebody marked as not for strangers.
    /// </para>
    /// </summary>
    public async Task<IssuePage> ListIssuesAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        IssueFilter filter,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (skip < 0 || take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        PermissionDecision reader = await RequireReaderAsync(db, null, identity, projectId, cancellationToken);

        IssueQueryParameters parameters = IssueQuery(projectId, filter, reader, identity, take, skip);
        IEnumerable<IssueRow> rows = await db.QueryAsync<IssueRow>(IssueSelect + IssueWhere + """
            ORDER BY i.Iid DESC
            LIMIT @Take OFFSET @Skip;
            """, parameters);
        long total = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM Issues i " + IssueWhere, parameters);
        return new(await HydrateIssuesAsync(db, projectId, rows), total);
    }

    public async Task<Issue?> FindIssueAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        long iid,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        PermissionDecision reader = await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        IssueRow? row = await db.QuerySingleOrDefaultAsync<IssueRow>(IssueSelect +
            "WHERE i.ProjectId = @ProjectId AND i.Iid = @Iid;", new CollaborationItemParameters(projectId, iid));
        if (row is null || !MaySeeIssue(row, reader, identity)) return null;
        return (await HydrateIssuesAsync(db, projectId, [row]))[0];
    }

    /// <summary>A confidential issue is for members and its author. Everybody else is told it does not exist.</summary>
    private static bool MaySeeIssue(IssueRowBase row, PermissionDecision reader, AuthenticatedIdentity? identity) =>
        row.Confidential == 0 || reader.Grant == AccessGrant.Member || (identity is not null && identity.UserId == row.AuthorUserId);

    private static IssueQueryParameters IssueQuery(long projectId, IssueFilter filter, PermissionDecision reader,
        AuthenticatedIdentity? identity, int take, int skip)
    {
        string labels = string.Join(",", (filter.Labels ?? []).Select(Normalize).Where(name => name.Length > 0));
        int labelCount = labels.Length == 0 ? 0 : labels.Split(',').Distinct(StringComparer.Ordinal).Count();
        string without = string.Join(",", (filter.WithoutLabels ?? []).Select(Normalize).Where(name => name.Length > 0));
        long? milestoneIid = null;
        int milestoneMode = 0;
        if (filter.Milestone is { Length: > 0 } milestone)
        {
            if (string.Equals(milestone, IssueFilter.None, StringComparison.OrdinalIgnoreCase)) milestoneMode = 1;
            else if (long.TryParse(milestone, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)) { milestoneMode = 2; milestoneIid = parsed; }
            else throw new ArgumentException("milestone_id is a milestone id or None.", nameof(filter));
        }
        int assigneeMode = 0;
        string assignee = "";
        if (filter.Assignee is { Length: > 0 } who)
        {
            if (string.Equals(who, IssueFilter.None, StringComparison.OrdinalIgnoreCase)) assigneeMode = 1;
            else { assigneeMode = 2; assignee = Normalize(who); }
        }
        return new(projectId, StateName(filter.State), filter.State is null ? 1 : 0,
            "," + labels + ",", labelCount, "," + without + ",", without.Length == 0 ? 0 : 1,
            milestoneMode, milestoneIid ?? 0,
            assigneeMode, assignee,
            filter.Search is { Length: > 0 } ? "%" + filter.Search.Trim().ToLowerInvariant().Replace("%", "", StringComparison.Ordinal) + "%" : null,
            filter.Confidential is null ? -1 : filter.Confidential.Value ? 1 : 0,
            reader.Grant == AccessGrant.Member ? 1 : 0, identity?.UserId ?? 0,
            take, skip);
    }

    private const string IssueSelect = """
        SELECT i.Id, i.ProjectId, i.Iid, i.AuthorUserId, u.Username AS AuthorUsername, i.Title, i.Description,
               i.State, i.CreatedAtUtc, i.UpdatedAtUtc, i.ClosedAtUtc, i.DueDate, i.Confidential, i.TimeEstimateSeconds,
               (SELECT COALESCE(SUM(t.Seconds), 0) FROM TimeEntries t WHERE t.SubjectType = 'issue' AND t.SubjectId = i.Id) AS TimeSpentSeconds,
               i.MilestoneId, ms.Iid AS MilestoneIid, ms.Title AS MilestoneTitle,
               (SELECT COUNT(*) FROM Comments c WHERE c.SubjectType = 'issue' AND c.SubjectId = i.Id) AS Comments
        FROM Issues i
        JOIN Users u ON u.Id = i.AuthorUserId
        LEFT JOIN Milestones ms ON ms.Id = i.MilestoneId

        """;

    // Label filters use the comma-list trick: the wanted names arrive as one comma-bounded string, and a
    // label matches when its own name, comma-bounded, is a substring. Names cannot contain commas, which
    // RequireLabelName guarantees, so this is exact. It keeps the statement static, which Dapper.AOT needs.
    private const string IssueWhere = """
        WHERE i.ProjectId = @ProjectId AND (@AnyState = 1 OR i.State = @State)
          AND (@LabelCount = 0 OR (SELECT COUNT(*) FROM SubjectLabels sl JOIN Labels l ON l.Id = sl.LabelId
               WHERE sl.SubjectType = 'issue' AND sl.SubjectId = i.Id AND instr(@Labels, ',' || l.NormalizedName || ',') > 0) = @LabelCount)
          AND (@HasWithout = 0 OR (SELECT COUNT(*) FROM SubjectLabels sl JOIN Labels l ON l.Id = sl.LabelId
               WHERE sl.SubjectType = 'issue' AND sl.SubjectId = i.Id AND instr(@Without, ',' || l.NormalizedName || ',') > 0) = 0)
          AND (@MilestoneMode = 0 OR (@MilestoneMode = 1 AND i.MilestoneId IS NULL)
               OR (@MilestoneMode = 2 AND i.MilestoneId = @MilestoneIid))
          AND (@AssigneeMode = 0
               OR (@AssigneeMode = 1 AND NOT EXISTS (SELECT 1 FROM SubjectPeople sp WHERE sp.SubjectType = 'issue' AND sp.SubjectId = i.Id AND sp.Kind = 'assignee'))
               OR (@AssigneeMode = 2 AND EXISTS (SELECT 1 FROM SubjectPeople sp JOIN Users au ON au.Id = sp.UserId
                   WHERE sp.SubjectType = 'issue' AND sp.SubjectId = i.Id AND sp.Kind = 'assignee' AND au.NormalizedUsername = @Assignee)))
          AND (@Search IS NULL OR lower(i.Title) LIKE @Search OR lower(i.Description) LIKE @Search)
          AND (@ConfidentialFilter = -1 OR i.Confidential = @ConfidentialFilter)
          AND (@SeesConfidential = 1 OR i.Confidential = 0 OR i.AuthorUserId = @ViewerId)

        """;

    private static async Task<IReadOnlyList<Issue>> HydrateIssuesAsync(SqliteConnection db, long projectId, IEnumerable<IssueRow> rows)
    {
        IssueRow[] page = [.. rows];
        if (page.Length == 0) return [];
        ILookup<long, Label> labels = await LabelsBySubjectAsync(db, projectId, CommentSubject.Issue);
        ILookup<(long SubjectId, string Kind), Person> people = await PeopleBySubjectAsync(db, projectId, CommentSubject.Issue);
        return [.. page.Select(row => ToIssue(row) with
        {
            Labels = [.. labels[row.Id]],
            Assignees = [.. people[(row.Id, "assignee")]]
        })];
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
    public Task<Issue> UpdateIssueAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long iid,
        string? title,
        string? description,
        CollaborationState? state,
        CancellationToken cancellationToken = default) =>
        UpdateIssueAsync(actor, projectId, iid, title, description, state, null, cancellationToken);

    /// <summary>
    /// Edits an issue. Three rules apply to three kinds of change. The words and the state: the author or a
    /// Maintainer. Labels, milestone, assignees and due date: the author or anybody who could push, because
    /// triage is what a Developer does to other people's issues. Confidentiality: the author or a
    /// Maintainer, because it changes who may read the issue at all.
    /// </summary>
    public async Task<Issue> UpdateIssueAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long iid,
        string? title,
        string? description,
        CollaborationState? state,
        IssueChanges? changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (state is CollaborationState.Merged) throw new ArgumentException("An issue cannot be merged.", nameof(state));
        string? cleanTitle = title is null ? null : RequireTitle(title);
        string? cleanDescription = description is null ? null : RequireBody(description, nameof(description), allowEmpty: true);
        bool wordsOrState = cleanTitle is not null || cleanDescription is not null || state is not null;

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity editor = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);
        IssueRow? existing = await db.QuerySingleOrDefaultAsync<IssueRow>(IssueSelect +
            "WHERE i.ProjectId = @ProjectId AND i.Iid = @Iid;", new CollaborationItemParameters(projectId, iid), transaction);
        if (existing is null) throw new CollaborationNotFoundException("Issue is unavailable.");
        // A confidential issue is unavailable to a reader who may not see it, before any rule about editing.
        PermissionDecision reader = await DecideAsync(db, transaction, editor, projectId, ProjectAction.ReadCollaboration, cancellationToken);
        if (!MaySeeIssue(existing, reader, editor)) throw new CollaborationNotFoundException("Issue is unavailable.");
        if (wordsOrState || changes?.Confidential is not null)
            await RequireEditorAsync(db, transaction, editor, projectId, existing.AuthorUserId,
                textChanged: cleanTitle is not null || cleanDescription is not null, cancellationToken);
        if (changes is { Any: true })
            await RequireTriagerAsync(db, transaction, editor, projectId, existing.AuthorUserId, cancellationToken);

        string now = Now();
        string nextState = state is null ? existing.State : StateName(state)!;
        await db.ExecuteAsync("""
            UPDATE Issues
            SET Title = COALESCE(@Title, Title),
                Description = COALESCE(@Description, Description),
                State = @State,
                ClosedAtUtc = CASE WHEN @State = 'closed' THEN COALESCE(ClosedAtUtc, @Now) ELSE NULL END,
                DueDate = CASE WHEN @SetDueDate = 1 THEN @DueDate ELSE DueDate END,
                Confidential = COALESCE(@Confidential, Confidential),
                UpdatedAtUtc = @Now
            WHERE Id = @Id;
            """, new IssueUpdateParameters(existing.Id, cleanTitle, cleanDescription, nextState, now,
                changes is { DueDate.Set: true } ? 1 : 0, changes is { DueDate.Set: true } ? Day(changes.DueDate.Value) : null,
                changes?.Confidential is null ? null : changes.Confidential.Value ? 1 : 0), transaction);
        List<NotificationDelivery> deliveries = [];
        if (changes is not null)
            deliveries.AddRange(await NotifyAddedAsync(db, transaction, editor.UserId, projectId, CommentSubject.Issue, existing.Id,
                await ApplyPlanningAsync(db, transaction, projectId, CommentSubject.Issue, existing.Id, changes.Labels, changes.AddLabels,
                    changes.RemoveLabels, changes.MilestoneId, changes.Assignees, null, now), now));
        if (nextState != existing.State)
        {
            await AuditAsync(db, transaction, editor.UserId, nextState == "closed" ? "close-issue" : "reopen-issue",
                "issue", Reference(projectId, iid), nextState == "closed" ? "Issue closed." : "Issue reopened.", now);
            deliveries.AddRange(await NotifyAsync(db, transaction, editor.UserId, projectId, CommentSubject.Issue, existing.Id,
                nextState == "closed" ? "closed" : "reopened",
                await ParticipantsAsync(db, transaction, CommentSubject.Issue, existing.Id, editor.UserId), now));
        }
        if (changes?.Confidential is bool confidential && confidential != (existing.Confidential != 0))
            await AuditAsync(db, transaction, editor.UserId, confidential ? "make-issue-confidential" : "make-issue-public",
                "issue", Reference(projectId, iid), confidential ? "Issue made confidential." : "Issue made visible to every reader.", now);
        await transaction.CommitAsync(cancellationToken);
        await RaiseAsync(deliveries);
        return await FindIssueAsync(actor, projectId, iid, cancellationToken)
            ?? throw new CollaborationNotFoundException("Issue is unavailable.");
    }

    public Task<MergeProposal> CreateMergeProposalAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string sourceBranch,
        string targetBranch,
        string title,
        string description = "",
        CancellationToken cancellationToken = default) =>
        CreateMergeProposalAsync(actor, projectId, sourceBranch, targetBranch, title, description, null, cancellationToken);

    public Task<MergeProposal> CreateMergeProposalAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string sourceBranch,
        string targetBranch,
        string title,
        string description,
        ProposalChanges? changes,
        CancellationToken cancellationToken = default) =>
        CreateMergeProposalAsync(actor, projectId, null, sourceBranch, targetBranch, title, description, changes, cancellationToken);

    /// <summary>
    /// Opens a proposal. <paramref name="sourceProjectId"/> names a fork the source branch lives in; then the
    /// author needs the permission to push on the fork and to read the target, and nothing more. That is
    /// the one way somebody outside a project's membership writes on it, and it is narrow on purpose: a
    /// proposal from a fork is a branch of work with a name on it, not an anonymous message.
    /// </summary>
    public async Task<MergeProposal> CreateMergeProposalAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long? sourceProjectId,
        string sourceBranch,
        string targetBranch,
        string title,
        string description,
        ProposalChanges? changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string source = RequireBranch(sourceBranch, nameof(sourceBranch));
        string target = RequireBranch(targetBranch, nameof(targetBranch));
        if (sourceProjectId is null && string.Equals(source, target, StringComparison.Ordinal))
            throw new ArgumentException("A merge proposal needs two different branches.", nameof(sourceBranch));
        if (sourceProjectId == projectId) sourceProjectId = null;
        string cleanTitle = RequireTitle(title);
        string cleanDescription = RequireBody(description, nameof(description), allowEmpty: true);

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity author;
        if (sourceProjectId is long fork)
        {
            ForkRow? relation = await db.QuerySingleOrDefaultAsync<ForkRow>(
                "SELECT Id, ForkedFromProjectId FROM Projects WHERE Id = @Id AND State = 'ready';", new IdParameters(fork), transaction);
            if (relation is null || relation.ForkedFromProjectId != projectId)
                throw new ArgumentException("source_project_id must name a fork of this project.", nameof(sourceProjectId));
            PermissionDecision push = await DecideAsync(db, transaction, actor, fork, ProjectAction.PushRepository, cancellationToken);
            if (!push.Allowed) throw new UnauthorizedAccessException("Proposing from a fork needs the permission to push to the fork.");
            PermissionDecision read = await DecideAsync(db, transaction, actor, projectId, ProjectAction.ReadCollaboration, cancellationToken);
            if (!read.Allowed) throw new UnauthorizedAccessException(read.Explanation);
            author = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        }
        else author = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);

        // One open proposal per pair of branches. A second would be two conversations about one change, and
        // whichever was merged would silently decide the other.
        long open = await db.QuerySingleAsync<long>("""
            SELECT COUNT(*) FROM MergeRequests
            WHERE ProjectId = @ProjectId AND SourceBranch = @Source AND TargetBranch = @Target AND State = 'opened'
              AND COALESCE(SourceProjectId, 0) = @SourceProjectId;
            """, new MergeBranchParameters(projectId, source, target, sourceProjectId ?? 0), transaction);
        if (open != 0)
            throw new CollaborationConflictException("An open merge proposal already exists for these branches.");

        string now = Now();
        long iid = await NextNumberAsync(db, transaction, CommentSubject.MergeRequest, projectId);
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO MergeRequests (ProjectId, Iid, AuthorUserId, Title, Description, SourceBranch, TargetBranch,
                                       State, CreatedAtUtc, UpdatedAtUtc, Draft, SourceProjectId)
            VALUES (@ProjectId, @Iid, @AuthorUserId, @Title, @Description, @SourceBranch, @TargetBranch,
                    'opened', @CreatedAtUtc, @CreatedAtUtc, @Draft, @SourceProjectId)
            RETURNING Id;
            """, new MergeInsertParameters(projectId, iid, author.UserId, cleanTitle, cleanDescription, source, target, now,
                changes?.Draft == true ? 1 : 0, sourceProjectId),
            transaction);
        List<NotificationDelivery> deliveries = [];
        if (changes is not null)
            deliveries.AddRange(await NotifyAddedAsync(db, transaction, author.UserId, projectId, CommentSubject.MergeRequest, id,
                await ApplyPlanningAsync(db, transaction, projectId, CommentSubject.MergeRequest, id, changes.Labels, changes.AddLabels,
                    changes.RemoveLabels, changes.MilestoneId, changes.Assignees, changes.Reviewers, now), now));
        await AuditAsync(db, transaction, author.UserId, "open-merge-request", "merge_request",
            Reference(projectId, iid), $"Merge proposed from {source} into {target}.", now);
        await transaction.CommitAsync(cancellationToken);
        await RaiseAsync(deliveries);
        return (await FindMergeProposalAsync(actor, projectId, iid, null, cancellationToken))!;
    }

    public Task<MergeProposalPage> ListMergeProposalsAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        CollaborationState? state,
        int skip,
        int take,
        CancellationToken cancellationToken = default) =>
        ListMergeProposalsAsync(identity, projectId, new MergeFilter(state), skip, take, cancellationToken);

    public async Task<MergeProposalPage> ListMergeProposalsAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        MergeFilter filter,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (skip < 0 || take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);

        string labels = string.Join(",", (filter.Labels ?? []).Select(Normalize).Where(name => name.Length > 0));
        int labelCount = labels.Length == 0 ? 0 : labels.Split(',').Distinct(StringComparer.Ordinal).Count();
        long? milestoneIid = null;
        int milestoneMode = 0;
        if (filter.Milestone is { Length: > 0 } milestone)
        {
            if (string.Equals(milestone, IssueFilter.None, StringComparison.OrdinalIgnoreCase)) milestoneMode = 1;
            else if (long.TryParse(milestone, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)) { milestoneMode = 2; milestoneIid = parsed; }
            else throw new ArgumentException("milestone_id is a milestone id or None.", nameof(filter));
        }
        int personMode = 0;
        string person = "";
        string personKind = "assignee";
        if (filter.Reviewer is { Length: > 0 } reviewer) { personKind = "reviewer"; (personMode, person) = PersonFilter(reviewer); }
        else if (filter.Assignee is { Length: > 0 } assignee) (personMode, person) = PersonFilter(assignee);

        MergeQueryParameters parameters = new(projectId, StateName(filter.State), filter.State is null ? 1 : 0,
            "," + labels + ",", labelCount, milestoneMode, milestoneIid ?? 0, personMode, person, personKind,
            filter.Search is { Length: > 0 } ? "%" + filter.Search.Trim().ToLowerInvariant().Replace("%", "", StringComparison.Ordinal) + "%" : null,
            take, skip);
        // No approvals here, for the same reason a listing does not decide mergeability: it would be a
        // second query per row to answer a question the list does not ask.
        IEnumerable<MergeRow> rows = await db.QueryAsync<MergeRow>(MergeSelect + MergeWhere + """
            ORDER BY m.Iid DESC
            LIMIT @Take OFFSET @Skip;
            """, parameters);
        long total = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM MergeRequests m " + MergeWhere, parameters);
        return new(await HydrateProposalsAsync(db, projectId, rows), total);
    }

    private static (int Mode, string Name) PersonFilter(string value) =>
        string.Equals(value, IssueFilter.None, StringComparison.OrdinalIgnoreCase) ? (1, "") : (2, Normalize(value));

    private const string MergeSelect = """
        SELECT m.Id, m.ProjectId, m.Iid, m.AuthorUserId, a.Username AS AuthorUsername, m.Title, m.Description,
               m.SourceBranch, m.TargetBranch, m.State, m.MergeCommitSha, g.Username AS MergedByUsername,
               m.SourceProjectId, sp.Slug AS SourceSlug, su.Username AS SourceNamespace,
               m.CreatedAtUtc, m.UpdatedAtUtc, m.ClosedAtUtc, m.Draft, m.TimeEstimateSeconds,
               (SELECT COALESCE(SUM(t.Seconds), 0) FROM TimeEntries t WHERE t.SubjectType = 'merge_request' AND t.SubjectId = m.Id) AS TimeSpentSeconds,
               m.MilestoneId, ms.Iid AS MilestoneIid, ms.Title AS MilestoneTitle,
               (SELECT COUNT(*) FROM Comments c WHERE c.SubjectType = 'merge_request' AND c.SubjectId = m.Id) AS Comments,
               p.ApprovalsRequired
        FROM MergeRequests m
        JOIN Users a ON a.Id = m.AuthorUserId
        JOIN Projects p ON p.Id = m.ProjectId
        LEFT JOIN Users g ON g.Id = m.MergedByUserId
        LEFT JOIN Milestones ms ON ms.Id = m.MilestoneId
        LEFT JOIN Projects sp ON sp.Id = m.SourceProjectId
        LEFT JOIN Users su ON su.Id = sp.OwnerUserId

        """;

    private const string MergeWhere = """
        WHERE m.ProjectId = @ProjectId AND (@AnyState = 1 OR m.State = @State)
          AND (@LabelCount = 0 OR (SELECT COUNT(*) FROM SubjectLabels sl JOIN Labels l ON l.Id = sl.LabelId
               WHERE sl.SubjectType = 'merge_request' AND sl.SubjectId = m.Id AND instr(@Labels, ',' || l.NormalizedName || ',') > 0) = @LabelCount)
          AND (@MilestoneMode = 0 OR (@MilestoneMode = 1 AND m.MilestoneId IS NULL)
               OR (@MilestoneMode = 2 AND m.MilestoneId = @MilestoneIid))
          AND (@PersonMode = 0
               OR (@PersonMode = 1 AND NOT EXISTS (SELECT 1 FROM SubjectPeople sp WHERE sp.SubjectType = 'merge_request' AND sp.SubjectId = m.Id AND sp.Kind = @PersonKind))
               OR (@PersonMode = 2 AND EXISTS (SELECT 1 FROM SubjectPeople sp JOIN Users pu ON pu.Id = sp.UserId
                   WHERE sp.SubjectType = 'merge_request' AND sp.SubjectId = m.Id AND sp.Kind = @PersonKind AND pu.NormalizedUsername = @Person)))
          AND (@Search IS NULL OR lower(m.Title) LIKE @Search OR lower(m.Description) LIKE @Search)

        """;

    private static async Task<IReadOnlyList<MergeProposal>> HydrateProposalsAsync(SqliteConnection db, long projectId, IEnumerable<MergeRow> rows)
    {
        MergeRow[] page = [.. rows];
        if (page.Length == 0) return [];
        ILookup<long, Label> labels = await LabelsBySubjectAsync(db, projectId, CommentSubject.MergeRequest);
        ILookup<(long SubjectId, string Kind), Person> people = await PeopleBySubjectAsync(db, projectId, CommentSubject.MergeRequest);
        return [.. page.Select(row => ToProposal(row) with
        {
            Labels = [.. labels[row.Id]],
            Assignees = [.. people[(row.Id, "assignee")]],
            Reviewers = [.. people[(row.Id, "reviewer")]]
        })];
    }

    /// <summary>
    /// Reads one proposal, with everyone who has approved it. <paramref name="sourceCommitSha"/> is the
    /// branch tip the caller has just resolved; an approval recorded against that commit is current and
    /// any other is stale. Passing null leaves every approval stale, which is the safe reading for a
    /// caller that does not know where the branch is.
    /// </summary>
    public async Task<MergeProposal?> FindMergeProposalAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        long iid,
        string? sourceCommitSha = null,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        MergeRow? row = await db.QuerySingleOrDefaultAsync<MergeRow>(MergeSelect +
            "WHERE m.ProjectId = @ProjectId AND m.Iid = @Iid;", new CollaborationItemParameters(projectId, iid));
        if (row is null) return null;

        IEnumerable<ApprovalRow> approvals = await db.QueryAsync<ApprovalRow>("""
            SELECT a.UserId, u.Username, a.SourceCommitSha, a.CreatedAtUtc
            FROM MergeRequestApprovals a
            JOIN Users u ON u.Id = a.UserId
            WHERE a.MergeRequestId = @Id
            ORDER BY a.CreatedAtUtc;
            """, new IdParameters(row.Id));
        return (await HydrateProposalsAsync(db, projectId, [row]))[0] with
        {
            Approvals = [.. approvals.Select(approval => new Approval(approval.UserId, approval.Username,
                approval.SourceCommitSha,
                sourceCommitSha is not null && string.Equals(approval.SourceCommitSha, sourceCommitSha, StringComparison.Ordinal),
                Instant(approval.CreatedAtUtc)))]
        };
    }

    /// <summary>
    /// Edits a merge proposal's text or closes and reopens it. The branches are fixed once proposed: a
    /// proposal that could be repointed would invalidate every comment made about it.
    /// </summary>
    public Task<MergeProposal> UpdateMergeProposalAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long iid,
        string? title,
        string? description,
        CollaborationState? state,
        CancellationToken cancellationToken = default) =>
        UpdateMergeProposalAsync(actor, projectId, iid, title, description, state, null, cancellationToken);

    /// <summary>
    /// The same three rules as an issue. The draft flag follows the words: the author or a Maintainer,
    /// because saying a proposal is ready is the author's call and overriding it is a project decision.
    /// </summary>
    public async Task<MergeProposal> UpdateMergeProposalAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long iid,
        string? title,
        string? description,
        CollaborationState? state,
        ProposalChanges? changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (state is CollaborationState.Merged)
            throw new ArgumentException("Merging is done by merging, not by setting a state.", nameof(state));
        string? cleanTitle = title is null ? null : RequireTitle(title);
        string? cleanDescription = description is null ? null : RequireBody(description, nameof(description), allowEmpty: true);
        bool wordsOrState = cleanTitle is not null || cleanDescription is not null || state is not null || changes?.Draft is not null;

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity editor = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);
        CollaborationOwnerRow? existing = await db.QuerySingleOrDefaultAsync<CollaborationOwnerRow>(
            "SELECT Id, AuthorUserId, State FROM MergeRequests WHERE ProjectId = @ProjectId AND Iid = @Iid;",
            new CollaborationItemParameters(projectId, iid), transaction);
        if (existing is null) throw new CollaborationNotFoundException("Merge proposal is unavailable.");
        if (existing.State == "merged")
            throw new CollaborationConflictException("A merged proposal cannot be changed: its work is already in the target.");
        if (wordsOrState)
            await RequireEditorAsync(db, transaction, editor, projectId, existing.AuthorUserId,
                textChanged: cleanTitle is not null || cleanDescription is not null, cancellationToken);
        if (changes is { Any: true })
            await RequireTriagerAsync(db, transaction, editor, projectId, existing.AuthorUserId, cancellationToken);

        string now = Now();
        string nextState = state is null ? existing.State : StateName(state)!;
        await db.ExecuteAsync("""
            UPDATE MergeRequests
            SET Title = COALESCE(@Title, Title),
                Description = COALESCE(@Description, Description),
                State = @State,
                ClosedAtUtc = CASE WHEN @State = 'closed' THEN COALESCE(ClosedAtUtc, @Now) ELSE NULL END,
                Draft = COALESCE(@Draft, Draft),
                UpdatedAtUtc = @Now
            WHERE Id = @Id;
            """, new MergeUpdateParameters(existing.Id, cleanTitle, cleanDescription, nextState, now,
                changes?.Draft is null ? null : changes.Draft.Value ? 1 : 0), transaction);
        List<NotificationDelivery> deliveries = [];
        if (changes is not null)
            deliveries.AddRange(await NotifyAddedAsync(db, transaction, editor.UserId, projectId, CommentSubject.MergeRequest, existing.Id,
                await ApplyPlanningAsync(db, transaction, projectId, CommentSubject.MergeRequest, existing.Id, changes.Labels, changes.AddLabels,
                    changes.RemoveLabels, changes.MilestoneId, changes.Assignees, changes.Reviewers, now), now));
        if (nextState != existing.State)
        {
            await AuditAsync(db, transaction, editor.UserId,
                nextState == "closed" ? "close-merge-request" : "reopen-merge-request", "merge_request",
                Reference(projectId, iid), nextState == "closed" ? "Merge proposal closed." : "Merge proposal reopened.", now);
            deliveries.AddRange(await NotifyAsync(db, transaction, editor.UserId, projectId, CommentSubject.MergeRequest, existing.Id,
                nextState == "closed" ? "closed" : "reopened",
                await ParticipantsAsync(db, transaction, CommentSubject.MergeRequest, existing.Id, editor.UserId), now));
        }
        await transaction.CommitAsync(cancellationToken);
        await RaiseAsync(deliveries);
        return await FindMergeProposalAsync(actor, projectId, iid, null, cancellationToken)
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
        CollaborationOwnerRow merged = (await db.QuerySingleOrDefaultAsync<CollaborationOwnerRow>(
            "SELECT Id, AuthorUserId, State FROM MergeRequests WHERE ProjectId = @ProjectId AND Iid = @Iid;",
            new CollaborationItemParameters(projectId, iid), transaction))!;
        List<NotificationDelivery> deliveries = await NotifyAsync(db, transaction, actor.UserId, projectId, CommentSubject.MergeRequest, merged.Id,
            "merged", await ParticipantsAsync(db, transaction, CommentSubject.MergeRequest, merged.Id, actor.UserId), now);
        await transaction.CommitAsync(cancellationToken);
        await RaiseAsync(deliveries);
        return await FindMergeProposalAsync(actor, projectId, iid, null, cancellationToken)
            ?? throw new CollaborationNotFoundException("Merge proposal is unavailable.");
    }

    /// <summary>
    /// Adds a comment. The four anchor arguments are given together or not at all: all four present means
    /// a comment on one line of one file as it looked at <paramref name="anchorCommitSha"/>, and all four
    /// absent means a comment on the whole thing.
    ///
    /// <para>
    /// The store does not check that the path or the line exists in that commit. It cannot: that is a
    /// question for the Git engine, and the caller has just computed the diff the reader clicked on.
    /// </para>
    /// </summary>
    /// <summary>
    /// Records or withdraws one person's approval of a merge proposal.
    ///
    /// <para>
    /// Approving needs the permission to push, not merely to comment. An approval is what permits a merge
    /// where a project requires one, so it needs the same standing as merging; a Reporter can still say so
    /// in a comment, which is an opinion rather than a gate.
    /// </para>
    ///
    /// <para>
    /// Nobody approves their own proposal. A gate the author can satisfy alone is not a gate, and the
    /// refusal is worth making explicit rather than leaving as a convention.
    /// </para>
    /// </summary>
    public async Task<MergeProposal> SetApprovalAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long iid,
        bool approved,
        string sourceCommitSha,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (approved && (sourceCommitSha.Length != 40 ||
            !sourceCommitSha.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("An approval records the commit it approved.", nameof(sourceCommitSha));

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId,
            ProjectAction.PushRepository, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);
        AuthenticatedIdentity reviewer = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);

        CollaborationOwnerRow? existing = await db.QuerySingleOrDefaultAsync<CollaborationOwnerRow>(
            "SELECT Id, AuthorUserId, State FROM MergeRequests WHERE ProjectId = @ProjectId AND Iid = @Iid;",
            new CollaborationItemParameters(projectId, iid), transaction);
        if (existing is null) throw new CollaborationNotFoundException("Merge proposal is unavailable.");
        if (existing.State != "opened")
            throw new CollaborationConflictException("Only an open proposal can be approved.");
        if (approved && existing.AuthorUserId == reviewer.UserId)
            throw new CollaborationConflictException("An author cannot approve their own proposal.");

        string now = Now();
        if (approved)
        {
            await db.ExecuteAsync("""
                INSERT INTO MergeRequestApprovals (MergeRequestId, UserId, SourceCommitSha, CreatedAtUtc)
                VALUES (@MergeRequestId, @UserId, @SourceCommitSha, @CreatedAtUtc)
                ON CONFLICT(MergeRequestId, UserId) DO UPDATE SET
                    SourceCommitSha = excluded.SourceCommitSha, CreatedAtUtc = excluded.CreatedAtUtc;
                """, new ApprovalParameters(existing.Id, reviewer.UserId, sourceCommitSha, now), transaction);
        }
        else
        {
            await db.ExecuteAsync("DELETE FROM MergeRequestApprovals WHERE MergeRequestId = @ProjectId AND UserId = @Iid;",
                new CollaborationItemParameters(existing.Id, reviewer.UserId), transaction);
        }
        await AuditAsync(db, transaction, reviewer.UserId, approved ? "approve-merge-request" : "unapprove-merge-request",
            "merge_request", Reference(projectId, iid),
            approved ? $"Approved at {sourceCommitSha}." : "Approval withdrawn.", now);
        // The author hears about an approval; a withdrawal is the reviewer's own business until something
        // else happens to the proposal.
        List<NotificationDelivery> deliveries = approved
            ? await NotifyAsync(db, transaction, reviewer.UserId, projectId, CommentSubject.MergeRequest, existing.Id, "approved", [existing.AuthorUserId], now)
            : [];
        await transaction.CommitAsync(cancellationToken);
        await RaiseAsync(deliveries);
        return await FindMergeProposalAsync(actor, projectId, iid, sourceCommitSha, cancellationToken)
            ?? throw new CollaborationNotFoundException("Merge proposal is unavailable.");
    }

    /// <summary>
    /// How many more current approvals a proposal needs, or zero when it needs none. Read inside the merge
    /// path so the gate cannot be bypassed by a client that simply does not ask.
    /// </summary>
    public async Task<int> ApprovalsOutstandingAsync(
        long projectId,
        long iid,
        string sourceCommitSha,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        ApprovalCountRow? row = await db.QuerySingleOrDefaultAsync<ApprovalCountRow>("""
            SELECT p.ApprovalsRequired AS Required,
                   (SELECT COUNT(*) FROM MergeRequestApprovals a
                    WHERE a.MergeRequestId = m.Id AND a.SourceCommitSha = @Sha) AS Current
            FROM MergeRequests m
            JOIN Projects p ON p.Id = m.ProjectId
            WHERE m.ProjectId = @ProjectId AND m.Iid = @Iid;
            """, new ApprovalCountParameters(projectId, iid, sourceCommitSha));
        return row is null ? 0 : (int)Math.Max(0, row.Required - row.Current);
    }

    /// <summary>
    /// How many approvals this project requires. Takes no identity: it is a property of the project rather
    /// than of a caller, and whoever is being shown it has already been authorized to see the project.
    /// </summary>
    public async Task<int> GetApprovalsRequiredAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        return (int)await db.QuerySingleAsync<long>(
            "SELECT COALESCE((SELECT ApprovalsRequired FROM Projects WHERE Id = @Id), 0);",
            new IdParameters(projectId));
    }

    /// <summary>Whether this project requires a code owner's approval for every owned path a proposal changes.</summary>
    public async Task<bool> GetRequireCodeOwnerApprovalAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        return await db.QuerySingleAsync<long>(
            "SELECT COALESCE((SELECT RequireCodeOwnerApproval FROM Projects WHERE Id = @Id), 0);",
            new IdParameters(projectId)) != 0;
    }

    /// <summary>
    /// Turns the code owner rule on or off. Maintainer-equivalent access, like the approval count. With it
    /// on, every changed path that the repository's CODEOWNERS file names needs a current approval from one
    /// of the people it names; with no file, the rule requires nothing.
    /// </summary>
    public async Task SetRequireCodeOwnerApprovalAsync(
        AuthenticatedIdentity actor,
        long projectId,
        bool required,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId,
            ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);
        string now = Now();
        await db.ExecuteAsync("UPDATE Projects SET RequireCodeOwnerApproval = @Value WHERE Id = @Id;",
            new ProjectFlagParameters(projectId, required ? 1 : 0), transaction);
        await AuditAsync(db, transaction, actor.UserId, "set-code-owner-approval", "project",
            projectId.ToString(CultureInfo.InvariantCulture),
            required ? "Merge proposals now need a code owner's approval for every owned path." : "Code owner approval is no longer required.", now);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>The usernames whose approval stands against the given source tip. Read inside the merge path.</summary>
    public async Task<IReadOnlyList<string>> CurrentApproversAsync(
        long projectId,
        long iid,
        string sourceCommitSha,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        IEnumerable<ApproverRow> rows = await db.QueryAsync<ApproverRow>("""
            SELECT u.Username
            FROM MergeRequestApprovals a
            JOIN MergeRequests m ON m.Id = a.MergeRequestId
            JOIN Users u ON u.Id = a.UserId
            WHERE m.ProjectId = @ProjectId AND m.Iid = @Iid AND a.SourceCommitSha = @Sha;
            """, new ApprovalCountParameters(projectId, iid, sourceCommitSha));
        return [.. rows.Select(row => row.Username)];
    }

    /// <summary>
    /// Sets how many current approvals this project requires before a merge. Zero removes the gate.
    /// Maintainer-equivalent access, because it is a project rule rather than a review.
    /// </summary>
    public async Task SetApprovalsRequiredAsync(
        AuthenticatedIdentity actor,
        long projectId,
        int required,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (required is < 0 or > 10) throw new ArgumentOutOfRangeException(nameof(required));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId,
            ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);

        string now = Now();
        await db.ExecuteAsync("UPDATE Projects SET ApprovalsRequired = @Value WHERE Id = @Id;",
            new ProjectFlagParameters(projectId, required), transaction);
        await AuditAsync(db, transaction, actor.UserId, "set-approvals-required", "project",
            projectId.ToString(CultureInfo.InvariantCulture),
            $"Merge proposals now require {required.ToString(CultureInfo.InvariantCulture)} approvals.", now);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<Comment> AddCommentAsync(
        AuthenticatedIdentity actor,
        long projectId,
        CommentSubject subject,
        long iid,
        string body,
        string? path = null,
        int? line = null,
        DiffSide? side = null,
        string? anchorCommitSha = null,
        long? parentCommentId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string cleanBody = RequireBody(body, nameof(body), allowEmpty: false);
        bool anchored = path is not null || line is not null || side is not null || anchorCommitSha is not null;
        // A reply sits where its thread sits. It carries no anchor of its own, because a reply anchored to a
        // different line would be a new thread pretending to be an answer.
        if (anchored && parentCommentId is not null)
            throw new ArgumentException("A reply follows its thread and cannot name a line of its own.", nameof(parentCommentId));
        if (anchored)
        {
            if (path is null || line is null || side is null || anchorCommitSha is null)
                throw new ArgumentException("A comment on a line needs a path, a line, a side and the commit it was written against.", nameof(path));
            if (path.Length is 0 or > 1024) throw new ArgumentException("The path is out of range.", nameof(path));
            if (line is < 1 or > 10_000_000) throw new ArgumentException("The line is out of range.", nameof(line));
            if (anchorCommitSha.Length != 40 || !anchorCommitSha.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new ArgumentException("The anchor is a lowercase hexadecimal object id.", nameof(anchorCommitSha));
            if (subject != CommentSubject.MergeRequest)
                throw new ArgumentException("Only a merge proposal has a diff to comment on.", nameof(subject));
        }

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        long subjectId = await ResolveSubjectAsync(db, transaction, projectId, subject, iid);
        // The author of a proposal from a fork is not a member and may still talk about their own
        // proposal: a review they cannot answer would be a review they cannot pass. Anybody else needs
        // membership, as every other write does.
        AuthenticatedIdentity author;
        PermissionDecision participate = await DecideAsync(db, transaction, actor, projectId, ProjectAction.WriteCollaboration, cancellationToken);
        if (participate.Allowed) author = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        else
        {
            long own = subject == CommentSubject.MergeRequest
                ? await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM MergeRequests WHERE Id = @Id AND AuthorUserId = @ProjectId AND SourceProjectId IS NOT NULL;",
                    new ScopedIdParameters(subjectId, actor.UserId), transaction)
                : 0;
            if (own == 0) throw new UnauthorizedAccessException(participate.Explanation);
            PermissionDecision read = await DecideAsync(db, transaction, actor, projectId, ProjectAction.ReadCollaboration, cancellationToken);
            if (!read.Allowed) throw new UnauthorizedAccessException(read.Explanation);
            author = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        }
        // A member of any role may see a confidential issue, so a participant always may; the check matters
        // for the author rule only in that it cannot be reached by somebody the listing conceals it from.

        // The parent must be on this same issue or proposal, and a reply to a reply attaches to the thread's
        // first comment, which keeps every thread exactly one level deep however it was addressed.
        if (parentCommentId is not null)
        {
            CommentThreadRow? parent = await db.QuerySingleOrDefaultAsync<CommentThreadRow>("""
                SELECT Id, ParentCommentId, ResolvedAtUtc FROM Comments
                WHERE Id = @Id AND ProjectId = @ProjectId AND SubjectType = @SubjectType AND SubjectId = @SubjectId;
                """, new CommentThreadParameters(parentCommentId.Value, projectId, SubjectName(subject), subjectId), transaction);
            if (parent is null) throw new CollaborationNotFoundException("The comment being answered is unavailable.");
            parentCommentId = parent.ParentCommentId ?? parent.Id;
        }

        string now = Now();
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO Comments (ProjectId, SubjectType, SubjectId, AuthorUserId, Body, CreatedAtUtc,
                                  Path, Line, Side, AnchorCommitSha, ParentCommentId)
            VALUES (@ProjectId, @SubjectType, @SubjectId, @AuthorUserId, @Body, @CreatedAtUtc,
                    @Path, @Line, @Side, @AnchorCommitSha, @ParentCommentId)
            RETURNING Id;
            """, new CommentInsertParameters(projectId, SubjectName(subject), subjectId, author.UserId, cleanBody, now,
                path, line, side is null ? null : SideName(side.Value), anchorCommitSha, parentCommentId),
            transaction);
        // The subject's own timestamp moves, so a list ordered by activity is possible later and a reader
        // can see that a closed issue is still being discussed.
        await db.ExecuteAsync(subject == CommentSubject.Issue
            ? "UPDATE Issues SET UpdatedAtUtc = @UpdatedAtUtc WHERE Id = @Id;"
            : "UPDATE MergeRequests SET UpdatedAtUtc = @UpdatedAtUtc WHERE Id = @Id;",
            new TouchParameters(subjectId, now), transaction);
        // Everybody part of the conversation hears a new comment, except whoever just made it. The
        // participants are read after the insert, so the author of this comment is now one of them for
        // the next comment and not for this one.
        List<NotificationDelivery> deliveries = await NotifyAsync(db, transaction, author.UserId, projectId, subject, subjectId, "commented",
            await ParticipantsAsync(db, transaction, subject, subjectId, author.UserId), now);
        await transaction.CommitAsync(cancellationToken);
        await RaiseAsync(deliveries);
        return new(id, author.UserId, author.Username, cleanBody, Instant(now), path, line, side, anchorCommitSha,
            parentCommentId);
    }

    /// <summary>
    /// Changes a comment's words. The author, or a Maintainer, exactly as for deleting one; the edit is
    /// recorded as having happened, because a comment rewritten silently after it was answered would make
    /// the answers to it read as answers to something else.
    /// </summary>
    public async Task<Comment> EditCommentAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long commentId,
        string body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string cleanBody = RequireBody(body, nameof(body), allowEmpty: false);

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity editor = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);
        CommentOwnerRow? existing = await db.QuerySingleOrDefaultAsync<CommentOwnerRow>(
            "SELECT Id, AuthorUserId FROM Comments WHERE Id = @Id AND ProjectId = @ProjectId;",
            new CommentOwnerParameters(commentId, projectId), transaction);
        if (existing is null) throw new CollaborationNotFoundException("Comment is unavailable.");
        await RequireEditorAsync(db, transaction, editor, projectId, existing.AuthorUserId, textChanged: true, cancellationToken);

        string now = Now();
        await db.ExecuteAsync("UPDATE Comments SET Body = @Body, EditedAtUtc = @Now WHERE Id = @Id;",
            new CommentEditParameters(commentId, cleanBody, now), transaction);
        await AuditAsync(db, transaction, editor.UserId, "edit-comment", "comment",
            commentId.ToString(CultureInfo.InvariantCulture), "Comment edited.", now);
        await transaction.CommitAsync(cancellationToken);
        return await ReadCommentAsync(db, commentId) ?? throw new CollaborationNotFoundException("Comment is unavailable.");
    }

    /// <summary>
    /// Marks a thread settled, or reopens it. Addressed by any comment in the thread and applied to its
    /// first comment, which is where the flag lives.
    ///
    /// <para>
    /// Who may: anybody who could push, because resolving is a judgement that the point was dealt with and
    /// that is the standing a merge needs; and the person who started the thread, because they may withdraw
    /// their own point. A Reporter who commented cannot close somebody else's thread, which is the rule
    /// that stops a conversation being tidied away by the person it was about.
    /// </para>
    /// </summary>
    public async Task<Comment> SetThreadResolvedAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long commentId,
        bool resolved,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity resolver = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);
        CommentThreadOwnerRow? existing = await db.QuerySingleOrDefaultAsync<CommentThreadOwnerRow>("""
            SELECT root.Id, root.AuthorUserId, root.ResolvedAtUtc
            FROM Comments c
            JOIN Comments root ON root.Id = COALESCE(c.ParentCommentId, c.Id)
            WHERE c.Id = @Id AND c.ProjectId = @ProjectId;
            """, new CommentOwnerParameters(commentId, projectId), transaction);
        if (existing is null) throw new CollaborationNotFoundException("Comment is unavailable.");

        if (existing.AuthorUserId != resolver.UserId)
        {
            PermissionDecision permission = await DecideAsync(db, transaction, resolver, projectId,
                ProjectAction.PushRepository, cancellationToken);
            if (!permission.Allowed)
                throw new UnauthorizedAccessException("Resolving a thread needs the permission to push, or to have started it.");
        }

        string now = Now();
        if (resolved)
            await db.ExecuteAsync("""
                UPDATE Comments SET ResolvedAtUtc = COALESCE(ResolvedAtUtc, @Now),
                                    ResolvedByUserId = COALESCE(ResolvedByUserId, @UserId)
                WHERE Id = @Id;
                """, new ThreadResolveParameters(existing.Id, resolver.UserId, now), transaction);
        else
            await db.ExecuteAsync("UPDATE Comments SET ResolvedAtUtc = NULL, ResolvedByUserId = NULL WHERE Id = @Id;",
                new IdParameters(existing.Id), transaction);
        await AuditAsync(db, transaction, resolver.UserId, resolved ? "resolve-thread" : "unresolve-thread", "comment",
            existing.Id.ToString(CultureInfo.InvariantCulture), resolved ? "Thread resolved." : "Thread reopened.", now);
        await transaction.CommitAsync(cancellationToken);
        return await ReadCommentAsync(db, existing.Id) ?? throw new CollaborationNotFoundException("Comment is unavailable.");
    }

    /// <summary>One comment by row id, with no permission check: callers have already made one.</summary>
    private static async Task<Comment?> ReadCommentAsync(SqliteConnection db, long commentId)
    {
        CommentRow? row = await db.QuerySingleOrDefaultAsync<CommentRow>("""
            SELECT c.Id, c.AuthorUserId, u.Username AS AuthorUsername, c.Body, c.CreatedAtUtc,
                   c.Path, c.Line, c.Side, c.AnchorCommitSha, c.ParentCommentId, c.EditedAtUtc,
                   c.ResolvedAtUtc, r.Username AS ResolvedByUsername
            FROM Comments c
            JOIN Users u ON u.Id = c.AuthorUserId
            LEFT JOIN Users r ON r.Id = c.ResolvedByUserId
            WHERE c.Id = @Id;
            """, new IdParameters(commentId));
        return row is null ? null : ToComment(row);
    }

    /// <summary>Which issue or proposal a comment sits on, by its public number, or null for no such comment.</summary>
    public async Task<(CommentSubject Subject, long Iid)?> FindCommentSubjectAsync(
        long projectId,
        long commentId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        CommentHomeRow? row = await db.QuerySingleOrDefaultAsync<CommentHomeRow>("""
            SELECT c.SubjectType, COALESCE(i.Iid, m.Iid) AS Iid
            FROM Comments c
            LEFT JOIN Issues i ON c.SubjectType = 'issue' AND i.Id = c.SubjectId
            LEFT JOIN MergeRequests m ON c.SubjectType = 'merge_request' AND m.Id = c.SubjectId
            WHERE c.Id = @Id AND c.ProjectId = @ProjectId;
            """, new CommentOwnerParameters(commentId, projectId));
        if (row is null || row.Iid is null) return null;
        return (row.SubjectType == "issue" ? CommentSubject.Issue : CommentSubject.MergeRequest, row.Iid.Value);
    }

    /// <summary>
    /// Finds which issue or proposal a comment belongs to, or null when it is not on the subject named.
    /// Lets a route that addresses a comment beneath an issue refuse one that belongs to another.
    /// </summary>
    public async Task<bool> CommentBelongsToAsync(
        long projectId,
        CommentSubject subject,
        long iid,
        long commentId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        long subjectId;
        try { subjectId = await ResolveSubjectAsync(db, null, projectId, subject, iid); }
        catch (CollaborationNotFoundException) { return false; }
        return await db.QuerySingleAsync<long>("""
            SELECT COUNT(*) FROM Comments
            WHERE Id = @Id AND ProjectId = @ProjectId AND SubjectType = @SubjectType AND SubjectId = @SubjectId;
            """, new CommentThreadParameters(commentId, projectId, SubjectName(subject), subjectId)) == 1;
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
        PermissionDecision reader = await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        long subjectId = await ResolveSubjectAsync(db, null, projectId, subject, iid);
        // The conversation on a confidential issue is as confidential as the issue.
        if (subject == CommentSubject.Issue && reader.Grant != AccessGrant.Member)
        {
            IssueRow? issue = await db.QuerySingleOrDefaultAsync<IssueRow>(IssueSelect +
                "WHERE i.ProjectId = @ProjectId AND i.Iid = @Iid;", new CollaborationItemParameters(projectId, iid));
            if (issue is null || !MaySeeIssue(issue, reader, identity)) throw new CollaborationNotFoundException("Issue is unavailable.");
        }
        IEnumerable<CommentRow> rows = await db.QueryAsync<CommentRow>("""
            SELECT c.Id, c.AuthorUserId, u.Username AS AuthorUsername, c.Body, c.CreatedAtUtc,
                   c.Path, c.Line, c.Side, c.AnchorCommitSha, c.ParentCommentId, c.EditedAtUtc,
                   c.ResolvedAtUtc, r.Username AS ResolvedByUsername
            FROM Comments c
            JOIN Users u ON u.Id = c.AuthorUserId
            LEFT JOIN Users r ON r.Id = c.ResolvedByUserId
            WHERE c.SubjectType = @SubjectType AND c.SubjectId = @SubjectId
            ORDER BY c.Id
            LIMIT @Take OFFSET @Skip;
            """, new CommentListParameters(SubjectName(subject), subjectId, take, skip));
        return [.. rows.Select(ToComment)];
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

        // The first comment of a thread carries the replies to it. Removing it would remove, or orphan,
        // words other people wrote, so a thread with answers is kept; the author can edit it instead.
        long replies = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM Comments WHERE ParentCommentId = @Id;",
            new IdParameters(commentId), transaction);
        if (replies > 0)
            throw new CollaborationConflictException("This comment has replies, so it cannot be deleted; edit it instead.");

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

    private static async Task<PermissionDecision> RequireReaderAsync(
        SqliteConnection db,
        SqliteTransaction? transaction,
        AuthenticatedIdentity? identity,
        long projectId,
        CancellationToken cancellationToken)
    {
        PermissionDecision permission = await DecideAsync(db, transaction, identity, projectId,
            ProjectAction.ReadCollaboration, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);
        return permission;
    }

    private static async Task<AuthenticatedIdentity> RequireParticipantAsync(
        SqliteConnection db,
        SqliteTransaction? transaction,
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

    private static string SideName(DiffSide side) => side == DiffSide.Old ? "old" : "new";

    private static DiffSide? ParseSide(string? value) => value switch
    {
        "old" => DiffSide.Old,
        "new" => DiffSide.New,
        _ => null
    };

    private static Comment ToComment(CommentRow row) => new(
        row.Id, row.AuthorUserId, row.AuthorUsername, row.Body, Instant(row.CreatedAtUtc),
        row.Path, row.Line is null ? null : (int)row.Line.Value, ParseSide(row.Side), row.AnchorCommitSha,
        row.ParentCommentId,
        row.EditedAtUtc is null ? null : Instant(row.EditedAtUtc),
        row.ResolvedAtUtc is null ? null : Instant(row.ResolvedAtUtc),
        row.ResolvedByUsername);

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

    private static Issue ToIssue(IssueRowBase row) => new(
        row.Id, row.ProjectId, row.Iid, row.AuthorUserId, row.AuthorUsername, row.Title, row.Description,
        ParseState(row.State), Instant(row.CreatedAtUtc), Instant(row.UpdatedAtUtc),
        row.ClosedAtUtc is null ? null : Instant(row.ClosedAtUtc), row.Comments,
        Milestone: row.MilestoneId is null ? null : new MilestoneReference(row.MilestoneId.Value, row.MilestoneIid ?? 0, row.MilestoneTitle ?? ""),
        DueDate: ParseDay(row.DueDate),
        Confidential: row.Confidential != 0,
        TimeEstimateSeconds: row.TimeEstimateSeconds, TimeSpentSeconds: row.TimeSpentSeconds);

    private static MergeProposal ToProposal(MergeRowBase row) => new(
        row.Id, row.ProjectId, row.Iid, row.AuthorUserId, row.AuthorUsername, row.Title, row.Description,
        row.SourceBranch, row.TargetBranch, ParseState(row.State), row.MergeCommitSha, row.MergedByUsername,
        Instant(row.CreatedAtUtc), Instant(row.UpdatedAtUtc),
        row.ClosedAtUtc is null ? null : Instant(row.ClosedAtUtc), row.Comments,
        ApprovalsRequired: (int)row.ApprovalsRequired,
        Milestone: row.MilestoneId is null ? null : new MilestoneReference(row.MilestoneId.Value, row.MilestoneIid ?? 0, row.MilestoneTitle ?? ""),
        Draft: row.Draft != 0,
        TimeEstimateSeconds: row.TimeEstimateSeconds, TimeSpentSeconds: row.TimeSpentSeconds,
        SourceProjectId: row.SourceProjectId,
        SourceProjectPath: row.SourceProjectId is null ? null : row.SourceNamespace + "/" + row.SourceSlug);
}

/// <summary>The issue, proposal or comment named does not exist in this project.</summary>
public sealed class CollaborationNotFoundException(string message) : Exception(message);

/// <summary>The operation contradicts the current state: a merged proposal, or a duplicate of an open one.</summary>
public sealed class CollaborationConflictException(string message) : Exception(message);

internal sealed record IssueInsertParameters(long ProjectId, long Iid, long AuthorUserId, string Title,
    string Description, string CreatedAtUtc, string? DueDate, int Confidential);
internal sealed record MergeInsertParameters(long ProjectId, long Iid, long AuthorUserId, string Title,
    string Description, string SourceBranch, string TargetBranch, string CreatedAtUtc, int Draft, long? SourceProjectId);
internal sealed record IssueUpdateParameters(long Id, string? Title, string? Description, string State, string Now,
    int SetDueDate, string? DueDate, int? Confidential);
internal sealed record MergeUpdateParameters(long Id, string? Title, string? Description, string State, string Now, int? Draft);
internal sealed record IssueQueryParameters(long ProjectId, string? State, int AnyState, string Labels, int LabelCount,
    string Without, int HasWithout, int MilestoneMode, long MilestoneIid, int AssigneeMode, string Assignee, string? Search,
    int ConfidentialFilter, int SeesConfidential, long ViewerId, int Take, int Skip);
internal sealed record MergeQueryParameters(long ProjectId, string? State, int AnyState, string Labels, int LabelCount,
    int MilestoneMode, long MilestoneIid, int PersonMode, string Person, string PersonKind, string? Search, int Take, int Skip);
internal sealed record MergeRecordParameters(long ProjectId, long Iid, string Sha, long UserId, string Now);
internal sealed record MergeBranchParameters(long ProjectId, string Source, string Target, long SourceProjectId);
internal sealed class ForkRow { public long Id { get; set; } public long? ForkedFromProjectId { get; set; } }
internal sealed record CollaborationItemParameters(long ProjectId, long Iid);
internal sealed record CollaborationListParameters(long ProjectId, string? State, int AnyState, int Take, int Skip);
internal sealed record CommentInsertParameters(long ProjectId, string SubjectType, long SubjectId, long AuthorUserId,
    string Body, string CreatedAtUtc, string? Path, int? Line, string? Side, string? AnchorCommitSha, long? ParentCommentId);
internal sealed record CommentThreadParameters(long Id, long ProjectId, string SubjectType, long SubjectId);
internal sealed record CommentEditParameters(long Id, string Body, string Now);
internal sealed record ThreadResolveParameters(long Id, long UserId, string Now);
internal sealed record ApprovalParameters(long MergeRequestId, long UserId, string SourceCommitSha, string CreatedAtUtc);
internal sealed record ApprovalCountParameters(long ProjectId, long Iid, string Sha);
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

internal sealed class CommentThreadRow
{
    public long Id { get; set; }
    public long? ParentCommentId { get; set; }
    public string? ResolvedAtUtc { get; set; }
}

internal sealed class CommentThreadOwnerRow
{
    public long Id { get; set; }
    public long AuthorUserId { get; set; }
    public string? ResolvedAtUtc { get; set; }
}

internal sealed class CommentHomeRow
{
    public string SubjectType { get; set; } = "";
    public long? Iid { get; set; }
}

internal sealed class SubjectRow { public long Id { get; set; } }

internal sealed class IssueRow : IssueRowBase { }

internal class IssueRowBase
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
    public string? DueDate { get; set; }
    public long Confidential { get; set; }
    public long? MilestoneId { get; set; }
    public long? MilestoneIid { get; set; }
    public string? MilestoneTitle { get; set; }
    public long TimeEstimateSeconds { get; set; }
    public long TimeSpentSeconds { get; set; }
}

internal sealed class MergeRow : MergeRowBase { }

internal class MergeRowBase
{
    public long? SourceProjectId { get; set; }
    public string? SourceSlug { get; set; }
    public string? SourceNamespace { get; set; }
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
    public long ApprovalsRequired { get; set; }
    public long Draft { get; set; }
    public long? MilestoneId { get; set; }
    public long? MilestoneIid { get; set; }
    public string? MilestoneTitle { get; set; }
    public long TimeEstimateSeconds { get; set; }
    public long TimeSpentSeconds { get; set; }
}

internal sealed class CommentRow
{
    public long Id { get; set; }
    public long AuthorUserId { get; set; }
    public string AuthorUsername { get; set; } = "";
    public string Body { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
    public string? Path { get; set; }
    public long? Line { get; set; }
    public string? Side { get; set; }
    public string? AnchorCommitSha { get; set; }
    public long? ParentCommentId { get; set; }
    public string? EditedAtUtc { get; set; }
    public string? ResolvedAtUtc { get; set; }
    public string? ResolvedByUsername { get; set; }
}

internal sealed class ApprovalRow
{
    public long UserId { get; set; }
    public string Username { get; set; } = "";
    public string SourceCommitSha { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
}

internal sealed class ApproverRow { public string Username { get; set; } = ""; }

internal sealed class ApprovalCountRow
{
    public long Required { get; set; }
    public long Current { get; set; }
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
