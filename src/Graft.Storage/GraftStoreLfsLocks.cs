using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>A locked path in a project's repository, held by one person until they or a Maintainer let it go (ADR 0057).</summary>
public sealed record LfsLockInfo(long Id, long ProjectId, string Path, long OwnerUserId, string OwnerUsername, DateTimeOffset LockedAtUtc);

/// <summary>
/// Git LFS file locks (ADR 0057), and where a published page's site starts (ADR 0056). Neither takes an identity to
/// decide with: the routes that use them have already decided, with the same permission as the Git transport, so
/// these are the rows and nothing else.
/// </summary>
public sealed partial class GraftStore
{
    /// <summary>More than any project should hold at once; a ceiling so a client cannot grow the table without end.</summary>
    public const int MaximumLfsLocksPerProject = 10_000;

    /// <summary>The longest path a lock may name, which is longer than any path Git itself is comfortable with.</summary>
    public const int MaximumLfsLockPathLength = 4096;

    private const string LfsLockSelect = """
        SELECT l.Id, l.ProjectId, l.Path, l.OwnerUserId, u.Username AS OwnerUsername, l.CreatedAtUtc
        FROM LfsLocks l
        JOIN Users u ON u.Id = l.OwnerUserId

        """;

    /// <summary>
    /// Locks a path for somebody. When the path is already locked the existing lock is returned with
    /// <c>Created</c> false, whoever holds it, so the caller can say who; the lock is never taken over.
    /// </summary>
    public async Task<(LfsLockInfo Lock, bool Created)> CreateLfsLockAsync(long projectId, long ownerUserId, string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path.Length > MaximumLfsLockPathLength) throw new ArgumentException("The path is too long to lock.", nameof(path));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        LfsLockRow? existing = await db.QuerySingleOrDefaultAsync<LfsLockRow>(LfsLockSelect + "WHERE l.ProjectId = @ProjectId AND l.Path = @Path;",
            new LfsLockPathParameters(projectId, path), transaction);
        if (existing is not null) return (ToLfsLock(existing), false);
        long held = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM LfsLocks WHERE ProjectId = @Id;", new IdParameters(projectId), transaction);
        if (held >= MaximumLfsLocksPerProject)
            throw new InvalidOperationException("This project already holds " + MaximumLfsLocksPerProject.ToString(CultureInfo.InvariantCulture) + " locks.");
        long id = await db.ExecuteScalarAsync<long>("""
            INSERT INTO LfsLocks (ProjectId, Path, OwnerUserId, CreatedAtUtc) VALUES (@ProjectId, @Path, @OwnerUserId, @CreatedAtUtc);
            SELECT last_insert_rowid();
            """, new LfsLockInsertParameters(projectId, path, ownerUserId, Now()), transaction);
        LfsLockRow created = await db.QuerySingleAsync<LfsLockRow>(LfsLockSelect + "WHERE l.Id = @Id;", new IdParameters(id), transaction);
        await transaction.CommitAsync(cancellationToken);
        return (ToLfsLock(created), true);
    }

    /// <summary>
    /// A project's locks in the order they were taken, optionally narrowed to one path or one id, starting after
    /// <paramref name="afterId"/>. Asks for one more than <paramref name="limit"/> so the caller knows whether to
    /// offer a next page.
    /// </summary>
    public async Task<IReadOnlyList<LfsLockInfo>> ListLfsLocksAsync(long projectId, string? path, long? id, long afterId, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        IEnumerable<LfsLockRow> rows = await db.QueryAsync<LfsLockRow>(LfsLockSelect + """
            WHERE l.ProjectId = @ProjectId AND l.Id > @AfterId
              AND (@Path IS NULL OR l.Path = @Path) AND (@LockId IS NULL OR l.Id = @LockId)
            ORDER BY l.Id
            LIMIT @Limit;
            """, new LfsLockListParameters(projectId, path, id, afterId, limit + 1));
        return rows.Select(ToLfsLock).ToArray();
    }

    /// <summary>Every lock in a project. Bounded by <see cref="MaximumLfsLocksPerProject"/>; a push is judged against these.</summary>
    public Task<IReadOnlyList<LfsLockInfo>> AllLfsLocksAsync(long projectId, CancellationToken cancellationToken = default) =>
        ListLfsLocksAsync(projectId, null, null, 0, MaximumLfsLocksPerProject, cancellationToken);

    public async Task<LfsLockInfo?> GetLfsLockAsync(long projectId, long id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        LfsLockRow? row = await db.QuerySingleOrDefaultAsync<LfsLockRow>(LfsLockSelect + "WHERE l.Id = @Id AND l.ProjectId = @ProjectId;",
            new LfsLockIdParameters(id, projectId));
        return row is null ? null : ToLfsLock(row);
    }

    /// <summary>
    /// Releases a lock. A lock released by somebody other than its holder is a Maintainer overriding a colleague, so
    /// it is written to the audit trail; a holder releasing their own is routine and is not.
    /// </summary>
    public async Task<LfsLockInfo?> DeleteLfsLockAsync(long actorUserId, long projectId, long id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        LfsLockRow? row = await db.QuerySingleOrDefaultAsync<LfsLockRow>(LfsLockSelect + "WHERE l.Id = @Id AND l.ProjectId = @ProjectId;",
            new LfsLockIdParameters(id, projectId), transaction);
        if (row is null) return null;
        await db.ExecuteAsync("DELETE FROM LfsLocks WHERE Id = @Id;", new IdParameters(id), transaction);
        if (row.OwnerUserId != actorUserId)
            await AuditAsync(db, transaction, actorUserId, "force-unlock-lfs", "project", projectId.ToString(CultureInfo.InvariantCulture),
                $"Lock on {row.Path} held by {row.OwnerUsername} released.", Now());
        await transaction.CommitAsync(cancellationToken);
        return ToLfsLock(row);
    }

    /// <summary>
    /// Records where a project's published site starts: null for no site, empty for the site's root, otherwise the
    /// name of the page artifact that is its front door. Derived from the repository, so it carries no actor.
    /// </summary>
    public async Task SetPageSiteAsync(long projectId, string? site, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await db.ExecuteAsync("UPDATE Projects SET PageSite = @Site WHERE Id = @Id;", new PageSiteParameters(projectId, site));
    }

    /// <summary>Where a project's site starts, as <see cref="SetPageSiteAsync"/> stored it; null when there is none.</summary>
    public async Task<string?> GetPageSiteAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        return await db.QuerySingleOrDefaultAsync<string?>("SELECT PageSite FROM Projects WHERE Id = @Id;", new IdParameters(projectId));
    }

    /// <summary>Every ready project that publishes a page, so their sites can be read once when the server starts.</summary>
    public async Task<IReadOnlyList<long>> ListPagePublishedProjectIdsAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        IEnumerable<long> ids = await db.QueryAsync<long>("SELECT Id FROM Projects WHERE PagePublished = 1 AND State = 'ready' ORDER BY Id;");
        return ids.ToArray();
    }

    private static LfsLockInfo ToLfsLock(LfsLockRow row) =>
        new(row.Id, row.ProjectId, row.Path, row.OwnerUserId, row.OwnerUsername, Parse(row.CreatedAtUtc));
}

internal sealed record LfsLockRow(long Id, long ProjectId, string Path, long OwnerUserId, string OwnerUsername, string CreatedAtUtc);
internal sealed record LfsLockPathParameters(long ProjectId, string Path);
internal sealed record LfsLockIdParameters(long Id, long ProjectId);
internal sealed record LfsLockInsertParameters(long ProjectId, string Path, long OwnerUserId, string CreatedAtUtc);
internal sealed record LfsLockListParameters(long ProjectId, string? Path, long? LockId, long AfterId, int Limit);
internal sealed record PageSiteParameters(long Id, string? Site);
