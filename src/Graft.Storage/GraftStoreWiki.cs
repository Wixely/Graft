using System.Globalization;
using System.Text;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// The wiki, snippets and time tracking.
///
/// <para>
/// A wiki page is read wherever the conversation is read and written by whoever could push: prose about the
/// project is organised the way the project is. A snippet is made by any participant and changed by its
/// author or a Maintainer, like a comment. Time spent is each person's own to record; an estimate is set
/// by whoever may triage the item.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    private const int MaximumWikiBytes = 512 * 1024;
    private const int MaximumSnippetBytes = 512 * 1024;
    private const int MaximumWikiVersions = 500;

    // ---------------------------------------------------------------------------------------------------
    // Wiki
    // ---------------------------------------------------------------------------------------------------

    public async Task<WikiPage> CreateWikiPageAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string title,
        string content,
        string message = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string cleanTitle = RequireTitle(title);
        string cleanContent = RequireDocument(content, MaximumWikiBytes, nameof(content));
        string slug = Slugify(cleanTitle);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity author = await RequireWriterAsync(db, transaction, actor, projectId, cancellationToken);
        long clash = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM WikiPages WHERE ProjectId = @ProjectId AND Slug = @Name;",
            new LabelLookupParameters(projectId, slug), transaction);
        if (clash != 0) throw new CollaborationConflictException("A page with this address already exists.");
        string now = Now();
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO WikiPages (ProjectId, Slug, Title, Content, CreatedByUserId, UpdatedByUserId, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@ProjectId, @Slug, @Title, @Content, @UserId, @UserId, @Now, @Now)
            RETURNING Id;
            """, new WikiInsertParameters(projectId, slug, cleanTitle, cleanContent, author.UserId, now), transaction);
        await db.ExecuteAsync("""
            INSERT INTO WikiPageVersions (PageId, Title, Content, Message, AuthorUserId, CreatedAtUtc)
            VALUES (@PageId, @Title, @Content, @Message, @AuthorUserId, @CreatedAtUtc);
            """, new WikiVersionParameters(id, cleanTitle, cleanContent, RequireMessage(message), author.UserId, now), transaction);
        await AuditAsync(db, transaction, author.UserId, "create-wiki-page", "wiki",
            id.ToString(CultureInfo.InvariantCulture), $"Wiki page '{slug}' created.", now);
        await transaction.CommitAsync(cancellationToken);
        return (await FindWikiPageAsync(actor, projectId, slug, cancellationToken))!;
    }

    public async Task<IReadOnlyList<WikiPage>> ListWikiPagesAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        // Content is left out of a listing: a wiki is read one page at a time.
        IEnumerable<WikiRow> rows = await db.QueryAsync<WikiRow>(WikiSelect.Replace("p.Content,", "'' AS Content,", StringComparison.Ordinal) +
            "WHERE p.ProjectId = @Id ORDER BY lower(p.Title);", new IdParameters(projectId));
        return [.. rows.Select(ToWikiPage)];
    }

    public async Task<WikiPage?> FindWikiPageAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        string slug,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        WikiRow? row = await db.QuerySingleOrDefaultAsync<WikiRow>(WikiSelect +
            "WHERE p.ProjectId = @ProjectId AND p.Slug = @Name;", new LabelLookupParameters(projectId, slug.Trim().ToLowerInvariant()));
        return row is null ? null : ToWikiPage(row);
    }

    /// <summary>Every saved state of a page, newest first, without content; one is read with <see cref="FindWikiVersionAsync"/>.</summary>
    public async Task<IReadOnlyList<WikiPageVersion>> ListWikiVersionsAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        string slug,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        IEnumerable<WikiVersionRow> rows = await db.QueryAsync<WikiVersionRow>("""
            SELECT v.Id, v.Title, '' AS Content, v.Message, u.Username AS AuthorUsername, v.CreatedAtUtc
            FROM WikiPageVersions v
            JOIN WikiPages p ON p.Id = v.PageId
            JOIN Users u ON u.Id = v.AuthorUserId
            WHERE p.ProjectId = @ProjectId AND p.Slug = @Name
            ORDER BY v.Id DESC;
            """, new LabelLookupParameters(projectId, slug.Trim().ToLowerInvariant()));
        return [.. rows.Select(ToWikiVersion)];
    }

    public async Task<WikiPageVersion?> FindWikiVersionAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        string slug,
        long versionId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        WikiVersionRow? row = await db.QuerySingleOrDefaultAsync<WikiVersionRow>("""
            SELECT v.Id, v.Title, v.Content, v.Message, u.Username AS AuthorUsername, v.CreatedAtUtc
            FROM WikiPageVersions v
            JOIN WikiPages p ON p.Id = v.PageId
            JOIN Users u ON u.Id = v.AuthorUserId
            WHERE p.ProjectId = @ProjectId AND p.Slug = @Slug AND v.Id = @Id;
            """, new WikiVersionKeyParameters(projectId, slug.Trim().ToLowerInvariant(), versionId));
        return row is null ? null : ToWikiVersion(row);
    }

    /// <summary>
    /// Saves a page. A title change keeps the slug, because the slug is the page's address and links to it
    /// should keep working; the previous state is kept as a version. The oldest versions are dropped past a
    /// bound, so a page edited forever does not grow a history forever.
    /// </summary>
    public async Task<WikiPage> UpdateWikiPageAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string slug,
        string? title,
        string? content,
        string message = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string? cleanTitle = title is null ? null : RequireTitle(title);
        string? cleanContent = content is null ? null : RequireDocument(content, MaximumWikiBytes, nameof(content));
        if (cleanTitle is null && cleanContent is null) throw new ArgumentException("Nothing to change.", nameof(content));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity editor = await RequireWriterAsync(db, transaction, actor, projectId, cancellationToken);
        WikiRow? existing = await db.QuerySingleOrDefaultAsync<WikiRow>(WikiSelect +
            "WHERE p.ProjectId = @ProjectId AND p.Slug = @Name;", new LabelLookupParameters(projectId, slug.Trim().ToLowerInvariant()), transaction);
        if (existing is null) throw new CollaborationNotFoundException("Page is unavailable.");
        string nextTitle = cleanTitle ?? existing.Title;
        string nextContent = cleanContent ?? existing.Content;
        if (nextTitle == existing.Title && nextContent == existing.Content)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToWikiPage(existing);
        }
        string now = Now();
        await db.ExecuteAsync("UPDATE WikiPages SET Title = @Title, Content = @Content, UpdatedByUserId = @UserId, UpdatedAtUtc = @Now WHERE Id = @Id;",
            new WikiUpdateParameters(existing.Id, nextTitle, nextContent, editor.UserId, now), transaction);
        await db.ExecuteAsync("""
            INSERT INTO WikiPageVersions (PageId, Title, Content, Message, AuthorUserId, CreatedAtUtc)
            VALUES (@PageId, @Title, @Content, @Message, @AuthorUserId, @CreatedAtUtc);
            """, new WikiVersionParameters(existing.Id, nextTitle, nextContent, RequireMessage(message), editor.UserId, now), transaction);
        await db.ExecuteAsync("""
            DELETE FROM WikiPageVersions WHERE PageId = @Id AND Id NOT IN (
                SELECT Id FROM WikiPageVersions WHERE PageId = @Id ORDER BY Id DESC LIMIT @Value);
            """, new ProjectFlagParameters(existing.Id, MaximumWikiVersions), transaction);
        await AuditAsync(db, transaction, editor.UserId, "update-wiki-page", "wiki",
            existing.Id.ToString(CultureInfo.InvariantCulture), $"Wiki page '{existing.Slug}' saved.", now);
        await transaction.CommitAsync(cancellationToken);
        return (await FindWikiPageAsync(actor, projectId, existing.Slug, cancellationToken))!;
    }

    public async Task DeleteWikiPageAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string slug,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequireWriterAsync(db, transaction, actor, projectId, cancellationToken);
        SubjectRow? existing = await db.QuerySingleOrDefaultAsync<SubjectRow>(
            "SELECT Id FROM WikiPages WHERE ProjectId = @ProjectId AND Slug = @Name;",
            new LabelLookupParameters(projectId, slug.Trim().ToLowerInvariant()), transaction);
        if (existing is null) throw new CollaborationNotFoundException("Page is unavailable.");
        await db.ExecuteAsync("DELETE FROM WikiPageVersions WHERE PageId = @Id;", new IdParameters(existing.Id), transaction);
        await db.ExecuteAsync("DELETE FROM WikiPages WHERE Id = @Id;", new IdParameters(existing.Id), transaction);
        await AuditAsync(db, transaction, actor.UserId, "delete-wiki-page", "wiki",
            existing.Id.ToString(CultureInfo.InvariantCulture), $"Wiki page '{slug.Trim()}' deleted with its history.", Now());
        await transaction.CommitAsync(cancellationToken);
    }

    // ---------------------------------------------------------------------------------------------------
    // Snippets
    // ---------------------------------------------------------------------------------------------------

    public async Task<Snippet> CreateSnippetAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string title,
        string fileName,
        string content,
        string description = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string cleanTitle = RequireTitle(title);
        string cleanFile = RequireAssetName(fileName);
        string cleanContent = RequireDocument(content, MaximumSnippetBytes, nameof(content));
        string cleanDescription = RequireBody(description, nameof(description), allowEmpty: true);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity author = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);
        string now = Now();
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO Snippets (ProjectId, Title, FileName, Description, Content, AuthorUserId, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@ProjectId, @Title, @FileName, @Description, @Content, @AuthorUserId, @Now, @Now)
            RETURNING Id;
            """, new SnippetInsertParameters(projectId, cleanTitle, cleanFile, cleanDescription, cleanContent, author.UserId, now), transaction);
        await AuditAsync(db, transaction, author.UserId, "create-snippet", "snippet", id.ToString(CultureInfo.InvariantCulture), "Snippet created.", now);
        await transaction.CommitAsync(cancellationToken);
        return (await FindSnippetAsync(actor, projectId, id, cancellationToken))!;
    }

    public async Task<IReadOnlyList<Snippet>> ListSnippetsAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        IEnumerable<SnippetRow> rows = await db.QueryAsync<SnippetRow>(SnippetSelect.Replace("s.Content,", "'' AS Content,", StringComparison.Ordinal) +
            "WHERE s.ProjectId = @Id ORDER BY s.UpdatedAtUtc DESC, s.Id DESC;", new IdParameters(projectId));
        return [.. rows.Select(ToSnippet)];
    }

    public async Task<Snippet?> FindSnippetAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        long snippetId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        SnippetRow? row = await db.QuerySingleOrDefaultAsync<SnippetRow>(SnippetSelect +
            "WHERE s.ProjectId = @ProjectId AND s.Id = @Id;", new ScopedIdParameters(snippetId, projectId));
        return row is null ? null : ToSnippet(row);
    }

    public async Task<Snippet> UpdateSnippetAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long snippetId,
        string? title,
        string? fileName,
        string? content,
        string? description,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string? cleanTitle = title is null ? null : RequireTitle(title);
        string? cleanFile = fileName is null ? null : RequireAssetName(fileName);
        string? cleanContent = content is null ? null : RequireDocument(content, MaximumSnippetBytes, nameof(content));
        string? cleanDescription = description is null ? null : RequireBody(description, nameof(description), allowEmpty: true);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity editor = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);
        CommentOwnerRow? existing = await db.QuerySingleOrDefaultAsync<CommentOwnerRow>(
            "SELECT Id, AuthorUserId FROM Snippets WHERE Id = @Id AND ProjectId = @ProjectId;",
            new ScopedIdParameters(snippetId, projectId), transaction);
        if (existing is null) throw new CollaborationNotFoundException("Snippet is unavailable.");
        await RequireEditorAsync(db, transaction, editor, projectId, existing.AuthorUserId, textChanged: true, cancellationToken);
        await db.ExecuteAsync("""
            UPDATE Snippets SET Title = COALESCE(@Title, Title), FileName = COALESCE(@FileName, FileName),
                                Content = COALESCE(@Content, Content), Description = COALESCE(@Description, Description),
                                UpdatedAtUtc = @Now
            WHERE Id = @Id;
            """, new SnippetUpdateParameters(snippetId, cleanTitle, cleanFile, cleanContent, cleanDescription, Now()), transaction);
        await transaction.CommitAsync(cancellationToken);
        return (await FindSnippetAsync(actor, projectId, snippetId, cancellationToken))!;
    }

    public async Task DeleteSnippetAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long snippetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity editor = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);
        CommentOwnerRow? existing = await db.QuerySingleOrDefaultAsync<CommentOwnerRow>(
            "SELECT Id, AuthorUserId FROM Snippets WHERE Id = @Id AND ProjectId = @ProjectId;",
            new ScopedIdParameters(snippetId, projectId), transaction);
        if (existing is null) throw new CollaborationNotFoundException("Snippet is unavailable.");
        await RequireEditorAsync(db, transaction, editor, projectId, existing.AuthorUserId, textChanged: true, cancellationToken);
        await db.ExecuteAsync("DELETE FROM Snippets WHERE Id = @Id;", new IdParameters(snippetId), transaction);
        await AuditAsync(db, transaction, editor.UserId, "delete-snippet", "snippet", snippetId.ToString(CultureInfo.InvariantCulture), "Snippet deleted.", Now());
        await transaction.CommitAsync(cancellationToken);
    }

    // ---------------------------------------------------------------------------------------------------
    // Time tracking
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Sets the estimate, as a whole. Zero clears it. The author or anybody who could push.</summary>
    public async Task SetTimeEstimateAsync(
        AuthenticatedIdentity actor,
        long projectId,
        CommentSubject subject,
        long iid,
        long seconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (seconds < 0 || seconds > TimeTracking.MaximumSeconds) throw new ArgumentOutOfRangeException(nameof(seconds));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity editor = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);
        CollaborationOwnerRow? existing = await db.QuerySingleOrDefaultAsync<CollaborationOwnerRow>(subject == CommentSubject.Issue
            ? "SELECT Id, AuthorUserId, State FROM Issues WHERE ProjectId = @ProjectId AND Iid = @Iid;"
            : "SELECT Id, AuthorUserId, State FROM MergeRequests WHERE ProjectId = @ProjectId AND Iid = @Iid;",
            new CollaborationItemParameters(projectId, iid), transaction);
        if (existing is null) throw new CollaborationNotFoundException(subject == CommentSubject.Issue ? "Issue is unavailable." : "Merge proposal is unavailable.");
        await RequireTriagerAsync(db, transaction, editor, projectId, existing.AuthorUserId, cancellationToken);
        await db.ExecuteAsync(subject == CommentSubject.Issue
            ? "UPDATE Issues SET TimeEstimateSeconds = @Value WHERE Id = @Id;"
            : "UPDATE MergeRequests SET TimeEstimateSeconds = @Value WHERE Id = @Id;",
            new LongValueParameters(existing.Id, seconds), transaction);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Records time spent. Any participant may record their own; a negative amount takes time back, which
    /// is how GitLab's API corrects an entry, and is refused if it would take the total below zero.
    /// </summary>
    public async Task AddTimeSpentAsync(
        AuthenticatedIdentity actor,
        long projectId,
        CommentSubject subject,
        long iid,
        long seconds,
        DateOnly? spentOn,
        string summary = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (seconds == 0 || Math.Abs(seconds) > TimeTracking.MaximumSeconds) throw new ArgumentOutOfRangeException(nameof(seconds));
        string cleanSummary = RequireBody(summary, nameof(summary), allowEmpty: true);
        if (cleanSummary.Length > 255) throw new ArgumentException("A summary is at most 255 characters.", nameof(summary));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity who = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);
        long subjectId = await ResolveSubjectAsync(db, transaction, projectId, subject, iid);
        long total = await db.QuerySingleAsync<long>(
            "SELECT COALESCE(SUM(Seconds), 0) FROM TimeEntries WHERE SubjectType = @SubjectType AND SubjectId = @SubjectId;",
            new SubjectParameters(SubjectName(subject), subjectId), transaction);
        if (total + seconds < 0) throw new ArgumentException("That would take the time spent below zero.", nameof(seconds));
        string now = Now();
        await db.ExecuteAsync("""
            INSERT INTO TimeEntries (SubjectType, SubjectId, UserId, Seconds, SpentOn, Summary, CreatedAtUtc)
            VALUES (@SubjectType, @SubjectId, @UserId, @Seconds, @SpentOn, @Summary, @CreatedAtUtc);
            """, new TimeInsertParameters(SubjectName(subject), subjectId, who.UserId, seconds,
                Day(spentOn ?? DateOnly.FromDateTime(DateTime.UtcNow))!, cleanSummary, now), transaction);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Clears every entry. The author or a Maintainer, because it removes other people's records.</summary>
    public async Task ResetTimeSpentAsync(
        AuthenticatedIdentity actor,
        long projectId,
        CommentSubject subject,
        long iid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity editor = await RequireParticipantAsync(db, transaction, actor, projectId, cancellationToken);
        CollaborationOwnerRow? existing = await db.QuerySingleOrDefaultAsync<CollaborationOwnerRow>(subject == CommentSubject.Issue
            ? "SELECT Id, AuthorUserId, State FROM Issues WHERE ProjectId = @ProjectId AND Iid = @Iid;"
            : "SELECT Id, AuthorUserId, State FROM MergeRequests WHERE ProjectId = @ProjectId AND Iid = @Iid;",
            new CollaborationItemParameters(projectId, iid), transaction);
        if (existing is null) throw new CollaborationNotFoundException(subject == CommentSubject.Issue ? "Issue is unavailable." : "Merge proposal is unavailable.");
        await RequireEditorAsync(db, transaction, editor, projectId, existing.AuthorUserId, textChanged: true, cancellationToken);
        await db.ExecuteAsync("DELETE FROM TimeEntries WHERE SubjectType = @SubjectType AND SubjectId = @SubjectId;",
            new SubjectParameters(SubjectName(subject), existing.Id), transaction);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TimeEntry>> ListTimeEntriesAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        CommentSubject subject,
        long iid,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireReaderAsync(db, null, identity, projectId, cancellationToken);
        long subjectId = await ResolveSubjectAsync(db, null, projectId, subject, iid);
        IEnumerable<TimeEntryRow> rows = await db.QueryAsync<TimeEntryRow>("""
            SELECT t.Id, t.UserId, u.Username, t.Seconds, t.SpentOn, t.Summary, t.CreatedAtUtc
            FROM TimeEntries t JOIN Users u ON u.Id = t.UserId
            WHERE t.SubjectType = @SubjectType AND t.SubjectId = @SubjectId
            ORDER BY t.Id DESC;
            """, new SubjectParameters(SubjectName(subject), subjectId));
        return [.. rows.Select(row => new TimeEntry(row.Id, row.UserId, row.Username, row.Seconds,
            DateOnly.ParseExact(row.SpentOn, "yyyy-MM-dd", CultureInfo.InvariantCulture), row.Summary, Instant(row.CreatedAtUtc)))];
    }

    private const string WikiSelect = """
        SELECT p.Id, p.ProjectId, p.Slug, p.Title, p.Content, c.Username AS CreatedByUsername, e.Username AS UpdatedByUsername,
               p.CreatedAtUtc, p.UpdatedAtUtc,
               (SELECT COUNT(*) FROM WikiPageVersions v WHERE v.PageId = p.Id) AS Versions
        FROM WikiPages p
        JOIN Users c ON c.Id = p.CreatedByUserId
        JOIN Users e ON e.Id = p.UpdatedByUserId

        """;

    private const string SnippetSelect = """
        SELECT s.Id, s.ProjectId, s.Title, s.FileName, s.Description, s.Content, s.AuthorUserId, u.Username AS AuthorUsername,
               s.CreatedAtUtc, s.UpdatedAtUtc
        FROM Snippets s
        JOIN Users u ON u.Id = s.AuthorUserId

        """;

    /// <summary>Writing prose about the project needs what organising it needs: the permission to push.</summary>
    private static async Task<AuthenticatedIdentity> RequireWriterAsync(SqliteConnection db, SqliteTransaction transaction,
        AuthenticatedIdentity actor, long projectId, CancellationToken cancellationToken)
    {
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId, ProjectAction.PushRepository, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException("Writing the wiki needs the permission to push.");
        PermissionDecision api = await DecideAsync(db, transaction, actor, projectId, ProjectAction.WriteCollaboration, cancellationToken);
        if (!api.Allowed) throw new UnauthorizedAccessException(api.Explanation);
        return await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
    }

    /// <summary>
    /// A page's address from its title: lowercase letters, digits and hyphens, so the address is safe in a
    /// URL and stable across what anybody's browser does to case. A title that leaves nothing is refused.
    /// </summary>
    public static string Slugify(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        StringBuilder slug = new();
        bool hyphen = false;
        foreach (char character in title.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character)) { slug.Append(character); hyphen = false; }
            else if (!hyphen && slug.Length > 0) { slug.Append('-'); hyphen = true; }
        }
        string result = slug.ToString().TrimEnd('-');
        if (result.Length == 0) throw new ArgumentException("A title needs at least one letter or digit.", nameof(title));
        return result.Length > 120 ? result[..120].TrimEnd('-') : result;
    }

    private static string RequireDocument(string value, int maximumBytes, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);
        string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (Encoding.UTF8.GetByteCount(normalized) > maximumBytes)
            throw new ArgumentException($"At most {maximumBytes / 1024} KiB.", parameterName);
        if (normalized.Contains('\0')) throw new ArgumentException("Text, not bytes.", parameterName);
        return normalized;
    }

    private static string RequireMessage(string value)
    {
        string trimmed = (value ?? "").Trim();
        return trimmed.Length > 255 ? trimmed[..255] : trimmed;
    }

    private static WikiPage ToWikiPage(WikiRow row) => new(row.Id, row.ProjectId, row.Slug, row.Title, row.Content,
        row.CreatedByUsername, row.UpdatedByUsername, Instant(row.CreatedAtUtc), Instant(row.UpdatedAtUtc), row.Versions);

    private static WikiPageVersion ToWikiVersion(WikiVersionRow row) =>
        new(row.Id, row.Title, row.Content, row.Message, row.AuthorUsername, Instant(row.CreatedAtUtc));

    private static Snippet ToSnippet(SnippetRow row) => new(row.Id, row.ProjectId, row.Title, row.FileName, row.Description,
        row.Content, row.AuthorUserId, row.AuthorUsername, Instant(row.CreatedAtUtc), Instant(row.UpdatedAtUtc));
}

