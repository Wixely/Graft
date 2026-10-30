using System.Globalization;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>How a list of issues is narrowed. Every field is optional and they combine with AND.</summary>
public sealed record IssueFilter(
    CollaborationState? State = null,
    /// <summary>Every one of these labels, by name.</summary>
    IReadOnlyList<string>? Labels = null,
    /// <summary>None of these labels, by name. What a board's first column asks for.</summary>
    IReadOnlyList<string>? WithoutLabels = null,
    /// <summary>A milestone number, or <see cref="None"/> for issues with no milestone.</summary>
    string? Milestone = null,
    /// <summary>A username, or <see cref="None"/> for unassigned issues.</summary>
    string? Assignee = null,
    /// <summary>A substring of the title or description, matched without regard to case.</summary>
    string? Search = null,
    bool? Confidential = null)
{
    public const string None = "None";
}

/// <summary>
/// What an issue update may change beyond its words and state. <c>null</c> on a list leaves it alone;
/// <see cref="Labels"/> replaces the set where <see cref="AddLabels"/> and <see cref="RemoveLabels"/> adjust it.
/// </summary>
public sealed record IssueChanges(
    IReadOnlyList<string>? Labels = null,
    IReadOnlyList<string>? AddLabels = null,
    IReadOnlyList<string>? RemoveLabels = null,
    Change<long?> MilestoneId = default,
    IReadOnlyList<string>? Assignees = null,
    Change<DateOnly?> DueDate = default,
    bool? Confidential = null)
{
    public bool Any => Labels is not null || AddLabels is not null || RemoveLabels is not null ||
        MilestoneId.Set || Assignees is not null || DueDate.Set || Confidential is not null;
}

/// <summary>The same for a merge proposal, which has reviewers and a draft flag and no due date.</summary>
public sealed record ProposalChanges(
    IReadOnlyList<string>? Labels = null,
    IReadOnlyList<string>? AddLabels = null,
    IReadOnlyList<string>? RemoveLabels = null,
    Change<long?> MilestoneId = default,
    IReadOnlyList<string>? Assignees = null,
    IReadOnlyList<string>? Reviewers = null,
    bool? Draft = null)
{
    public bool Any => Labels is not null || AddLabels is not null || RemoveLabels is not null ||
        MilestoneId.Set || Assignees is not null || Reviewers is not null || Draft is not null;
}

