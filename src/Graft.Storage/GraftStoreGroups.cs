using System.Globalization;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// Groups: namespaces with members, which may hold groups. A role in a group is held in every group and on
/// every project beneath it, at the same level; a direct grant on a project that is higher stands. An
/// Owner manages the group; on its projects an Owner is a Maintainer.
///
/// <para>
/// Anyone signed in may create a top-level group, whose path shares the namespace of usernames and may not
/// collide with one. A subgroup needs Owner of the parent. A group is deleted only when it holds no projects
/// and no subgroups: a deletion that took projects with it would be project deletions without their own
/// confirmation and audit, which [ADR 0013] made deliberate.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    /// <summary>How many groups may sit above a project. Deep enough for any team; shallow enough to read.</summary>
    public const int MaximumGroupDepth = 5;

    /// <summary>Every group paired with itself and each of its ancestors. SQLite cannot correlate a recursive
    /// query with an outer row, so the closure is built once at the top of a statement and joined.</summary>
    internal const string GroupAncestors = """
        WITH RECURSIVE ancestors(GroupId, AncestorId) AS (
            SELECT Id, Id FROM Groups
            UNION ALL SELECT a.GroupId, g.ParentGroupId FROM ancestors a JOIN Groups g ON g.Id = a.AncestorId WHERE g.ParentGroupId IS NOT NULL)

        """;

    private const string GroupSelect = """
        SELECT g.Id, g.ParentGroupId, g.Path, g.FullPath, g.NormalizedFullPath, g.Name, g.Description, g.CreatedAtUtc,
               (SELECT COUNT(*) FROM Projects p WHERE p.GroupId = g.Id AND p.State = 'ready') AS ProjectCount,
               (SELECT COUNT(*) FROM Groups c WHERE c.ParentGroupId = g.Id) AS SubgroupCount
        FROM Groups g
        """;

    public async Task<Group> CreateGroupAsync(AuthenticatedIdentity actor, string path, string name, string description, long? parentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string normalizedPath = NormalizeName(path, nameof(path));
        if (ReservedNames.Contains(normalizedPath) || normalizedPath is "projects" or "api" or "-")
            throw new ArgumentException("That name is reserved by the application.", nameof(path));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 200 || description.Length > 2_000) throw new ArgumentException("Group text is too long.");

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        string fullPath = path;
        string normalizedFullPath = normalizedPath;
        if (parentId is long parent)
        {
            GroupRow parentRow = await db.QuerySingleOrDefaultAsync<GroupRow>(GroupSelect + " WHERE g.Id = @Id;", new IdParameters(parent), transaction)
                ?? throw new GroupNotFoundException();
            GroupRole? role = await GroupRoleForAsync(db, transaction, liveActor.UserId, parent);
            // Somebody outside the group is not told it exists; a member without Owner is told what is needed.
            if (!liveActor.IsAdministrator && role is null) throw new GroupNotFoundException();
            if (!liveActor.IsAdministrator && role != GroupRole.Owner)
                throw new UnauthorizedAccessException("Creating a subgroup needs the Owner role in the parent group.");
            if (parentRow.FullPath.Count(c => c == '/') + 1 >= MaximumGroupDepth)
                throw new ArgumentException("Groups cannot be nested deeper than " + MaximumGroupDepth.ToString(CultureInfo.InvariantCulture) + " levels.", nameof(parentId));
            fullPath = parentRow.FullPath + "/" + path;
            normalizedFullPath = parentRow.NormalizedFullPath + "/" + normalizedPath;
        }
        else if (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Users WHERE NormalizedUsername = @NormalizedUsername;",
            new NormalizedNameParameters(normalizedPath), transaction) > 0)
            throw new GroupConflictException("An account already has this name.");

        string now = Now();
        long id;
        try
        {
            id = await db.QuerySingleAsync<long>("""
                INSERT INTO Groups (ParentGroupId, Path, NormalizedPath, FullPath, NormalizedFullPath, Name, Description, CreatedAtUtc)
                VALUES (@ParentGroupId, @Path, @NormalizedPath, @FullPath, @NormalizedFullPath, @Name, @Description, @CreatedAtUtc)
                RETURNING Id;
                """, new GroupInsertParameters(parentId, path, normalizedPath, fullPath, normalizedFullPath, name, description, now), transaction);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            throw new GroupConflictException("A group with this path already exists.", error);
        }
        // The creator owns what they made. A subgroup's creator is already an Owner above it; the explicit row
        // keeps the group's own member list honest about who made it.
        await db.ExecuteAsync("""
            INSERT INTO GroupMemberships (GroupId, UserId, Role, GrantedByUserId, CreatedAtUtc) VALUES (@GroupId, @UserId, @Role, @GrantedByUserId, @CreatedAtUtc);
            """, new GroupMemberParameters(id, liveActor.UserId, (int)GroupRole.Owner, liveActor.UserId, now), transaction);
        await AuditAsync(db, transaction, liveActor.UserId, "create-group", "group", id.ToString(CultureInfo.InvariantCulture), "Group " + fullPath + " created.", now);
        await transaction.CommitAsync(cancellationToken);
        return new(id, parentId, path, fullPath, name, description, Instant(now), 0, 0);
    }

    /// <summary>The groups the caller is a member of, directly or through an ancestor; every group for an administrator.</summary>
    public async Task<IReadOnlyList<Group>> ListGroupsAsync(AuthenticatedIdentity actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        IEnumerable<GroupRow> rows = await db.QueryAsync<GroupRow>(GroupAncestors + GroupSelect + """

            WHERE @IsAdministrator = 1 OR EXISTS (
                SELECT 1 FROM GroupMemberships gm JOIN ancestors a ON a.AncestorId = gm.GroupId
                WHERE gm.UserId = @UserId AND a.GroupId = g.Id)
            ORDER BY g.NormalizedFullPath;
            """, new GroupListParameters(liveActor.UserId, liveActor.IsAdministrator ? 1 : 0));
        return rows.Select(ToGroup).ToArray();
    }

    /// <summary>A group by id or by full path, if the caller may see it: a member at any level, or an administrator.</summary>
    public async Task<Group?> FindGroupAsync(AuthenticatedIdentity actor, string idOrFullPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        GroupRow? row = await FindGroupRowAsync(db, null, idOrFullPath);
        if (row is null) return null;
        if (!liveActor.IsAdministrator && await GroupRoleForAsync(db, null, liveActor.UserId, row.Id) is null) return null;
        return ToGroup(row);
    }

    public async Task<IReadOnlyList<Group>> ListSubgroupsAsync(AuthenticatedIdentity actor, long groupId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        if (!liveActor.IsAdministrator && await GroupRoleForAsync(db, null, liveActor.UserId, groupId) is null) throw new GroupNotFoundException();
        IEnumerable<GroupRow> rows = await db.QueryAsync<GroupRow>(GroupSelect + " WHERE g.ParentGroupId = @Id ORDER BY g.NormalizedPath;",
            new IdParameters(groupId));
        return rows.Select(ToGroup).ToArray();
    }

    /// <summary>The group's projects the caller may read: all of them for a member or administrator, which is who gets here.</summary>
    public async Task<ProjectPage> ListGroupProjectsAsync(AuthenticatedIdentity actor, long groupId, int skip, int take, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (skip < 0 || take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        if (!liveActor.IsAdministrator && await GroupRoleForAsync(db, null, liveActor.UserId, groupId) is null) throw new GroupNotFoundException();
        IEnumerable<ProjectDetailsRow> rows = await db.QueryAsync<ProjectDetailsRow>("""
            SELECT p.Id, p.OwnerUserId, COALESCE(g.FullPath, u.Username) AS Namespace, p.GroupId, p.Slug, p.Name, p.Description, p.StorageId, p.CreatedAtUtc,
                   p.DefaultBranch, p.Visibility, p.PagePublished, p.GlobalSlug
            FROM Projects p
            JOIN Users u ON u.Id = p.OwnerUserId
            LEFT JOIN Groups g ON g.Id = p.GroupId
            WHERE p.GroupId = @Id AND p.State = 'ready'
            ORDER BY p.Id
            LIMIT @Take OFFSET @Skip;
            """, new GroupProjectsParameters(groupId, take, skip));
        long total = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Projects p WHERE p.GroupId = @Id AND p.State = 'ready';", new IdParameters(groupId));
        return new(rows.Select(ToDetails).ToArray(), total);
    }

    /// <summary>The group's members: its own rows, and those inherited from ancestors, each person once at their greatest role.</summary>
    public async Task<IReadOnlyList<GroupMember>> ListGroupMembersAsync(AuthenticatedIdentity actor, long groupId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        if (!liveActor.IsAdministrator && await GroupRoleForAsync(db, null, liveActor.UserId, groupId) is null) throw new GroupNotFoundException();
        IEnumerable<GroupMemberRow> rows = await db.QueryAsync<GroupMemberRow>("""
            WITH RECURSIVE chain(Id) AS (
                SELECT @Id
                UNION ALL SELECT g.ParentGroupId FROM Groups g JOIN chain c ON g.Id = c.Id WHERE g.ParentGroupId IS NOT NULL)
            SELECT u.Id AS UserId, u.Username, MAX(gm.Role) AS Role, MIN(CASE WHEN gm.GroupId = @Id THEN 0 ELSE 1 END) AS Inherited
            FROM GroupMemberships gm
            JOIN Users u ON u.Id = gm.UserId
            WHERE gm.GroupId IN (SELECT Id FROM chain) AND u.Disabled = 0
            GROUP BY u.Id, u.Username
            ORDER BY u.NormalizedUsername;
            """, new IdParameters(groupId));
        return rows.Select(row => new GroupMember(row.UserId, row.Username, (GroupRole)row.Role, row.Inherited != 0)).ToArray();
    }

    /// <summary>Gives a person a role in the group, or changes it. Owners only. The last Owner cannot be demoted.</summary>
    public async Task SetGroupMemberAsync(AuthenticatedIdentity actor, long groupId, long userId, GroupRole role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        await RequireGroupOwnerAsync(db, transaction, liveActor, groupId);
        long active = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Users WHERE Id = @Id AND Disabled = 0;", new IdParameters(userId), transaction);
        if (active != 1) throw new InvalidOperationException("That account is unavailable.");
        if (role != GroupRole.Owner && await IsLastOwnerAsync(db, transaction, groupId, userId))
            throw new InvalidOperationException("A group keeps at least one Owner.");
        string now = Now();
        await db.ExecuteAsync("""
            INSERT INTO GroupMemberships (GroupId, UserId, Role, GrantedByUserId, CreatedAtUtc) VALUES (@GroupId, @UserId, @Role, @GrantedByUserId, @CreatedAtUtc)
            ON CONFLICT(GroupId, UserId) DO UPDATE SET Role = excluded.Role, GrantedByUserId = excluded.GrantedByUserId, CreatedAtUtc = excluded.CreatedAtUtc;
            """, new GroupMemberParameters(groupId, userId, (int)role, liveActor.UserId, now), transaction);
        await AuditAsync(db, transaction, liveActor.UserId, "set-group-member", "group", groupId.ToString(CultureInfo.InvariantCulture),
            "User " + userId.ToString(CultureInfo.InvariantCulture) + " given the " + role + " role.", now);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Removes a person's own row in the group. Owners only; the last Owner stays.</summary>
    public async Task RemoveGroupMemberAsync(AuthenticatedIdentity actor, long groupId, long userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        await RequireGroupOwnerAsync(db, transaction, liveActor, groupId);
        if (await IsLastOwnerAsync(db, transaction, groupId, userId)) throw new InvalidOperationException("A group keeps at least one Owner.");
        int removed = await db.ExecuteAsync("DELETE FROM GroupMemberships WHERE GroupId = @GroupId AND UserId = @UserId;",
            new GroupUserParameters(groupId, userId), transaction);
        if (removed != 1) throw new InvalidOperationException("That person is not a member of this group.");
        await AuditAsync(db, transaction, liveActor.UserId, "remove-group-member", "group", groupId.ToString(CultureInfo.InvariantCulture),
            "User " + userId.ToString(CultureInfo.InvariantCulture) + " removed.", Now());
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Deletes an empty group. Owners only; a group with projects or subgroups is refused by name.</summary>
    public async Task DeleteGroupAsync(AuthenticatedIdentity actor, long groupId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        await RequireGroupOwnerAsync(db, transaction, liveActor, groupId);
        GroupRow row = await db.QuerySingleOrDefaultAsync<GroupRow>(GroupSelect + " WHERE g.Id = @Id;", new IdParameters(groupId), transaction)
            ?? throw new GroupNotFoundException();
        if (row.ProjectCount > 0) throw new InvalidOperationException("The group still holds projects; move or delete them first.");
        if (row.SubgroupCount > 0) throw new InvalidOperationException("The group still holds groups; delete them first.");
        await db.ExecuteAsync("DELETE FROM GroupMemberships WHERE GroupId = @Id;", new IdParameters(groupId), transaction);
        await db.ExecuteAsync("DELETE FROM Groups WHERE Id = @Id;", new IdParameters(groupId), transaction);
        await AuditAsync(db, transaction, liveActor.UserId, "delete-group", "group", groupId.ToString(CultureInfo.InvariantCulture),
            "Group " + row.FullPath + " deleted.", Now());
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Creates a project under a group, which the caller needs Developer or better in.</summary>
    public async Task<ProjectInfo> CreateProjectInGroupWithRepositoryAsync(
        AuthenticatedIdentity actor,
        long groupId,
        string slug,
        string name,
        Func<ProjectInfo, CancellationToken, Task> initializeRepository,
        string description = "",
        CancellationToken cancellationToken = default,
        string origin = "the API or browser")
    {
        ArgumentNullException.ThrowIfNull(initializeRepository);
        return await CreateProjectCoreAsync(actor, slug, name, description, initializeRepository, cancellationToken, origin, groupId);
    }

    /// <summary>The caller's role in the group, directly or through an ancestor, at its greatest; null for none.</summary>
    public async Task<GroupRole?> GetGroupRoleAsync(AuthenticatedIdentity actor, long groupId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        return liveActor.IsAdministrator ? GroupRole.Owner : await GroupRoleForAsync(db, null, liveActor.UserId, groupId);
    }

    private static async Task<GroupRole?> GroupRoleForAsync(SqliteConnection db, SqliteTransaction? transaction, long userId, long groupId)
    {
        long? role = await db.ExecuteScalarAsync<long?>("""
            WITH RECURSIVE chain(Id) AS (
                SELECT @GroupId
                UNION ALL SELECT g.ParentGroupId FROM Groups g JOIN chain c ON g.Id = c.Id WHERE g.ParentGroupId IS NOT NULL)
            SELECT MAX(gm.Role) FROM GroupMemberships gm WHERE gm.UserId = @UserId AND gm.GroupId IN (SELECT Id FROM chain);
            """, new GroupRoleParameters(groupId, userId), transaction);
        return role is null ? null : (GroupRole)role.Value;
    }

    private static async Task RequireGroupOwnerAsync(SqliteConnection db, SqliteTransaction transaction, AuthenticatedIdentity liveActor, long groupId)
    {
        long exists = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Groups WHERE Id = @Id;", new IdParameters(groupId), transaction);
        if (exists != 1) throw new GroupNotFoundException();
        if (liveActor.IsAdministrator) return;
        GroupRole? role = await GroupRoleForAsync(db, transaction, liveActor.UserId, groupId);
        if (role is null) throw new GroupNotFoundException();
        if (role != GroupRole.Owner) throw new UnauthorizedAccessException("This needs the Owner role in the group.");
    }

    private static async Task<bool> IsLastOwnerAsync(SqliteConnection db, SqliteTransaction transaction, long groupId, long userId)
    {
        long ownerRows = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM GroupMemberships WHERE GroupId = @GroupId AND Role = 40;",
            new GroupUserParameters(groupId, userId), transaction);
        long thisOne = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM GroupMemberships WHERE GroupId = @GroupId AND UserId = @UserId AND Role = 40;",
            new GroupUserParameters(groupId, userId), transaction);
        return thisOne == 1 && ownerRows == 1;
    }

    private static async Task<GroupRow?> FindGroupRowAsync(SqliteConnection db, SqliteTransaction? transaction, string idOrFullPath)
    {
        if (long.TryParse(idOrFullPath, NumberStyles.None, CultureInfo.InvariantCulture, out long id))
            return await db.QuerySingleOrDefaultAsync<GroupRow>(GroupSelect + " WHERE g.Id = @Id;", new IdParameters(id), transaction);
        string normalized;
        try { normalized = NormalizeNamespacePath(idOrFullPath); }
        catch (ArgumentException) { return null; }
        return await db.QuerySingleOrDefaultAsync<GroupRow>(GroupSelect + " WHERE g.NormalizedFullPath = @NormalizedFullPath;",
            new GroupPathParameters(normalized), transaction);
    }

    private static Group ToGroup(GroupRow row) =>
        new(row.Id, row.ParentGroupId, row.Path, row.FullPath, row.Name, row.Description, Instant(row.CreatedAtUtc), row.ProjectCount, row.SubgroupCount);
}

public sealed class GroupNotFoundException() : Exception("Group is unavailable.");
public sealed class GroupConflictException : Exception
{
    public GroupConflictException(string message) : base(message) { }
    public GroupConflictException(string message, Exception inner) : base(message, inner) { }
}

internal sealed class GroupRow
{
    public long Id { get; set; }
    public long? ParentGroupId { get; set; }
    public string Path { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string NormalizedFullPath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
    public long ProjectCount { get; set; }
    public long SubgroupCount { get; set; }
}
internal sealed record GroupInsertParameters(long? ParentGroupId, string Path, string NormalizedPath, string FullPath, string NormalizedFullPath, string Name, string Description, string CreatedAtUtc);
internal sealed record GroupMemberParameters(long GroupId, long UserId, int Role, long GrantedByUserId, string CreatedAtUtc);
internal sealed record GroupListParameters(long UserId, int IsAdministrator);
internal sealed record GroupProjectsParameters(long Id, int Take, int Skip);
internal sealed record GroupMemberRow(long UserId, string Username, long Role, long Inherited);
internal sealed record GroupUserParameters(long GroupId, long UserId);
internal sealed record GroupRoleParameters(long GroupId, long UserId);
internal sealed record GroupPathParameters(string NormalizedFullPath);
