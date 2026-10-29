using System.Security.Claims;
using System.Text.Encodings.Web;
using DnaX.Uploads;
using Graft.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Graft.Server;

/// <summary>
/// Resolves a browser session secret to a Graft identity. Graft owns its session storage, so this indirection
/// lets the authentication handler reach it without the store itself becoming a container-owned service.
/// </summary>
public sealed class GraftSessionResolver(Func<string, CancellationToken, Task<AuthenticatedIdentity?>> resolve)
{
    public Task<AuthenticatedIdentity?> ResolveAsync(string secret, CancellationToken cancellationToken) =>
        resolve(secret, cancellationToken);
}

public sealed class GraftSessionAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>Name of the session cookie Graft issues at sign-in.</summary>
    public string CookieName { get; set; } = "";
}

/// <summary>
/// Bridges Graft's opaque session cookie into an ASP.NET Core <see cref="ClaimsPrincipal"/>.
///
/// Graft authenticates its own API by hand and does not otherwise use the authentication stack. DnaX.Uploads,
/// however, applies <c>RequireAuthorization</c> and identifies an upload owner through the
/// <see cref="ClaimTypes.NameIdentifier"/> claim, so the upload endpoints need a real scheme. Only those
/// endpoints opt in; every pre-existing Graft route keeps its own checks and is unaffected.
/// </summary>
public sealed class GraftSessionAuthenticationHandler(
    IOptionsMonitor<GraftSessionAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    GraftSessionResolver sessions)
    : AuthenticationHandler<GraftSessionAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "GraftSession";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? secret = Request.Cookies[Options.CookieName];
        if (string.IsNullOrEmpty(secret)) return AuthenticateResult.NoResult();

        // A revoked, expired or disabled account resolves to nothing, which immediately makes every upload
        // session owned by that identity unreachable even though the staged bytes have not expired yet.
        AuthenticatedIdentity? identity = await sessions.ResolveAsync(secret, Context.RequestAborted);
        if (identity is null) return AuthenticateResult.NoResult();

        ClaimsIdentity claims = new(
            [new Claim(ClaimTypes.NameIdentifier, Owner(identity)), new Claim(ClaimTypes.Name, identity.Username)],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(claims), SchemeName));
    }

    /// <summary>
    /// The stable upload owner. This uses the numeric account id rather than the username: ids survive a
    /// rename, and a reused username must never inherit the previous holder's staged uploads.
    /// </summary>
    public static string Owner(AuthenticatedIdentity identity) => "graft:user:" + identity.UserId;
}

/// <summary>
/// Server-owned upload policy. These limits are enforced independently of anything the browser asks for, and
/// a profile is captured immutably into each session when it is created, so redeploying new limits never
/// changes a transfer that is already running.
/// </summary>
public static class UploadLimits
{
    public const string ArchiveProfile = "archive";
    public const string AvatarProfile = "avatar";
    public const string RepositoryFileProfile = "repository-file";
    public const string ReleaseAssetProfile = "release-asset";

    /// <summary>Total staged bytes admitted across all owners, including completed receipts until expiry.</summary>
    public const long ReservedBytes = 64L * 1024 * 1024 * 1024;
    public const int Sessions = 64;

    /// <summary>Largest single upload request body. Chunked profiles never exceed their chunk size and
    /// whole-file profiles never exceed their file cap, so this is a ceiling, not a per-route allowance:
    /// DnaX narrows it again for each route.</summary>
    public const long MaximumRequestBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Import bundles and backup archives. Large, resumable and chunked: this is the transfer that most needs
    /// to survive a reload. Job metadata is kept so a returning browser can reconcile, but file bytes are
    /// never copied into browser storage - an archive is reselected and resumes from the committed offset.
    /// </summary>
    public static readonly UploadProfile Archive = new()
    {
        Multiple = false,
        Chunking = true,
        ChunkBytes = 8 * 1024 * 1024,
        Resume = true,
        AutomaticRetry = true,
        RetryLimit = 6,
        PersistMetadata = true,
        PersistFileBytes = 0,
        ConcurrentFiles = 1,
        MaximumFiles = 4,
        MaximumFileBytes = 20L * 1024 * 1024 * 1024,
        Lifetime = TimeSpan.FromDays(7)
    };

    /// <summary>Project and user avatars: one small image, ingested immediately.</summary>
    public static readonly UploadProfile Avatar = new()
    {
        Multiple = false,
        Chunking = false,
        Resume = false,
        AutomaticRetry = true,
        RetryLimit = 3,
        PersistMetadata = false,
        PersistFileBytes = 0,
        DragAndDrop = true,
        ConcurrentFiles = 1,
        MaximumFiles = 1,
        MaximumFileBytes = 2L * 1024 * 1024,
        Lifetime = TimeSpan.FromHours(6)
    };

    /// <summary>
    /// Files committed into a repository through the browser. Capped at the Git engine's own per-object limit:
    /// authoring a larger blob would produce an object that no client could ever push back through the pack
    /// path, leaving a repository inconsistent with its own transport. Raising that cap is a Git-engine
    /// decision, not a side effect of an upload feature.
    /// </summary>
    public static readonly UploadProfile RepositoryFile = new()
    {
        Multiple = true,
        Chunking = false,
        Resume = false,
        AutomaticRetry = true,
        RetryLimit = 3,
        PersistMetadata = false,
        PersistFileBytes = 0,
        DragAndDrop = true,
        ConcurrentFiles = 2,
        MaximumFiles = 20,
        MaximumFileBytes = Graft.Git.Limits.ObjectBytes,
        Lifetime = TimeSpan.FromHours(6)
    };

    /// <summary>
    /// Files attached to a release. Chunked, because a build artefact is the one upload here that a phone
    /// might reasonably be asked to send while backgrounded, and capped at the store's own asset limit so
    /// the ingest never has to refuse what the transport accepted.
    /// </summary>
    public static readonly UploadProfile ReleaseAsset = new()
    {
        Multiple = true,
        Chunking = true,
        ChunkBytes = 8 * 1024 * 1024,
        Resume = true,
        AutomaticRetry = true,
        RetryLimit = 3,
        PersistMetadata = true,
        PersistFileBytes = 0,
        DragAndDrop = true,
        ConcurrentFiles = 1,
        MaximumFiles = 10,
        MaximumFileBytes = Graft.Storage.GraftStore.MaximumAssetBytes,
        Lifetime = TimeSpan.FromHours(6)
    };
}