/// <summary>
/// Planning: labels, milestones, the people on a piece of work, and boards.
///
/// <para>
/// The permission rule throughout is that a Reporter files and a Developer organises. Creating a label or a
/// milestone, putting an issue in one, assigning somebody: each needs the permission to push, or to be the
/// author of the issue in question. Marking an issue confidential changes who can read it, so it follows
/// the stricter rule the words follow: the author or a Maintainer.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    private const int MaximumLabelLength = 255;
    private const int MaximumPeople = 20;
    private const int MaximumBoardLists = 20;

    // ---------------------------------------------------------------------------------------------------
    // Labels
    // ---------------------------------------------------------------------------------------------------

    public async Task<Label> CreateLabelAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string name,
        string color,
        string description = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string cleanName = RequireLabelName(name);
        string cleanColor = RequireColor(color);
        string cleanDescription = RequireBody(description, nameof(description), allowEmpty: true);
        if (cleanDescription.Length > MaximumLabelLength)
            throw new ArgumentException("A label description is at most 255 characters.", nameof(description));

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequireOrganiserAsync(db, transaction, actor, projectId, cancellationToken);

        long existing = await db.QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM Labels WHERE ProjectId = @ProjectId AND NormalizedName = @Name;",
            new LabelLookupParameters(projectId, Normalize(cleanName)), transaction);
        if (existing != 0) throw new CollaborationConflictException("A label with this name already exists.");

        string now = Now();
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO Labels (ProjectId, Name, NormalizedName, Color, Description, CreatedAtUtc)
            VALUES (@ProjectId, @Name, @NormalizedName, @Color, @Description, @CreatedAtUtc)
            RETURNING Id;
            """, new LabelInsertParameters(projectId, cleanName, Normalize(cleanName), cleanColor, cleanDescription, now), transaction);
        await AuditAsync(db, transaction, actor.UserId, "create-label", "label",
            id.ToString(CultureInfo.InvariantCulture), $"Label '{cleanName}' created.", now);
        await transaction.CommitAsync(cancellationToken);
        return new(id, cleanName, cleanColor, cleanDescription);
    }

    public async Task<IReadOnlyList<Label>> ListLabelsAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        IEnumerable<LabelRow> rows = await db.QueryAsync<LabelRow>(
            "SELECT Id, Name, Color, Description FROM Labels WHERE ProjectId = @Id ORDER BY NormalizedName;",
            new IdParameters(projectId));
        return [.. rows.Select(ToLabel)];
    }

    public async Task<Label> UpdateLabelAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long labelId,
        string? name,
        string? color,
        string? description,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string? cleanName = name is null ? null : RequireLabelName(name);
        string? cleanColor = color is null ? null : RequireColor(color);
        if (description is { Length: > MaximumLabelLength })
            throw new ArgumentException("A label description is at most 255 characters.", nameof(description));

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequireOrganiserAsync(db, transaction, actor, projectId, cancellationToken);
        LabelRow? existing = await db.QuerySingleOrDefaultAsync<LabelRow>(
            "SELECT Id, Name, Color, Description FROM Labels WHERE Id = @Id AND ProjectId = @ProjectId;",
            new ScopedIdParameters(labelId, projectId), transaction);
        if (existing is null) throw new CollaborationNotFoundException("Label is unavailable.");
        if (cleanName is not null)
        {
            long clash = await db.QuerySingleAsync<long>(
                "SELECT COUNT(*) FROM Labels WHERE ProjectId = @ProjectId AND NormalizedName = @Name AND Id <> @Id;",
                new LabelClashParameters(projectId, Normalize(cleanName), labelId), transaction);
            if (clash != 0) throw new CollaborationConflictException("A label with this name already exists.");
        }

        await db.ExecuteAsync("""
            UPDATE Labels
            SET Name = COALESCE(@Name, Name), NormalizedName = COALESCE(@NormalizedName, NormalizedName),
                Color = COALESCE(@Color, Color), Description = COALESCE(@Description, Description)
            WHERE Id = @Id;
            """, new LabelUpdateParameters(labelId, cleanName, cleanName is null ? null : Normalize(cleanName), cleanColor, description), transaction);
        await AuditAsync(db, transaction, actor.UserId, "update-label", "label",
            labelId.ToString(CultureInfo.InvariantCulture), "Label changed.", Now());
        await transaction.CommitAsync(cancellationToken);
        return new(labelId, cleanName ?? existing.Name, cleanColor ?? existing.Color, description ?? existing.Description);
    }

    /// <summary>Removes a label from the project and from everything it was on.</summary>
    public async Task DeleteLabelAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long labelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequireOrganiserAsync(db, transaction, actor, projectId, cancellationToken);
        // Explicit rather than cascaded, for the reason project deletion gives: whether the provider enforces
        // foreign keys is a pragma, and a dangling label on an issue would be shown as nothing.
        await db.ExecuteAsync("DELETE FROM BoardLists WHERE LabelId = @Id;", new IdParameters(labelId), transaction);
        await db.ExecuteAsync("DELETE FROM SubjectLabels WHERE LabelId = @Id;", new IdParameters(labelId), transaction);
        int changed = await db.ExecuteAsync("DELETE FROM Labels WHERE Id = @Id AND ProjectId = @ProjectId;",
            new ScopedIdParameters(labelId, projectId), transaction);
        if (changed != 1) throw new CollaborationNotFoundException("Label is unavailable.");
        await AuditAsync(db, transaction, actor.UserId, "delete-label", "label",
            labelId.ToString(CultureInfo.InvariantCulture), "Label deleted.", Now());
        await transaction.CommitAsync(cancellationToken);
    }

    // ---------------------------------------------------------------------------------------------------
    // Milestones
    // ---------------------------------------------------------------------------------------------------

    public async Task<Milestone> CreateMilestoneAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string title,
        string description = "",
        DateOnly? dueDate = null,
        DateOnly? startDate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string cleanTitle = RequireTitle(title);
        string cleanDescription = RequireBody(description, nameof(description), allowEmpty: true);
        if (startDate is not null && dueDate is not null && startDate > dueDate)
            throw new ArgumentException("A milestone cannot start after it is due.", nameof(startDate));

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequireOrganiserAsync(db, transaction, actor, projectId, cancellationToken);

        string now = Now();
        long iid = await db.QuerySingleAsync<long>("SELECT COALESCE(MAX(Iid), 0) + 1 FROM Milestones WHERE ProjectId = @Id;",
            new IdParameters(projectId), transaction);
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO Milestones (ProjectId, Iid, Title, Description, State, DueDate, StartDate, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@ProjectId, @Iid, @Title, @Description, 'active', @DueDate, @StartDate, @Now, @Now)
            RETURNING Id;
            """, new MilestoneInsertParameters(projectId, iid, cleanTitle, cleanDescription, Day(dueDate), Day(startDate), now), transaction);
        await AuditAsync(db, transaction, actor.UserId, "create-milestone", "milestone",
            Reference(projectId, iid), $"Milestone '{cleanTitle}' created.", now);
        await transaction.CommitAsync(cancellationToken);
        return new(id, projectId, iid, cleanTitle, cleanDescription, MilestoneState.Active, dueDate, startDate, Instant(now), Instant(now));
    }

    public async Task<IReadOnlyList<Milestone>> ListMilestonesAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        MilestoneState? state,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        IEnumerable<MilestoneRow> rows = await db.QueryAsync<MilestoneRow>(MilestoneSelect + """
            WHERE m.ProjectId = @ProjectId AND (@AnyState = 1 OR m.State = @State)
            ORDER BY CASE WHEN m.DueDate IS NULL THEN 1 ELSE 0 END, m.DueDate, m.Iid DESC;
            """, new MilestoneListParameters(projectId, state is null ? "" : MilestoneStateName(state.Value), state is null ? 1 : 0));
        return [.. rows.Select(ToMilestone)];
    }

    public async Task<Milestone?> FindMilestoneAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        long milestoneId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        MilestoneRow? row = await db.QuerySingleOrDefaultAsync<MilestoneRow>(MilestoneSelect +
            "WHERE m.ProjectId = @ProjectId AND m.Id = @Id;", new MilestoneKeyParameters(projectId, milestoneId));
        return row is null ? null : ToMilestone(row);
    }

    public async Task<Milestone> UpdateMilestoneAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long milestoneId,
        string? title,
        string? description,
        MilestoneState? state,
        Change<DateOnly?> dueDate = default,
        Change<DateOnly?> startDate = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string? cleanTitle = title is null ? null : RequireTitle(title);
        string? cleanDescription = description is null ? null : RequireBody(description, nameof(description), allowEmpty: true);

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequireOrganiserAsync(db, transaction, actor, projectId, cancellationToken);
        MilestoneRow? existing = await db.QuerySingleOrDefaultAsync<MilestoneRow>(MilestoneSelect +
            "WHERE m.ProjectId = @ProjectId AND m.Id = @Id;", new MilestoneKeyParameters(projectId, milestoneId), transaction);
        if (existing is null) throw new CollaborationNotFoundException("Milestone is unavailable.");

        string? due = dueDate.Set ? Day(dueDate.Value) : existing.DueDate;
        string? start = startDate.Set ? Day(startDate.Value) : existing.StartDate;
        if (due is not null && start is not null && string.CompareOrdinal(start, due) > 0)
            throw new ArgumentException("A milestone cannot start after it is due.", nameof(startDate));

        string now = Now();
        await db.ExecuteAsync("""
            UPDATE Milestones
            SET Title = COALESCE(@Title, Title), Description = COALESCE(@Description, Description),
                State = @State, DueDate = @DueDate, StartDate = @StartDate, UpdatedAtUtc = @Now
            WHERE Id = @Id;
            """, new MilestoneUpdateParameters(existing.Id, cleanTitle, cleanDescription,
                state is null ? existing.State : MilestoneStateName(state.Value), due, start, now), transaction);
        if (state is not null && MilestoneStateName(state.Value) != existing.State)
            await AuditAsync(db, transaction, actor.UserId, state == MilestoneState.Closed ? "close-milestone" : "reopen-milestone",
                "milestone", Reference(projectId, existing.Iid), state == MilestoneState.Closed ? "Milestone closed." : "Milestone reopened.", now);
        await transaction.CommitAsync(cancellationToken);
        return (await FindMilestoneAsync(actor, projectId, milestoneId, cancellationToken))!;
    }

    /// <summary>Deletes a milestone. The issues in it stay, with no milestone.</summary>
    public async Task DeleteMilestoneAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long milestoneId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequireOrganiserAsync(db, transaction, actor, projectId, cancellationToken);
        MilestoneRow? existing = await db.QuerySingleOrDefaultAsync<MilestoneRow>(MilestoneSelect +
            "WHERE m.ProjectId = @ProjectId AND m.Id = @Id;",
            new MilestoneKeyParameters(projectId, milestoneId), transaction);
        if (existing is null) throw new CollaborationNotFoundException("Milestone is unavailable.");
        await db.ExecuteAsync("UPDATE Issues SET MilestoneId = NULL WHERE MilestoneId = @Id;", new IdParameters(existing.Id), transaction);
        await db.ExecuteAsync("UPDATE MergeRequests SET MilestoneId = NULL WHERE MilestoneId = @Id;", new IdParameters(existing.Id), transaction);
        await db.ExecuteAsync("DELETE FROM Milestones WHERE Id = @Id;", new IdParameters(existing.Id), transaction);
        await AuditAsync(db, transaction, actor.UserId, "delete-milestone", "milestone",
            Reference(projectId, existing.Iid), "Milestone deleted; its issues were kept.", Now());
        await transaction.CommitAsync(cancellationToken);
    }

    // ---------------------------------------------------------------------------------------------------
    // Boards
    // ---------------------------------------------------------------------------------------------------

    public async Task<Board> CreateBoardAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string cleanName = RequireTitle(name);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequireOrganiserAsync(db, transaction, actor, projectId, cancellationToken);
        long count = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM Boards WHERE ProjectId = @Id;",
            new IdParameters(projectId), transaction);
        if (count >= 10) throw new CollaborationConflictException("A project has at most ten boards.");
        string now = Now();
        long id = await db.QuerySingleAsync<long>(
            "INSERT INTO Boards (ProjectId, Name, CreatedAtUtc) VALUES (@ProjectId, @Name, @CreatedAtUtc) RETURNING Id;",
            new BoardInsertParameters(projectId, cleanName, now), transaction);
        await AuditAsync(db, transaction, actor.UserId, "create-board", "board",
            id.ToString(CultureInfo.InvariantCulture), $"Board '{cleanName}' created.", now);
        await transaction.CommitAsync(cancellationToken);
        return new(id, projectId, cleanName, []);
    }

    public async Task<IReadOnlyList<Board>> ListBoardsAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        IEnumerable<BoardRow> boards = await db.QueryAsync<BoardRow>(
            "SELECT Id, ProjectId, Name FROM Boards WHERE ProjectId = @Id ORDER BY Id;", new IdParameters(projectId));
        IEnumerable<BoardListRow> lists = await db.QueryAsync<BoardListRow>("""
            SELECT bl.Id, bl.BoardId, bl.Position, l.Id AS LabelId, l.Name, l.Color, l.Description
            FROM BoardLists bl
            JOIN Boards b ON b.Id = bl.BoardId
            JOIN Labels l ON l.Id = bl.LabelId
            WHERE b.ProjectId = @Id
            ORDER BY bl.Position, bl.Id;
            """, new IdParameters(projectId));
        ILookup<long, BoardListRow> byBoard = lists.ToLookup(list => list.BoardId);
        return [.. boards.Select(board => new Board(board.Id, board.ProjectId, board.Name,
            [.. byBoard[board.Id].Select(ToBoardList)]))];
    }

    public async Task<Board?> FindBoardAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        long boardId,
        CancellationToken cancellationToken = default) =>
        (await ListBoardsAsync(identity, projectId, cancellationToken)).FirstOrDefault(board => board.Id == boardId);

    public async Task<Board> RenameBoardAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long boardId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string cleanName = RequireTitle(name);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequireOrganiserAsync(db, transaction, actor, projectId, cancellationToken);
        int changed = await db.ExecuteAsync("UPDATE Boards SET Name = @Name WHERE Id = @Id AND ProjectId = @ProjectId;",
            new BoardRenameParameters(boardId, projectId, cleanName), transaction);
        if (changed != 1) throw new CollaborationNotFoundException("Board is unavailable.");
        await transaction.CommitAsync(cancellationToken);
        return (await FindBoardAsync(actor, projectId, boardId, cancellationToken))!;
    }

    public async Task DeleteBoardAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long boardId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequireOrganiserAsync(db, transaction, actor, projectId, cancellationToken);
        await db.ExecuteAsync("DELETE FROM BoardLists WHERE BoardId = @Id;", new IdParameters(boardId), transaction);
        int changed = await db.ExecuteAsync("DELETE FROM Boards WHERE Id = @Id AND ProjectId = @ProjectId;",
            new ScopedIdParameters(boardId, projectId), transaction);
        if (changed != 1) throw new CollaborationNotFoundException("Board is unavailable.");
        await AuditAsync(db, transaction, actor.UserId, "delete-board", "board",
            boardId.ToString(CultureInfo.InvariantCulture), "Board deleted.", Now());
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Adds a list bound to a label, at the end. A label appears on a board at most once.</summary>
    public async Task<Board> AddBoardListAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long boardId,
        long labelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequireOrganiserAsync(db, transaction, actor, projectId, cancellationToken);
        long board = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM Boards WHERE Id = @Id AND ProjectId = @ProjectId;",
            new ScopedIdParameters(boardId, projectId), transaction);
        if (board != 1) throw new CollaborationNotFoundException("Board is unavailable.");
        long label = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM Labels WHERE Id = @Id AND ProjectId = @ProjectId;",
            new ScopedIdParameters(labelId, projectId), transaction);
        if (label != 1) throw new CollaborationNotFoundException("Label is unavailable.");
        long present = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM BoardLists WHERE BoardId = @Id AND LabelId = @ProjectId;",
            new ScopedIdParameters(boardId, labelId), transaction);
        if (present != 0) throw new CollaborationConflictException("That label already has a list on this board.");
        long lists = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM BoardLists WHERE BoardId = @Id;",
            new IdParameters(boardId), transaction);
        if (lists >= MaximumBoardLists) throw new CollaborationConflictException("A board has at most twenty lists.");
        long position = await db.QuerySingleAsync<long>("SELECT COALESCE(MAX(Position), 0) + 1 FROM BoardLists WHERE BoardId = @Id;",
            new IdParameters(boardId), transaction);
        await db.ExecuteAsync("INSERT INTO BoardLists (BoardId, LabelId, Position) VALUES (@BoardId, @LabelId, @Position);",
            new BoardListInsertParameters(boardId, labelId, position), transaction);
        await transaction.CommitAsync(cancellationToken);
        return (await FindBoardAsync(actor, projectId, boardId, cancellationToken))!;
    }

    /// <summary>Moves a list to a position, counted from one, shifting the others.</summary>
    public async Task<Board> MoveBoardListAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long boardId,
        long listId,
        int position,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (position < 1) throw new ArgumentOutOfRangeException(nameof(position));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequireOrganiserAsync(db, transaction, actor, projectId, cancellationToken);
        IEnumerable<BoardListOrderRow> rows = await db.QueryAsync<BoardListOrderRow>("""
            SELECT bl.Id FROM BoardLists bl JOIN Boards b ON b.Id = bl.BoardId
            WHERE bl.BoardId = @Id AND b.ProjectId = @ProjectId ORDER BY bl.Position, bl.Id;
            """, new ScopedIdParameters(boardId, projectId), transaction);
        List<long> order = [.. rows.Select(row => row.Id)];
        if (!order.Remove(listId)) throw new CollaborationNotFoundException("List is unavailable.");
        order.Insert(Math.Min(position - 1, order.Count), listId);
        for (int index = 0; index < order.Count; index++)
            await db.ExecuteAsync("UPDATE BoardLists SET Position = @Value WHERE Id = @Id;",
                new ProjectFlagParameters(order[index], index + 1), transaction);
        await transaction.CommitAsync(cancellationToken);
        return (await FindBoardAsync(actor, projectId, boardId, cancellationToken))!;
    }

    public async Task DeleteBoardListAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long boardId,
        long listId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequireOrganiserAsync(db, transaction, actor, projectId, cancellationToken);
        int changed = await db.ExecuteAsync("""
            DELETE FROM BoardLists WHERE Id = @Id AND BoardId = @BoardId
              AND BoardId IN (SELECT Id FROM Boards WHERE ProjectId = @ProjectId);
            """, new BoardListDeleteParameters(listId, boardId, projectId), transaction);
        if (changed != 1) throw new CollaborationNotFoundException("List is unavailable.");
        await transaction.CommitAsync(cancellationToken);
    }

    // ---------------------------------------------------------------------------------------------------
    // Who may be assigned: the project's members, by name.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The people who can be assigned to or asked to review work on this project: its owner and every
    /// direct member. Readable by anybody who may write on the project, which is weaker than the Maintainer
    /// standing the membership list itself needs, because an assignee picker is not a management act.
    /// </summary>
    public async Task<IReadOnlyList<Person>> ListAssignableAsync(
        AuthenticatedIdentity actor,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireParticipantAsync(db, null!, actor, projectId, cancellationToken);
        IEnumerable<PersonRow> rows = await db.QueryAsync<PersonRow>(AssignableSelect, new IdParameters(projectId));
        return [.. rows.Select(row => new Person(row.UserId, row.Username))];
    }

    private const string AssignableSelect = """
        SELECT u.Id AS UserId, u.Username
        FROM Users u
        WHERE u.Disabled = 0 AND (
            u.Id IN (SELECT OwnerUserId FROM Projects WHERE Id = @Id)
            OR u.Id IN (SELECT UserId FROM ProjectMemberships WHERE ProjectId = @Id))
        ORDER BY u.NormalizedUsername;
        """;

    // ---------------------------------------------------------------------------------------------------
    // Applying planning to an issue or a proposal. Shared by both update paths.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Applies label, milestone and people changes to one subject, inside the caller's transaction. The
    /// caller has already decided the actor may; this resolves names to rows and refuses what does not
    /// exist, by name, rather than creating it, because a typo should be an error and not a new label.
    /// </summary>
    /// <summary>Applies the planning fields and returns the people newly placed in a role, as
    /// <c>("assignee", userId)</c> or <c>("reviewer", userId)</c>, for the caller to notify.</summary>
    private static async Task<List<(string Role, long UserId)>> ApplyPlanningAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        long projectId,
        CommentSubject subject,
        long subjectId,
        IReadOnlyList<string>? labels,
        IReadOnlyList<string>? addLabels,
        IReadOnlyList<string>? removeLabels,
        Change<long?> milestoneId,
        IReadOnlyList<string>? assignees,
        IReadOnlyList<string>? reviewers,
        string now)
    {
        List<(string Role, long UserId)> added = [];
        string kind = SubjectName(subject);
        if (labels is not null)
        {
            List<long> ids = await ResolveLabelsAsync(db, transaction, projectId, labels);
            await db.ExecuteAsync("DELETE FROM SubjectLabels WHERE SubjectType = @SubjectType AND SubjectId = @SubjectId;",
                new SubjectParameters(kind, subjectId), transaction);
            foreach (long id in ids)
                await db.ExecuteAsync("INSERT OR IGNORE INTO SubjectLabels (SubjectType, SubjectId, LabelId) VALUES (@SubjectType, @SubjectId, @Id);",
                    new SubjectLinkParameters(kind, subjectId, id), transaction);
        }
        if (addLabels is not null)
            foreach (long id in await ResolveLabelsAsync(db, transaction, projectId, addLabels))
                await db.ExecuteAsync("INSERT OR IGNORE INTO SubjectLabels (SubjectType, SubjectId, LabelId) VALUES (@SubjectType, @SubjectId, @Id);",
                    new SubjectLinkParameters(kind, subjectId, id), transaction);
        if (removeLabels is not null)
            foreach (long id in await ResolveLabelsAsync(db, transaction, projectId, removeLabels))
                await db.ExecuteAsync("DELETE FROM SubjectLabels WHERE SubjectType = @SubjectType AND SubjectId = @SubjectId AND LabelId = @Id;",
                    new SubjectLinkParameters(kind, subjectId, id), transaction);

        if (milestoneId.Set)
        {
            long? resolved = null;
            if (milestoneId.Value is long wanted)
            {
                SubjectRow? milestone = await db.QuerySingleOrDefaultAsync<SubjectRow>(
                    "SELECT Id FROM Milestones WHERE ProjectId = @ProjectId AND Id = @Id;",
                    new MilestoneKeyParameters(projectId, wanted), transaction);
                resolved = milestone?.Id ?? throw new ArgumentException("That milestone does not exist in this project.", nameof(milestoneId));
            }
            await db.ExecuteAsync(subject == CommentSubject.Issue
                ? "UPDATE Issues SET MilestoneId = @Value WHERE Id = @Id;"
                : "UPDATE MergeRequests SET MilestoneId = @Value WHERE Id = @Id;",
                new NullableValueParameters(subjectId, resolved), transaction);
        }

        if (assignees is not null)
            foreach (long id in await SetPeopleAsync(db, transaction, projectId, kind, subjectId, "assignee", assignees)) added.Add(("assignee", id));
        if (reviewers is not null)
            foreach (long id in await SetPeopleAsync(db, transaction, projectId, kind, subjectId, "reviewer", reviewers)) added.Add(("reviewer", id));

        await db.ExecuteAsync(subject == CommentSubject.Issue
            ? "UPDATE Issues SET UpdatedAtUtc = @UpdatedAtUtc WHERE Id = @Id;"
            : "UPDATE MergeRequests SET UpdatedAtUtc = @UpdatedAtUtc WHERE Id = @Id;",
            new TouchParameters(subjectId, now), transaction);
        return added;
    }

    private static async Task<List<long>> ResolveLabelsAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        long projectId,
        IReadOnlyList<string> names)
    {
        if (names.Count > 50) throw new ArgumentException("At most fifty labels at once.", nameof(names));
        List<long> ids = [];
        foreach (string name in names)
        {
            string trimmed = name.Trim();
            if (trimmed.Length == 0) continue;
            SubjectRow? label = await db.QuerySingleOrDefaultAsync<SubjectRow>(
                "SELECT Id FROM Labels WHERE ProjectId = @ProjectId AND NormalizedName = @Name;",
                new LabelLookupParameters(projectId, Normalize(trimmed)), transaction);
            if (label is null) throw new ArgumentException($"There is no label named '{trimmed}' in this project.", nameof(names));
            if (!ids.Contains(label.Id)) ids.Add(label.Id);
        }
        return ids;
    }

    /// <summary>Replaces the people of one kind on a subject. Each must be a member of the project.</summary>
    /// <summary>Replaces the people in one role and returns those who were not in it before, so the caller
    /// can tell them.</summary>
    private static async Task<List<long>> SetPeopleAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        long projectId,
        string kind,
        long subjectId,
        string role,
        IReadOnlyList<string> usernames)
    {
        if (usernames.Count > MaximumPeople) throw new ArgumentException("At most twenty people.", nameof(usernames));
        List<long> before = (await db.QueryAsync<long>(
            "SELECT UserId FROM SubjectPeople WHERE SubjectType = @SubjectType AND SubjectId = @SubjectId AND Kind = @Kind;",
            new PeopleClearParameters(kind, subjectId, role), transaction)).ToList();
        List<long> ids = [];
        foreach (string username in usernames)
        {
            string trimmed = username.Trim();
            if (trimmed.Length == 0) continue;
            PersonRow? person = await db.QuerySingleOrDefaultAsync<PersonRow>("""
                SELECT u.Id AS UserId, u.Username
                FROM Users u
                WHERE u.Disabled = 0 AND u.NormalizedUsername = @Name AND (
                    u.Id IN (SELECT OwnerUserId FROM Projects WHERE Id = @ProjectId)
                    OR u.Id IN (SELECT UserId FROM ProjectMemberships WHERE ProjectId = @ProjectId));
                """, new LabelLookupParameters(projectId, Normalize(trimmed)), transaction);
            if (person is null)
                throw new ArgumentException($"'{trimmed}' is not a member of this project, so cannot be {(role == "assignee" ? "assigned" : "asked to review")}.", nameof(usernames));
            if (!ids.Contains(person.UserId)) ids.Add(person.UserId);
        }
        await db.ExecuteAsync("DELETE FROM SubjectPeople WHERE SubjectType = @SubjectType AND SubjectId = @SubjectId AND Kind = @Kind;",
            new PeopleClearParameters(kind, subjectId, role), transaction);
        foreach (long id in ids)
            await db.ExecuteAsync("INSERT INTO SubjectPeople (SubjectType, SubjectId, UserId, Kind) VALUES (@SubjectType, @SubjectId, @UserId, @Kind);",
                new PeopleInsertParameters(kind, subjectId, id, role), transaction);
        return ids.Where(id => !before.Contains(id)).ToList();
    }

    /// <summary>Every label on every subject of one kind in a project, grouped by subject row id.</summary>
    private static async Task<ILookup<long, Label>> LabelsBySubjectAsync(SqliteConnection db, long projectId, CommentSubject subject)
    {
        IEnumerable<SubjectLabelRow> rows = await db.QueryAsync<SubjectLabelRow>(subject == CommentSubject.Issue ? """
            SELECT sl.SubjectId, l.Id, l.Name, l.Color, l.Description
            FROM SubjectLabels sl JOIN Labels l ON l.Id = sl.LabelId JOIN Issues i ON i.Id = sl.SubjectId
            WHERE sl.SubjectType = 'issue' AND i.ProjectId = @Id ORDER BY l.NormalizedName;
            """ : """
            SELECT sl.SubjectId, l.Id, l.Name, l.Color, l.Description
            FROM SubjectLabels sl JOIN Labels l ON l.Id = sl.LabelId JOIN MergeRequests m ON m.Id = sl.SubjectId
            WHERE sl.SubjectType = 'merge_request' AND m.ProjectId = @Id ORDER BY l.NormalizedName;
            """, new IdParameters(projectId));
        return rows.ToLookup(row => row.SubjectId, row => new Label(row.Id, row.Name, row.Color, row.Description));
    }

    /// <summary>Every person on every subject of one kind in a project, grouped by subject row id and role.</summary>
    private static async Task<ILookup<(long SubjectId, string Kind), Person>> PeopleBySubjectAsync(SqliteConnection db, long projectId, CommentSubject subject)
    {
        IEnumerable<SubjectPersonRow> rows = await db.QueryAsync<SubjectPersonRow>(subject == CommentSubject.Issue ? """
            SELECT sp.SubjectId, sp.Kind, u.Id AS UserId, u.Username
            FROM SubjectPeople sp JOIN Users u ON u.Id = sp.UserId JOIN Issues i ON i.Id = sp.SubjectId
            WHERE sp.SubjectType = 'issue' AND i.ProjectId = @Id ORDER BY u.NormalizedUsername;
            """ : """
            SELECT sp.SubjectId, sp.Kind, u.Id AS UserId, u.Username
            FROM SubjectPeople sp JOIN Users u ON u.Id = sp.UserId JOIN MergeRequests m ON m.Id = sp.SubjectId
            WHERE sp.SubjectType = 'merge_request' AND m.ProjectId = @Id ORDER BY u.NormalizedUsername;
            """, new IdParameters(projectId));
        return rows.ToLookup(row => (row.SubjectId, row.Kind), row => new Person(row.UserId, row.Username));
    }

    /// <summary>
    /// Whether this caller may organise the project's work: create labels and milestones, assign people,
    /// put an issue in a milestone. The permission to push, because a Reporter files and a Developer
    /// organises. The author of an issue may organise that one issue, which <see cref="RequireTriagerAsync"/>
    /// handles; this is the project-wide form.
    /// </summary>
    private static async Task RequireOrganiserAsync(
        SqliteConnection db,
        SqliteTransaction? transaction,
        AuthenticatedIdentity actor,
        long projectId,
        CancellationToken cancellationToken)
    {
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId, ProjectAction.PushRepository, cancellationToken);
        if (!permission.Allowed)
            throw new UnauthorizedAccessException("Organising a project's work needs the permission to push.");
        PermissionDecision api = await DecideAsync(db, transaction, actor, projectId, ProjectAction.WriteCollaboration, cancellationToken);
        if (!api.Allowed) throw new UnauthorizedAccessException(api.Explanation);
    }

    /// <summary>The author of this item, or anybody who could push, may change its labels, milestone and people.</summary>
    private static async Task RequireTriagerAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        AuthenticatedIdentity actor,
        long projectId,
        long authorUserId,
        CancellationToken cancellationToken)
    {
        if (actor.UserId == authorUserId) return;
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId, ProjectAction.PushRepository, cancellationToken);
        if (!permission.Allowed)
            throw new UnauthorizedAccessException("Only the author or somebody who can push may change labels, milestone or people.");
    }

    private static string RequireLabelName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string trimmed = value.Trim();
        if (trimmed.Length > MaximumLabelLength) throw new ArgumentException("A label name is at most 255 characters.", nameof(value));
        // Commas separate labels in GitLab's own API, so a name holding one could never be addressed there.
        if (trimmed.Any(character => char.IsControl(character) || character == ','))
            throw new ArgumentException("A label name has no commas or control characters.", nameof(value));
        return trimmed;
    }

    /// <summary>A six-digit hex colour, normalised to lowercase with a leading hash. Nothing else is stored,
    /// so a colour can never carry markup into the page that paints it.</summary>
    private static string RequireColor(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string trimmed = value.Trim();
        if (trimmed.StartsWith('#')) trimmed = trimmed[1..];
        if (trimmed.Length == 3) trimmed = new string([trimmed[0], trimmed[0], trimmed[1], trimmed[1], trimmed[2], trimmed[2]]);
        if (trimmed.Length != 6 || !trimmed.All(char.IsAsciiHexDigit))
            throw new ArgumentException("A colour is six hexadecimal digits, like #d73a4a.", nameof(value));
        return "#" + trimmed.ToLowerInvariant();
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static string? Day(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? ParseDay(string? value) =>
        value is null ? null : DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string MilestoneStateName(MilestoneState state) => state == MilestoneState.Closed ? "closed" : "active";

    private static Label ToLabel(LabelRow row) => new(row.Id, row.Name, row.Color, row.Description);

    private static BoardList ToBoardList(BoardListRow row) =>
        new(row.Id, new Label(row.LabelId, row.Name, row.Color, row.Description), (int)row.Position);

    private const string MilestoneSelect = """
        SELECT m.Id, m.ProjectId, m.Iid, m.Title, m.Description, m.State, m.DueDate, m.StartDate, m.CreatedAtUtc, m.UpdatedAtUtc,
               (SELECT COUNT(*) FROM Issues i WHERE i.MilestoneId = m.Id AND i.State = 'opened') AS OpenIssues,
               (SELECT COUNT(*) FROM Issues i WHERE i.MilestoneId = m.Id AND i.State = 'closed') AS ClosedIssues,
               (SELECT COUNT(*) FROM MergeRequests r WHERE r.MilestoneId = m.Id) AS MergeRequests
        FROM Milestones m

        """;

    private static Milestone ToMilestone(MilestoneRow row) => new(
        row.Id, row.ProjectId, row.Iid, row.Title, row.Description,
        row.State == "closed" ? MilestoneState.Closed : MilestoneState.Active,
        ParseDay(row.DueDate), ParseDay(row.StartDate), Instant(row.CreatedAtUtc), Instant(row.UpdatedAtUtc),
        row.OpenIssues, row.ClosedIssues, row.MergeRequests);
}

internal sealed record LabelLookupParameters(long ProjectId, string Name);
internal sealed record LabelClashParameters(long ProjectId, string Name, long Id);
internal sealed record LabelInsertParameters(long ProjectId, string Name, string NormalizedName, string Color, string Description, string CreatedAtUtc);
internal sealed record LabelUpdateParameters(long Id, string? Name, string? NormalizedName, string? Color, string? Description);
internal sealed record ScopedIdParameters(long Id, long ProjectId);
internal sealed record MilestoneInsertParameters(long ProjectId, long Iid, string Title, string Description, string? DueDate, string? StartDate, string Now);
internal sealed record MilestoneListParameters(long ProjectId, string State, int AnyState);
internal sealed record MilestoneUpdateParameters(long Id, string? Title, string? Description, string State, string? DueDate, string? StartDate, string Now);
internal sealed record BoardInsertParameters(long ProjectId, string Name, string CreatedAtUtc);
internal sealed record BoardRenameParameters(long Id, long ProjectId, string Name);
internal sealed record BoardListInsertParameters(long BoardId, long LabelId, long Position);
internal sealed record BoardListDeleteParameters(long Id, long BoardId, long ProjectId);
internal sealed record SubjectParameters(string SubjectType, long SubjectId);
internal sealed record SubjectLinkParameters(string SubjectType, long SubjectId, long Id);
internal sealed record NullableValueParameters(long Id, long? Value);
internal sealed record PeopleClearParameters(string SubjectType, long SubjectId, string Kind);
internal sealed record PeopleInsertParameters(string SubjectType, long SubjectId, long UserId, string Kind);

internal sealed class LabelRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "";
    public string Description { get; set; } = "";
}

