using System.Globalization;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>Where a project came from and how many came from it.</summary>
public sealed record ForkInfo(long? UpstreamId, string? UpstreamPath, long ForksCount);

/// <summary>
/// Forks: a project copied into another account so that somebody who cannot write to it can still propose
/// a change to it.
/// </summary>
public sealed partial class GraftStore
{
    /// <summary>
    /// Creates a fork of <paramref name="upstreamId"/> in the actor's own namespace. Anybody who may read the
    /// repository may fork it, because a fork is a clone with an address, and the clone was already theirs
    /// to take. The fork keeps the upstream's slug where that is free in the actor's namespace, and its
    /// visibility, so that the people who could see the original can see what was proposed from the copy.
    /// <paramref name="initializeRepository"/> copies the storage; the row exists only once that succeeds.
    /// </summary>
    public async Task<ProjectInfo> ForkProjectAsync(
        AuthenticatedIdentity actor,
        long upstreamId,
        Func<ProjectInfo, ProjectInfo, CancellationToken, Task> initializeRepository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(initializeRepository);
        ProjectDetails? upstream;
        await using (SqliteConnection db = await OpenConnectionAsync(cancellationToken))
        {
            PermissionDecision read = await DecideAsync(db, null, actor, upstreamId, ProjectAction.BrowseRepository, cancellationToken);
            if (!read.Allowed) throw new UnauthorizedAccessException(read.Explanation);
            AuthenticatedIdentity live = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
            upstream = await GetReadyProjectAsync(upstreamId, cancellationToken);
            if (upstream is null) throw new UnauthorizedAccessException("Project is unavailable.");
            if (upstream.Project.OwnerUserId == live.UserId)
                throw new CollaborationConflictException("A project cannot be forked into the namespace it already lives in.");
            long already = await db.QuerySingleAsync<long>(
                "SELECT COUNT(*) FROM Projects WHERE ForkedFromProjectId = @ProjectId AND OwnerUserId = @UserId AND State = 'ready';",
                new PermissionQueryParameters(upstreamId, live.UserId));
            if (already != 0) throw new CollaborationConflictException("You already have a fork of this project.");
        }

        string slug = upstream.Project.Slug;
        if (await FindReadyProjectAsync(actor.Username, slug, cancellationToken) is not null) slug = slug + "-fork";
        ProjectInfo fork = await CreateProjectCoreAsync(actor, slug, upstream.Project.Name, upstream.Description,
            (created, token) => initializeRepository(upstream.Project, created, token), cancellationToken, "a fork of " + upstream.Project.Namespace + "/" + upstream.Project.Slug);

        await using (SqliteConnection db = await OpenConnectionAsync(cancellationToken))
        await using (SqliteTransaction transaction = db.BeginTransaction(deferred: false))
        {
            await db.ExecuteAsync("UPDATE Projects SET ForkedFromProjectId = @Value, Visibility = @Visibility, DefaultBranch = @DefaultBranch WHERE Id = @Id;",
                new ForkLinkParameters(fork.Id, upstreamId, VisibilityNameOf(upstream.Project.Visibility), upstream.DefaultBranch), transaction);
            await AuditAsync(db, transaction, actor.UserId, "fork-project", "project", fork.Id.ToString(CultureInfo.InvariantCulture),
                $"Forked from {upstream.Project.Namespace}/{upstream.Project.Slug}.", Now());
            await transaction.CommitAsync(cancellationToken);
        }
        return fork with { Visibility = upstream.Project.Visibility, DefaultBranch = upstream.DefaultBranch };
    }

    /// <summary>Where a project came from, and how many forks of it there are. Takes no identity: both facts
    /// are about the project, and whoever asks has already been shown the project.</summary>
    public async Task<ForkInfo> GetForkInfoAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        ForkInfoRow? row = await db.QuerySingleOrDefaultAsync<ForkInfoRow>("""
            SELECT p.ForkedFromProjectId AS UpstreamId, u.Username AS UpstreamNamespace, up.Slug AS UpstreamSlug,
                   (SELECT COUNT(*) FROM Projects f WHERE f.ForkedFromProjectId = p.Id AND f.State = 'ready') AS ForksCount
            FROM Projects p
            LEFT JOIN Projects up ON up.Id = p.ForkedFromProjectId AND up.State = 'ready'
            LEFT JOIN Users u ON u.Id = up.OwnerUserId
            WHERE p.Id = @Id;
            """, new IdParameters(projectId));
        if (row is null) return new(null, null, 0);
        return new(row.UpstreamSlug is null ? null : row.UpstreamId, row.UpstreamSlug is null ? null : row.UpstreamNamespace + "/" + row.UpstreamSlug, row.ForksCount);
    }

    /// <summary>The forks of a project the caller may see, which for a stranger is the public ones.</summary>
    public async Task<IReadOnlyList<ProjectDetails>> ListForksAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        bool includePublic,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        PermissionDecision read = await DecideAsync(db, null, identity, projectId, ProjectAction.BrowseRepository, cancellationToken);
        if (!read.Allowed) throw new UnauthorizedAccessException(read.Explanation);
        AuthenticatedIdentity? live = identity is null ? null : await FindLiveIdentityAsync(db, null, identity, cancellationToken);
        IEnumerable<ProjectDetailsRow> rows = await db.QueryAsync<ProjectDetailsRow>("""
            SELECT p.Id, p.OwnerUserId, COALESCE(g.FullPath, u.Username) AS Namespace, p.GroupId, p.Slug, p.Name, p.Description, p.StorageId, p.CreatedAtUtc,
                   p.DefaultBranch, p.Visibility, p.PagePublished, p.GlobalSlug
            FROM Projects p
            JOIN Users u ON u.Id = p.OwnerUserId
            LEFT JOIN Groups g ON g.Id = p.GroupId
            WHERE p.ForkedFromProjectId = @ForkedFrom AND
            """ + VisibleProjectClause + """

            ORDER BY p.Id;
            """, new ForkListParameters(projectId, live?.UserId ?? 0, live?.IsAdministrator == true ? 1 : 0, includePublic ? 1 : 0));
        return [.. rows.Select(ToDetails)];
    }

    private static string VisibilityNameOf(ProjectVisibility visibility) => visibility switch
    {
        ProjectVisibility.Public => "public",
        ProjectVisibility.Unlisted => "unlisted",
        _ => "private"
    };
}

internal sealed record ForkLinkParameters(long Id, long Value, string Visibility, string? DefaultBranch);
internal sealed record ForkListParameters(long ForkedFrom, long UserId, int IsAdministrator, int IncludePublic);

internal sealed class ForkInfoRow
{
    public long? UpstreamId { get; set; }
    public string? UpstreamNamespace { get; set; }
    public string? UpstreamSlug { get; set; }
    public long ForksCount { get; set; }
}
