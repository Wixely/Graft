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

    private GraftStore(ServiceProvider services, string connectionString)
    {
        this.services = services;
        this.connectionString = connectionString;
    }

    public static async Task<GraftStore> OpenAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
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
            return new(provider, connectionString);
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
        string normalized = NormalizeName(username, nameof(username));
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
        string normalized = NormalizeName(username, nameof(username));
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
        DateTimeOffset expires = DateTimeOffset.UtcNow.AddMinutes(15);
        string now = Now();
        await db.ExecuteAsync("""
            INSERT INTO BrowserSessions (UserId, SecretDigest, ExpiresAtUtc, RevokedAtUtc, CreatedAtUtc)
            VALUES (@UserId, @SecretDigest, @ExpiresAtUtc, NULL, @CreatedAtUtc);
            """, new SessionParameters(user.Id, Digest(secret), expires.ToString("O"), now));
        return new(secret, expires);
    }

    public async Task<AuthenticatedIdentity?> ResolveSessionAsync(string secret, CancellationToken cancellationToken = default)
    {
        if (!ValidSecret(secret, "graft_session_")) return null;
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        IdentityRow? row = await db.QuerySingleOrDefaultAsync<IdentityRow>("""
            SELECT u.Id, u.Username, u.IsAdministrator, u.Disabled
            FROM BrowserSessions s
            JOIN Users u ON u.Id = s.UserId
            WHERE s.SecretDigest = @SecretDigest
              AND s.RevokedAtUtc IS NULL
              AND s.ExpiresAtUtc > @NowUtc;
            """, new ResolveSecretParameters(Digest(secret), Now()));
        return row is null || row.Disabled != 0
            ? null
            : new(row.Id, row.Username, row.IsAdministrator != 0, IdentityKind.BrowserSession);
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

    public async Task<ProjectInfo> CreateProjectWithRepositoryAsync(
        AuthenticatedIdentity actor,
        string slug,
        string name,
        Func<ProjectInfo, CancellationToken, Task> initializeRepository,
        string description = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initializeRepository);
        return await CreateProjectCoreAsync(actor, slug, name, description, initializeRepository, cancellationToken);
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
            SELECT p.Id, p.OwnerUserId, u.Username AS Namespace, p.Slug, p.Name, p.StorageId
            FROM Projects p
            JOIN Users u ON u.Id = p.OwnerUserId
            WHERE u.NormalizedUsername = @NormalizedNamespace
              AND p.NormalizedSlug = @NormalizedSlug
              AND p.State = 'ready';
            """, new ProjectPathParameters(normalizedNamespace, normalizedSlug));
        return row is null ? null : new(row.Id, row.OwnerUserId, row.Namespace, row.Slug, row.Name, row.StorageId);
    }

    public async Task<ProjectDetails?> GetReadyProjectAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        ProjectDetailsRow? row = await db.QuerySingleOrDefaultAsync<ProjectDetailsRow>("""
            SELECT p.Id, p.OwnerUserId, u.Username AS Namespace, p.Slug, p.Name, p.Description, p.StorageId, p.CreatedAtUtc
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
            SELECT p.Id, p.OwnerUserId, u.Username AS Namespace, p.Slug, p.Name, p.Description, p.StorageId, p.CreatedAtUtc
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
        CancellationToken cancellationToken)
    {
        string normalizedSlug = NormalizeName(slug, nameof(slug));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 200 || description.Length > 2_000) throw new ArgumentException("Project text is too long.");

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        string storageId = Guid.NewGuid().ToString("N");
        string now = Now();
        long id;
        try
        {
            id = await db.QuerySingleAsync<long>("""
                INSERT INTO Projects (OwnerUserId, Slug, NormalizedSlug, Name, Description, StorageId, State, CreatedAtUtc)
                VALUES (@OwnerUserId, @Slug, @NormalizedSlug, @Name, @Description, @StorageId, 'pending', @CreatedAtUtc)
                RETURNING Id;
                """, new CreateProjectParameters(liveActor.UserId, slug, normalizedSlug, name, description, storageId, now), transaction);
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
        await AuditAsync(readyDb, readyTransaction, actor.UserId, "create-project", "project", id.ToString(CultureInfo.InvariantCulture), "Private project and repository are ready.", Now());
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
        string now = Now();
        int changed = await db.ExecuteAsync("UPDATE Users SET Disabled = 1 WHERE Id = @Id AND Disabled = 0;", new IdParameters(userId), transaction);
        if (changed != 1) throw new InvalidOperationException("User is unavailable or already disabled.");
        await RevokeUserCredentialsAsync(db, transaction, userId, now);
        await AuditAsync(db, transaction, actor.UserId, "disable-user", "user", userId.ToString(CultureInfo.InvariantCulture), "User disabled and credentials revoked.", now);
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
            SELECT p.OwnerUserId, m.Role
            FROM Projects p
            LEFT JOIN ProjectMemberships m ON m.ProjectId = p.Id AND m.UserId = @UserId
            WHERE p.Id = @ProjectId AND p.State = 'ready';
            """, new PermissionQueryParameters(projectId, identity?.UserId ?? 0), transaction);
        if (project is null) return new(false, null, "Unavailable", "Project is unavailable.");

        AuthenticatedIdentity? liveIdentity = identity is null
            ? null
            : await FindLiveIdentityAsync(db, transaction, identity, cancellationToken);
        ProjectRole? membership = project.Role is null ? null : (ProjectRole)project.Role.Value;
        return ProjectAuthorization.Decide(liveIdentity, project.OwnerUserId, membership, action);
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

    private static string NormalizeName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length is < 2 or > 39 || value[0] is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9') ||
            value.Any(character => character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.')))
            throw new ArgumentException("Names must be 2-39 ASCII letters, numbers, dots, underscores or hyphens and start with a letter or number.", parameterName);
        return value.ToLowerInvariant();
    }

    private static string NewSecret(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private static bool ValidSecret(string secret, string prefix) =>
        secret.Length == prefix.Length + 64 && secret.StartsWith(prefix, StringComparison.Ordinal) &&
        secret.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', 'f') < 0 &&
        secret.AsSpan(prefix.Length).ToString().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Digest(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static ProjectDetails ToDetails(ProjectDetailsRow row) => new(
        new(row.Id, row.OwnerUserId, row.Namespace, row.Slug, row.Name, row.StorageId),
        row.Description,
        DateTimeOffset.Parse(row.CreatedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }

}

public sealed class ProjectConflictException(string message, Exception innerException) : Exception(message, innerException);
public sealed class UserConflictException(string message, Exception innerException) : Exception(message, innerException);

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
internal sealed class ProjectAccessRow { public long OwnerUserId { get; set; } public long? Role { get; set; } }
internal sealed class ProjectMemberRow { public long UserId { get; set; } public string Username { get; set; } = ""; public long Role { get; set; } }
internal class ProjectPathRow
{
    public long Id { get; set; }
    public long OwnerUserId { get; set; }
    public string Namespace { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string StorageId { get; set; } = "";
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
internal sealed record RevokeSecretParameters(string SecretDigest, string RevokedAtUtc);
internal sealed record TokenParameters(long UserId, string Name, string SecretDigest, int Scopes, string ExpiresAtUtc, string CreatedAtUtc);
internal sealed record IdParameters(long Id);
internal sealed record RevokeByIdParameters(long Id, string RevokedAtUtc);
internal sealed record RevokeOwnTokenParameters(long Id, long UserId, string RevokedAtUtc);
internal sealed record ActiveTokensParameters(long UserId, string NowUtc);
internal sealed record RevokeUserParameters(long UserId, string RevokedAtUtc);
internal sealed record PasswordUpdateParameters(long Id, string PasswordHash);
internal sealed record CreateProjectParameters(long OwnerUserId, string Slug, string NormalizedSlug, string Name, string Description, string StorageId, string CreatedAtUtc);
internal sealed record MembershipParameters(long ProjectId, long UserId, int Role, long GrantedByUserId, string CreatedAtUtc);
internal sealed record ProjectUserParameters(long ProjectId, long UserId);
internal sealed record PermissionQueryParameters(long ProjectId, long UserId);
internal sealed record ProjectPathParameters(string NormalizedNamespace, string NormalizedSlug);
internal sealed record ProjectListParameters(long UserId, int IsAdministrator, int Take, int Skip);
internal sealed record RawValueParameters(string Value);
internal sealed record AuditParameters(long? ActorUserId, string Action, string TargetType, string? TargetId, string Detail, string CreatedAtUtc);