internal sealed class SubjectLabelRow
{
    public long SubjectId { get; set; }
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "";
    public string Description { get; set; } = "";
}

internal sealed class SubjectPersonRow
{
    public long SubjectId { get; set; }
    public string Kind { get; set; } = "";
    public long UserId { get; set; }
    public string Username { get; set; } = "";
}

internal sealed class PersonRow
{
    public long UserId { get; set; }
    public string Username { get; set; } = "";
}

internal sealed class MilestoneRow
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public long Iid { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string State { get; set; } = "active";
    public string? DueDate { get; set; }
    public string? StartDate { get; set; }
    public string CreatedAtUtc { get; set; } = "";
    public string UpdatedAtUtc { get; set; } = "";
    public long OpenIssues { get; set; }
    public long ClosedIssues { get; set; }
    public long MergeRequests { get; set; }
}

internal sealed class BoardRow
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string Name { get; set; } = "";
}

internal sealed class BoardListRow
{
    public long Id { get; set; }
    public long BoardId { get; set; }
    public long Position { get; set; }
    public long LabelId { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "";
    public string Description { get; set; } = "";
}

internal sealed class BoardListOrderRow { public long Id { get; set; } }

/// <summary>How a list of merge proposals is narrowed. Reviewer wins over Assignee when both are given.</summary>
public sealed record MergeFilter(
    CollaborationState? State = null,
    IReadOnlyList<string>? Labels = null,
    string? Milestone = null,
    string? Assignee = null,
    string? Reviewer = null,
    string? Search = null);
internal sealed record MilestoneKeyParameters(long ProjectId, long Id);
