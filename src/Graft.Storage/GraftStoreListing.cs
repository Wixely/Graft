using System.Globalization;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// What a project's description needs beyond its own row, for one caller: whether it and its owner have
/// avatars, where it was forked from, its stars, its import, and the caller's decision for each action asked.
/// </summary>
public sealed record ProjectListingFacts(
    bool HasAvatar,
    bool OwnerHasAvatar,
    ForkInfo Forks,
    long Stars,
    ProjectMirror? Mirror,
    IReadOnlyDictionary<ProjectAction, PermissionDecision> Decisions);

public sealed partial class GraftStore
{
    /// <summary>
    /// The <see cref="ProjectListingFacts"/> of many projects at once, on one connection: a query for the
    /// rows, one for the imports, and one role lookup per owning group, rather than a connection and a query
    /// per fact per project. The decisions are the ones <see cref="GetPermissionAsync(AuthenticatedIdentity?, long, ProjectAction, CancellationToken)"/>
    /// would give, made from the same row and the same rules; for a project that is not ready they refuse, as
    /// that does. An id with no project is left out.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, ProjectListingFacts>> GetProjectListingFactsAsync(
        AuthenticatedIdentity? identity,
        IReadOnlyCollection<long> projectIds,
        IReadOnlyList<ProjectAction> actions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        ArgumentNullException.ThrowIfNull(actions);
        Dictionary<long, ProjectListingFacts> facts = [];
        if (projectIds.Count == 0) return facts;
        // The ids travel as one JSON array, which SQLite unpacks itself, so the statement is the same text
        // however many there are.
        ListingParameters parameters = new(
            "[" + string.Join(",", projectIds.Select(id => id.ToString(CultureInfo.InvariantCulture))) + "]",
            identity?.UserId ?? 0);

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: true);
        IEnumerable<ListingRow> rows = await db.QueryAsync<ListingRow>("""
            SELECT p.Id, p.State, p.OwnerUserId, p.Visibility, p.PagePublished, p.GroupId, m.Role,
                   EXISTS (SELECT 1 FROM Avatars a WHERE a.OwnerType = 'project' AND a.OwnerId = p.Id) AS HasAvatar,
                   EXISTS (SELECT 1 FROM Avatars a WHERE a.OwnerType = 'user' AND a.OwnerId = p.OwnerUserId) AS OwnerHasAvatar,
                   (SELECT COUNT(*) FROM ProjectStars s WHERE s.ProjectId = p.Id) AS Stars,
                   p.ForkedFromProjectId AS UpstreamId, u.Username AS UpstreamNamespace, up.Slug AS UpstreamSlug,
                   (SELECT COUNT(*) FROM Projects f WHERE f.ForkedFromProjectId = p.Id AND f.State = 'ready') AS ForksCount
            FROM Projects p
            LEFT JOIN ProjectMemberships m ON m.ProjectId = p.Id AND m.UserId = @UserId
            LEFT JOIN Projects up ON up.Id = p.ForkedFromProjectId AND up.State = 'ready'
            LEFT JOIN Users u ON u.Id = up.OwnerUserId
            WHERE p.Id IN (SELECT value FROM json_each(@Ids));
            """, parameters, transaction);
        Dictionary<long, ProjectMirror> mirrors = (await db.QueryAsync<MirrorRow>(
                MirrorSelect + " WHERE m.ProjectId IN (SELECT value FROM json_each(@Ids));", parameters, transaction))
            .Select(ToMirror)
            .ToDictionary(mirror => mirror.ProjectId);

        AuthenticatedIdentity? live = identity is null ? null : await FindLiveIdentityAsync(db, transaction, identity, cancellationToken);
        Dictionary<long, GroupRole?> groupRoles = [];
        foreach (ListingRow row in rows)
        {
            ProjectRole? groupRole = null;
            if (row.GroupId is long groupId && live is not null)
            {
                if (!groupRoles.TryGetValue(groupId, out GroupRole? held))
                    groupRoles[groupId] = held = await GroupRoleForAsync(db, transaction, live.UserId, groupId);
                if (held is GroupRole role) groupRole = role >= GroupRole.Maintainer ? ProjectRole.Maintainer : (ProjectRole)(int)role;
            }
            ProjectAccess access = new(
                row.OwnerUserId,
                ParseVisibility(row.Visibility),
                row.PagePublished != 0,
                row.Role is null ? null : (ProjectRole)row.Role.Value,
                groupRole);
            Dictionary<ProjectAction, PermissionDecision> decisions = [];
            foreach (ProjectAction action in actions)
                decisions[action] = row.State == "ready"
                    ? ProjectAuthorization.Decide(live, access, action)
                    : new(false, null, "Unavailable", "Project is unavailable.");
            facts[row.Id] = new(
                row.HasAvatar != 0,
                row.OwnerHasAvatar != 0,
                new(row.UpstreamSlug is null ? null : row.UpstreamId, row.UpstreamSlug is null ? null : row.UpstreamNamespace + "/" + row.UpstreamSlug, row.ForksCount),
                row.Stars,
                mirrors.GetValueOrDefault(row.Id),
                decisions);
        }
        return facts;
    }
}

internal sealed record ListingParameters(string Ids, long UserId);

internal sealed class ListingRow
{
    public long Id { get; set; }
    public string State { get; set; } = "";
    public long OwnerUserId { get; set; }
    public string Visibility { get; set; } = "private";
    public long PagePublished { get; set; }
    public long? GroupId { get; set; }
    public long? Role { get; set; }
    public long HasAvatar { get; set; }
    public long OwnerHasAvatar { get; set; }
    public long Stars { get; set; }
    public long? UpstreamId { get; set; }
    public string? UpstreamNamespace { get; set; }
    public string? UpstreamSlug { get; set; }
    public long ForksCount { get; set; }
}
