using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// The audit trail as one project sees it.
///
/// <para>
/// The trail is instance-wide and most of what it records is not a project's business: accounts, tokens,
/// keys, groups, the server's switches. What is a project's business is decided by how an entry names its
/// target. An entry about the project itself names its id; one about an issue, proposal or comment names
/// <c>projectId#iid</c>; one about a release, package, container image or snippet names a row whose project
/// is looked up. A row that no longer exists cannot be looked up, so an entry about a deleted release stays in
/// the instance trail and leaves the project's view; that is recorded as the rule's edge rather than hidden.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    /// <summary>The project's entries, newest first, for a caller who may manage the project.</summary>
    public async Task<AuditPage> ListProjectAuditEventsAsync(
        AuthenticatedIdentity actor,
        long projectId,
        int skip,
        int take,
        string? action = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (skip < 0 || take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        PermissionDecision permission = await DecideAsync(db, null, actor, projectId, ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);

        ProjectAuditParameters parameters = new(projectId.ToString(System.Globalization.CultureInfo.InvariantCulture), projectId,
            action, action is null ? 1 : 0, take, skip);
        const string scope = """
            (
                (e.TargetType = 'project' AND e.TargetId = @ProjectText)
                OR (e.TargetType IN ('issue', 'merge_request', 'comment') AND e.TargetId LIKE @ProjectText || '#%')
                OR (e.TargetType = 'release' AND e.TargetId IN (SELECT CAST(Id AS TEXT) FROM Releases WHERE ProjectId = @ProjectId))
                OR (e.TargetType = 'package' AND e.TargetId IN (SELECT CAST(Id AS TEXT) FROM Packages WHERE ProjectId = @ProjectId))
                OR (e.TargetType = 'container' AND e.TargetId IN (SELECT CAST(Id AS TEXT) FROM ContainerRepositories WHERE ProjectId = @ProjectId))
                OR (e.TargetType = 'snippet' AND e.TargetId IN (SELECT CAST(Id AS TEXT) FROM Snippets WHERE ProjectId = @ProjectId))
            )
            AND (@AnyAction = 1 OR e.Action = @Action)
            """;
        IEnumerable<AuditEventRow> rows = await db.QueryAsync<AuditEventRow>("""
            SELECT e.Id, e.ActorUserId, u.Username AS ActorUsername, e.Action, e.TargetType, e.TargetId,
                   e.Detail, e.CreatedAtUtc
            FROM AuditEvents e
            LEFT JOIN Users u ON u.Id = e.ActorUserId
            WHERE
            """ + scope + """

            ORDER BY e.Id DESC
            LIMIT @Take OFFSET @Skip;
            """, parameters);
        long total = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM AuditEvents e WHERE " + scope + ";", parameters);
        return new([.. rows.Select(row => new AuditEntry(row.Id, row.ActorUserId, row.ActorUsername,
            row.Action, row.TargetType, row.TargetId, row.Detail, Instant(row.CreatedAtUtc)))], total);
    }
}

internal sealed record ProjectAuditParameters(string ProjectText, long ProjectId, string? Action, int AnyAction, int Take, int Skip);
