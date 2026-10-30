using System.Globalization;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// Email addresses and password resets by link.
///
/// <para>
/// An address is optional: the forge works without any, and sends nothing unless the operator gave it a
/// mail server. A reset is a one-time secret with an expiry. It is issued either by an administrator, who
/// hands the link to the person by whatever channel they share, or by the account itself when the server
/// can send it to the address on file. Redeeming it sets the password and revokes every session and token
/// the account had, as an administrator's reset does, because a reset is what you do when you no longer
/// know who holds what.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    /// <summary>How long an administrator's link lives: long enough to be handed over and acted on in a day.</summary>
    public static readonly TimeSpan AdministratorResetLifetime = TimeSpan.FromHours(24);

    /// <summary>How long an emailed link lives: a message is read soon or not at all.</summary>
    public static readonly TimeSpan EmailedResetLifetime = TimeSpan.FromHours(1);

    /// <summary>Sets or clears the caller's own address.</summary>
    public async Task SetOwnEmailAsync(AuthenticatedIdentity actor, string? email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        await SetEmailAsync(db, transaction, liveActor.UserId, liveActor.UserId, email, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Sets or clears another account's address; administrators only.</summary>
    public async Task SetUserEmailAsync(AuthenticatedIdentity actor, long userId, string? email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        if (!liveActor.IsAdministrator) throw new UnauthorizedAccessException("Administrator access is required.");
        await SetEmailAsync(db, transaction, liveActor.UserId, userId, email, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task SetEmailAsync(SqliteConnection db, SqliteTransaction transaction, long actorUserId, long userId, string? email,
        CancellationToken cancellationToken)
    {
        string? normalized = NormalizeEmail(email);
        string now = Now();
        int changed;
        try
        {
            changed = await db.ExecuteAsync("UPDATE Users SET Email = @Email WHERE Id = @Id;", new EmailParameters(userId, normalized), transaction);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            throw new EmailConflictException();
        }
        if (changed != 1) throw new InvalidOperationException("User is unavailable.");
        await AuditAsync(db, transaction, actorUserId, normalized is null ? "clear-email" : "set-email", "user",
            userId.ToString(CultureInfo.InvariantCulture), normalized is null ? "Email address removed." : "Email address set.", now);
    }

    /// <summary>
    /// The address in canonical form, or null for none. The shape check is deliberately plain: one <c>@</c>
    /// with something either side and no whitespace or control characters. Anything stricter refuses real
    /// addresses, and the only proof of an address is a message that arrives.
    /// </summary>
    public static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        string trimmed = email.Trim();
        int at = trimmed.IndexOf('@');
        if (trimmed.Length > 254 || at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1 ||
            trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or ',' or ';' or '"'))
            throw new ArgumentException("That is not an email address.", nameof(email));
        return trimmed;
    }

    /// <summary>The active account a username or address names, or null. Never says which part was wrong.</summary>
    public async Task<ResetTarget?> FindResetTargetAsync(string login, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(login) || login.Length > 254) return null;
        string trimmed = login.Trim();
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        UserListRow? row;
        if (trimmed.Contains('@'))
        {
            row = await db.QuerySingleOrDefaultAsync<UserListRow>("""
                SELECT Id, Username, IsAdministrator, Disabled, Email
                FROM Users WHERE lower(Email) = lower(@Email) AND Disabled = 0;
                """, new EmailLookupParameters(trimmed));
        }
        else
        {
            string normalized;
            try { normalized = NormalizeName(trimmed, nameof(login)); }
            catch (ArgumentException) { return null; }
            row = await db.QuerySingleOrDefaultAsync<UserListRow>("""
                SELECT Id, Username, IsAdministrator, Disabled, Email
                FROM Users WHERE NormalizedUsername = @NormalizedUsername AND Disabled = 0;
                """, new NormalizedNameParameters(normalized));
        }
        return row is null ? null : new(row.Id, row.Username, row.Email);
    }

    /// <summary>
    /// Issues a reset for <paramref name="userId"/>. <paramref name="issuer"/> is the administrator making a
    /// link to hand over, or null when the account asked for itself and the link is going by email. An
    /// administrator cannot issue one for their own account, for the reason they cannot reset their own
    /// password: that path is the current password.
    /// </summary>
    public async Task<PasswordResetGrant> IssuePasswordResetAsync(AuthenticatedIdentity? issuer, long userId, TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        long? issuerId = null;
        if (issuer is not null)
        {
            AuthenticatedIdentity liveIssuer = await RequireLiveIdentityAsync(db, transaction, issuer, cancellationToken);
            if (!liveIssuer.IsAdministrator) throw new UnauthorizedAccessException("Administrator access is required.");
            if (liveIssuer.UserId == userId) throw new InvalidOperationException("Administrators change their own password with the current password.");
            issuerId = liveIssuer.UserId;
        }
        long active = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Users WHERE Id = @Id AND Disabled = 0;", new IdParameters(userId), transaction);
        if (active != 1) throw new InvalidOperationException("User is unavailable or disabled.");

        string secret = NewSecret("graft_reset_");
        string now = Now();
        DateTimeOffset expires = DateTimeOffset.UtcNow + lifetime;
        await db.ExecuteAsync("""
            INSERT INTO PasswordResets (UserId, SecretDigest, IssuedByUserId, CreatedAtUtc, ExpiresAtUtc, UsedAtUtc)
            VALUES (@UserId, @SecretDigest, @IssuedByUserId, @CreatedAtUtc, @ExpiresAtUtc, NULL);
            """, new ResetParameters(userId, Digest(secret), issuerId, now, expires.ToString("O", CultureInfo.InvariantCulture)), transaction);
        await AuditAsync(db, transaction, issuerId ?? userId, "issue-reset-link", "user", userId.ToString(CultureInfo.InvariantCulture),
            issuerId is null ? "Password reset link requested by the account, sent by email." : "Password reset link issued by an administrator.", now);
        await transaction.CommitAsync(cancellationToken);
        return new(secret, expires);
    }

    /// <summary>
    /// Sets a new password with a reset secret, once. Returns false when the secret is unknown, used, expired
    /// or for a disabled account, which the caller reports as one refusal. Every session and token of the
    /// account is revoked with it.
    /// </summary>
    public async Task<bool> RedeemPasswordResetAsync(string secret, string newPassword, CancellationToken cancellationToken = default)
    {
        if (!ValidSecret(secret, "graft_reset_")) return false;
        PasswordHash.ValidatePassword(newPassword);
        string passwordHash = PasswordHash.Create(newPassword);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        string now = Now();
        ResetRow? reset = await db.QuerySingleOrDefaultAsync<ResetRow>("""
            SELECT r.Id, r.UserId
            FROM PasswordResets r
            JOIN Users u ON u.Id = r.UserId
            WHERE r.SecretDigest = @SecretDigest AND r.UsedAtUtc IS NULL AND r.ExpiresAtUtc > @NowUtc AND u.Disabled = 0;
            """, new ResolveSecretParameters(Digest(secret), now), transaction);
        if (reset is null) return false;
        await db.ExecuteAsync("UPDATE PasswordResets SET UsedAtUtc = @NowUtc WHERE Id = @Id;", new TouchKeyParameters(reset.Id, now), transaction);
        await db.ExecuteAsync("UPDATE Users SET PasswordHash = @PasswordHash WHERE Id = @Id;",
            new PasswordUpdateParameters(reset.UserId, passwordHash), transaction);
        await RevokeUserCredentialsAsync(db, transaction, reset.UserId, now);
        await AuditAsync(db, transaction, reset.UserId, "reset-password-link", "user", reset.UserId.ToString(CultureInfo.InvariantCulture),
            "Password set through a reset link; credentials revoked.", now);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}

/// <summary>The address is already on another account.</summary>
public sealed class EmailConflictException() : Exception("This email address is already used by another account.");

internal sealed record UserListRow(long Id, string Username, long IsAdministrator, long Disabled, string? Email);
internal sealed record EmailParameters(long Id, string? Email);
internal sealed record EmailLookupParameters(string Email);
internal sealed record ResetParameters(long UserId, string SecretDigest, long? IssuedByUserId, string CreatedAtUtc, string ExpiresAtUtc);
internal sealed record ResetRow(long Id, long UserId);
