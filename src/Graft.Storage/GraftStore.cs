using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using DnaX.Data.Migrations;
using DnaX.Data.Migrations.Sqlite;
using Graft.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

[module: DapperAot]

namespace Graft.Storage;

public sealed class GraftStore : IAsyncDisposable
{
    private const TokenScope AllTokenScopes = TokenScope.ReadApi | TokenScope.Api |
        TokenScope.ReadRepository | TokenScope.WriteRepository;
    private readonly ServiceProvider services;
    private readonly string connectionString;

    /// <summary>
    /// How long a browser session stays valid without being used. Using it past the halfway point pushes it
    /// out again, so a session does not expire underneath somebody who is working, while an abandoned one
    /// still lapses.
    /// </summary>
    public TimeSpan SessionWindow { get; }

    /// <summary>
    /// The longest a session may live however much it is used. Renewal never reaches past this, so a
    /// cookie that has been copied cannot be kept alive forever by using it; eventually its holder has to
    /// present a password again.
    /// </summary>
    public static readonly TimeSpan SessionMaximum = TimeSpan.FromDays(30);

    private GraftStore(ServiceProvider services, string connectionString, TimeSpan sessionWindow)
    {
        this.services = services;
        this.connectionString = connectionString;
        SessionWindow = sessionWindow;
    }

