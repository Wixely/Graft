using System.Globalization;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// Where a project records that it wants to be told when something happens.
///
/// <para>
/// Managing a hook needs Maintainer-equivalent access, because a hook makes the server issue a request to
/// an address somebody chose. Nothing here decides whether the server will honour that; the operator's
/// switch does, and it is checked before any of this is reachable.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    /// <summary>Few enough that a project cannot turn one push into a flood of outbound requests.</summary>
    private const int MaximumHooksPerProject = 10;

    public async Task<Webhook> CreateWebhookAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string url,
        string secret,
        WebhookEvent events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        if (url.Length > 2048) throw new ArgumentException("The address is too long.", nameof(url));
        if (secret.Length is < 16 or > 256)
            throw new ArgumentException("A signing secret is between 16 and 256 characters.", nameof(secret));
        if (events == WebhookEvent.None) throw new ArgumentException("A hook needs at least one event.", nameof(events));

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId,
            ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);
        AuthenticatedIdentity live = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);

        long existing = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM Webhooks WHERE ProjectId = @Id;",
            new IdParameters(projectId), transaction);
        if (existing >= MaximumHooksPerProject)
            throw new CollaborationConflictException($"A project may have at most {MaximumHooksPerProject} hooks.");

        string now = Now();
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO Webhooks (ProjectId, Url, Secret, Events, Active, CreatedByUserId, CreatedAtUtc)
            VALUES (@ProjectId, @Url, @Secret, @Events, 1, @CreatedByUserId, @CreatedAtUtc)
            RETURNING Id;
            """, new WebhookInsertParameters(projectId, url, secret, WebhookEvents.Format(events), live.UserId, now),
            transaction);
        // The address is recorded; the secret never is. An audit entry naming it would put a credential in
        // the one place the product promises never to hold one.
        await AuditAsync(db, transaction, live.UserId, "create-webhook", "project",
            projectId.ToString(CultureInfo.InvariantCulture), $"Webhook added for {url}.", now);
        await transaction.CommitAsync(cancellationToken);
        return new(id, projectId, url, events, true, live.Username, Instant(now), null, null, null);
    }

    public async Task<IReadOnlyList<Webhook>> ListWebhooksAsync(
        AuthenticatedIdentity actor,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        PermissionDecision permission = await DecideAsync(db, null, actor, projectId,
            ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);

        IEnumerable<WebhookRow> rows = await db.QueryAsync<WebhookRow>("""
            SELECT w.Id, w.ProjectId, w.Url, w.Events, w.Active, u.Username AS CreatedByUsername,
                   w.CreatedAtUtc, w.LastStatus, w.LastError, w.LastDeliveredAtUtc
            FROM Webhooks w
            JOIN Users u ON u.Id = w.CreatedByUserId
            WHERE w.ProjectId = @Id
            ORDER BY w.Id;
            """, new IdParameters(projectId));
        return [.. rows.Select(ToWebhook)];
    }

    public async Task DeleteWebhookAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long hookId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId,
            ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);

        int changed = await db.ExecuteAsync("DELETE FROM Webhooks WHERE Id = @Id AND ProjectId = @ProjectId;",
            new CommentOwnerParameters(hookId, projectId), transaction);
        if (changed != 1) throw new CollaborationNotFoundException("Webhook is unavailable.");
        await AuditAsync(db, transaction, actor.UserId, "delete-webhook", "project",
            projectId.ToString(CultureInfo.InvariantCulture), "Webhook removed.", Now());
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The hooks that want one event on one project, with the secret to sign with.
    ///
    /// <para>
    /// Takes no identity, because nobody is asking: this is read while answering somebody else's request,
    /// on behalf of the project rather than of a caller. The permission that mattered was checked when the
    /// hook was created.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<WebhookTarget>> FindWebhookTargetsAsync(
        long projectId,
        WebhookEvent single,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        IEnumerable<WebhookTargetRow> rows = await db.QueryAsync<WebhookTargetRow>("""
            SELECT Id, Url, Secret, Events FROM Webhooks
            WHERE ProjectId = @Id AND Active = 1;
            """, new IdParameters(projectId));
        return [.. rows
            .Where(row => WebhookEvents.TryParse(row.Events, out WebhookEvent events) && events.HasFlag(single))
            .Select(row => new WebhookTarget(row.Id, row.Url, row.Secret))];
    }

    /// <summary>
    /// Records how a delivery went. One row per hook, overwritten each time: a delivery history would be a
    /// table that grows without bound to answer a question nobody asks twice.
    /// </summary>
    public async Task RecordWebhookResultAsync(
        long hookId,
        int? status,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await db.ExecuteAsync("""
            UPDATE Webhooks SET LastStatus = @Status, LastError = @Error, LastDeliveredAtUtc = @Now
            WHERE Id = @Id;
            """, new WebhookResultParameters(hookId, status, error is { Length: > 500 } ? error[..500] : error, Now()));
    }

    private static Webhook ToWebhook(WebhookRow row) => new(
        row.Id, row.ProjectId, row.Url,
        WebhookEvents.TryParse(row.Events, out WebhookEvent events) ? events : WebhookEvent.None,
        row.Active != 0, row.CreatedByUsername, Instant(row.CreatedAtUtc),
        row.LastStatus is null ? null : (int)row.LastStatus.Value, row.LastError,
        row.LastDeliveredAtUtc is null ? null : Instant(row.LastDeliveredAtUtc));
}

internal sealed record WebhookInsertParameters(long ProjectId, string Url, string Secret, string Events,
    long CreatedByUserId, string CreatedAtUtc);
internal sealed record WebhookResultParameters(long Id, int? Status, string? Error, string Now);

internal sealed class WebhookRow
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string Url { get; set; } = "";
    public string Events { get; set; } = "";
    public long Active { get; set; }
    public string CreatedByUsername { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
    public long? LastStatus { get; set; }
    public string? LastError { get; set; }
    public string? LastDeliveredAtUtc { get; set; }
}

internal sealed class WebhookTargetRow
{
    public long Id { get; set; }
    public string Url { get; set; } = "";
    public string Secret { get; set; } = "";
    public string Events { get; set; } = "";
}
