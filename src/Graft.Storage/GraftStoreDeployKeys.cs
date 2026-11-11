using System.Globalization;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// Deploy keys (ADR 0049): an SSH key a project admits that belongs to no account. A machine that builds or
/// deploys a project needs its repository and nothing else, so a deploy key opens one project, reads it, and
/// pushes to it only when the project said it may.
///
/// <para>
/// Managing one needs what managing the project needs, because a deploy key is a door into the repository that
/// no membership list shows. A key that is an account's key cannot also be a deploy key, and the reverse,
/// because a key authenticates as exactly one thing.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    /// <summary>Enough for every machine that deploys a project; few enough to read on one screen.</summary>
    public const int MaximumDeployKeysPerProject = 50;

    public async Task<DeployKeyInfo> AddDeployKeyAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string title,
        string keyType,
        string publicKey,
        string fingerprint,
        bool canPush,
        DateTimeOffset? expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyType);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (title.Length > 100) throw new ArgumentException("Key titles cannot exceed 100 characters.", nameof(title));
        if (expiresAtUtc is DateTimeOffset expiry && expiry <= DateTimeOffset.UtcNow)
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "A key cannot expire in the past.");

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity live = await RequireDeployKeyManagerAsync(db, transaction, actor, projectId, cancellationToken);
        DeployKeyInfo added = await InsertDeployKeyAsync(db, transaction, live.UserId, projectId, title, keyType, publicKey, fingerprint,
            canPush, expiresAtUtc?.ToUniversalTime());
        await transaction.CommitAsync(cancellationToken);
        return added;
    }

    public async Task<IReadOnlyList<DeployKeyInfo>> ListDeployKeysAsync(AuthenticatedIdentity actor, long projectId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireDeployKeyManagerAsync(db, null, actor, projectId, cancellationToken);
        IEnumerable<DeployKeyRow> rows = await db.QueryAsync<DeployKeyRow>(DeployKeySelect + "WHERE ProjectId = @Id ORDER BY Id;",
            new IdParameters(projectId));
        return rows.Select(ToDeployKey).ToArray();
    }

    public async Task<DeployKeyInfo?> GetDeployKeyAsync(AuthenticatedIdentity actor, long projectId, long keyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireDeployKeyManagerAsync(db, null, actor, projectId, cancellationToken);
        DeployKeyRow? row = await db.QuerySingleOrDefaultAsync<DeployKeyRow>(DeployKeySelect + "WHERE Id = @Id AND ProjectId = @ProjectId;",
            new DeployKeyIdParameters(keyId, projectId));
        return row is null ? null : ToDeployKey(row);
    }

    /// <summary>Renames a deploy key or changes whether it may push. Null leaves that field alone; null when there is no such key.</summary>
    public async Task<DeployKeyInfo?> UpdateDeployKeyAsync(AuthenticatedIdentity actor, long projectId, long keyId, string? title, bool? canPush,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (title is not null && (title.Trim().Length == 0 || title.Length > 100))
            throw new ArgumentException("A key title is 1 to 100 characters.", nameof(title));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity live = await RequireDeployKeyManagerAsync(db, transaction, actor, projectId, cancellationToken);
        DeployKeyRow? existing = await db.QuerySingleOrDefaultAsync<DeployKeyRow>(DeployKeySelect + "WHERE Id = @Id AND ProjectId = @ProjectId;",
            new DeployKeyIdParameters(keyId, projectId), transaction);
        if (existing is null) return null;
        await db.ExecuteAsync("UPDATE DeployKeys SET Title = COALESCE(@Title, Title), CanPush = COALESCE(@CanPush, CanPush) WHERE Id = @Id;",
            new DeployKeyUpdateParameters(keyId, title?.Trim(), canPush is null ? null : canPush.Value ? 1 : 0), transaction);
        // Write access changing is the part an auditor cares about, so it is said when it changes.
        string message = canPush is bool push && push != (existing.CanPush != 0)
            ? "Deploy key " + existing.Fingerprint + (push ? " may now push." : " may no longer push.")
            : "Deploy key " + existing.Fingerprint + " renamed.";
        await AuditAsync(db, transaction, live.UserId, "update-deploy-key", "project", projectId.ToString(CultureInfo.InvariantCulture), message, Now());
        await transaction.CommitAsync(cancellationToken);
        return ToDeployKey((await db.QuerySingleAsync<DeployKeyRow>(DeployKeySelect + "WHERE Id = @Id AND ProjectId = @ProjectId;",
            new DeployKeyIdParameters(keyId, projectId)))!);
    }

    public async Task<bool> DeleteDeployKeyAsync(AuthenticatedIdentity actor, long projectId, long keyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity live = await RequireDeployKeyManagerAsync(db, transaction, actor, projectId, cancellationToken);
        string? fingerprint = await db.QuerySingleOrDefaultAsync<string>("SELECT Fingerprint FROM DeployKeys WHERE Id = @Id AND ProjectId = @ProjectId;",
            new DeployKeyIdParameters(keyId, projectId), transaction);
        if (fingerprint is null) return false;
        await db.ExecuteAsync("DELETE FROM DeployKeys WHERE Id = @Id AND ProjectId = @ProjectId;", new DeployKeyIdParameters(keyId, projectId), transaction);
        await AuditAsync(db, transaction, live.UserId, "delete-deploy-key", "project", projectId.ToString(CultureInfo.InvariantCulture),
            "Deploy key " + fingerprint + " removed.", Now());
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Admits a deploy key another project already has, by its id there: GitLab's "enable". The caller must be
    /// able to manage both projects, or be an administrator, because enabling copies a credential out of one
    /// project's settings into another's. Read only, whatever the source allowed; push is granted here deliberately.
    /// Returns the key as this project now holds it, or null when there is no such key the caller may copy.
    /// </summary>
    public async Task<DeployKeyInfo?> EnableDeployKeyAsync(AuthenticatedIdentity actor, long projectId, long sourceKeyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity live = await RequireDeployKeyManagerAsync(db, transaction, actor, projectId, cancellationToken);
        DeployKeyRow? source = await db.QuerySingleOrDefaultAsync<DeployKeyRow>(DeployKeySelect + "WHERE Id = @Id;", new IdParameters(sourceKeyId), transaction);
        if (source is null) return null;
        if (!live.IsAdministrator &&
            !(await DecideAsync(db, transaction, live, source.ProjectId, ProjectAction.ManageProject, cancellationToken)).Allowed)
            return null;
        DeployKeyRow? present = await db.QuerySingleOrDefaultAsync<DeployKeyRow>(DeployKeySelect + "WHERE ProjectId = @ProjectId AND Fingerprint = @Fingerprint;",
            new DeployKeyFingerprintParameters(projectId, source.Fingerprint), transaction);
        if (present is not null) return ToDeployKey(present);
        DeployKeyInfo added = await InsertDeployKeyAsync(db, transaction, live.UserId, projectId, source.Title, source.KeyType, source.PublicKey,
            source.Fingerprint, false, source.ExpiresAtUtc is null ? null : Parse(source.ExpiresAtUtc));
        await transaction.CommitAsync(cancellationToken);
        return added;
    }

    /// <summary>Every deploy key on the server, for an administrator.</summary>
    public async Task<IReadOnlyList<DeployKeyInfo>> ListAllDeployKeysAsync(AuthenticatedIdentity actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity live = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        if (!live.IsAdministrator) throw new UnauthorizedAccessException("Only an administrator may list every deploy key.");
        IEnumerable<DeployKeyRow> rows = await db.QueryAsync<DeployKeyRow>(DeployKeySelect + "ORDER BY Id;");
        return rows.Select(ToDeployKey).ToArray();
    }

    /// <summary>Whether any project admits this key today. The SSH server asks at authentication, before it knows which
    /// repository the connection is for.</summary>
    public async Task<bool> IsDeployKeyAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        return await db.ExecuteScalarAsync<long>("""
            SELECT COUNT(*) FROM DeployKeys WHERE Fingerprint = @Fingerprint AND (ExpiresAtUtc IS NULL OR ExpiresAtUtc > @NowUtc);
            """, new ResolveKeyParameters(fingerprint, Now())) > 0;
    }

    /// <summary>
    /// What this key may do to this project, or null when the project does not admit it. A push is attributed to whoever
    /// added the key, as GitLab does, or to the project's owner once that account is gone or disabled, so webhooks and
    /// the activity feed always name a person. Records the use when asked.
    /// </summary>
    public async Task<DeployKeyGrant?> FindDeployKeyGrantAsync(string fingerprint, long projectId, bool recordUse, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        string now = Now();
        DeployKeyGrantRow? row = await db.QuerySingleOrDefaultAsync<DeployKeyGrantRow>("""
            SELECT k.Id, k.ProjectId, k.CanPush,
                   COALESCE(c.Id, o.Id) AS UserId, COALESCE(c.Username, o.Username) AS Username,
                   COALESCE(c.IsAdministrator, o.IsAdministrator) AS IsAdministrator
            FROM DeployKeys k
            JOIN Projects p ON p.Id = k.ProjectId AND p.State = 'ready'
            JOIN Users o ON o.Id = p.OwnerUserId
            LEFT JOIN Users c ON c.Id = k.CreatedByUserId AND c.Disabled = 0
            WHERE k.Fingerprint = @Fingerprint AND k.ProjectId = @ProjectId
              AND (k.ExpiresAtUtc IS NULL OR k.ExpiresAtUtc > @NowUtc);
            """, new DeployKeyGrantParameters(fingerprint, projectId, now));
        if (row is null) return null;
        if (recordUse) await db.ExecuteAsync("UPDATE DeployKeys SET LastUsedAtUtc = @NowUtc WHERE Id = @Id;", new TouchKeyParameters(row.Id, now));
        return new(row.Id, row.ProjectId, row.CanPush != 0, new(row.UserId, row.Username, row.IsAdministrator != 0, IdentityKind.SshKey));
    }

    private static async Task<AuthenticatedIdentity> RequireDeployKeyManagerAsync(SqliteConnection db, SqliteTransaction? transaction,
        AuthenticatedIdentity actor, long projectId, CancellationToken cancellationToken)
    {
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId, ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);
        return await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
    }

    private static async Task<DeployKeyInfo> InsertDeployKeyAsync(SqliteConnection db, SqliteTransaction transaction, long actorUserId, long projectId,
        string title, string keyType, string publicKey, string fingerprint, bool canPush, DateTimeOffset? expiresAtUtc)
    {
        long count = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM DeployKeys WHERE ProjectId = @Id;", new IdParameters(projectId), transaction);
        if (count >= MaximumDeployKeysPerProject) throw new InvalidOperationException("This project already admits the most deploy keys it may.");
        // An account's key authenticates as that account; it cannot also authenticate as a project's deploy key.
        if (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM SshKeys WHERE Fingerprint = @Fingerprint;",
                new FingerprintParameters(fingerprint), transaction) > 0)
            throw new SshKeyConflictException();
        if (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM DeployKeys WHERE ProjectId = @ProjectId AND Fingerprint = @Fingerprint;",
                new DeployKeyFingerprintParameters(projectId, fingerprint), transaction) > 0)
            throw new SshKeyConflictException();

        string now = Now();
        string? expires = expiresAtUtc?.ToString("O", CultureInfo.InvariantCulture);
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO DeployKeys (ProjectId, Title, KeyType, PublicKey, Fingerprint, CanPush, CreatedByUserId, CreatedAtUtc, ExpiresAtUtc, LastUsedAtUtc)
            VALUES (@ProjectId, @Title, @KeyType, @PublicKey, @Fingerprint, @CanPush, @CreatedByUserId, @CreatedAtUtc, @ExpiresAtUtc, NULL)
            RETURNING Id;
            """, new DeployKeyInsertParameters(projectId, title, keyType, publicKey, fingerprint, canPush ? 1 : 0, actorUserId, now, expires), transaction);
        await AuditAsync(db, transaction, actorUserId, "add-deploy-key", "project", projectId.ToString(CultureInfo.InvariantCulture),
            "Deploy key " + fingerprint + (canPush ? " added, with push." : " added, read only."), now);
        return new(id, projectId, title, keyType, publicKey, fingerprint, canPush, Parse(now), expiresAtUtc, null);
    }

    private const string DeployKeySelect = """
        SELECT Id, ProjectId, Title, KeyType, PublicKey, Fingerprint, CanPush, CreatedAtUtc, ExpiresAtUtc, LastUsedAtUtc
        FROM DeployKeys

        """;

    private static DeployKeyInfo ToDeployKey(DeployKeyRow row) => new(row.Id, row.ProjectId, row.Title, row.KeyType, row.PublicKey, row.Fingerprint,
        row.CanPush != 0, Parse(row.CreatedAtUtc), row.ExpiresAtUtc is null ? null : Parse(row.ExpiresAtUtc),
        row.LastUsedAtUtc is null ? null : Parse(row.LastUsedAtUtc));
}

internal sealed record DeployKeyRow(long Id, long ProjectId, string Title, string KeyType, string PublicKey, string Fingerprint, long CanPush,
    string CreatedAtUtc, string? ExpiresAtUtc, string? LastUsedAtUtc);
internal sealed record DeployKeyIdParameters(long Id, long ProjectId);
internal sealed record DeployKeyFingerprintParameters(long ProjectId, string Fingerprint);
internal sealed record DeployKeyUpdateParameters(long Id, string? Title, int? CanPush);
internal sealed record DeployKeyInsertParameters(long ProjectId, string Title, string KeyType, string PublicKey, string Fingerprint, int CanPush,
    long CreatedByUserId, string CreatedAtUtc, string? ExpiresAtUtc);
internal sealed record DeployKeyGrantParameters(string Fingerprint, long ProjectId, string NowUtc);
internal sealed record DeployKeyGrantRow(long Id, long ProjectId, long CanPush, long UserId, string Username, long IsAdministrator);