    /// <summary>
    /// Opens the store. <paramref name="sessionWindow"/> is how long a browser session survives without
    /// being used; it is clamped to <see cref="SessionMaximum"/>, because a window longer than the absolute
    /// ceiling would be a setting that silently did nothing.
    /// </summary>
    public static async Task<GraftStore> OpenAsync(string databasePath, CancellationToken cancellationToken = default,
        TimeSpan? sessionWindow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        TimeSpan window = sessionWindow ?? TimeSpan.FromHours(12);
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sessionWindow));
        if (window > SessionMaximum) window = SessionMaximum;
        string fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();

        ServiceCollection registrations = new();
        registrations.AddLogging();
        registrations.AddDnaXDataMigrations("Graft", options =>
        {
            options.ConnectionFactory = _ => new SqliteConnection(connectionString);
            options.Manifest = GraftSchema.Manifest;
            options.ApplicationVersion = "mvp-04";
            options.UseSqlite(sqlite =>
            {
                sqlite.LockTimeout = TimeSpan.FromSeconds(10);
                sqlite.LockRetryDelay = TimeSpan.FromMilliseconds(25);
            });
        });
        ServiceProvider provider = registrations.BuildServiceProvider();
        try
        {
            await provider.GetRequiredService<IDnaXDatabaseMigrator>()
                .MigrateAsync("Graft", cancellationToken);
            return new(provider, connectionString, window);
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    public async Task<UserAccount> BootstrapAdministratorAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        string normalized = NormalizeNewUsername(username);
        string passwordHash = PasswordHash.Create(password);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        if (await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM Users;", transaction: transaction) != 0)
            throw new InvalidOperationException("Bootstrap is available only for an empty user database.");

        string now = Now();
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO Users (Username, NormalizedUsername, PasswordHash, IsAdministrator, Disabled, CreatedAtUtc)
            VALUES (@Username, @NormalizedUsername, @PasswordHash, 1, 0, @CreatedAtUtc)
            RETURNING Id;
            """, new CreateUserParameters(username, normalized, passwordHash, now), transaction);
        await AuditAsync(db, transaction, id, "bootstrap-admin", "user", id.ToString(CultureInfo.InvariantCulture), "First administrator created.", now);
        await transaction.CommitAsync(cancellationToken);
        return new(id, username, true, false);
    }

    public async Task<UserAccount> CreateUserAsync(
        AuthenticatedIdentity actor,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        string normalized = NormalizeNewUsername(username);
        string passwordHash = PasswordHash.Create(password);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        if (!liveActor.IsAdministrator) throw new UnauthorizedAccessException("Administrator access is required.");

        string now = Now();
        long id;
        try
        {
            id = await db.QuerySingleAsync<long>("""
                INSERT INTO Users (Username, NormalizedUsername, PasswordHash, IsAdministrator, Disabled, CreatedAtUtc)
                VALUES (@Username, @NormalizedUsername, @PasswordHash, 0, 0, @CreatedAtUtc)
                RETURNING Id;
                """, new CreateUserParameters(username, normalized, passwordHash, now), transaction);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            throw new UserConflictException("A user with this username already exists.", error);
        }
        await AuditAsync(db, transaction, actor.UserId, "create-user", "user", id.ToString(CultureInfo.InvariantCulture), "Ordinary user created.", now);
        await transaction.CommitAsync(cancellationToken);
        return new(id, username, false, false);
    }

    public async Task<IReadOnlyList<UserAccount>> ListUsersAsync(
        AuthenticatedIdentity actor,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        if (!liveActor.IsAdministrator) throw new UnauthorizedAccessException("Administrator access is required.");
        IEnumerable<IdentityRow> rows = await db.QueryAsync<IdentityRow>("""
            SELECT Id, Username, IsAdministrator, Disabled
            FROM Users
            ORDER BY NormalizedUsername;
            """);
        return rows.Select(row => new UserAccount(row.Id, row.Username, row.IsAdministrator != 0, row.Disabled != 0)).ToArray();
    }

    public async Task ChangePasswordAsync(
        AuthenticatedIdentity actor,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        PasswordHash.ValidatePassword(newPassword);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        UserCredentialRow? user = await db.QuerySingleOrDefaultAsync<UserCredentialRow>("""
            SELECT Id, Username, PasswordHash, IsAdministrator, Disabled
            FROM Users WHERE Id = @Id;
            """, new IdParameters(actor.UserId), transaction);
        if (user is null || user.Disabled != 0) throw new UnauthorizedAccessException("Identity is unavailable.");
        if (!PasswordHash.Verify(currentPassword, user.PasswordHash))
            throw new InvalidOperationException("Current password was not accepted.");
        if (PasswordHash.Verify(newPassword, user.PasswordHash))
            throw new InvalidOperationException("New password must differ from the current password.");

        string passwordHash = PasswordHash.Create(newPassword);
        string now = Now();
        await db.ExecuteAsync("UPDATE Users SET PasswordHash = @PasswordHash WHERE Id = @Id;",
            new PasswordUpdateParameters(actor.UserId, passwordHash), transaction);
        await RevokeUserCredentialsAsync(db, transaction, actor.UserId, now);
        await AuditAsync(db, transaction, actor.UserId, "change-password", "user", actor.UserId.ToString(CultureInfo.InvariantCulture), "Password changed and credentials revoked.", now);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ResetPasswordAsync(
        AuthenticatedIdentity actor,
        long userId,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (actor.UserId == userId) throw new InvalidOperationException("Administrators must change their own password using the current password.");
        string passwordHash = PasswordHash.Create(newPassword);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        if (!liveActor.IsAdministrator) throw new UnauthorizedAccessException("Administrator access is required.");
        string now = Now();
        int changed = await db.ExecuteAsync("UPDATE Users SET PasswordHash = @PasswordHash WHERE Id = @Id AND Disabled = 0;",
            new PasswordUpdateParameters(userId, passwordHash), transaction);
        if (changed != 1) throw new InvalidOperationException("User is unavailable or disabled.");
        await RevokeUserCredentialsAsync(db, transaction, userId, now);
        await AuditAsync(db, transaction, actor.UserId, "reset-password", "user", userId.ToString(CultureInfo.InvariantCulture), "Password reset and credentials revoked.", now);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task EnableUserAsync(
        AuthenticatedIdentity actor,
        long userId,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (actor.UserId == userId) throw new InvalidOperationException("Administrators cannot enable their current account.");
        string passwordHash = PasswordHash.Create(newPassword);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        if (!liveActor.IsAdministrator) throw new UnauthorizedAccessException("Administrator access is required.");
        string now = Now();
        int changed = await db.ExecuteAsync("UPDATE Users SET PasswordHash = @PasswordHash, Disabled = 0 WHERE Id = @Id AND Disabled = 1;",
            new PasswordUpdateParameters(userId, passwordHash), transaction);
        if (changed != 1) throw new InvalidOperationException("User is unavailable or already active.");
        await RevokeUserCredentialsAsync(db, transaction, userId, now);
        await AuditAsync(db, transaction, actor.UserId, "enable-user", "user", userId.ToString(CultureInfo.InvariantCulture), "User enabled with a fresh password; older credentials remain revoked.", now);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<SessionGrant?> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
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

        string secret = NewSecret("graft_session_");
        DateTimeOffset expires = DateTimeOffset.UtcNow + SessionWindow;
        string now = Now();
        await db.ExecuteAsync("""
            INSERT INTO BrowserSessions (UserId, SecretDigest, ExpiresAtUtc, RevokedAtUtc, CreatedAtUtc)
            VALUES (@UserId, @SecretDigest, @ExpiresAtUtc, NULL, @CreatedAtUtc);
            """, new SessionParameters(user.Id, Digest(secret), expires.ToString("O"), now));
        return new(secret, expires);
    }

    /// <summary>
    /// Resolves a session without touching it. This is the one the ASP.NET authentication handler uses, and
    /// it must stay a pure read: that handler runs on every request through <c>UseAuthentication</c>, well
    /// before any route, and it has no way to send the browser a refreshed cookie. If renewal happened here
    /// the stored expiry would creep forward while the cookie's stayed where it was, and the session would
    /// die in the browser while the server still considered it good.
    /// </summary>
    public async Task<AuthenticatedIdentity?> ResolveSessionAsync(string secret, CancellationToken cancellationToken = default) =>
        (await LookUpSessionAsync(secret, renew: false, cancellationToken))?.Identity;

    /// <summary>
    /// Resolves a session and renews it when it is past the halfway point of its window, reporting the
    /// expiry the caller should now put on the cookie.
    ///
    /// <para>
    /// Renewal is what makes a session an idle timeout rather than a countdown from sign-in. Without it a
    /// window of any length throws the user out mid-sentence once it elapses, however hard they were
    /// working, which is what this used to do with a window of fifteen minutes.
    /// </para>
    ///
    /// <para>
    /// It renews only past halfway because resolving happens on nearly every request. Extending on each one
    /// would mean a database write per request against a store with a single writer; this bounds it to one
    /// write per half window per session and changes nothing a user could notice.
    /// </para>
    /// </summary>
    public async Task<ResolvedSession?> ResolveSessionDetailedAsync(string secret, CancellationToken cancellationToken = default) =>
        await LookUpSessionAsync(secret, renew: true, cancellationToken);

    private async Task<ResolvedSession?> LookUpSessionAsync(string secret, bool renew, CancellationToken cancellationToken)
    {
        if (!ValidSecret(secret, "graft_session_")) return null;
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        SessionIdentityRow? row = await db.QuerySingleOrDefaultAsync<SessionIdentityRow>("""
            SELECT u.Id, u.Username, u.IsAdministrator, u.Disabled, s.CreatedAtUtc, s.ExpiresAtUtc
            FROM BrowserSessions s
            JOIN Users u ON u.Id = s.UserId
            WHERE s.SecretDigest = @SecretDigest
              AND s.RevokedAtUtc IS NULL
              AND s.ExpiresAtUtc > @NowUtc;
            """, new ResolveSecretParameters(Digest(secret), Now()));
        if (row is null || row.Disabled != 0) return null;

        AuthenticatedIdentity identity = new(row.Id, row.Username, row.IsAdministrator != 0, IdentityKind.BrowserSession);
        DateTimeOffset expires = DateTimeOffset.Parse(row.ExpiresAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        DateTimeOffset created = DateTimeOffset.Parse(row.CreatedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (!renew || expires - now > SessionWindow / 2) return new(identity, expires, false);

        // Never past the absolute ceiling, and never backwards: a window shortened by an operator must not
        // extend a session that already runs longer than the new setting allows.
        DateTimeOffset renewed = now + SessionWindow;
        DateTimeOffset ceiling = created + SessionMaximum;
        if (renewed > ceiling) renewed = ceiling;
        if (renewed <= expires) return new(identity, expires, false);

        await db.ExecuteAsync("""
            UPDATE BrowserSessions SET ExpiresAtUtc = @ExpiresAtUtc
            WHERE SecretDigest = @SecretDigest AND RevokedAtUtc IS NULL;
            """, new RenewSessionParameters(Digest(secret), renewed.ToString("O")));
        return new(identity, renewed, true);
    }

    public async Task LogoutAsync(string secret, CancellationToken cancellationToken = default)
    {
        if (!ValidSecret(secret, "graft_session_")) return;
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await db.ExecuteAsync("""
            UPDATE BrowserSessions SET RevokedAtUtc = @RevokedAtUtc
            WHERE SecretDigest = @SecretDigest AND RevokedAtUtc IS NULL;
            """, new RevokeSecretParameters(Digest(secret), Now()));
    }

    public async Task<TokenGrant> CreateTokenAsync(
        AuthenticatedIdentity actor,
        string name,
        TokenScope scopes,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 100) throw new ArgumentException("Token names cannot exceed 100 characters.", nameof(name));
        if (scopes == TokenScope.None || (scopes & ~AllTokenScopes) != 0) throw new ArgumentOutOfRangeException(nameof(scopes));
        expiresAtUtc = expiresAtUtc.ToUniversalTime();
        if (expiresAtUtc <= DateTimeOffset.UtcNow || expiresAtUtc > DateTimeOffset.UtcNow.AddYears(1))
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "Token expiry must be within one year.");

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        string secret = NewSecret("graft_pat_");
        string now = Now();
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO AccessTokens (UserId, Name, SecretDigest, Scopes, ExpiresAtUtc, RevokedAtUtc, CreatedAtUtc)
            VALUES (@UserId, @Name, @SecretDigest, @Scopes, @ExpiresAtUtc, NULL, @CreatedAtUtc)
            RETURNING Id;
            """, new TokenParameters(actor.UserId, name, Digest(secret), (int)scopes, expiresAtUtc.ToString("O"), now), transaction);
        await AuditAsync(db, transaction, actor.UserId, "create-token", "token", id.ToString(CultureInfo.InvariantCulture), "Personal token created.", now);
        await transaction.CommitAsync(cancellationToken);
        return new(id, secret, scopes, expiresAtUtc);
    }

    public async Task<AuthenticatedIdentity?> ResolveTokenAsync(string secret, CancellationToken cancellationToken = default)
    {
        if (!ValidSecret(secret, "graft_pat_")) return null;
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        TokenIdentityRow? row = await db.QuerySingleOrDefaultAsync<TokenIdentityRow>("""
            SELECT u.Id, u.Username, u.IsAdministrator, u.Disabled, t.Scopes
            FROM AccessTokens t
            JOIN Users u ON u.Id = t.UserId
            WHERE t.SecretDigest = @SecretDigest
              AND t.RevokedAtUtc IS NULL
              AND t.ExpiresAtUtc > @NowUtc;
            """, new ResolveSecretParameters(Digest(secret), Now()));
        return row is null || row.Disabled != 0
            ? null
            : new(row.Id, row.Username, row.IsAdministrator != 0, IdentityKind.PersonalAccessToken, (TokenScope)row.Scopes);
    }

    public async Task<IReadOnlyList<PersonalAccessToken>> ListActiveTokensAsync(
        AuthenticatedIdentity actor,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        IEnumerable<TokenRow> rows = await db.QueryAsync<TokenRow>("""
            SELECT Id, Name, Scopes, ExpiresAtUtc, CreatedAtUtc
            FROM AccessTokens
            WHERE UserId = @UserId AND RevokedAtUtc IS NULL AND ExpiresAtUtc > @NowUtc
            ORDER BY Id DESC;
            """, new ActiveTokensParameters(liveActor.UserId, Now()));
        return rows.Select(row => new PersonalAccessToken(row.Id, row.Name, (TokenScope)row.Scopes,
            DateTimeOffset.Parse(row.ExpiresAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(row.CreatedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))).ToArray();
    }

    public async Task RevokeOwnTokenAsync(
        AuthenticatedIdentity actor,
        long tokenId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        string now = Now();
        int changed = await db.ExecuteAsync("""
            UPDATE AccessTokens SET RevokedAtUtc = @RevokedAtUtc
            WHERE Id = @Id AND UserId = @UserId AND RevokedAtUtc IS NULL;
            """, new RevokeOwnTokenParameters(tokenId, liveActor.UserId, now), transaction);
        if (changed != 1) throw new UnauthorizedAccessException("Token unavailable.");
        await AuditAsync(db, transaction, actor.UserId, "revoke-token", "token", tokenId.ToString(CultureInfo.InvariantCulture), "Personal token revoked.", now);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RevokeTokenAsync(
        AuthenticatedIdentity actor,
        long tokenId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        TokenOwnerRow? token = await db.QuerySingleOrDefaultAsync<TokenOwnerRow>(
            "SELECT UserId FROM AccessTokens WHERE Id = @Id;", new IdParameters(tokenId), transaction);
        if (token is null || (!liveActor.IsAdministrator && token.UserId != liveActor.UserId))
            throw new UnauthorizedAccessException("Token unavailable.");
        string now = Now();
        await db.ExecuteAsync("UPDATE AccessTokens SET RevokedAtUtc = @RevokedAtUtc WHERE Id = @Id AND RevokedAtUtc IS NULL;",
            new RevokeByIdParameters(tokenId, now), transaction);
        await AuditAsync(db, transaction, actor.UserId, "revoke-token", "token", tokenId.ToString(CultureInfo.InvariantCulture), "Personal token revoked.", now);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ProjectInfo> CreateProjectAsync(
        AuthenticatedIdentity actor,
        string slug,
        string name,
        string description = "",
        CancellationToken cancellationToken = default) =>
        await CreateProjectCoreAsync(actor, slug, name, description, null, cancellationToken);

    /// <summary>
    /// Creates a project and its repository. <paramref name="origin"/> is recorded in the audit trail and
    /// nowhere else: a project that appeared because somebody pushed to a path that did not exist should be
    /// distinguishable afterwards from one somebody deliberately created.
    /// </summary>
    public async Task<ProjectInfo> CreateProjectWithRepositoryAsync(
        AuthenticatedIdentity actor,
        string slug,
        string name,
        Func<ProjectInfo, CancellationToken, Task> initializeRepository,
        string description = "",
        CancellationToken cancellationToken = default,
        string origin = "the API or browser")
    {
        ArgumentNullException.ThrowIfNull(initializeRepository);
        return await CreateProjectCoreAsync(actor, slug, name, description, initializeRepository, cancellationToken, origin);
    }

    public async Task<ProjectInfo?> FindReadyProjectAsync(
        string projectNamespace,
        string slug,
        CancellationToken cancellationToken = default)
    {
        string normalizedNamespace;
        string normalizedSlug;
        try
        {
            normalizedNamespace = NormalizeName(projectNamespace, nameof(projectNamespace));
            normalizedSlug = NormalizeName(slug, nameof(slug));
        }
        catch (ArgumentException) { return null; }

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        ProjectPathRow? row = await db.QuerySingleOrDefaultAsync<ProjectPathRow>("""
            SELECT p.Id, p.OwnerUserId, u.Username AS Namespace, p.Slug, p.Name, p.StorageId, p.DefaultBranch,
                   p.Visibility, p.PagePublished, p.GlobalSlug
            FROM Projects p
            JOIN Users u ON u.Id = p.OwnerUserId
            WHERE u.NormalizedUsername = @NormalizedNamespace
              AND p.NormalizedSlug = @NormalizedSlug
              AND p.State = 'ready';
            """, new ProjectPathParameters(normalizedNamespace, normalizedSlug));
        return row is null ? null : ToInfo(row);
    }

    public async Task<ProjectDetails?> GetReadyProjectAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        ProjectDetailsRow? row = await db.QuerySingleOrDefaultAsync<ProjectDetailsRow>("""
            SELECT p.Id, p.OwnerUserId, u.Username AS Namespace, p.Slug, p.Name, p.Description, p.StorageId, p.CreatedAtUtc,
                   p.DefaultBranch, p.Visibility, p.PagePublished, p.GlobalSlug
            FROM Projects p
            JOIN Users u ON u.Id = p.OwnerUserId
            WHERE p.Id = @Id AND p.State = 'ready';
            """, new IdParameters(projectId));
        return row is null ? null : ToDetails(row);
    }

    public async Task<ProjectDetails?> FindReadyProjectDetailsAsync(
        string projectNamespace,
        string slug,
        CancellationToken cancellationToken = default)
    {
        ProjectInfo? project = await FindReadyProjectAsync(projectNamespace, slug, cancellationToken);
        return project is null ? null : await GetReadyProjectAsync(project.Id, cancellationToken);
    }

    public async Task<ProjectPage> ListAccessibleProjectsAsync(
        AuthenticatedIdentity identity,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0 || take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity live = await RequireLiveIdentityAsync(db, null, identity, cancellationToken);
        ProjectListParameters parameters = new(live.UserId, live.IsAdministrator ? 1 : 0, take, skip);
        IEnumerable<ProjectDetailsRow> rows = await db.QueryAsync<ProjectDetailsRow>("""
            SELECT p.Id, p.OwnerUserId, u.Username AS Namespace, p.Slug, p.Name, p.Description, p.StorageId, p.CreatedAtUtc,
                   p.DefaultBranch, p.Visibility, p.PagePublished, p.GlobalSlug
            FROM Projects p
            JOIN Users u ON u.Id = p.OwnerUserId
            WHERE p.State = 'ready' AND (
                @IsAdministrator = 1 OR p.OwnerUserId = @UserId OR
                EXISTS (SELECT 1 FROM ProjectMemberships m WHERE m.ProjectId = p.Id AND m.UserId = @UserId)
            )
            ORDER BY p.Id
            LIMIT @Take OFFSET @Skip;
            """, parameters);
        long total = await db.QuerySingleAsync<long>("""
            SELECT COUNT(*) FROM Projects p
            WHERE p.State = 'ready' AND (
                @IsAdministrator = 1 OR p.OwnerUserId = @UserId OR
                EXISTS (SELECT 1 FROM ProjectMemberships m WHERE m.ProjectId = p.Id AND m.UserId = @UserId)
            );
            """, parameters);
        return new(rows.Select(ToDetails).ToArray(), total);
    }

    private async Task<ProjectInfo> CreateProjectCoreAsync(
        AuthenticatedIdentity actor,
        string slug,
        string name,
        string description,
        Func<ProjectInfo, CancellationToken, Task>? initializeRepository,
        CancellationToken cancellationToken,
        string origin = "the API or browser")
    {
        string normalizedSlug = NormalizeName(slug, nameof(slug));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 200 || description.Length > 2_000) throw new ArgumentException("Project text is too long.");

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        string storageId = Guid.NewGuid().ToString("N");
        string now = Now();
        string globalSlug = await ReserveGlobalSlugAsync(db, transaction, normalizedSlug);
        long id;
        try
        {
            id = await db.QuerySingleAsync<long>("""
                INSERT INTO Projects (OwnerUserId, Slug, NormalizedSlug, Name, Description, StorageId, State, CreatedAtUtc, GlobalSlug)
                VALUES (@OwnerUserId, @Slug, @NormalizedSlug, @Name, @Description, @StorageId, 'pending', @CreatedAtUtc, @GlobalSlug)
                RETURNING Id;
                """, new CreateProjectParameters(liveActor.UserId, slug, normalizedSlug, name, description, storageId, now, globalSlug), transaction);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            throw new ProjectConflictException("A project with this path already exists in the namespace.", error);
        }
        await AuditAsync(db, transaction, actor.UserId, "begin-create-project", "project", id.ToString(CultureInfo.InvariantCulture), "Private project metadata created in pending state.", now);
        await transaction.CommitAsync(cancellationToken);

        ProjectInfo project = new(id, liveActor.UserId, liveActor.Username, slug, name, storageId);
        if (initializeRepository is not null) await initializeRepository(project, cancellationToken);

        await using SqliteConnection readyDb = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction readyTransaction = readyDb.BeginTransaction(deferred: false);
        int changed = await readyDb.ExecuteAsync("UPDATE Projects SET State = 'ready' WHERE Id = @Id AND State = 'pending';",
            new IdParameters(id), readyTransaction);
        if (changed != 1) throw new InvalidOperationException("Pending project could not be made ready.");
        await AuditAsync(readyDb, readyTransaction, actor.UserId, "create-project", "project", id.ToString(CultureInfo.InvariantCulture), "Private project and repository are ready, created through " + origin + ".", Now());
        await readyTransaction.CommitAsync(cancellationToken);
        return project;
    }

    public async Task SetMembershipAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long userId,
        ProjectRole role,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId, ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);
        ProjectOwnerRow owner = await db.QuerySingleAsync<ProjectOwnerRow>(
            "SELECT OwnerUserId FROM Projects WHERE Id = @Id;", new IdParameters(projectId), transaction);
        if (owner.OwnerUserId == userId) throw new InvalidOperationException("Project ownership is already Maintainer-equivalent and cannot be replaced by membership.");
        long active = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM Users WHERE Id = @Id AND Disabled = 0;", new IdParameters(userId), transaction);
        if (active != 1) throw new InvalidOperationException("Membership target is unavailable.");
        string now = Now();
        await db.ExecuteAsync("""
            INSERT INTO ProjectMemberships (ProjectId, UserId, Role, GrantedByUserId, CreatedAtUtc)
            VALUES (@ProjectId, @UserId, @Role, @GrantedByUserId, @CreatedAtUtc)
            ON CONFLICT(ProjectId, UserId) DO UPDATE SET
                Role = excluded.Role,
                GrantedByUserId = excluded.GrantedByUserId,
                CreatedAtUtc = excluded.CreatedAtUtc;
            """, new MembershipParameters(projectId, userId, (int)role, actor.UserId, now), transaction);
        await AuditAsync(db, transaction, actor.UserId, "set-membership", "project", projectId.ToString(CultureInfo.InvariantCulture), $"Direct role set to {role}.", now);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<UserAccount?> FindActiveUserAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        string normalized = NormalizeName(username, nameof(username));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        IdentityRow? row = await db.QuerySingleOrDefaultAsync<IdentityRow>("""
            SELECT Id, Username, IsAdministrator, Disabled
            FROM Users WHERE NormalizedUsername = @NormalizedUsername AND Disabled = 0;
            """, new NormalizedNameParameters(normalized));
        return row is null ? null : new(row.Id, row.Username, row.IsAdministrator != 0, false);
    }

    public async Task<IReadOnlyList<ProjectMember>> ListProjectMembersAsync(
        AuthenticatedIdentity actor,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        PermissionDecision permission = await DecideAsync(db, null, actor, projectId, ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);
        IEnumerable<ProjectMemberRow> rows = await db.QueryAsync<ProjectMemberRow>("""
            SELECT u.Id AS UserId, u.Username, m.Role
            FROM ProjectMemberships m
            JOIN Users u ON u.Id = m.UserId
            WHERE m.ProjectId = @Id AND u.Disabled = 0
            ORDER BY u.NormalizedUsername;
            """, new IdParameters(projectId));
        return rows.Select(row => new ProjectMember(row.UserId, row.Username, (ProjectRole)row.Role)).ToArray();
    }

    /// <summary>
    /// Moves a project to a new owner. Only the current owner or an administrator may do this: a Maintainer
    /// membership grants management, not the right to give the project away.
    ///
    /// <para>
    /// <b>This changes the project's URL.</b> A project's namespace is its owner's username, so transferring
    /// <c>alice/thing</c> to bob makes it <c>bob/thing</c>, and the previous clone URL stops resolving. Graft
    /// has no redirect for the old path. The previous owner is left a Maintainer membership so they keep
    /// access rather than silently losing the project they just handed over.
    /// </para>
    /// </summary>
    public async Task TransferProjectOwnershipAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long newOwnerUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);

        ProjectOwnerRow? current = await db.QuerySingleOrDefaultAsync<ProjectOwnerRow>(
            "SELECT OwnerUserId FROM Projects WHERE Id = @Id;", new IdParameters(projectId), transaction);
        if (current is null) throw new UnauthorizedAccessException("Project is unavailable.");
        if (!liveActor.IsAdministrator && liveActor.UserId != current.OwnerUserId)
            throw new UnauthorizedAccessException("Only the project owner or an administrator can transfer ownership.");
        if (current.OwnerUserId == newOwnerUserId)
            throw new InvalidOperationException("The project already has this owner.");

        long active = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM Users WHERE Id = @Id AND Disabled = 0;",
            new IdParameters(newOwnerUserId), transaction);
        if (active != 1) throw new InvalidOperationException("Transfer target is unavailable.");

        string now = Now();
        try
        {
            await db.ExecuteAsync("UPDATE Projects SET OwnerUserId = @UserId WHERE Id = @ProjectId;",
                new ProjectUserParameters(projectId, newOwnerUserId), transaction);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            // UNIQUE (OwnerUserId, NormalizedSlug): the new owner already has a project of this name.
            throw new ProjectOwnershipException("The new owner already has a project with this path.", error);
        }

        // The owner is Maintainer-equivalent by virtue of ownership, so a membership row for them would be
        // redundant and is rejected elsewhere. Drop the incoming owner's row and give the outgoing owner one.
        await db.ExecuteAsync("DELETE FROM ProjectMemberships WHERE ProjectId = @ProjectId AND UserId = @UserId;",
            new ProjectUserParameters(projectId, newOwnerUserId), transaction);
        await db.ExecuteAsync("""
            INSERT INTO ProjectMemberships (ProjectId, UserId, Role, GrantedByUserId, CreatedAtUtc)
            VALUES (@ProjectId, @UserId, @Role, @GrantedByUserId, @CreatedAtUtc)
            ON CONFLICT(ProjectId, UserId) DO UPDATE SET Role = excluded.Role;
            """, new MembershipParameters(projectId, current.OwnerUserId, (int)ProjectRole.Maintainer, actor.UserId, now), transaction);

        await AuditAsync(db, transaction, actor.UserId, "transfer-ownership", "project",
            projectId.ToString(CultureInfo.InvariantCulture),
            $"Ownership moved from user {current.OwnerUserId} to user {newOwnerUserId}; the project path changed.", now);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Records which branch a project treats as its default. Pass null to clear the preference and let the
    /// repository fall back to convention. The caller validates that the branch exists: storage keeps a name,
    /// not a guarantee, because the branch can be deleted afterwards.
    /// </summary>
    public async Task SetDefaultBranchAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string? branch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (branch is { Length: > 240 }) throw new ArgumentException("Branch name is too long.", nameof(branch));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId, ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);

        string now = Now();
        await db.ExecuteAsync("UPDATE Projects SET DefaultBranch = @Value WHERE Id = @Id;",
            new DefaultBranchParameters(projectId, string.IsNullOrWhiteSpace(branch) ? null : branch), transaction);
        await AuditAsync(db, transaction, actor.UserId, "set-default-branch", "project",
            projectId.ToString(CultureInfo.InvariantCulture),
            branch is null ? "Default branch cleared." : "Default branch set to " + branch + ".", now);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Sets how widely a project may be read. Maintainer-equivalent, so the owner or an administrator; a
    /// Maintainer membership grants it too, in common with every other project setting.
    ///
    /// <para>
    /// This is the switch the request calls final. It is stored here rather than read from the repository's
    /// own config because a push must never be able to publish a project: authoring <c>.graft</c> needs only
    /// Developer access, and this needs Maintainer.
    /// </para>
    /// </summary>
    public async Task SetProjectVisibilityAsync(
        AuthenticatedIdentity actor,
        long projectId,
        ProjectVisibility visibility,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!Enum.IsDefined(visibility)) throw new ArgumentOutOfRangeException(nameof(visibility));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId, ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);

        string now = Now();
        await db.ExecuteAsync("UPDATE Projects SET Visibility = @Value WHERE Id = @Id;",
            new ProjectTextParameters(projectId, StoreVisibility(visibility)), transaction);
        await AuditAsync(db, transaction, actor.UserId, "set-visibility", "project",
            projectId.ToString(CultureInfo.InvariantCulture),
            "Repository visibility set to " + StoreVisibility(visibility) + ".", now);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Changes a project's human-facing text: the name it is listed under and the sentence describing it.
    /// Neither is an address, so neither moves the project or changes any link to it; the path and the
    /// instance-wide slug are untouched.
    ///
    /// <para>
    /// Maintainer-equivalent, like the other project settings. A null leaves that field alone, so a caller
    /// can change one without having to resend the other and race somebody who edited it.
    /// </para>
    /// </summary>
    public async Task SetProjectDetailsAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string? name,
        string? description,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (name is null && description is null) return;
        if (name is not null && (string.IsNullOrWhiteSpace(name) || name.Length > 200))
            throw new ArgumentException("A project name is 1 to 200 characters.", nameof(name));
        if (description is not null && description.Length > 2_000)
            throw new ArgumentException("A project description is at most 2000 characters.", nameof(description));

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId, ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);

        string now = Now();
        if (name is not null)
            await db.ExecuteAsync("UPDATE Projects SET Name = @Value WHERE Id = @Id;",
                new ProjectTextParameters(projectId, name), transaction);
        if (description is not null)
            await db.ExecuteAsync("UPDATE Projects SET Description = @Value WHERE Id = @Id;",
                new ProjectTextParameters(projectId, description), transaction);

        // The audit says which fields moved rather than quoting them: a description can be two thousand
        // characters, and an audit log is not the place to keep a second copy of one.
        await AuditAsync(db, transaction, actor.UserId, "set-project-details", "project",
            projectId.ToString(CultureInfo.InvariantCulture),
            "Changed: " + string.Join(", ", new[] { name is null ? null : "name", description is null ? null : "description" }
                .Where(field => field is not null)) + ".", now);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Publishes or withdraws the project page: the curated surface that can exist over a private
    /// repository. Maintainer-equivalent, for the same reason as visibility.
    /// </summary>
    public async Task SetPagePublishedAsync(
        AuthenticatedIdentity actor,
        long projectId,
        bool published,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId, ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);

        string now = Now();
        await db.ExecuteAsync("UPDATE Projects SET PagePublished = @Value WHERE Id = @Id;",
            new ProjectFlagParameters(projectId, published ? 1 : 0), transaction);
        await AuditAsync(db, transaction, actor.UserId, "set-page-published", "project",
            projectId.ToString(CultureInfo.InvariantCulture),
            published ? "Project page published." : "Project page withdrawn.", now);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The public browser's listing: projects whose source is public, plus projects that publish a page
    /// without publishing their source. Unlisted projects are deliberately absent, because being absent from
    /// this listing is the only thing that distinguishes unlisted from public.
    ///
    /// <para>
    /// Takes no identity. It answers for the anonymous public, so a signed-in caller sees exactly what a
    /// stranger sees, and the result can never depend on who is asking.
    /// </para>
    /// </summary>
    public async Task<ProjectPage> ListPublishedProjectsAsync(
        string? search,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0 || take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        if (search is { Length: > 200 }) throw new ArgumentException("Search text is too long.", nameof(search));

        // A caller-supplied LIKE pattern would otherwise be a wildcard they control, so % and _ are escaped
        // and the escape character itself is escaped first. An empty or whitespace search matches everything
        // rather than matching nothing, because a blank search box means "show me what is here".
        string? pattern = string.IsNullOrWhiteSpace(search)
            ? null
            : "%" + search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        PublishedListParameters parameters = new(pattern, pattern is null ? 1 : 0, take, skip);
        const string where = """
            WHERE p.State = 'ready'
              AND (p.Visibility = 'public' OR p.PagePublished = 1)
              AND (@MatchAll = 1
                   OR p.Name LIKE @Pattern ESCAPE '\'
                   OR p.Slug LIKE @Pattern ESCAPE '\'
                   OR p.Description LIKE @Pattern ESCAPE '\'
                   OR u.Username LIKE @Pattern ESCAPE '\')
            """;
        IEnumerable<ProjectDetailsRow> rows = await db.QueryAsync<ProjectDetailsRow>($"""
            SELECT p.Id, p.OwnerUserId, u.Username AS Namespace, p.Slug, p.Name, p.Description, p.StorageId, p.CreatedAtUtc,
                   p.DefaultBranch, p.Visibility, p.PagePublished, p.GlobalSlug
            FROM Projects p
            JOIN Users u ON u.Id = p.OwnerUserId
            {where}
            ORDER BY p.CreatedAtUtc DESC, p.Id DESC
            LIMIT @Take OFFSET @Skip;
            """, parameters);
        long total = await db.QuerySingleAsync<long>($"""
            SELECT COUNT(*) FROM Projects p
            JOIN Users u ON u.Id = p.OwnerUserId
            {where};
            """, parameters);
        return new(rows.Select(ToDetails).ToArray(), total);
    }

    /// <summary>
    /// Whether this account owns anything that appears in the public listing. Used to decide whether an
    /// anonymous caller may read their avatar: the listing already names the owner of every published
    /// project, so this reveals nothing the listing does not, while accounts that publish nothing stay
    /// unmentioned rather than becoming enumerable one id at a time.
    /// </summary>
    public async Task<bool> HasPublishedProjectAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        return await db.QuerySingleAsync<long>("""
            SELECT COUNT(*) FROM Projects
            WHERE OwnerUserId = @Id AND State = 'ready' AND (Visibility = 'public' OR PagePublished = 1);
            """, new IdParameters(userId)) != 0;
    }

    public async Task RevokeMembershipAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId, ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);
        string now = Now();
        await db.ExecuteAsync("DELETE FROM ProjectMemberships WHERE ProjectId = @ProjectId AND UserId = @UserId;",
            new ProjectUserParameters(projectId, userId), transaction);
        await AuditAsync(db, transaction, actor.UserId, "revoke-membership", "project", projectId.ToString(CultureInfo.InvariantCulture), "Direct membership revoked.", now);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DisableUserAsync(
        AuthenticatedIdentity actor,
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (actor.UserId == userId) throw new InvalidOperationException("Administrators cannot disable their current account.");
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        if (!liveActor.IsAdministrator) throw new UnauthorizedAccessException("Administrator access is required.");

        // A project has exactly one owner, and only that owner or an administrator can manage it. Disabling an
        // owner would therefore leave the project with no one but an administrator able to manage it, and no
        // way to appoint a replacement. Transfer first. For an urgent response to a compromised account,
        // resetting its password already revokes every session and token immediately.
        IEnumerable<ProjectPathRow> owned = await db.QueryAsync<ProjectPathRow>("""
            SELECT p.Id, p.OwnerUserId, u.Username AS Namespace, p.Slug, p.Name, p.StorageId, p.DefaultBranch,
                   p.Visibility, p.PagePublished, p.GlobalSlug
            FROM Projects p
            JOIN Users u ON u.Id = p.OwnerUserId
            WHERE p.OwnerUserId = @Id
            ORDER BY p.NormalizedSlug;
            """, new IdParameters(userId), transaction);
        string[] blocking = [.. owned.Select(row => row.Slug)];
        if (blocking.Length > 0)
            throw new ProjectOwnershipException(
                "Transfer ownership before disabling this account. It still owns: " + string.Join(", ", blocking) + ".");

        string now = Now();
        int changed = await db.ExecuteAsync("UPDATE Users SET Disabled = 1 WHERE Id = @Id AND Disabled = 0;", new IdParameters(userId), transaction);
        if (changed != 1) throw new InvalidOperationException("User is unavailable or already disabled.");
        await RevokeUserCredentialsAsync(db, transaction, userId, now);
        await AuditAsync(db, transaction, actor.UserId, "disable-user", "user", userId.ToString(CultureInfo.InvariantCulture), "User disabled and credentials revoked.", now);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Stores an avatar for a user or project, replacing any existing one. A user may set their own and an
    /// administrator may set anyone's; a project avatar needs the same permission as managing the project.
    /// </summary>
    public async Task SetAvatarAsync(
        AuthenticatedIdentity actor,
        AvatarOwner ownerType,
        long ownerId,
        ValidatedImage image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(image);
        if (!Enum.IsDefined(ownerType)) throw new ArgumentOutOfRangeException(nameof(ownerType));

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        if (ownerType == AvatarOwner.Project)
        {
            PermissionDecision permission = await DecideAsync(db, transaction, actor, ownerId, ProjectAction.ManageProject, cancellationToken);
            if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);
        }
        else if (actor.UserId != ownerId && !actor.IsAdministrator)
            throw new UnauthorizedAccessException("Only the account holder or an administrator can set this avatar.");
        else
        {
            long active = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM Users WHERE Id = @Id AND Disabled = 0;",
                new IdParameters(ownerId), transaction);
            if (active != 1) throw new InvalidOperationException("Avatar target is unavailable.");
        }

        string now = Now();
        string digest = Convert.ToHexStringLower(SHA256.HashData(image.Bytes));

        // This one statement uses a provider command rather than Dapper. Dapper.AOT binds a byte array by
        // expanding it into one SQL variable per element, which is how it supports IN clauses and cannot
        // express a BLOB; a 350 KB image became 350,000 variables and SQLite refused it. Reading the column
        // back through Dapper is fine, so only the write drops down. Verified by the avatar acceptance case.
        await using (SqliteCommand command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Avatars (OwnerType, OwnerId, ContentType, Width, Height, Bytes, Sha256, UpdatedAtUtc)
                VALUES ($ownerType, $ownerId, $contentType, $width, $height, $bytes, $sha256, $updatedAtUtc)
                ON CONFLICT(OwnerType, OwnerId) DO UPDATE SET
                    ContentType = excluded.ContentType,
                    Width = excluded.Width,
                    Height = excluded.Height,
                    Bytes = excluded.Bytes,
                    Sha256 = excluded.Sha256,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            command.Parameters.AddWithValue("$ownerType", Owner(ownerType));
            command.Parameters.AddWithValue("$ownerId", ownerId);
            command.Parameters.AddWithValue("$contentType", image.ContentType);
            command.Parameters.AddWithValue("$width", image.Width);
            command.Parameters.AddWithValue("$height", image.Height);
            command.Parameters.Add("$bytes", SqliteType.Blob).Value = image.Bytes;
            command.Parameters.AddWithValue("$sha256", digest);
            command.Parameters.AddWithValue("$updatedAtUtc", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await AuditAsync(db, transaction, actor.UserId, "set-avatar", Owner(ownerType),
            ownerId.ToString(CultureInfo.InvariantCulture), "Avatar replaced.", now);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Reads an avatar, or null when none is set. Callers decide who may see the owner.</summary>
    public async Task<StoredAvatar?> GetAvatarAsync(
        AvatarOwner ownerType,
        long ownerId,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(ownerType)) throw new ArgumentOutOfRangeException(nameof(ownerType));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AvatarRow? row = await db.QuerySingleOrDefaultAsync<AvatarRow>("""
            SELECT ContentType, Bytes, Sha256 FROM Avatars WHERE OwnerType = @OwnerType AND OwnerId = @OwnerId;
            """, new AvatarReadParameters(Owner(ownerType), ownerId));
        return row is null ? null : new(row.ContentType, row.Bytes, row.Sha256);
    }

    /// <summary>Whether an avatar exists, without reading its bytes, for building listing payloads.</summary>
    public async Task<bool> HasAvatarAsync(AvatarOwner ownerType, long ownerId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        return await db.QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM Avatars WHERE OwnerType = @OwnerType AND OwnerId = @OwnerId;",
            new AvatarReadParameters(Owner(ownerType), ownerId)) == 1;
    }

    private static string Owner(AvatarOwner ownerType) => ownerType == AvatarOwner.Project ? "project" : "user";

    /// <summary>
    /// The administrator's Model Context Protocol switches. Read per request by the endpoint rather than
    /// cached, so a change in the browser takes effect on the next call without anything being restarted;
    /// three indexed rows are cheaper than the request that follows them.
    /// </summary>
    public async Task<McpSettings> GetMcpSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        IEnumerable<SettingRow> rows = await db.QueryAsync<SettingRow>(
            "SELECT Name, Value FROM ServerSettings WHERE Name IN ('mcp.enabled', 'mcp.allow-writes', 'mcp.allow-anonymous');");
        McpSettings settings = McpSettings.Default;
        foreach (SettingRow row in rows)
        {
            // Anything other than the literal "true" reads as false. The only writer stores one of two
            // literals, so this is about a hand-edited database failing closed rather than open.
            bool value = row.Value == "true";
            settings = row.Name switch
            {
                "mcp.enabled" => settings with { Enabled = value },
                "mcp.allow-writes" => settings with { AllowWrites = value },
                "mcp.allow-anonymous" => settings with { AllowAnonymous = value },
                _ => settings
            };
        }
        return settings;
    }

    public async Task SetMcpSettingsAsync(
        AuthenticatedIdentity actor,
        McpSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        if (!liveActor.IsAdministrator) throw new UnauthorizedAccessException("Administrator access is required.");

        string now = Now();
        foreach ((string name, bool value) in new[]
        {
            ("mcp.enabled", settings.Enabled),
            ("mcp.allow-writes", settings.AllowWrites),
            ("mcp.allow-anonymous", settings.AllowAnonymous)
        })
        {
            await db.ExecuteAsync("""
                INSERT INTO ServerSettings (Name, Value, UpdatedByUserId, UpdatedAtUtc)
                VALUES (@Name, @Value, @UpdatedByUserId, @UpdatedAtUtc)
                ON CONFLICT (Name) DO UPDATE SET
                    Value = excluded.Value,
                    UpdatedByUserId = excluded.UpdatedByUserId,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
                """, new SettingWriteParameters(name, value ? "true" : "false", liveActor.UserId, now), transaction);
        }
        // Audited with the whole resulting posture, so a reader of the log sees what the server was doing
        // after the change rather than reconstructing it from a sequence of single-switch events.
        await AuditAsync(db, transaction, liveActor.UserId, "mcp-settings", "server", null,
            "enabled=" + (settings.Enabled ? "true" : "false") +
            "; allow-writes=" + (settings.AllowWrites ? "true" : "false") +
            "; allow-anonymous=" + (settings.AllowAnonymous ? "true" : "false"), now);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PermissionDecision> GetPermissionAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        ProjectAction action,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        return await DecideAsync(db, null, identity, projectId, action, cancellationToken);
    }

    public async Task<bool> ContainsStoredSecretAsync(string rawSecret, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        long count = await db.QuerySingleAsync<long>("""
            SELECT
                (SELECT COUNT(*) FROM Users WHERE PasswordHash = @Value) +
                (SELECT COUNT(*) FROM BrowserSessions WHERE SecretDigest = @Value) +
                (SELECT COUNT(*) FROM AccessTokens WHERE SecretDigest = @Value);
            """, new RawValueParameters(rawSecret));
        return count != 0;
    }

    public async Task<long> CountAuditEventsAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        return await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM AuditEvents;");
    }

    private static async Task<PermissionDecision> DecideAsync(
        SqliteConnection db,
        SqliteTransaction? transaction,
        AuthenticatedIdentity? identity,
        long projectId,
        ProjectAction action,
        CancellationToken cancellationToken)
    {
        ProjectAccessRow? project = await db.QuerySingleOrDefaultAsync<ProjectAccessRow>("""
            SELECT p.OwnerUserId, p.Visibility, p.PagePublished, m.Role
            FROM Projects p
            LEFT JOIN ProjectMemberships m ON m.ProjectId = p.Id AND m.UserId = @UserId
            WHERE p.Id = @ProjectId AND p.State = 'ready';
            """, new PermissionQueryParameters(projectId, identity?.UserId ?? 0), transaction);
        if (project is null) return new(false, null, "Unavailable", "Project is unavailable.");

        AuthenticatedIdentity? liveIdentity = identity is null
            ? null
            : await FindLiveIdentityAsync(db, transaction, identity, cancellationToken);
        ProjectAccess access = new(
            project.OwnerUserId,
            ParseVisibility(project.Visibility),
            project.PagePublished != 0,
            project.Role is null ? null : (ProjectRole)project.Role.Value);
        return ProjectAuthorization.Decide(liveIdentity, access, action);
    }

    private static async Task<AuthenticatedIdentity?> FindLiveIdentityAsync(
        SqliteConnection db,
        SqliteTransaction? transaction,
        AuthenticatedIdentity identity,
        CancellationToken cancellationToken)
    {
        IdentityRow? row = await db.QuerySingleOrDefaultAsync<IdentityRow>("""
            SELECT Id, Username, IsAdministrator, Disabled FROM Users WHERE Id = @Id;
            """, new IdParameters(identity.UserId), transaction);
        await Task.CompletedTask;
        return row is null || row.Disabled != 0
            ? null
            : new(row.Id, row.Username, row.IsAdministrator != 0, identity.Kind, identity.TokenScopes);
    }

    private static async Task<AuthenticatedIdentity> RequireLiveIdentityAsync(
        SqliteConnection db,
        SqliteTransaction? transaction,
        AuthenticatedIdentity identity,
        CancellationToken cancellationToken) =>
        await FindLiveIdentityAsync(db, transaction, identity, cancellationToken)
        ?? throw new UnauthorizedAccessException("Identity is unavailable.");

    private static async Task AuditAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        long? actorUserId,
        string action,
        string targetType,
        string? targetId,
        string detail,
        string createdAtUtc)
    {
        await db.ExecuteAsync("""
            INSERT INTO AuditEvents (ActorUserId, Action, TargetType, TargetId, Detail, CreatedAtUtc)
            VALUES (@ActorUserId, @Action, @TargetType, @TargetId, @Detail, @CreatedAtUtc);
            """, new AuditParameters(actorUserId, action, targetType, targetId, detail, createdAtUtc), transaction);
    }

    private static async Task RevokeUserCredentialsAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        long userId,
        string revokedAtUtc)
    {
        await db.ExecuteAsync("UPDATE BrowserSessions SET RevokedAtUtc = @RevokedAtUtc WHERE UserId = @UserId AND RevokedAtUtc IS NULL;",
            new RevokeUserParameters(userId, revokedAtUtc), transaction);
        await db.ExecuteAsync("UPDATE AccessTokens SET RevokedAtUtc = @RevokedAtUtc WHERE UserId = @UserId AND RevokedAtUtc IS NULL;",
            new RevokeUserParameters(userId, revokedAtUtc), transaction);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>
    /// Path segments the application answers to itself, which therefore cannot be account names. A
    /// namespace is a username and sits at the root, so a user called "projects" would own every link the
    /// instance-wide slugs live under, and one called "api" would shadow the REST surface.
    ///
    /// <para>Checked when an account is created, not when one is read, so an instance that already has such
    /// an account keeps working and simply loses that one path to the application.</para>
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "projects", "api", "healthz", "mcp", "index.html"
    };

    /// <summary>Normalizes a name that will become an account, refusing the ones the application owns.</summary>
    private static string NormalizeNewUsername(string value)
    {
        string normalized = NormalizeName(value, "username");
        if (ReservedNames.Contains(normalized))
            throw new ArgumentException("That name is reserved by the application.", "username");
        return normalized;
    }

    private static string NormalizeName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length is < 2 or > 39 || value[0] is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9') ||
            value.Any(character => character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.')))
            throw new ArgumentException("Names must be 2-39 ASCII letters, numbers, dots, underscores or hyphens and start with a letter or number.", parameterName);
        return value.ToLowerInvariant();
    }

    /// <summary>
    /// Picks the instance-wide slug a new project will answer to, inside the caller's transaction.
    ///
    /// <para>
    /// A project asks for its own path first, because that is the name somebody would guess. Instance-wide
    /// that name is first-come, so when it is taken the project gets its path with a short random suffix
    /// rather than waiting for a human to choose. Random rather than a counter: a counter would publish how
    /// many projects of that name exist, and the second one would always be predictable from the first.
    /// </para>
    ///
    /// <para>
    /// Safe against a concurrent creation because the transaction is immediate, so no other writer can slip
    /// a row in between this check and the insert that follows it. The unique index is still the authority;
    /// this only avoids making a caller retry.
    /// </para>
    /// </summary>
    private static async Task<string> ReserveGlobalSlugAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        string preferred)
    {
        if (!await TakenAsync(preferred)) return preferred;
        // Widening, not endless: four hex characters give 65,536 names per path, and the loop grows the
        // suffix rather than spinning on the same width if a path is somehow that popular.
        for (int width = 2; width <= 8; width *= 2)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                string candidate = preferred + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(width));
                if (!await TakenAsync(candidate)) return candidate;
            }
        }
        throw new InvalidOperationException("No instance-wide slug was available for this project path.");

        async Task<bool> TakenAsync(string candidate) => await db.QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM Projects WHERE GlobalSlug = @Value;",
            new RawValueParameters(candidate), transaction) != 0;
    }

    /// <summary>
    /// Finds a ready project by its instance-wide slug. Authorization is the caller's, exactly as it is for
    /// the namespaced lookup: this answers what exists, never who may see it.
    /// </summary>
    public async Task<ProjectInfo?> FindReadyProjectByGlobalSlugAsync(string globalSlug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(globalSlug)) return null;
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        ProjectPathRow? row = await db.QuerySingleOrDefaultAsync<ProjectPathRow>("""
            SELECT p.Id, p.OwnerUserId, u.Username AS Namespace, p.Slug, p.Name, p.StorageId, p.DefaultBranch,
                   p.Visibility, p.PagePublished, p.GlobalSlug
            FROM Projects p
            JOIN Users u ON u.Id = p.OwnerUserId
            WHERE p.GlobalSlug = @Value AND p.State = 'ready';
            """, new RawValueParameters(globalSlug.Trim().ToLowerInvariant()));
        return row is null ? null : ToInfo(row);
    }

    /// <summary>Finds a ready project and its details by instance-wide slug.</summary>
    public async Task<ProjectDetails?> FindReadyProjectDetailsByGlobalSlugAsync(string globalSlug, CancellationToken cancellationToken = default)
    {
        ProjectInfo? project = await FindReadyProjectByGlobalSlugAsync(globalSlug, cancellationToken);
        return project is null ? null : await GetReadyProjectAsync(project.Id, cancellationToken);
    }

    private static string NewSecret(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private static bool ValidSecret(string secret, string prefix) =>
        secret.Length == prefix.Length + 64 && secret.StartsWith(prefix, StringComparison.Ordinal) &&
        secret.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', 'f') < 0 &&
        secret.AsSpan(prefix.Length).ToString().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Digest(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static ProjectDetails ToDetails(ProjectDetailsRow row) => new(
        ToInfo(row),
        row.Description,
        DateTimeOffset.Parse(row.CreatedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        row.DefaultBranch);

    private static ProjectInfo ToInfo(ProjectPathRow row) => new(
        row.Id, row.OwnerUserId, row.Namespace, row.Slug, row.Name, row.StorageId, row.DefaultBranch,
        ParseVisibility(row.Visibility), row.PagePublished != 0, row.GlobalSlug);

    /// <summary>
    /// Turns the stored text into the enum. An unrecognised value is treated as private rather than thrown
    /// on: the column has a CHECK constraint so it cannot happen through this code, and if a future schema
    /// change ever introduced a wider value, failing closed is the only safe way to misread it.
    /// </summary>
    internal static ProjectVisibility ParseVisibility(string? stored) => stored switch
    {
        "public" => ProjectVisibility.Public,
        "unlisted" => ProjectVisibility.Unlisted,
        _ => ProjectVisibility.Private
    };

    internal static string StoreVisibility(ProjectVisibility visibility) => visibility switch
    {
        ProjectVisibility.Public => "public",
        ProjectVisibility.Unlisted => "unlisted",
        _ => "private"
    };

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }

}

public sealed class ProjectConflictException(string message, Exception innerException) : Exception(message, innerException);
public sealed class UserConflictException(string message, Exception innerException) : Exception(message, innerException);

/// <summary>A project ownership rule refused the operation: an owner still holds projects, or a transfer
/// would collide with a path the new owner already uses.</summary>
public sealed class ProjectOwnershipException : Exception
{
    public ProjectOwnershipException(string message) : base(message) { }
    public ProjectOwnershipException(string message, Exception innerException) : base(message, innerException) { }
}

internal sealed class UserCredentialRow
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public long IsAdministrator { get; set; }
    public long Disabled { get; set; }
}

