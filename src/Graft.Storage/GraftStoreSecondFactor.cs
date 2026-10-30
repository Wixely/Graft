using System.Globalization;
using System.Security.Cryptography;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// The second factor: a time-based code from an authenticator app, with recovery codes for the day the
/// app is gone. Nothing here needs email. Enrolment is two steps, because a secret the app never received
/// would lock the account: the secret is issued pending, and the first correct code enables it.
///
/// <para>
/// Signing in becomes two steps for an account with the factor on: the password earns a challenge, and the
/// code earns the session. A code is accepted once per thirty-second step, so a code seen on the wire cannot
/// be replayed in the same window. Personal access tokens are unaffected, as GitLab's are: a token is a
/// credential issued from inside a session that already passed both factors.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    private const int RecoveryCodeCount = 10;

    /// <summary>The first step: the password. Returns a session when that is all the account needs, or the
    /// account that still owes a code. Null means the password was wrong or the account is unavailable.</summary>
    public async Task<FirstFactorResult?> LoginFirstFactorAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        string normalized;
        try { normalized = NormalizeName(username, nameof(username)); }
        catch (ArgumentException) { return null; }

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        UserCredentialRow? user = await db.QuerySingleOrDefaultAsync<UserCredentialRow>("""
            SELECT Id, Username, PasswordHash, IsAdministrator, Disabled
            FROM Users WHERE NormalizedUsername = @NormalizedUsername;
            """, new NormalizedNameParameters(normalized));
        if (user is null || user.Disabled != 0 || !PasswordHash.Verify(password, user.PasswordHash)) return null;

        long enabled = await db.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM SecondFactors WHERE UserId = @UserId AND EnabledAtUtc IS NOT NULL;", new UserParameters(user.Id));
        if (enabled > 0) return new(null, user.Id);
        return new(await CreateSessionAsync(db, user.Id), null);
    }

    /// <summary>The second step: a code from the app, or a recovery code, for an account the first step named.</summary>
    public async Task<SessionGrant?> LoginSecondFactorAsync(long userId, string code, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        long active = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Users WHERE Id = @Id AND Disabled = 0;", new IdParameters(userId), transaction);
        if (active != 1) return null;
        if (!await AcceptCodeAsync(db, transaction, userId, code, cancellationToken)) return null;
        SessionGrant grant = await CreateSessionAsync(db, userId, transaction);
        await transaction.CommitAsync(cancellationToken);
        return grant;
    }

    /// <summary>Starts, or restarts, an enrolment: a fresh secret, pending until a code confirms it. An account
    /// with the factor already on must turn it off first, so a stolen session cannot swap the app.</summary>
    public async Task<SecondFactorSetup> BeginSecondFactorAsync(AuthenticatedIdentity actor, string issuer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        long enabled = await db.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM SecondFactors WHERE UserId = @UserId AND EnabledAtUtc IS NOT NULL;", new UserParameters(liveActor.UserId), transaction);
        if (enabled > 0) throw new InvalidOperationException("The second factor is already on; turn it off before enrolling again.");
        byte[] secret = Totp.NewSecret();
        string now = Now();
        await db.ExecuteAsync("""
            INSERT INTO SecondFactors (UserId, Secret, CreatedAtUtc, EnabledAtUtc, LastStep) VALUES (@UserId, @Secret, @CreatedAtUtc, NULL, 0)
            ON CONFLICT(UserId) DO UPDATE SET Secret = excluded.Secret, CreatedAtUtc = excluded.CreatedAtUtc, EnabledAtUtc = NULL, LastStep = 0;
            """, new SecondFactorParameters(liveActor.UserId, Base32.Encode(secret), now), transaction);
        await transaction.CommitAsync(cancellationToken);
        return new(Base32.Encode(secret), Totp.Uri(issuer, liveActor.Username, secret));
    }

    /// <summary>Confirms the pending enrolment with a code from the app, turns the factor on, and returns the
    /// recovery codes, which are shown once and kept only as digests.</summary>
    public async Task<IReadOnlyList<string>> ConfirmSecondFactorAsync(AuthenticatedIdentity actor, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        SecondFactorRow? pending = await db.QuerySingleOrDefaultAsync<SecondFactorRow>(
            "SELECT UserId, Secret, EnabledAtUtc, LastStep FROM SecondFactors WHERE UserId = @UserId;", new UserParameters(liveActor.UserId), transaction);
        if (pending is null) throw new InvalidOperationException("There is no enrolment to confirm; begin one first.");
        if (pending.EnabledAtUtc is not null) throw new InvalidOperationException("The second factor is already on.");
        if (!Totp.Verify(Base32.Decode(pending.Secret), code ?? "", DateTimeOffset.UtcNow, out long step))
            throw new InvalidOperationException("That code was not accepted; check the app's time and try the next code.");

        string now = Now();
        await db.ExecuteAsync("UPDATE SecondFactors SET EnabledAtUtc = @NowUtc, LastStep = @Step WHERE UserId = @UserId;",
            new EnableFactorParameters(liveActor.UserId, now, step), transaction);
        await db.ExecuteAsync("DELETE FROM RecoveryCodes WHERE UserId = @UserId;", new UserParameters(liveActor.UserId), transaction);
        List<string> codes = [];
        for (int index = 0; index < RecoveryCodeCount; index++)
        {
            string recovery = NewRecoveryCode();
            codes.Add(recovery);
            await db.ExecuteAsync("INSERT INTO RecoveryCodes (UserId, CodeDigest, UsedAtUtc) VALUES (@UserId, @CodeDigest, NULL);",
                new RecoveryCodeParameters(liveActor.UserId, Digest(NormalizeRecoveryCode(recovery))), transaction);
        }
        await AuditAsync(db, transaction, liveActor.UserId, "enable-second-factor", "user", liveActor.UserId.ToString(CultureInfo.InvariantCulture),
            "Authenticator app enrolled; recovery codes issued.", now);
        await transaction.CommitAsync(cancellationToken);
        return codes;
    }

    /// <summary>Turns the factor off, given the password and a current code or a recovery code: a session is not enough.</summary>
    public async Task DisableSecondFactorAsync(AuthenticatedIdentity actor, string password, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        UserCredentialRow? user = await db.QuerySingleOrDefaultAsync<UserCredentialRow>(
            "SELECT Id, Username, PasswordHash, IsAdministrator, Disabled FROM Users WHERE Id = @Id;", new IdParameters(actor.UserId), transaction);
        if (user is null || user.Disabled != 0) throw new UnauthorizedAccessException("Identity is unavailable.");
        if (!PasswordHash.Verify(password ?? "", user.PasswordHash)) throw new InvalidOperationException("The password was not accepted.");
        if (!await AcceptCodeAsync(db, transaction, user.Id, code, cancellationToken)) throw new InvalidOperationException("The code was not accepted.");
        await RemoveSecondFactorAsync(db, transaction, user.Id, user.Id, "Second factor turned off by its owner.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>An administrator removes another account's factor, for the person who lost both the app and the
    /// codes. Not their own: that path is the password and a code, as for everyone.</summary>
    public async Task RemoveSecondFactorAsync(AuthenticatedIdentity actor, long userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.UserId == userId) throw new InvalidOperationException("An administrator turns off their own second factor with their password and a code.");
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        if (!liveActor.IsAdministrator) throw new UnauthorizedAccessException("Administrator access is required.");
        await RemoveSecondFactorAsync(db, transaction, liveActor.UserId, userId, "Second factor removed by an administrator.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> HasSecondFactorAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        return await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM SecondFactors WHERE UserId = @UserId AND EnabledAtUtc IS NOT NULL;",
            new UserParameters(userId)) > 0;
    }

    private static async Task RemoveSecondFactorAsync(SqliteConnection db, SqliteTransaction transaction, long actorUserId, long userId, string detail,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int removed = await db.ExecuteAsync("DELETE FROM SecondFactors WHERE UserId = @UserId;", new UserParameters(userId), transaction);
        await db.ExecuteAsync("DELETE FROM RecoveryCodes WHERE UserId = @UserId;", new UserParameters(userId), transaction);
        if (removed == 0) throw new InvalidOperationException("That account has no second factor.");
        await AuditAsync(db, transaction, actorUserId, "disable-second-factor", "user", userId.ToString(CultureInfo.InvariantCulture), detail, Now());
    }

    /// <summary>A code from the app for the current window, not yet used in that window, or an unused recovery code.</summary>
    private static async Task<bool> AcceptCodeAsync(SqliteConnection db, SqliteTransaction transaction, long userId, string code, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(code) || code.Length > 32) return false;
        SecondFactorRow? factor = await db.QuerySingleOrDefaultAsync<SecondFactorRow>(
            "SELECT UserId, Secret, EnabledAtUtc, LastStep FROM SecondFactors WHERE UserId = @UserId AND EnabledAtUtc IS NOT NULL;",
            new UserParameters(userId), transaction);
        if (factor is null) return false;
        string trimmed = code.Trim();
        if (Totp.Verify(Base32.Decode(factor.Secret), trimmed, DateTimeOffset.UtcNow, out long step))
        {
            if (step <= factor.LastStep) return false;
            await db.ExecuteAsync("UPDATE SecondFactors SET LastStep = @Step WHERE UserId = @UserId;", new StepParameters(userId, step), transaction);
            return true;
        }
        int used = await db.ExecuteAsync("""
            UPDATE RecoveryCodes SET UsedAtUtc = @NowUtc WHERE UserId = @UserId AND CodeDigest = @CodeDigest AND UsedAtUtc IS NULL;
            """, new UseRecoveryParameters(userId, Digest(NormalizeRecoveryCode(trimmed)), Now()), transaction);
        return used == 1;
    }

    private async Task<SessionGrant> CreateSessionAsync(SqliteConnection db, long userId, SqliteTransaction? transaction = null)
    {
        string secret = NewSecret("graft_session_");
        DateTimeOffset expires = DateTimeOffset.UtcNow + SessionWindow;
        await db.ExecuteAsync("""
            INSERT INTO BrowserSessions (UserId, SecretDigest, ExpiresAtUtc, RevokedAtUtc, CreatedAtUtc)
            VALUES (@UserId, @SecretDigest, @ExpiresAtUtc, NULL, @CreatedAtUtc);
            """, new SessionParameters(userId, Digest(secret), expires.ToString("O"), Now()), transaction);
        return new(secret, expires);
    }

    /// <summary>Ten characters of an unambiguous alphabet in two groups, as a person types them from paper.</summary>
    private static string NewRecoveryCode()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        byte[] bytes = RandomNumberGenerator.GetBytes(10);
        char[] chars = new char[11];
        for (int index = 0; index < 10; index++) chars[index < 5 ? index : index + 1] = alphabet[bytes[index] % alphabet.Length];
        chars[5] = '-';
        return new string(chars);
    }

    private static string NormalizeRecoveryCode(string code) => code.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
}

internal sealed record SecondFactorParameters(long UserId, string Secret, string CreatedAtUtc);
internal sealed record SecondFactorRow(long UserId, string Secret, string? EnabledAtUtc, long LastStep);
internal sealed record EnableFactorParameters(long UserId, string NowUtc, long Step);
internal sealed record RecoveryCodeParameters(long UserId, string CodeDigest);
internal sealed record StepParameters(long UserId, long Step);
internal sealed record UseRecoveryParameters(long UserId, string CodeDigest, string NowUtc);
