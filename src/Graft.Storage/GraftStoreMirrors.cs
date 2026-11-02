using System.Globalization;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// Projects imported from another server's address, and kept up to date from there (ADR 0042).
///
/// <para>
/// The first import and every later update are one operation, run by the server's mirror worker one at a time: a row is
/// claimed when it is due, fetched, and its result recorded with the time of the next attempt. A mirror that keeps
/// failing is retried less often, never given up on, because a repository that has gone away is the case a mirror is
/// for, and the copy it already holds is untouched by a failed attempt.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    private const string MirrorSelect = """
        SELECT m.ProjectId, m.Url, m.Enabled, m.Status, m.CreatedAtUtc, m.LastAttemptAtUtc, m.LastSuccessAtUtc, m.LastChangeAtUtc,
               m.LastError, m.ConsecutiveFailures, m.NextAttemptAtUtc
        FROM ProjectMirrors m
        """;

    /// <summary>
    /// Points a project at an address to import from, or changes the address or whether it is kept up to date. A
    /// Maintainer's, as every project setting is. A new address, or turning updates on, schedules an update now.
    /// </summary>
    public async Task<ProjectMirror> ConfigureMirrorAsync(AuthenticatedIdentity actor, long projectId, string? url, bool? enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (url is { Length: > ImportAddresses.MaximumUrlLength }) throw new ArgumentException("The address is too long.", nameof(url));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision manage = await DecideAsync(db, transaction, actor, projectId, ProjectAction.ManageProject, cancellationToken);
        if (!manage.Allowed) throw new UnauthorizedAccessException(manage.Explanation);
        AuthenticatedIdentity live = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        string now = Now();
        MirrorRow? existing = await db.QuerySingleOrDefaultAsync<MirrorRow>(MirrorSelect + " WHERE m.ProjectId = @Id;", new IdParameters(projectId), transaction);
        if (existing is null)
        {
            if (url is null) throw new KeyNotFoundException("This project is not imported from an address.");
            await db.ExecuteAsync("""
                INSERT INTO ProjectMirrors (ProjectId, Url, Enabled, Status, CreatedByUserId, CreatedAtUtc, NextAttemptAtUtc)
                VALUES (@ProjectId, @Url, @Enabled, 'scheduled', @UserId, @NowUtc, @NowUtc);
                """, new MirrorInsertParameters(projectId, url, enabled ?? true ? 1 : 0, live.UserId, now), transaction);
            await AuditAsync(db, transaction, live.UserId, "configure-mirror", "project", projectId.ToString(CultureInfo.InvariantCulture),
                "Imported from " + url + ((enabled ?? true) ? ", kept up to date." : ", once."), now);
        }
        else
        {
            string newUrl = url ?? existing.Url;
            bool newEnabled = enabled ?? existing.Enabled != 0;
            bool reschedule = newUrl != existing.Url || (newEnabled && existing.Enabled == 0);
            await db.ExecuteAsync("""
                UPDATE ProjectMirrors
                SET Url = @Url, Enabled = @Enabled,
                    Status = CASE WHEN @Reschedule = 1 AND Status <> 'started' THEN 'scheduled' ELSE Status END,
                    ConsecutiveFailures = CASE WHEN @Reschedule = 1 THEN 0 ELSE ConsecutiveFailures END,
                    NextAttemptAtUtc = CASE WHEN @Reschedule = 1 THEN @NowUtc ELSE NextAttemptAtUtc END
                WHERE ProjectId = @ProjectId;
                """, new MirrorUpdateParameters(projectId, newUrl, newEnabled ? 1 : 0, reschedule ? 1 : 0, now), transaction);
            if (newUrl != existing.Url || newEnabled != (existing.Enabled != 0))
                await AuditAsync(db, transaction, live.UserId, "configure-mirror", "project", projectId.ToString(CultureInfo.InvariantCulture),
                    (newUrl != existing.Url ? "Import address changed to " + newUrl + ". " : "") +
                    (newEnabled ? "Kept up to date." : "No longer kept up to date."), now);
        }
        MirrorRow row = await db.QuerySingleAsync<MirrorRow>(MirrorSelect + " WHERE m.ProjectId = @Id;", new IdParameters(projectId), transaction);
        await transaction.CommitAsync(cancellationToken);
        return ToMirror(row);
    }

    /// <summary>The project's import, or null when it was not imported from an address. The caller decides who may see it.</summary>
    public async Task<ProjectMirror?> GetMirrorAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        MirrorRow? row = await db.QuerySingleOrDefaultAsync<MirrorRow>(MirrorSelect + " WHERE m.ProjectId = @Id;", new IdParameters(projectId));
        return row is null ? null : ToMirror(row);
    }

    /// <summary>
    /// Asks for an update as soon as the worker is free: GitLab's "update now". A Maintainer's. Also how a failed import
    /// is tried again, or a mirror that is not kept up to date is refreshed once.
    /// </summary>
    public async Task<ProjectMirror> RequestMirrorUpdateAsync(AuthenticatedIdentity actor, long projectId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision manage = await DecideAsync(db, transaction, actor, projectId, ProjectAction.ManageProject, cancellationToken);
        if (!manage.Allowed) throw new UnauthorizedAccessException(manage.Explanation);
        await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        int changed = await db.ExecuteAsync("""
            UPDATE ProjectMirrors SET Status = CASE WHEN Status = 'started' THEN Status ELSE 'scheduled' END, NextAttemptAtUtc = @NowUtc
            WHERE ProjectId = @Id;
            """, new IdNowParameters(projectId, Now()), transaction);
        if (changed == 0) throw new KeyNotFoundException("This project is not imported from an address.");
        MirrorRow row = await db.QuerySingleAsync<MirrorRow>(MirrorSelect + " WHERE m.ProjectId = @Id;", new IdParameters(projectId), transaction);
        await transaction.CommitAsync(cancellationToken);
        return ToMirror(row);
    }

    /// <summary>
    /// Takes the mirror most overdue for an update, if any is due, and marks it started. Due means scheduled for now or
    /// earlier, on a ready project, and either kept up to date or asked for once.
    /// </summary>
    public async Task<MirrorJob?> ClaimDueMirrorAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        string at = now.ToString("O", CultureInfo.InvariantCulture);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        MirrorRow? row = await db.QuerySingleOrDefaultAsync<MirrorRow>(MirrorSelect + """
             JOIN Projects p ON p.Id = m.ProjectId AND p.State = 'ready'
            WHERE m.Status <> 'started' AND (m.Enabled = 1 OR m.Status = 'scheduled') AND m.NextAttemptAtUtc <= @NowUtc
            ORDER BY m.NextAttemptAtUtc
            LIMIT 1;
            """, new NowParameters(at), transaction);
        if (row is null) return null;
        await db.ExecuteAsync("UPDATE ProjectMirrors SET Status = 'started', LastAttemptAtUtc = @NowUtc WHERE ProjectId = @Id;",
            new IdNowParameters(row.ProjectId, at), transaction);
        await transaction.CommitAsync(cancellationToken);
        return new(row.ProjectId, row.Url, row.LastSuccessAtUtc is null);
    }

    /// <summary>When the next mirror falls due, or null when none ever will without somebody asking.</summary>
    public async Task<DateTimeOffset?> NextMirrorDueAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        string? next = await db.ExecuteScalarAsync<string?>("""
            SELECT MIN(m.NextAttemptAtUtc) FROM ProjectMirrors m JOIN Projects p ON p.Id = m.ProjectId AND p.State = 'ready'
            WHERE m.Status <> 'started' AND (m.Enabled = 1 OR m.Status = 'scheduled');
            """);
        return next is null ? null : Instant(next);
    }

    /// <summary>
    /// Records how an update went and when the next one is due: <paramref name="interval"/> after a success, and after a
    /// failure that interval doubled for each failure in a row, at most sixteen times, and never more than a day apart.
    /// </summary>
    public async Task<ProjectMirror?> RecordMirrorResultAsync(long projectId, bool success, bool changed, string? error, TimeSpan interval,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        MirrorRow? row = await db.QuerySingleOrDefaultAsync<MirrorRow>(MirrorSelect + " WHERE m.ProjectId = @Id;", new IdParameters(projectId), transaction);
        if (row is null) return null;
        long failures = success ? 0 : row.ConsecutiveFailures + 1;
        TimeSpan wait = success ? interval : TimeSpan.FromTicks(Math.Min(
            interval.Ticks * (1L << (int)Math.Min(failures - 1, 4)), Math.Max(interval.Ticks, TimeSpan.FromDays(1).Ticks)));
        string at = now.ToString("O", CultureInfo.InvariantCulture);
        if (error is { Length: > 1000 }) error = error[..1000];
        await db.ExecuteAsync("""
            UPDATE ProjectMirrors
            SET Status = @Status, LastSuccessAtUtc = CASE WHEN @Success = 1 THEN @NowUtc ELSE LastSuccessAtUtc END,
                LastChangeAtUtc = CASE WHEN @Changed = 1 THEN @NowUtc ELSE LastChangeAtUtc END,
                LastError = @Error, ConsecutiveFailures = @Failures, NextAttemptAtUtc = @NextUtc
            WHERE ProjectId = @ProjectId;
            """, new MirrorResultParameters(projectId, success ? "finished" : "failed", success ? 1 : 0, changed ? 1 : 0, success ? null : error,
                failures, at, now.Add(wait).ToString("O", CultureInfo.InvariantCulture)), transaction);
        MirrorRow updated = await db.QuerySingleAsync<MirrorRow>(MirrorSelect + " WHERE m.ProjectId = @Id;", new IdParameters(projectId), transaction);
        await transaction.CommitAsync(cancellationToken);
        return ToMirror(updated);
    }

    /// <summary>Puts back any update a stopped server left started, so it runs again. Called once at startup.</summary>
    public async Task ResetStartedMirrorsAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await db.ExecuteAsync("UPDATE ProjectMirrors SET Status = 'scheduled' WHERE Status = 'started';");
    }

    /// <summary>Opens an imported project at the branch its upstream opens at, unless somebody already chose one.</summary>
    public async Task SetImportedDefaultBranchAsync(long projectId, string branch, CancellationToken cancellationToken = default)
    {
        if (branch.Length is 0 or > 240) return;
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await db.ExecuteAsync("UPDATE Projects SET DefaultBranch = @Value WHERE Id = @Id AND DefaultBranch IS NULL;", new DefaultBranchParameters(projectId, branch));
    }

    private static ProjectMirror ToMirror(MirrorRow row) => new(row.ProjectId, row.Url, row.Enabled != 0, row.Status switch
    {
        "started" => MirrorStatus.Started,
        "finished" => MirrorStatus.Finished,
        "failed" => MirrorStatus.Failed,
        _ => MirrorStatus.Scheduled
    }, Instant(row.CreatedAtUtc), Maybe(row.LastAttemptAtUtc), Maybe(row.LastSuccessAtUtc), Maybe(row.LastChangeAtUtc), row.LastError,
        (int)row.ConsecutiveFailures, Instant(row.NextAttemptAtUtc));

    private static DateTimeOffset? Maybe(string? value) => value is null ? null : Instant(value);
}

internal sealed record MirrorRow(long ProjectId, string Url, long Enabled, string Status, string CreatedAtUtc, string? LastAttemptAtUtc,
    string? LastSuccessAtUtc, string? LastChangeAtUtc, string? LastError, long ConsecutiveFailures, string NextAttemptAtUtc);
internal sealed record MirrorInsertParameters(long ProjectId, string Url, int Enabled, long UserId, string NowUtc);
internal sealed record MirrorUpdateParameters(long ProjectId, string Url, int Enabled, int Reschedule, string NowUtc);
internal sealed record MirrorResultParameters(long ProjectId, string Status, int Success, int Changed, string? Error, long Failures, string NowUtc, string NextUtc);
internal sealed record IdNowParameters(long Id, string NowUtc);
internal sealed record NowParameters(string NowUtc);