internal sealed record WikiInsertParameters(long ProjectId, string Slug, string Title, string Content, long UserId, string Now);
internal sealed record WikiUpdateParameters(long Id, string Title, string Content, long UserId, string Now);
internal sealed record WikiVersionParameters(long PageId, string Title, string Content, string Message, long AuthorUserId, string CreatedAtUtc);
internal sealed record WikiVersionKeyParameters(long ProjectId, string Slug, long Id);
internal sealed record SnippetInsertParameters(long ProjectId, string Title, string FileName, string Description, string Content, long AuthorUserId, string Now);
internal sealed record SnippetUpdateParameters(long Id, string? Title, string? FileName, string? Content, string? Description, string Now);
internal sealed record LongValueParameters(long Id, long Value);
internal sealed record TimeInsertParameters(string SubjectType, long SubjectId, long UserId, long Seconds, string SpentOn, string Summary, string CreatedAtUtc);

internal sealed class WikiRow
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string CreatedByUsername { get; set; } = "";
    public string UpdatedByUsername { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
    public string UpdatedAtUtc { get; set; } = "";
    public long Versions { get; set; }
}

internal sealed class WikiVersionRow
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Message { get; set; } = "";
    public string AuthorUsername { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
}

internal sealed class SnippetRow
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string Title { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Content { get; set; } = "";
    public long AuthorUserId { get; set; }
    public string AuthorUsername { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
    public string UpdatedAtUtc { get; set; } = "";
}

internal sealed class TimeEntryRow
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Username { get; set; } = "";
    public long Seconds { get; set; }
    public string SpentOn { get; set; } = "";
    public string Summary { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
}
