using System.Globalization;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// The public keys an account has registered for Git over SSH.
///
/// <para>
/// A key is a credential, so it is handled as a token is: only its owner may list, add or remove it, every
/// change is audited, and resolving one to an identity refuses a disabled account and an expired key. The
/// store does not parse keys; the caller hands it the one-line form and the fingerprint, having validated
/// both, and the store keeps them as given.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    /// <summary>Keys one account may hold. Enough for every machine a person has; few enough to read.</summary>
    public const int MaximumSshKeysPerUser = 50;

    public async Task<SshKeyInfo> AddSshKeyAsync(
        AuthenticatedIdentity actor,
        string title,
        string keyType,
        string publicKey,
        string fingerprint,
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
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        SshKeyInfo added = await InsertSshKeyAsync(db, transaction, liveActor.UserId, liveActor.UserId, title, keyType, publicKey, fingerprint, expiresAtUtc);
        await transaction.CommitAsync(cancellationToken);
        return added;
    }

    /// <summary>
    /// Adds a key to another account, for an administrator (ADR 0049): setting up somebody who cannot paste their own, or
    /// a service account nobody signs in as. The audit entry names the administrator who did it.
    /// </summary>
    public async Task<SshKeyInfo> AddUserSshKeyAsync(
        AuthenticatedIdentity actor,
        long userId,
        string title,
        string keyType,
        string publicKey,
        string fingerprint,
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
        AuthenticatedIdentity liveActor = await RequireAdministratorAsync(db, transaction, actor, cancellationToken);
        if (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Users WHERE Id = @UserId;", new UserParameters(userId), transaction) == 0)
            throw new KeyNotFoundException("There is no such account.");
        SshKeyInfo added = await InsertSshKeyAsync(db, transaction, liveActor.UserId, userId, title, keyType, publicKey, fingerprint, expiresAtUtc);
        await transaction.CommitAsync(cancellationToken);
        return added;
    }

    /// <summary>An account's keys, for the account itself or an administrator. Null when there is no such account.</summary>
    public async Task<IReadOnlyList<SshKeyInfo>?> ListUserSshKeysAsync(AuthenticatedIdentity actor, long userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        if (liveActor.UserId != userId && !liveActor.IsAdministrator)
            throw new UnauthorizedAccessException("Only the account itself or an administrator may read its keys.");
        if (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Users WHERE Id = @UserId;", new UserParameters(userId)) == 0) return null;
        IEnumerable<SshKeyRow> rows = await db.QueryAsync<SshKeyRow>("""
            SELECT Id, UserId, Title, KeyType, PublicKey, Fingerprint, CreatedAtUtc, ExpiresAtUtc, LastUsedAtUtc
            FROM SshKeys
            WHERE UserId = @UserId
            ORDER BY Id;
            """, new UserParameters(userId));
        return rows.Select(ToInfo).ToArray();
    }

    /// <summary>Removes another account's key, for an administrator: the key on a lost laptop of somebody who cannot sign in.</summary>
    public async Task<bool> DeleteUserSshKeyAsync(AuthenticatedIdentity actor, long userId, long keyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireAdministratorAsync(db, transaction, actor, cancellationToken);
        string? fingerprint = await db.QuerySingleOrDefaultAsync<string>(
            "SELECT Fingerprint FROM SshKeys WHERE Id = @Id AND UserId = @UserId;", new OwnKeyParameters(keyId, userId), transaction);
        if (fingerprint is null) return false;
        await db.ExecuteAsync("DELETE FROM SshKeys WHERE Id = @Id AND UserId = @UserId;", new OwnKeyParameters(keyId, userId), transaction);
        await AuditAsync(db, transaction, liveActor.UserId, "delete-ssh-key", "ssh-key", keyId.ToString(CultureInfo.InvariantCulture),
            "SSH key " + fingerprint + " removed from account " + userId.ToString(CultureInfo.InvariantCulture) + " by an administrator.", Now());
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// One key and the account holding it, by id or by fingerprint, for an administrator: GitLab's <c>/keys</c>, which is
    /// how an operator answers "whose key is this" from a log line.
    /// </summary>
    public async Task<(SshKeyInfo Key, string Username)?> FindSshKeyAsync(AuthenticatedIdentity actor, long? keyId, string? fingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireAdministratorAsync(db, null, actor, cancellationToken);
        SshKeyOwnerRow? row = await db.QuerySingleOrDefaultAsync<SshKeyOwnerRow>("""
            SELECT k.Id, k.UserId, k.Title, k.KeyType, k.PublicKey, k.Fingerprint, k.CreatedAtUtc, k.ExpiresAtUtc, k.LastUsedAtUtc, u.Username
            FROM SshKeys k
            JOIN Users u ON u.Id = k.UserId
            WHERE (@Id <> 0 AND k.Id = @Id) OR (@Fingerprint <> '' AND k.Fingerprint = @Fingerprint);
            """, new KeyLookupParameters(keyId ?? 0, fingerprint ?? ""));
        if (row is null) return null;
        return (ToInfo(new SshKeyRow(row.Id, row.UserId, row.Title, row.KeyType, row.PublicKey, row.Fingerprint, row.CreatedAtUtc, row.ExpiresAtUtc, row.LastUsedAtUtc)),
            row.Username);
    }

    private static async Task<AuthenticatedIdentity> RequireAdministratorAsync(SqliteConnection db, SqliteTransaction? transaction,
        AuthenticatedIdentity actor, CancellationToken cancellationToken)
    {
        AuthenticatedIdentity live = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        if (!live.IsAdministrator) throw new UnauthorizedAccessException("Only an administrator may manage another account's keys.");
        return live;
    }

    /// <summary>One key row, checked against the account's bound and against every key on the server, account or
    /// deploy key: a key authenticates as exactly one thing.</summary>
    private static async Task<SshKeyInfo> InsertSshKeyAsync(SqliteConnection db, SqliteTransaction transaction, long actorUserId, long ownerUserId,
        string title, string keyType, string publicKey, string fingerprint, DateTimeOffset? expiresAtUtc)
    {
        long count = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM SshKeys WHERE UserId = @UserId;",
            new UserParameters(ownerUserId), transaction);
        if (count >= MaximumSshKeysPerUser) throw new InvalidOperationException("This account already holds the most keys it may.");
        long taken = await db.ExecuteScalarAsync<long>(
            "SELECT (SELECT COUNT(*) FROM SshKeys WHERE Fingerprint = @Fingerprint) + (SELECT COUNT(*) FROM DeployKeys WHERE Fingerprint = @Fingerprint);",
            new FingerprintParameters(fingerprint), transaction);
        if (taken > 0) throw new SshKeyConflictException();

        string now = Now();
        string? expires = expiresAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO SshKeys (UserId, Title, KeyType, PublicKey, Fingerprint, CreatedAtUtc, ExpiresAtUtc, LastUsedAtUtc)
            VALUES (@UserId, @Title, @KeyType, @PublicKey, @Fingerprint, @CreatedAtUtc, @ExpiresAtUtc, NULL)
            RETURNING Id;
            """, new SshKeyParameters(ownerUserId, title, keyType, publicKey, fingerprint, now, expires), transaction);
        await AuditAsync(db, transaction, actorUserId, "add-ssh-key", "ssh-key", id.ToString(CultureInfo.InvariantCulture),
            "SSH key " + fingerprint + " added" + (actorUserId == ownerUserId ? "." : " to account " + ownerUserId.ToString(CultureInfo.InvariantCulture) + " by an administrator."), now);
        return new(id, ownerUserId, title, keyType, publicKey, fingerprint, Parse(now), expiresAtUtc?.ToUniversalTime(), null);
    }

    public async Task<IReadOnlyList<SshKeyInfo>> ListSshKeysAsync(AuthenticatedIdentity actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        IEnumerable<SshKeyRow> rows = await db.QueryAsync<SshKeyRow>("""
            SELECT Id, UserId, Title, KeyType, PublicKey, Fingerprint, CreatedAtUtc, ExpiresAtUtc, LastUsedAtUtc
            FROM SshKeys
            WHERE UserId = @UserId
            ORDER BY Id;
            """, new UserParameters(liveActor.UserId));
        return rows.Select(ToInfo).ToArray();
    }

    /// <summary>One of the caller's own keys, or null.</summary>
    public async Task<SshKeyInfo?> GetSshKeyAsync(AuthenticatedIdentity actor, long keyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        SshKeyRow? row = await db.QuerySingleOrDefaultAsync<SshKeyRow>("""
            SELECT Id, UserId, Title, KeyType, PublicKey, Fingerprint, CreatedAtUtc, ExpiresAtUtc, LastUsedAtUtc
            FROM SshKeys
            WHERE Id = @Id AND UserId = @UserId;
            """, new OwnKeyParameters(keyId, liveActor.UserId));
        return row is null ? null : ToInfo(row);
    }

    /// <summary>Removes one of the caller's own keys. Returns false when there is no such key of theirs.</summary>
    public async Task<bool> DeleteSshKeyAsync(AuthenticatedIdentity actor, long keyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        string? fingerprint = await db.QuerySingleOrDefaultAsync<string>(
            "SELECT Fingerprint FROM SshKeys WHERE Id = @Id AND UserId = @UserId;", new OwnKeyParameters(keyId, liveActor.UserId), transaction);
        if (fingerprint is null) return false;
        await db.ExecuteAsync("DELETE FROM SshKeys WHERE Id = @Id AND UserId = @UserId;", new OwnKeyParameters(keyId, liveActor.UserId), transaction);
        string now = Now();
        await AuditAsync(db, transaction, liveActor.UserId, "delete-ssh-key", "ssh-key", keyId.ToString(CultureInfo.InvariantCulture),
            "SSH key " + fingerprint + " removed.", now);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The identity a key authenticates, or null when the key is unknown, expired, or belongs to a disabled
    /// account. Records the use when <paramref name="recordUse"/> is set, which the caller does only once a
    /// signature has verified: a query by a client that never signs is not a use.
    /// </summary>
    public async Task<AuthenticatedIdentity?> ResolveSshKeyAsync(string fingerprint, bool recordUse, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        string now = Now();
        SshKeyIdentityRow? row = await db.QuerySingleOrDefaultAsync<SshKeyIdentityRow>("""
            SELECT k.Id, u.Id AS UserId, u.Username, u.IsAdministrator, u.Disabled
            FROM SshKeys k
            JOIN Users u ON u.Id = k.UserId
            WHERE k.Fingerprint = @Fingerprint
              AND (k.ExpiresAtUtc IS NULL OR k.ExpiresAtUtc > @NowUtc);
            """, new ResolveKeyParameters(fingerprint, now));
        if (row is null || row.Disabled != 0) return null;
        if (recordUse)
            await db.ExecuteAsync("UPDATE SshKeys SET LastUsedAtUtc = @NowUtc WHERE Id = @Id;", new TouchKeyParameters(row.Id, now));
        return new(row.UserId, row.Username, row.IsAdministrator != 0, IdentityKind.SshKey);
    }

    private static SshKeyInfo ToInfo(SshKeyRow row) => new(row.Id, row.UserId, row.Title, row.KeyType, row.PublicKey, row.Fingerprint,
        Parse(row.CreatedAtUtc), row.ExpiresAtUtc is null ? null : Parse(row.ExpiresAtUtc), row.LastUsedAtUtc is null ? null : Parse(row.LastUsedAtUtc));

    private static DateTimeOffset Parse(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

/// <summary>The key is already registered, to this account or another.</summary>
public sealed class SshKeyConflictException() : Exception("This key is already registered.");

internal sealed record UserParameters(long UserId);
internal sealed record FingerprintParameters(string Fingerprint);
internal sealed record OwnKeyParameters(long Id, long UserId);
internal sealed record SshKeyParameters(long UserId, string Title, string KeyType, string PublicKey, string Fingerprint, string CreatedAtUtc, string? ExpiresAtUtc);
internal sealed record ResolveKeyParameters(string Fingerprint, string NowUtc);
internal sealed record TouchKeyParameters(long Id, string NowUtc);
internal sealed record SshKeyRow(long Id, long UserId, string Title, string KeyType, string PublicKey, string Fingerprint, string CreatedAtUtc, string? ExpiresAtUtc, string? LastUsedAtUtc);
internal sealed record SshKeyOwnerRow(long Id, long UserId, string Title, string KeyType, string PublicKey, string Fingerprint, string CreatedAtUtc,
    string? ExpiresAtUtc, string? LastUsedAtUtc, string Username);
internal sealed record KeyLookupParameters(long Id, string Fingerprint);
internal sealed record SshKeyIdentityRow(long Id, long UserId, string Username, long IsAdministrator, long Disabled);