internal class IdentityRow
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public long IsAdministrator { get; set; }
    public long Disabled { get; set; }
}

internal sealed class TokenIdentityRow : IdentityRow { public long Scopes { get; set; } }
internal sealed class SessionIdentityRow : IdentityRow
{
    public string CreatedAtUtc { get; set; } = "";
    public string ExpiresAtUtc { get; set; } = "";
}
internal sealed class TokenOwnerRow { public long UserId { get; set; } }
internal sealed class TokenRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long Scopes { get; set; }
    public string ExpiresAtUtc { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
}
internal sealed class ProjectOwnerRow { public long OwnerUserId { get; set; } }
internal sealed class ProjectAccessRow
{
    public long OwnerUserId { get; set; }
    public string Visibility { get; set; } = "private";
    public long PagePublished { get; set; }
    public long? Role { get; set; }
}
internal sealed class ProjectMemberRow { public long UserId { get; set; } public string Username { get; set; } = ""; public long Role { get; set; } }
internal class ProjectPathRow
{
    public long Id { get; set; }
    public long OwnerUserId { get; set; }
    public string Namespace { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string StorageId { get; set; } = "";
    public string? DefaultBranch { get; set; }
    public string Visibility { get; set; } = "private";
    public long PagePublished { get; set; }
    public string GlobalSlug { get; set; } = "";
}
internal sealed class ProjectDetailsRow : ProjectPathRow
{
    public string Description { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
}

internal sealed record CreateUserParameters(string Username, string NormalizedUsername, string PasswordHash, string CreatedAtUtc);
internal sealed record NormalizedNameParameters(string NormalizedUsername);
internal sealed record SessionParameters(long UserId, string SecretDigest, string ExpiresAtUtc, string CreatedAtUtc);
internal sealed record ResolveSecretParameters(string SecretDigest, string NowUtc);
internal sealed record RenewSessionParameters(string SecretDigest, string ExpiresAtUtc);
internal sealed record RevokeSecretParameters(string SecretDigest, string RevokedAtUtc);
internal sealed record TokenParameters(long UserId, string Name, string SecretDigest, int Scopes, string ExpiresAtUtc, string CreatedAtUtc);
internal sealed record IdParameters(long Id);
internal sealed record RevokeByIdParameters(long Id, string RevokedAtUtc);
internal sealed record RevokeOwnTokenParameters(long Id, long UserId, string RevokedAtUtc);
internal sealed record ActiveTokensParameters(long UserId, string NowUtc);
internal sealed record RevokeUserParameters(long UserId, string RevokedAtUtc);
internal sealed record PasswordUpdateParameters(long Id, string PasswordHash);
internal sealed record CreateProjectParameters(long OwnerUserId, string Slug, string NormalizedSlug, string Name, string Description, string StorageId, string CreatedAtUtc, string GlobalSlug);
internal sealed record MembershipParameters(long ProjectId, long UserId, int Role, long GrantedByUserId, string CreatedAtUtc);
internal sealed record ProjectUserParameters(long ProjectId, long UserId);
internal sealed record PermissionQueryParameters(long ProjectId, long UserId);
internal sealed record ProjectPathParameters(string NormalizedNamespace, string NormalizedSlug);
internal sealed record ProjectListParameters(long UserId, int IsAdministrator, int Take, int Skip);
internal sealed record RawValueParameters(string Value);
internal sealed record AuditParameters(long? ActorUserId, string Action, string TargetType, string? TargetId, string Detail, string CreatedAtUtc);
internal sealed record AvatarReadParameters(string OwnerType, long OwnerId);
internal sealed class AvatarRow
{
    public string ContentType { get; set; } = "";
    public byte[] Bytes { get; set; } = [];
    public string Sha256 { get; set; } = "";
}
internal sealed record DefaultBranchParameters(long Id, string? Value);
internal sealed record ProjectTextParameters(long Id, string Value);
internal sealed record ProjectFlagParameters(long Id, int Value);
internal sealed record PublishedListParameters(string? Pattern, int MatchAll, int Take, int Skip);
internal sealed record SettingWriteParameters(string Name, string Value, long UpdatedByUserId, string UpdatedAtUtc);
internal sealed class SettingRow
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}
