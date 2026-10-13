using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Graft.Contracts;
using Graft.Core;
using Graft.Git;
using Graft.Operations;
using Graft.Server;
using Graft.Storage;
using DnaX.Uploads;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;

// Several bundled components require their attribution to accompany the binary, so the binary can print it
// without the repository being present. The same text is served at /-/licences by a running server.
if (args.Length == 1 && args[0] == "--licences")
{
    // The copyright symbols in these notices are part of what the licences require to be reproduced, and a
    // Windows console defaults to a legacy code page that would render them as question marks.
    try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { /* output is redirected */ }
    Console.WriteLine(ThirdPartyNotices.Text);
    return 0;
}

if (!Arguments.TryParse(args, out Arguments? parsedSettings))
{
    Console.Error.WriteLine("Usage: Graft.Server --database <path> --repositories <directory> [--listen <https URI>] [--origin <https origin>] [--certificate <PKCS#12 path>] [--certificate-password-file <UTF-8 path>] [--public-browsing true|false] [--push-to-create true|false] [--session-minutes <5-43200>].");
    Console.Error.WriteLine("       Graft.Server --licences  prints the third-party notices this build redistributes.");
    return 2;
}
Arguments settings = parsedSettings!;

Directory.CreateDirectory(settings.Repositories);
using MaintenanceLease maintenanceLease = MaintenanceLease.Acquire(settings.Database, settings.Repositories);
await using GraftStore store = await GraftStore.OpenAsync(settings.Database, default,
    TimeSpan.FromMinutes(settings.SessionMinutes));
WebAssets assets = new();
ConcurrentDictionary<string, Lazy<RepositoryEndpoint>> repositories = new(StringComparer.Ordinal);
byte[] csrfKey = RandomNumberGenerator.GetBytes(32);
RequestThrottle loginThrottle = new(20);
// Looser than login because a single reader opening the front page and typing a search spends several of
// these, and far tighter than unbounded because the route answers without credentials.
RequestThrottle browseThrottle = new(240);
string? serverOrigin = null;
const string SessionCookie = "__Host-graft-session";

// Browser uploads are mounted under Graft's own API namespace, never GitLab's. GitLab owns "/uploads/..."
// and "/api/v4/projects/:id/uploads", so squatting either would block later compatibility work. Unmatched
// paths beneath "/api/" already return 404 rather than the SPA shell.
const string UploadPrefix = "/api/graft/uploads";
string stateDirectory = Path.GetDirectoryName(Path.GetFullPath(settings.Database))!;
string uploadRoot = Path.Combine(stateDirectory, "uploads");
string keyRingDirectory = Path.Combine(stateDirectory, "data-protection-keys");
string ingestDirectory = Path.Combine(stateDirectory, "ingest");
CreatePrivateDirectory(uploadRoot);
CreatePrivateDirectory(keyRingDirectory);
CreatePrivateDirectory(ingestDirectory);

using X509Certificate2 certificate = LoadCertificate(settings);

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
builder.Host.UseWindowsService(options => options.ServiceName = "Graft");
builder.Host.UseSystemd();
builder.Logging.ClearProviders();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJson.Default));

// Upload transport. DnaX.Uploads owns the browser-side queue in JavaScript with resumable chunked transfer,
// which is what survives a mobile browser being backgrounded while the native file picker is open. It is the
// browser transport only: API clients post ordinary request bodies to the application's own endpoints.
builder.Services.AddSingleton(new GraftSessionResolver(store.ResolveSessionAsync));
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingDirectory))
    .SetApplicationName("Graft");
builder.Services.AddAuthentication(GraftSessionAuthenticationHandler.SchemeName)
    .AddScheme<GraftSessionAuthenticationOptions, GraftSessionAuthenticationHandler>(
        GraftSessionAuthenticationHandler.SchemeName, options => options.CookieName = SessionCookie);
builder.Services.AddAuthorization();
builder.Services.AddDnaXUploads(options =>
{
    options.Root = uploadRoot;
    options.MaximumReservedBytes = UploadLimits.ReservedBytes;
    options.MaximumSessions = UploadLimits.Sessions;
    options.Profiles[UploadLimits.ArchiveProfile] = UploadLimits.Archive;
    options.Profiles[UploadLimits.AvatarProfile] = UploadLimits.Avatar;
    options.Profiles[UploadLimits.RepositoryFileProfile] = UploadLimits.RepositoryFile;
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(settings.ListenAddress, settings.ListenPort, listen => listen.UseHttps(certificate));
    options.Limits.MaxRequestBodySize = Limits.RequestBytes;
    // Raised from 16: an upload holds its connection for the length of the transfer, so the previous ceiling
    // let a few concurrent uploads starve Git and API traffic. Still bounded for low-resource hosts.
    options.Limits.MaxConcurrentConnections = 64;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
});

await using WebApplication app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    // Uploads are the one route group where a long request body is the point, so they get a longer bound and
    // a raised body limit. Everything else keeps the original 30 second deadline and the Kestrel-wide cap;
    // the Git routes additionally enforce Limits.RequestBytes while reading, so they are bounded regardless.
    bool uploading = context.Request.Path.StartsWithSegments(UploadPrefix);
    if (uploading)
    {
        IHttpMaxRequestBodySizeFeature? bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false }) bodySize.MaxRequestBodySize = UploadLimits.MaximumRequestBytes;
    }
    using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    deadline.CancelAfter(uploading ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(30));
    context.RequestAborted = deadline.Token;
    try { await next(context); }
    catch (GitInputException error)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        if (context.Request.Path.StartsWithSegments("/api/v4"))
            await context.Response.WriteAsJsonAsync(new ErrorResponse(error.Message), ApiJson.Default.ErrorResponse, null, CancellationToken.None);
        else
        {
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("Rejected: " + error.Message, CancellationToken.None);
        }
    }
    catch (OperationCanceledException) { context.Abort(); }
    catch (IOException)
    {
        context.Response.StatusCode = StatusCodes.Status507InsufficientStorage;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync("Repository storage failed.", CancellationToken.None);
    }
});

// Only the upload group opts into the authentication stack. UseAntiforgery validates automatically for
// endpoints that bind a form; Graft binds no forms, so existing routes keep their own Origin + X-Graft-CSRF
// checks untouched and DnaX validates its own X-DnaX-Antiforgery header inside its handlers.
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapDnaXUploads(UploadPrefix);

app.MapGet("/api/session", async context =>
{
    if (!await OnlyQuery(context)) return;
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: false);
    SessionResponse response;
    if (browser is null) response = new(false, null, false, "", 0, null, settings.PublicBrowsing);
    else
    {
        long userId = browser.Value.Identity.UserId;
        string? avatarUrl = await store.HasAvatarAsync(AvatarOwner.User, userId, context.RequestAborted)
            ? AvatarUrl(context, AvatarOwner.User, userId)
            : null;
        response = new(true, browser.Value.Identity.Username, browser.Value.Identity.IsAdministrator,
            Csrf(browser.Value.Secret), userId, avatarUrl, settings.PublicBrowsing);
    }
    await context.Response.WriteAsJsonAsync(response, ApiJson.Default.SessionResponse, null, context.RequestAborted);
});

// The isolated frame a README's diagrams render inside. It is served here rather than as a static asset
// because it needs a policy of its own: mermaid creates style elements while rendering, which the main
// policy forbids, and loosening that for the whole application would be worst on the page showing somebody
// else's repository content. The frame is sandboxed without same-origin access, so it cannot reach this
// site's DOM, cookies or session; the policy below gives it inline styles and nothing else, and names this
// origin explicitly because 'self' inside an opaque origin matches nothing and would block its own script.
app.MapGet("/-/mermaid-sandbox", async context =>
{
    if (!await OnlyQuery(context)) return;
    string origin = Origin(context);
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'none'; script-src " + origin + "; style-src 'unsafe-inline'; img-src data:; " +
        "connect-src 'none'; frame-ancestors " + origin + "; base-uri 'none'; form-action 'none'";
    context.Response.ContentType = "text/html; charset=utf-8";
    // The frame paints no background of its own, so the embedding page's surface shows through and a diagram
    // sits on the theme the reader actually chose rather than on the browser's default white canvas.
    await context.Response.WriteAsync($$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8" /><title>diagram</title>
        <style>html, body { background: transparent; margin: 0; } #diagram { overflow: auto; }</style>
        <meta name="color-scheme" content="light dark" />
        <script src="{{origin}}/vendor/mermaid/mermaid.min.js"></script>
        <script src="{{origin}}/mermaid-frame.js"></script>
        </head><body><div id="diagram"></div></body></html>
        """, context.RequestAborted);
});

app.MapGet("/healthz", async context =>
{
    if (!await OnlyQuery(context)) return;
    context.Response.ContentType = "text/plain; charset=utf-8";
    await context.Response.WriteAsync("healthy", context.RequestAborted);
});

// Several of the licences this build redistributes under bind the binary rather than the source tree, so the
// attribution has to be reachable from a running server and not only from the repository. Unauthenticated
// deliberately: a notice nobody can read without credentials does not discharge the obligation.
app.MapGet("/-/licences", async context =>
{
    if (!await OnlyQuery(context)) return;
    context.Response.ContentType = "text/plain; charset=utf-8";
    await context.Response.WriteAsync(ThirdPartyNotices.Text, context.RequestAborted);
});

app.MapPost("/api/login", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    if (!loginThrottle.TryEnter(DateTimeOffset.UtcNow)) { await ApiError(context, 429, "login-rate-limit"); return; }
    LoginRequest? login = await context.Request.ReadFromJsonAsync(ApiJson.Default.LoginRequest, context.RequestAborted);
    if (login?.Username is not { Length: >= 2 and <= 39 } || !PasswordPolicy.IsAcceptableLength(login.Password))
    { await ApiError(context, 400, "credentials"); return; }
    SessionGrant? grant = await store.LoginAsync(login.Username, login.Password, context.RequestAborted);
    if (grant is null) { await ApiError(context, 401, "credentials"); return; }
    context.Response.Cookies.Append(SessionCookie, grant.Secret, SessionCookieOptions(grant.ExpiresAtUtc));
    AuthenticatedIdentity identity = (await store.ResolveSessionAsync(grant.Secret, context.RequestAborted))!;
    await context.Response.WriteAsJsonAsync(new SessionResponse(true, identity.Username, identity.IsAdministrator,
        Csrf(grant.Secret), identity.UserId, null),
        ApiJson.Default.SessionResponse, null, context.RequestAborted);
});

app.MapPost("/api/logout", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    await store.LogoutAsync(browser.Value.Secret, context.RequestAborted);
    context.Response.Cookies.Delete(SessionCookie, SessionCookieOptions(null));
    context.Response.StatusCode = StatusCodes.Status204NoContent;
});

app.MapPost("/api/password", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    PasswordChangeRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.PasswordChangeRequest, context.RequestAborted);
    if (request is null || !PasswordPolicy.IsAcceptableLength(request.CurrentPassword) || !PasswordPolicy.IsAcceptableLength(request.NewPassword))
    { await ApiError(context, 400, "password"); return; }
    try
    {
        await store.ChangePasswordAsync(browser.Value.Identity, request.CurrentPassword, request.NewPassword, context.RequestAborted);
        context.Response.Cookies.Delete(SessionCookie, SessionCookieOptions(null));
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (ArgumentException) { await ApiError(context, 400, "password"); }
    catch (InvalidOperationException error) { await ApiError(context, 400, error.Message); }
});

app.MapGet("/api/users", async context =>
{
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!browser.Value.Identity.IsAdministrator) { await ApiError(context, 403, "administrator"); return; }
    if (!await OnlyQuery(context)) return;
    IReadOnlyList<UserAccount> users = await store.ListUsersAsync(browser.Value.Identity, context.RequestAborted);
    UserAccountResponse[] response = users.Select(user =>
        new UserAccountResponse(user.Id, user.Username, user.IsAdministrator, user.Disabled)).ToArray();
    await context.Response.WriteAsJsonAsync(response, ApiJson.Default.UserAccountResponseArray, null, context.RequestAborted);
});

app.MapPost("/api/users", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!browser.Value.Identity.IsAdministrator) { await ApiError(context, 403, "administrator"); return; }
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    UserCreateRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.UserCreateRequest, context.RequestAborted);
    if (request?.Username is not { Length: >= 2 and <= 39 } || !PasswordPolicy.IsAcceptableLength(request.Password))
    { await ApiError(context, 400, "user"); return; }
    try
    {
        UserAccount user = await store.CreateUserAsync(browser.Value.Identity, request.Username, request.Password, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(new UserAccountResponse(user.Id, user.Username, user.IsAdministrator, user.Disabled),
            ApiJson.Default.UserAccountResponse, null, context.RequestAborted);
    }
    catch (ArgumentException) { await ApiError(context, 400, "username"); }
    catch (UserConflictException) { await ApiError(context, 400, "username has already been taken"); }
});

app.MapPost("/api/users/{userId:long}/reset-password", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!browser.Value.Identity.IsAdministrator) { await ApiError(context, 403, "administrator"); return; }
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    PasswordResetRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.PasswordResetRequest, context.RequestAborted);
    if (request is null || !PasswordPolicy.IsAcceptableLength(request.NewPassword)) { await ApiError(context, 400, "password"); return; }
    long userId = Convert.ToInt64(context.Request.RouteValues["userId"], System.Globalization.CultureInfo.InvariantCulture);
    try
    {
        await store.ResetPasswordAsync(browser.Value.Identity, userId, request.NewPassword, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (ArgumentException) { await ApiError(context, 400, "password"); }
    catch (InvalidOperationException error) { await ApiError(context, 400, error.Message); }
});

app.MapPost("/api/users/{userId:long}/disable", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!browser.Value.Identity.IsAdministrator) { await ApiError(context, 403, "administrator"); return; }
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    long userId = Convert.ToInt64(context.Request.RouteValues["userId"], System.Globalization.CultureInfo.InvariantCulture);
    try
    {
        await store.DisableUserAsync(browser.Value.Identity, userId, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    // 409: the account is fine, but it still owns projects. The message names them so the administrator
    // knows what to transfer rather than being told only that it failed.
    catch (ProjectOwnershipException error) { await ApiError(context, 409, error.Message); }
    catch (InvalidOperationException error) { await ApiError(context, 400, error.Message); }
});

app.MapPost("/api/users/{userId:long}/enable", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!browser.Value.Identity.IsAdministrator) { await ApiError(context, 403, "administrator"); return; }
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    PasswordResetRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.PasswordResetRequest, context.RequestAborted);
    if (request is null || !PasswordPolicy.IsAcceptableLength(request.NewPassword)) { await ApiError(context, 400, "password"); return; }
    long userId = Convert.ToInt64(context.Request.RouteValues["userId"], System.Globalization.CultureInfo.InvariantCulture);
    try
    {
        await store.EnableUserAsync(browser.Value.Identity, userId, request.NewPassword, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (ArgumentException) { await ApiError(context, 400, "password"); }
    catch (InvalidOperationException error) { await ApiError(context, 400, error.Message); }
});

// Inspect a Git bundle that an administrator uploaded through the browser. This is the application half of
// the upload: DnaX moved the bytes, and nothing about that transport authorizes anything here. The caller is
// re-authenticated, re-authorized, and only ever reaches an upload it owns.
app.MapPost("/api/graft/admin/import/inspect", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    if (!browser.Value.Identity.IsAdministrator) { await ApiError(context, 403, "administrator"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }

    UploadIngestRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.UploadIngestRequest, context.RequestAborted);
    if (request is null || !Guid.TryParse(request.UploadId, out Guid uploadId))
    { await ApiError(context, 400, "upload-id"); return; }

    DiskUploadStore uploads = context.RequestServices.GetRequiredService<DiskUploadStore>();
    string owner = GraftSessionAuthenticationHandler.Owner(browser.Value.Identity);
    string staged = Path.Combine(ingestDirectory, "bundle-" + Guid.NewGuid().ToString("N") + ".tmp");
    try
    {
        UploadStatus status = uploads.Get(owner, uploadId);
        if (!string.Equals(status.Profile, UploadLimits.ArchiveProfile, StringComparison.Ordinal))
        { await ApiError(context, 400, "upload was not created for archives"); return; }
        // Reject before copying rather than after: the inventory reader has its own 1 GiB ceiling.
        if (status.Length > 1024L * 1024 * 1024) { await ApiError(context, 413, "bundle exceeds the 1 GiB inventory limit"); return; }
        await uploads.ReadCompletedAsync(owner, uploadId, async (source, cancellationToken) =>
        {
            await using FileStream target = new(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken);
            await target.FlushAsync(cancellationToken);
        }, context.RequestAborted);

        GitBundleInventory inventory = GitBundleInventory.Read(staged);
        BundleInventoryResponse response = new(status.FileName, inventory.Format, inventory.Sha256, inventory.Bytes,
            [.. inventory.Refs.Select(item => new ImportRefResponse(item.Name, item.ObjectId))],
            [.. inventory.Excluded.Select(item => new ImportExclusionResponse(item.Area, item.Reason))]);
        await context.Response.WriteAsJsonAsync(response, ApiJson.Default.BundleInventoryResponse, null, context.RequestAborted);
    }
    catch (UploadException error) { await ApiError(context, error.StatusCode, error.Message); }
    catch (InvalidDataException error) { await ApiError(context, 400, error.Message); }
    finally { try { if (File.Exists(staged)) File.Delete(staged); } catch (IOException) { } }
});

app.MapGet("/api/tokens", async context =>
{
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!await OnlyQuery(context)) return;
    IReadOnlyList<PersonalAccessToken> tokens = await store.ListActiveTokensAsync(browser.Value.Identity, context.RequestAborted);
    TokenResponse[] response = tokens.Select(token => new TokenResponse(
        token.Id, token.Name, TokenScopeNames(token.Scopes), token.ExpiresAtUtc, token.CreatedAtUtc)).ToArray();
    await context.Response.WriteAsJsonAsync(response, ApiJson.Default.TokenResponseArray, null, context.RequestAborted);
});

app.MapPost("/api/tokens", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    TokenCreateRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.TokenCreateRequest, context.RequestAborted);
    if (request?.Name is not { Length: >= 1 and <= 100 } || request.ExpiresInDays is < 1 or > 365 ||
        !TryTokenScopes(request.Scopes, out TokenScope scopes))
    { await ApiError(context, 400, "token"); return; }
    try
    {
        TokenGrant token = await store.CreateTokenAsync(browser.Value.Identity, request.Name.Trim(), scopes,
            DateTimeOffset.UtcNow.AddDays(request.ExpiresInDays), context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(new TokenCreatedResponse(
            token.Id, request.Name.Trim(), token.Secret, TokenScopeNames(token.Scopes), token.ExpiresAtUtc),
            ApiJson.Default.TokenCreatedResponse, null, context.RequestAborted);
    }
    catch (ArgumentException) { await ApiError(context, 400, "token"); }
});

app.MapDelete("/api/tokens/{tokenId:long}", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    long tokenId = Convert.ToInt64(context.Request.RouteValues["tokenId"], System.Globalization.CultureInfo.InvariantCulture);
    try
    {
        await store.RevokeOwnTokenAsync(browser.Value.Identity, tokenId, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "token"); }
});

app.MapGet("/api/projects", async context =>
{
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!await OnlyQuery(context)) return;
    ProjectPage projects = await store.ListAccessibleProjectsAsync(browser.Value.Identity, 0, 100, context.RequestAborted);
    List<ProjectResponse> response = [];
    foreach (ProjectDetails project in projects.Projects) response.Add(await ProjectResponseFor(context, project));
    await context.Response.WriteAsJsonAsync(response.ToArray(), ApiJson.Default.ProjectResponseArray, null, context.RequestAborted);
});

app.MapPost("/api/projects", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    ProjectCreateRequest? request = await ReadProjectCreateRequest(context);
    if (request is null) return;
    await CreateProject(context, browser.Value.Identity, request);
});

app.MapGet("/api/projects/{projectId:long}/members", async context =>
{
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!await OnlyQuery(context)) return;
    long projectId = Convert.ToInt64(context.Request.RouteValues["projectId"], System.Globalization.CultureInfo.InvariantCulture);
    try
    {
        IReadOnlyList<ProjectMember> members = await store.ListProjectMembersAsync(browser.Value.Identity, projectId, context.RequestAborted);
        ProjectMemberResponse[] response = members.Select(member =>
            new ProjectMemberResponse(member.UserId, member.Username, member.Role.ToString())).ToArray();
        await context.Response.WriteAsJsonAsync(response, ApiJson.Default.ProjectMemberResponseArray, null, context.RequestAborted);
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "project"); }
});

app.MapPost("/api/projects/{projectId:long}/members", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    MembershipSetRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.MembershipSetRequest, context.RequestAborted);
    if (request is null || !Enum.TryParse(request.Role, ignoreCase: true, out ProjectRole role) || !Enum.IsDefined(role))
    { await ApiError(context, 400, "role"); return; }
    long projectId = Convert.ToInt64(context.Request.RouteValues["projectId"], System.Globalization.CultureInfo.InvariantCulture);
    try
    {
        UserAccount? user = await store.FindActiveUserAsync(request.Username, context.RequestAborted);
        if (user is null) { await ApiError(context, 400, "user is unavailable"); return; }
        await store.SetMembershipAsync(browser.Value.Identity, projectId, user.Id, role, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (ArgumentException) { await ApiError(context, 400, "username"); }
    catch (InvalidOperationException error) { await ApiError(context, 400, error.Message); }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "project"); }
});

// Hand a project to a new owner. Only the current owner or an administrator may do this; a Maintainer
// membership grants management, not the right to give the project away. The project path changes with it.
app.MapPost("/api/projects/{projectId:long}/owner", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    OwnershipTransferRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.OwnershipTransferRequest, context.RequestAborted);
    if (request?.Username is not { Length: >= 2 and <= 39 }) { await ApiError(context, 400, "username"); return; }
    long projectId = Convert.ToInt64(context.Request.RouteValues["projectId"], System.Globalization.CultureInfo.InvariantCulture);
    try
    {
        UserAccount? target = await store.FindActiveUserAsync(request.Username, context.RequestAborted);
        if (target is null) { await ApiError(context, 400, "user is unavailable"); return; }
        await store.TransferProjectOwnershipAsync(browser.Value.Identity, projectId, target.Id, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "project"); }
    catch (ProjectOwnershipException error) { await ApiError(context, 409, error.Message); }
    catch (InvalidOperationException error) { await ApiError(context, 400, error.Message); }
});

// Choose which branch a project opens at. Null clears the preference, which returns the project to the
// convention the repository applies on its own.
app.MapPost("/api/projects/{projectId:long}/default-branch", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    DefaultBranchRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.DefaultBranchRequest, context.RequestAborted);
    if (request is null) { await ApiError(context, 400, "branch"); return; }

    long projectId = Convert.ToInt64(context.Request.RouteValues["projectId"], System.Globalization.CultureInfo.InvariantCulture);
    ProjectDetails? details = await store.GetReadyProjectAsync(projectId, context.RequestAborted);
    if (details is null) { await ApiError(context, 404, "project"); return; }

    string? branch = string.IsNullOrWhiteSpace(request.Branch) ? null : request.Branch.Trim();
    if (branch is not null)
    {
        // Refuse a branch that is not there. Storage would accept any string, and the engine would silently
        // fall back to convention, which looks like the setting was ignored.
        RepositoryEndpoint endpoint = EndpointFor(details.Project);
        await endpoint.Gate.WaitAsync(context.RequestAborted);
        try
        {
            if (!endpoint.Repository.Refs.ContainsKey("refs/heads/" + branch))
            { await ApiError(context, 400, "that branch does not exist"); return; }
        }
        finally { endpoint.Gate.Release(); }
    }

    try
    {
        await store.SetDefaultBranchAsync(browser.Value.Identity, projectId, branch, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "project"); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
});

// How widely a project may be read. Maintainer-equivalent, and refused outright on a server whose operator
// has not enabled public browsing, so the closed answer cannot be stored where it would take effect later.
app.MapPost("/api/projects/{projectId:long}/visibility", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    VisibilityRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.VisibilityRequest, context.RequestAborted);
    if (request is null) { await ApiError(context, 400, "visibility"); return; }
    if (!TryReadVisibility(request.Visibility, out ProjectVisibility visibility))
    { await ApiError(context, 400, "visibility must be private, unlisted or public"); return; }
    if (visibility != ProjectVisibility.Private && !settings.PublicBrowsing)
    { await ApiError(context, 400, "this server does not allow projects to be published"); return; }

    long projectId = Convert.ToInt64(context.Request.RouteValues["projectId"], System.Globalization.CultureInfo.InvariantCulture);
    try
    {
        await store.SetProjectVisibilityAsync(browser.Value.Identity, projectId, visibility, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "project"); }
});

// Publishes or withdraws the project page: the curated surface that can stand over a private repository.
app.MapPost("/api/projects/{projectId:long}/page", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    PagePublishRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.PagePublishRequest, context.RequestAborted);
    if (request is null) { await ApiError(context, 400, "published"); return; }
    if (request.Published && !settings.PublicBrowsing)
    { await ApiError(context, 400, "this server does not allow projects to be published"); return; }

    long projectId = Convert.ToInt64(context.Request.RouteValues["projectId"], System.Globalization.CultureInfo.InvariantCulture);
    try
    {
        await store.SetPagePublishedAsync(browser.Value.Identity, projectId, request.Published, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "project"); }
});

// The public browser. Anonymous by design, and it answers identically for everyone: a signed-in caller sees
// exactly what a stranger sees, so this listing can never become a way to discover a project that is not
// published. Unlisted projects are absent, which is the whole of what makes them unlisted.
app.MapGet("/api/browse", async context =>
{
    if (!await OnlyQuery(context, "search", "page")) return;
    if (!settings.PublicBrowsing) { await ApiError(context, 404, "public browsing is not enabled on this server"); return; }
    // Unauthenticated and therefore the cheapest oracle in the product, so it is throttled on the same
    // mechanism as login rather than left open to be swept.
    if (!browseThrottle.TryEnter(DateTimeOffset.UtcNow)) { await ApiError(context, 429, "browse-rate-limit"); return; }

    string search = context.Request.Query["search"].ToString();
    if (search.Length > 200) { await ApiError(context, 400, "search must be at most 200 characters"); return; }
    string pageText = context.Request.Query["page"].ToString();
    int page = 1;
    if (pageText.Length > 0 && (!int.TryParse(pageText, System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out page) || page is < 1 or > 1000))
    { await ApiError(context, 400, "page"); return; }

    const int perPage = 24;
    ProjectPage projects = await store.ListPublishedProjectsAsync(search, (page - 1) * perPage, perPage, context.RequestAborted);
    List<BrowseEntryResponse> entries = [];
    foreach (ProjectDetails project in projects.Projects)
    {
        string path = project.Project.Namespace + "/" + project.Project.Slug;
        string? avatarUrl = await store.HasAvatarAsync(AvatarOwner.Project, project.Project.Id, context.RequestAborted)
            ? AvatarUrl(context, AvatarOwner.Project, project.Project.Id)
            : null;
        entries.Add(new(project.Project.Id, project.Project.Name, path, project.Project.Namespace,
            project.Description, avatarUrl, Origin(context) + "/" + path, project.CreatedAtUtc,
            VisibilityName(project.Project.Visibility),
            project.Project.Visibility == ProjectVisibility.Public));
    }
    await context.Response.WriteAsJsonAsync(new BrowseResponse(entries.ToArray(), projects.Total, page, perPage),
        ApiJson.Default.BrowseResponse, null, context.RequestAborted);
});

app.MapDelete("/api/projects/{projectId:long}/members/{userId:long}", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    long projectId = Convert.ToInt64(context.Request.RouteValues["projectId"], System.Globalization.CultureInfo.InvariantCulture);
    long userId = Convert.ToInt64(context.Request.RouteValues["userId"], System.Globalization.CultureInfo.InvariantCulture);
    try
    {
        await store.RevokeMembershipAsync(browser.Value.Identity, projectId, userId, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "project"); }
});

// Avatars. The browser moves the bytes through DnaX; this ingests them. Image bytes are validated
// structurally and only recognised raster formats are kept, so nothing a browser might treat as markup can
// be stored. SVG is never accepted.
app.MapPost("/api/graft/avatars/{ownerType}/{ownerId:long}", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    if (!TryReadAvatarOwner(context, out AvatarOwner ownerType, out long ownerId)) { await ApiError(context, 404, "owner"); return; }

    UploadIngestRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.UploadIngestRequest, context.RequestAborted);
    if (request is null || !Guid.TryParse(request.UploadId, out Guid uploadId)) { await ApiError(context, 400, "upload-id"); return; }

    DiskUploadStore uploads = context.RequestServices.GetRequiredService<DiskUploadStore>();
    string owner = GraftSessionAuthenticationHandler.Owner(browser.Value.Identity);
    byte[] content = [];
    try
    {
        UploadStatus status = uploads.Get(owner, uploadId);
        if (!string.Equals(status.Profile, UploadLimits.AvatarProfile, StringComparison.Ordinal))
        { await ApiError(context, 400, "upload was not created for avatars"); return; }
        if (status.Length > ImageContent.MaximumBytes) { await ApiError(context, 413, "avatar exceeds the image limit"); return; }
        await uploads.ReadCompletedAsync(owner, uploadId, async (source, cancellationToken) =>
        {
            using MemoryStream buffer = new();
            await source.CopyToAsync(buffer, cancellationToken);
            content = buffer.ToArray();
        }, context.RequestAborted);
    }
    catch (UploadException error) { await ApiError(context, error.StatusCode, error.Message); return; }

    ValidatedImage? image = ImageContent.Validate(content);
    if (image is null) { await ApiError(context, 400, "file is not a supported image"); return; }
    try
    {
        await store.SetAvatarAsync(browser.Value.Identity, ownerType, ownerId, image, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "owner"); }
    catch (InvalidOperationException error) { await ApiError(context, 400, error.Message); }
});

// Serving an avatar. The stored content type is one this server chose at ingest, never one the client
// supplied, and the global nosniff header keeps the browser from reinterpreting it.
app.MapMethods("/api/graft/avatars/{ownerType}/{ownerId:long}", ["GET", "HEAD"], async context =>
{
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: false);
    if (!await OnlyQuery(context)) return;
    if (!TryReadAvatarOwner(context, out AvatarOwner ownerType, out long ownerId)) { await ApiError(context, 404, "owner"); return; }
    if (ownerType == AvatarOwner.Project &&
        !(await DecideFor(context, browser?.Identity, ownerId, ProjectAction.ReadPage)).Allowed)
    { await ApiError(context, 404, "owner"); return; }
    // A user avatar is readable without credentials only for someone who owns a published project, because
    // the public listing already names them. Opening every account's avatar to anonymous callers would turn
    // this route into a way to enumerate which accounts exist, on a server whose accounts are otherwise
    // administrator-only knowledge.
    if (ownerType == AvatarOwner.User && browser is null &&
        !(settings.PublicBrowsing && await store.HasPublishedProjectAsync(ownerId, context.RequestAborted)))
    { await ApiError(context, 404, "owner"); return; }

    StoredAvatar? avatar = await store.GetAvatarAsync(ownerType, ownerId, context.RequestAborted);
    if (avatar is null) { await ApiError(context, 404, "avatar"); return; }
    string etag = '"' + avatar.Sha256 + '"';
    context.Response.Headers.CacheControl = "no-cache, private";
    context.Response.Headers.ETag = etag;
    if (context.Request.GetTypedHeaders().IfNoneMatch?.Any(value => value.Tag.Value == etag || value.Tag.Value == "*") == true)
    { context.Response.StatusCode = StatusCodes.Status304NotModified; return; }
    context.Response.ContentType = avatar.ContentType;
    context.Response.ContentLength = avatar.Bytes.Length;
    // HEAD gets the same headers and no body, matching how the embedded static assets behave.
    if (HttpMethods.IsHead(context.Request.Method)) return;
    await context.Response.Body.WriteAsync(avatar.Bytes, context.RequestAborted);
});

// Serves an image committed to a repository, so a README can show pictures that live beside it. This is the
// only route that returns repository bytes to a browser as something other than text, so it runs them
// through the same validator that guards avatars: the content type is the one the bytes prove they are, the
// repository never gets to declare it, and anything that is not a recognised raster image is refused. SVG is
// refused with everything else, which is what stops a repository serving script from this origin.
app.MapMethods("/api/graft/projects/{projectId:long}/image", ["GET", "HEAD"], async context =>
{
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: false);
    if (!await OnlyQuery(context, "ref", "path")) return;
    long projectId = Convert.ToInt64(context.Request.RouteValues["projectId"], System.Globalization.CultureInfo.InvariantCulture);
    ProjectDetails? details = await store.GetReadyProjectAsync(projectId, context.RequestAborted);
    if (details is null ||
        !(await DecideFor(context, browser?.Identity, projectId, ProjectAction.BrowseRepository)).Allowed)
    { await ApiError(context, 404, "project"); return; }

    string revision = context.Request.Query["ref"].ToString();
    if (revision.Length == 0) revision = "HEAD";
    string path = context.Request.Query["path"].ToString();
    if (path.Length == 0) { await ApiError(context, 404, "image"); return; }

    await WithRepository(EndpointFor(details.Project), context, async repository =>
    {
        GitFile? file;
        try { file = RepositoryView.File(repository, revision, path); }
        catch (GitInputException) { await ApiError(context, 404, "image"); return; }
        if (file is null) { await ApiError(context, 404, "image"); return; }

        ValidatedImage? image = ImageContent.Validate(file.Content);
        if (image is null) { await ApiError(context, 415, "that file is not a supported image"); return; }

        string etag = '"' + file.BlobId + '"';
        context.Response.Headers.CacheControl = "no-cache, private";
        context.Response.Headers.ETag = etag;
        if (context.Request.GetTypedHeaders().IfNoneMatch?.Any(value => value.Tag.Value == etag || value.Tag.Value == "*") == true)
        { context.Response.StatusCode = StatusCodes.Status304NotModified; return; }
        context.Response.ContentType = image.ContentType;
        context.Response.ContentLength = image.Bytes.Length;
        if (HttpMethods.IsHead(context.Request.Method)) return;
        await context.Response.Body.WriteAsync(image.Bytes, context.RequestAborted);
    });
});

// Commit uploaded files into a repository. The browser moved the bytes through DnaX; this is the
// application half, so the caller is re-authenticated, re-authorized for push, and only ever reaches
// uploads it owns and that were created under the repository-file profile.
app.MapPost("/api/graft/projects/{projectId:long}/repository/files", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }

    RepositoryCommitRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.RepositoryCommitRequest, context.RequestAborted);
    if (request?.Files is not { Length: > 0 and <= 20 } || request.Branch is not { Length: > 0 })
    { await ApiError(context, 400, "files"); return; }

    long projectId = Convert.ToInt64(context.Request.RouteValues["projectId"], System.Globalization.CultureInfo.InvariantCulture);
    ProjectDetails? details = await store.GetReadyProjectAsync(projectId, context.RequestAborted);
    if (details is null ||
        !(await DecideFor(context, browser.Value.Identity, projectId, ProjectAction.PushRepository)).Allowed)
    { await ApiError(context, 404, "project"); return; }

    DiskUploadStore uploads = context.RequestServices.GetRequiredService<DiskUploadStore>();
    string owner = GraftSessionAuthenticationHandler.Owner(browser.Value.Identity);
    List<FileChange> changes = [];
    try
    {
        foreach (RepositoryFileUpload file in request.Files)
        {
            if (!Guid.TryParse(file.UploadId, out Guid uploadId)) { await ApiError(context, 400, "upload-id"); return; }
            UploadStatus status = uploads.Get(owner, uploadId);
            // A session created under another profile carries another profile's limits, so a 20 GiB archive
            // must never be committable as a blob.
            if (!string.Equals(status.Profile, UploadLimits.RepositoryFileProfile, StringComparison.Ordinal))
            { await ApiError(context, 400, "upload was not created for repository files"); return; }
            if (status.Length > Limits.ObjectBytes) { await ApiError(context, 413, "file exceeds the repository object limit"); return; }
            byte[] content = [];
            await uploads.ReadCompletedAsync(owner, uploadId, async (source, cancellationToken) =>
            {
                using MemoryStream buffer = new();
                await source.CopyToAsync(buffer, cancellationToken);
                content = buffer.ToArray();
            }, context.RequestAborted);
            changes.Add(new(file.Path, content, file.Executable));
        }
    }
    catch (UploadException error) { await ApiError(context, error.StatusCode, error.Message); return; }

    // Graft accounts carry no email address, so attribution uses the reserved .invalid domain rather than
    // inventing a deliverable one. The identity still round-trips through the commit reader.
    GitIdentity author = new(browser.Value.Identity.Username,
        browser.Value.Identity.Username + "@graft.invalid", DateTimeOffset.UtcNow);

    RepositoryEndpoint endpoint = EndpointFor(details.Project);
    await WithRepository(endpoint, context, async repository =>
    {
        AuthoredCommit authored = repository.CommitFiles(request.Branch, changes, author, request.Message, context.RequestAborted);
        await context.Response.WriteAsJsonAsync(
            new RepositoryCommitResponse(authored.CommitId, authored.TreeId, authored.Branch, authored.CreatedBranch),
            ApiJson.Default.RepositoryCommitResponse, null, context.RequestAborted);
    });
});

// The repository view. Reachable by numeric id and by path, because a reader who arrives at /alice/thing
// with no credentials has no way to learn the id: the listing that would have told them is precisely the
// thing a stranger cannot read.
Func<HttpContext, ProjectDetails?, Task> browseProject = async (context, details) =>
{
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: false);
    if (!await OnlyQuery(context, "ref", "path", "file")) return;
    long projectId = details?.Project.Id ?? 0;
    // BrowseRepository, not ReadPage: this route walks the tree, lists branches and reads history, so it is
    // a repository surface. A caller whose only grant is a published page is refused here and reads the page
    // route instead, because a path allowlist cannot be honoured by a listing that enumerates the tree.
    if (details is null || !(await DecideFor(context, browser?.Identity, projectId, ProjectAction.BrowseRepository)).Allowed)
    { await ApiError(context, 404, "project"); return; }
    string revision = context.Request.Query["ref"].ToString();
    if (revision.Length == 0) revision = "HEAD";
    string path = context.Request.Query["path"].ToString();
    string filePath = context.Request.Query["file"].ToString();
    RepositoryEndpoint endpoint = EndpointFor(details.Project);
    await WithRepository(endpoint, context, async repository =>
    {
        IReadOnlyList<GitTreeEntry>? tree = RepositoryView.Tree(repository, revision, path);
        if (tree is null) { await ApiError(context, 404, "tree"); return; }
        GitFile? file = filePath.Length == 0 ? null : RepositoryView.File(repository, revision, filePath);
        if (filePath.Length > 0 && file is null) { await ApiError(context, 404, "file"); return; }
        ProjectResponse project = await ProjectResponseForLocked(context, details, repository);
        PermissionDecision manage = await DecideFor(context, browser?.Identity, projectId, ProjectAction.ManageProject);
        PermissionDecision push = await DecideFor(context, browser?.Identity, projectId, ProjectAction.PushRepository);
        BranchResponse[] branches = RepositoryView.Branches(repository).Select(branch => new BranchResponse(
            branch.Name, false, false, branch.Name == repository.DefaultBranch, true, true, push.Allowed,
            ProjectWebUrl(context, details) + "/-/tree/" + Uri.EscapeDataString(branch.Name),
            CommitResponseFor(context, details, RepositoryView.ReadCommit(repository, branch.Id)))).ToArray();
        CommitResponse[] commits = RepositoryView.Commits(repository, revision, 0, 20)
            .Select(commit => CommitResponseFor(context, details, commit)).ToArray();
        TreeEntryResponse[] entries = tree.Select(entry => new TreeEntryResponse(entry.Id, entry.Name, entry.Type, entry.Path, entry.Mode)).ToArray();
        FileResponse? fileResponse = file is null ? null : FileResponseFor(file, revision);
        // The README is rendered for the root of the branch being viewed, so switching branch shows that
        // branch's README. Only the root is considered; a README deeper in the tree is an ordinary file.
        ReadmeResponse? readme = path.Length == 0 ? ReadmeFor(context, details, repository, revision) : null;
        RepositoryPageResponse response = new(project, manage.Allowed, push.Allowed, branches, commits, entries,
            fileResponse, readme, details.Project.PagePublished, settings.PublicBrowsing);
        await context.Response.WriteAsJsonAsync(response, ApiJson.Default.RepositoryPageResponse, null, context.RequestAborted);
    });
};

app.MapGet("/api/browser/projects/{projectId:long}", async context => await browseProject(context,
    await store.GetReadyProjectAsync(
        Convert.ToInt64(context.Request.RouteValues["projectId"], System.Globalization.CultureInfo.InvariantCulture),
        context.RequestAborted)));

app.MapGet("/api/browser/projects/{projectNamespace}/{slug}", async context => await browseProject(context,
    await store.FindReadyProjectDetailsAsync(
        context.Request.RouteValues["projectNamespace"]?.ToString() ?? "",
        context.Request.RouteValues["slug"]?.ToString() ?? "",
        context.RequestAborted)));

// What a commit changed. Every commit the API reports already carries a web URL pointing here, so this is
// the page behind a link the product has been handing out all along.
app.MapGet("/api/browser/projects/{projectId:long}/commit/{sha}", async context =>
{
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: false);
    if (!await OnlyQuery(context, "parent")) return;
    long projectId = Convert.ToInt64(context.Request.RouteValues["projectId"], System.Globalization.CultureInfo.InvariantCulture);
    ProjectDetails? details = await store.GetReadyProjectAsync(projectId, context.RequestAborted);
    // A diff prints the contents of the files a commit touched, so it is a repository surface and a page
    // grant can never reach it. Only visibility or membership opens this.
    if (details is null || !(await DecideFor(context, browser?.Identity, projectId, ProjectAction.BrowseRepository)).Allowed)
    { await ApiError(context, 404, "project"); return; }

    string sha = context.Request.RouteValues["sha"]?.ToString() ?? "";
    // A merge has more than one parent and no single answer to "what changed", so the caller says which
    // side it wants rather than the server picking one silently.
    int parent = int.TryParse(context.Request.Query["parent"].ToString(),
        System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int requested)
        ? requested : 0;
    if (parent is < 0 or > 15) { await ApiError(context, 400, "parent"); return; }

    await WithRepository(EndpointFor(details.Project), context, async repository =>
    {
        string? commitId = Limits.IsId(sha) && repository.Objects.ContainsKey(sha) ? sha : null;
        if (commitId is null) { await ApiError(context, 404, "commit"); return; }

        CommitDiff diff;
        try { diff = RepositoryDiff.ForCommit(repository, commitId, parent); }
        catch (GitInputException) { await ApiError(context, 404, "commit"); return; }

        ProjectResponse project = await ProjectResponseForLocked(context, details, repository);
        DiffFileResponse[] files = diff.Files.Select(file => new DiffFileResponse(
            file.Path, file.Status, file.OldMode, file.NewMode, file.IsBinary, file.TooLarge,
            file.Added, file.Removed,
            file.Hunks.Select(hunk => new DiffHunkResponse(HunkHeader(hunk),
                hunk.Lines.Select(line => new DiffLineResponse(
                    line.Kind.ToString(), line.Text, line.OldLine, line.NewLine, line.NoNewline)).ToArray()))
                .ToArray())).ToArray();

        CommitDiffResponse response = new(project, CommitResponseFor(context, details, diff.Commit),
            diff.ParentId, files, diff.Truncated, files.Sum(file => file.Added), files.Sum(file => file.Removed));
        await context.Response.WriteAsJsonAsync(response, ApiJson.Default.CommitDiffResponse, null, context.RequestAborted);
    });
});

app.MapGet("/api/v4/user", async context =>
{
    AuthenticatedIdentity? identity = await AuthenticateApi(context, requireApiScope: true);
    if (identity is null) return;
    if (!await OnlyQuery(context)) return;
    string origin = Origin(context);
    string? avatarUrl = await store.HasAvatarAsync(AvatarOwner.User, identity.UserId, context.RequestAborted)
        ? AvatarUrl(context, AvatarOwner.User, identity.UserId)
        : null;
    await context.Response.WriteAsJsonAsync(new UserResponse(
        identity.UserId, identity.Username, identity.Username, "active", false,
        origin + "/" + Uri.EscapeDataString(identity.Username), avatarUrl),
        ApiJson.Default.UserResponse, null, context.RequestAborted);
});

app.MapGet("/api/v4/projects", async context =>
{
    AuthenticatedIdentity? identity = await AuthenticateApi(context, requireApiScope: true);
    if (identity is null) return;
    if (!await OnlyQuery(context, "page", "per_page")) return;
    (int Page, int PerPage)? pagination = await Pagination(context);
    if (pagination is null) return;
    (int page, int perPage) = pagination.Value;
    ProjectPage projects = await store.ListAccessibleProjectsAsync(identity, (page - 1) * perPage, perPage, context.RequestAborted);
    List<ProjectResponse> response = [];
    foreach (ProjectDetails project in projects.Projects)
        response.Add(await ProjectResponseFor(context, project));
    SetPagination(context, page, perPage, projects.Total);
    await context.Response.WriteAsJsonAsync(response.ToArray(), ApiJson.Default.ProjectResponseArray, null, context.RequestAborted);
});

app.MapPost("/api/v4/projects", async context =>
{
    AuthenticatedIdentity? identity = await AuthenticateApi(context, requireApiScope: false);
    if (identity is null) return;
    if (!identity.TokenScopes.HasFlag(TokenScope.Api))
    { await ApiError(context, 403, "403 Forbidden: insufficient_scope"); return; }
    ProjectCreateRequest? request = await ReadProjectCreateRequest(context);
    if (request is null) return;
    await CreateProject(context, identity, request);
});

app.MapGet("/api/v4/projects/{projectId}/repository/branches", async context =>
{
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.BrowseRepository);
    if (project is null) return;
    if (!await OnlyQuery(context, "page", "per_page")) return;
    (int Page, int PerPage)? pagination = await Pagination(context);
    if (pagination is null) return;
    (int page, int perPage) = pagination.Value;
    await WithRepository(project.Endpoint, context, async repository =>
    {
        (string Name, string Id)[] branches = RepositoryView.Branches(repository).ToArray();
        PermissionDecision push = await DecideFor(context, project.Identity, project.Details.Project.Id, ProjectAction.PushRepository);
        BranchResponse[] result = branches.Skip((page - 1) * perPage).Take(perPage).Select(branch =>
            new BranchResponse(branch.Name, false, false, branch.Name == repository.DefaultBranch, true, true, push.Allowed,
                ProjectWebUrl(context, project.Details) + "/-/tree/" + Uri.EscapeDataString(branch.Name),
                CommitResponseFor(context, project.Details, RepositoryView.ReadCommit(repository, branch.Id)))).ToArray();
        SetPagination(context, page, perPage, branches.Length);
        await context.Response.WriteAsJsonAsync(result, ApiJson.Default.BranchResponseArray, null, context.RequestAborted);
    });
});

app.MapGet("/api/v4/projects/{projectId}/repository/commits", async context =>
{
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.BrowseRepository);
    if (project is null) return;
    if (!await OnlyQuery(context, "ref_name", "page", "per_page")) return;
    (int Page, int PerPage)? pagination = await Pagination(context);
    if (pagination is null) return;
    (int page, int perPage) = pagination.Value;
    string revision = context.Request.Query["ref_name"].ToString();
    if (revision.Length == 0) revision = "HEAD";
    await WithRepository(project.Endpoint, context, async repository =>
    {
        IReadOnlyList<GitCommit> all = RepositoryView.Commits(repository, revision, 0, Limits.ObjectCount);
        CommitResponse[] result = all.Skip((page - 1) * perPage).Take(perPage)
            .Select(commit => CommitResponseFor(context, project.Details, commit)).ToArray();
        SetPagination(context, page, perPage, all.Count);
        await context.Response.WriteAsJsonAsync(result, ApiJson.Default.CommitResponseArray, null, context.RequestAborted);
    });
});

app.MapGet("/api/v4/projects/{projectId}/repository/tree", async context =>
{
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.BrowseRepository);
    if (project is null) return;
    if (!await OnlyQuery(context, "ref", "path", "page", "per_page")) return;
    (int Page, int PerPage)? pagination = await Pagination(context);
    if (pagination is null) return;
    (int page, int perPage) = pagination.Value;
    string revision = context.Request.Query["ref"].ToString();
    if (revision.Length == 0) revision = "HEAD";
    string path = context.Request.Query["path"].ToString();
    await WithRepository(project.Endpoint, context, async repository =>
    {
        IReadOnlyList<GitTreeEntry>? entries = RepositoryView.Tree(repository, revision, path);
        if (entries is null) { await ApiError(context, 404, "404 Tree Not Found"); return; }
        TreeEntryResponse[] result = entries.Skip((page - 1) * perPage).Take(perPage)
            .Select(entry => new TreeEntryResponse(entry.Id, entry.Name, entry.Type, entry.Path, entry.Mode)).ToArray();
        SetPagination(context, page, perPage, entries.Count);
        await context.Response.WriteAsJsonAsync(result, ApiJson.Default.TreeEntryResponseArray, null, context.RequestAborted);
    });
});

app.MapGet("/api/v4/projects/{projectId}/repository/files/{**filePath}", async context =>
{
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.BrowseRepository);
    if (project is null) return;
    if (!await OnlyQuery(context, "ref")) return;
    string revision = context.Request.Query["ref"].ToString();
    if (revision.Length == 0) { await ApiError(context, 400, "ref is missing"); return; }
    string filePath = context.Request.RouteValues["filePath"]?.ToString() ?? "";
    await WithRepository(project.Endpoint, context, async repository =>
    {
        GitFile? file = RepositoryView.File(repository, revision, filePath);
        if (file is null) { await ApiError(context, 404, "404 File Not Found"); return; }
        byte[] sha256 = SHA256.HashData(file.Content);
        FileResponse response = new(
            Path.GetFileName(file.Path), file.Path, file.Content.Length, "base64", Convert.ToBase64String(file.Content),
            Convert.ToHexStringLower(sha256), revision, file.BlobId, file.CommitId, file.CommitId, file.Mode == "100755");
        await context.Response.WriteAsJsonAsync(response, ApiJson.Default.FileResponse, null, context.RequestAborted);
    });
});

app.MapGet("/api/v4/projects/{**projectId}", async context =>
{
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.ReadRest);
    if (project is null) return;
    if (!await OnlyQuery(context)) return;
    await context.Response.WriteAsJsonAsync(await ProjectResponseFor(context, project.Details), ApiJson.Default.ProjectResponse, null, context.RequestAborted);
});

// Changing an existing project. GitLab's own edit route carries dozens of attributes; this accepts exactly
// one, and refuses the rest by name rather than ignoring them, so a caller is never told it succeeded at
// something that did not happen.
//
// It lives on the GitLab-shaped surface because GitLab has this route and this attribute. Publishing a
// project page does not exist upstream, so it stays under Graft's own prefix below rather than inventing a
// field in a payload somebody else owns.
app.MapMethods("/api/v4/projects/{**projectId}", ["PUT"], async context =>
{
    // The scope is checked before the project is looked up, so a token that could never perform this is
    // told its scope is wrong rather than being told the project does not exist. A credential's own scope
    // is not a secret about somebody else's project, and the create route already answers this way.
    (bool unresolved, AuthenticatedIdentity? caller) = await AuthenticateApiOptional(context);
    if (unresolved) return;
    if (caller is null) { await ApiError(context, 401, "401 Unauthorized"); return; }
    if (!caller.TokenScopes.HasFlag(TokenScope.Api))
    { await ApiError(context, 403, "403 Forbidden: insufficient_scope"); return; }

    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.ManageProject);
    if (project is null) return;
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }

    ProjectVisibility visibility;
    try
    {
        using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        { await ApiError(context, 400, "project must be a JSON object"); return; }
        string? unsupported = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .FirstOrDefault(name => name != "visibility");
        if (unsupported is not null)
        { await ApiError(context, 400, "unsupported project property: " + unsupported); return; }
        if (!document.RootElement.TryGetProperty("visibility", out JsonElement value) || value.ValueKind != JsonValueKind.String)
        { await ApiError(context, 400, "visibility is required"); return; }
        if (!TryReadVisibility(value.GetString(), out visibility))
        { await ApiError(context, 400, "visibility must be private, unlisted or public"); return; }
    }
    catch (JsonException) { await ApiError(context, 400, "invalid JSON"); return; }

    if (visibility != ProjectVisibility.Private && !settings.PublicBrowsing)
    { await ApiError(context, 400, "this server does not allow projects to be published"); return; }

    try
    {
        await store.SetProjectVisibilityAsync(caller, project.Details.Project.Id, visibility, context.RequestAborted);
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "404 Project Not Found"); return; }

    ProjectDetails updated = (await store.GetReadyProjectAsync(project.Details.Project.Id, context.RequestAborted))!;
    await context.Response.WriteAsJsonAsync(await ProjectResponseFor(context, updated), ApiJson.Default.ProjectResponse, null, context.RequestAborted);
});

// Publishing the curated project page. Under Graft's own prefix because GitLab has no equivalent, and
// GL-012 in the compatibility record keeps the upstream upload path unclaimed for the same reason: a route
// somebody else owns should not come to mean something only this server understands.
app.MapMethods("/api/graft/projects/{projectId}/page", ["PUT"], async context =>
{
    (bool unresolved, AuthenticatedIdentity? caller) = await AuthenticateApiOptional(context);
    if (unresolved) return;
    if (caller is null) { await ApiError(context, 401, "401 Unauthorized"); return; }
    if (!caller.TokenScopes.HasFlag(TokenScope.Api))
    { await ApiError(context, 403, "403 Forbidden: insufficient_scope"); return; }

    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.ManageProject);
    if (project is null) return;
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }

    PagePublishRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.PagePublishRequest, context.RequestAborted);
    if (request is null) { await ApiError(context, 400, "published"); return; }
    if (request.Published && !settings.PublicBrowsing)
    { await ApiError(context, 400, "this server does not allow projects to be published"); return; }

    try
    {
        await store.SetPagePublishedAsync(caller, project.Details.Project.Id, request.Published, context.RequestAborted);
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "404 Project Not Found"); return; }

    ProjectDetails updated = (await store.GetReadyProjectAsync(project.Details.Project.Id, context.RequestAborted))!;
    await context.Response.WriteAsJsonAsync(new PageStateResponse(updated.Project.Id, updated.Project.PagePublished),
        ApiJson.Default.PageStateResponse, null, context.RequestAborted);
});

// Reading it back, so a client can see the state without a browser session.
app.MapGet("/api/graft/projects/{projectId}/page", async context =>
{
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.ReadRest);
    if (project is null) return;
    if (!await OnlyQuery(context)) return;
    await context.Response.WriteAsJsonAsync(
        new PageStateResponse(project.Details.Project.Id, project.Details.Project.PagePublished),
        ApiJson.Default.PageStateResponse, null, context.RequestAborted);
});

app.Map("/api/v4/{**path}", context => ApiError(context, 404, "404 API endpoint not found"));

app.MapGet("/{projectNamespace}/{project}.git/info/refs", async context =>
{
    string service = context.Request.Query["service"].ToString();
    ProjectAction action = service switch
    {
        "git-upload-pack" => ProjectAction.FetchRepository,
        "git-receive-pack" => ProjectAction.PushRepository,
        _ => throw new GitInputException("service")
    };
    AuthorizedRepository? authorized = await Authorize(context, action);
    if (authorized is null) return;
    if (!await authorized.Endpoint.Gate.WaitAsync(0, context.RequestAborted))
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return;
    }
    try
    {
        byte[] data = authorized.Endpoint.Repository.Advertise(service);
        context.Response.ContentType = "application/x-" + service + "-advertisement";
        await context.Response.Body.WriteAsync(data, context.RequestAborted);
    }
    finally { authorized.Endpoint.Gate.Release(); }
});

app.MapPost("/{projectNamespace}/{project}.git/{service}", async context =>
{
    string service = context.Request.RouteValues["service"]?.ToString() ?? "";
    ProjectAction action = service switch
    {
        "git-upload-pack" => ProjectAction.FetchRepository,
        "git-receive-pack" => ProjectAction.PushRepository,
        _ => throw new GitInputException("service")
    };
    AuthorizedRepository? authorized = await Authorize(context, action);
    if (authorized is null) return;
    Limits.Require(context.Request.ContentType == "application/x-" + service + "-request", "content-type");
    Limits.Require(!context.Request.Headers.ContainsKey("Content-Encoding"), "content-encoding");
    if (!await authorized.Endpoint.Gate.WaitAsync(0, context.RequestAborted))
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return;
    }
    try
    {
        using MemoryStream input = new();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
        {
            Limits.Require(input.Length + count <= Limits.RequestBytes, "request-limit");
            input.Write(buffer, 0, count);
        }
        byte[] body = input.ToArray();

        // A lone flush packet is the client settling authentication, not a request to act on. Git sends one
        // before any body it would be unable to send twice: once the body is streamed with chunked encoding
        // it cannot be replayed after a 401, so the client spends an empty request on the challenge first
        // and only then streams the real one.
        //
        // Answering that probe with an error ends the operation before it begins. It is why a push larger
        // than the client's http.postBuffer, a megabyte by default, failed here while a smaller one went
        // through: only the large one is chunked, and only the chunked one is probed. Real Git answers an
        // empty request with an empty result, so this does too.
        if (body is [(byte)'0', (byte)'0', (byte)'0', (byte)'0'])
        {
            context.Response.ContentType = "application/x-" + service + "-result";
            return;
        }

        byte[] data = service == "git-receive-pack"
            ? authorized.Endpoint.Repository.Receive(body, context.RequestAborted)
            : authorized.Endpoint.Repository.Upload(body, context.RequestAborted);
        context.Response.ContentType = "application/x-" + service + "-result";
        await context.Response.Body.WriteAsync(data, context.RequestAborted);
    }
    finally { authorized.Endpoint.Gate.Release(); }
});

app.MapFallback("/{**path}", context => assets.Serve(context));

await app.StartAsync();
string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
serverOrigin = settings.Origin ?? address;
NotifySystemd("READY=1\nSTATUS=Graft is ready");
app.Lifetime.ApplicationStopping.Register(() =>
{
    try { NotifySystemd("STOPPING=1\nSTATUS=Graft is stopping"); }
    catch (SocketException) { }
});
Console.WriteLine("MODE: " + (RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT"));
if (PasswordPolicy.RelaxedForLocalDebug)
    Console.WriteLine($"WARNING: debug build; passwords of {PasswordPolicy.MinimumLength} characters are accepted. " +
        "Do not expose this build. Release builds require 16.");
Console.WriteLine($"ASSETS: {assets.Count}; bytes={assets.Bytes}");
Console.WriteLine("READY: " + address);
await app.WaitForShutdownAsync();
return 0;

/// <summary>
/// The one place a project permission is resolved. Every route goes through here rather than calling the
/// store directly, so the operator switch can make a stored visibility inert: a member keeps their access,
/// and a grant that came from visibility or a published page is withdrawn.
///
/// <para>
/// This matters on restore. A database published on a host whose operator allowed public browsing must not
/// begin serving strangers when it is restored onto a host whose operator never did.
/// </para>
/// </summary>
async Task<PermissionDecision> DecideFor(HttpContext context, AuthenticatedIdentity? identity, long projectId, ProjectAction action)
{
    PermissionDecision decision = await store.GetPermissionAsync(identity, projectId, action, context.RequestAborted);
    if (settings.PublicBrowsing || decision.Grant is AccessGrant.Member or AccessGrant.None) return decision;
    return new(false, null, "Public browsing disabled", "This server does not serve projects to anonymous callers.");
}

async Task<AuthorizedRepository?> Authorize(HttpContext context, ProjectAction action)
{
    string projectNamespace = context.Request.RouteValues["projectNamespace"]?.ToString() ?? "";
    string projectSlug = context.Request.RouteValues["project"]?.ToString() ?? "";
    ProjectInfo? project = await store.FindReadyProjectAsync(projectNamespace, projectSlug, context.RequestAborted);
    AuthenticatedIdentity? identity = await Authenticate(context);

    // Push to a path that does not exist yet, and the project is created to receive it. Only on a push:
    // a fetch of a missing repository stays a miss, because creating something in order to report that it
    // is empty would be absurd. Creation happens on the ref advertisement rather than on the pack upload,
    // because the advertisement is the first request Git makes and it would otherwise fail before the push
    // began.
    if (project is null && identity is not null && action == ProjectAction.PushRepository && settings.PushToCreate)
        project = await CreateOnPush(context, identity, projectNamespace, projectSlug);

    // A public or unlisted repository clones without credentials, so the decision is taken before any
    // challenge rather than after one. Push is never granted by visibility, so a write still reaches the
    // challenge below and the client retries with a token.
    PermissionDecision? decision = project is null ? null : await DecideFor(context, identity, project.Id, action);
    if (decision is { Allowed: true }) return new(project!, identity, decision, EndpointFor(project!));

    if (identity is null)
    {
        // Identical whether the project is absent, private, or simply not readable by whoever is asking, so
        // an anonymous probe still cannot tell a repository that exists from one that does not. A real
        // client treats this as a prompt for credentials and retries.
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Graft Git\", charset=\"UTF-8\"";
        return null;
    }
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    return null;
}

/// <summary>
/// Creates the project a push is aimed at, when the operator has enabled it. Returns null to leave the push
/// refused exactly as a missing project would be, so every refusal below reads the same from outside.
///
/// <para>
/// The project is created private, owned by the caller, in the caller's own namespace. None of those is
/// configurable, and the reasons differ. Private, because publishing is a deliberate act and a side effect
/// of a push is the opposite of one. Owned by the caller, because a namespace in Graft is an account's
/// username. And the caller's own namespace only, because creating in someone else's would hand them a
/// project they never asked for, under their name, on the say-so of a stranger who knew their username.
/// </para>
/// </summary>
async Task<ProjectInfo?> CreateOnPush(HttpContext context, AuthenticatedIdentity identity, string projectNamespace, string slug)
{
    if (!string.Equals(identity.Username, projectNamespace, StringComparison.OrdinalIgnoreCase)) return null;

    // The same rules the REST create route applies. A path it would reject must not become creatable just
    // because it arrived over Git.
    if (!ValidProjectPath(slug)) return null;

    // A credential that may not push may not create by pushing. Creation is not treated as the larger
    // permission that the REST route's `api` scope guards, because what it produces here is a private
    // project owned by the caller in their own namespace, which is exactly what that account can already
    // make in the browser. An operator who does not want write_repository to imply that leaves this off.
    if (identity.Kind == IdentityKind.PersonalAccessToken &&
        !identity.TokenScopes.HasFlag(TokenScope.Api) && !identity.TokenScopes.HasFlag(TokenScope.WriteRepository))
        return null;

    try
    {
        return await store.CreateProjectWithRepositoryAsync(identity, slug, slug,
            (created, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = EndpointFor(created);
                return Task.CompletedTask;
            }, "", context.RequestAborted, "a push to a path that did not exist");
    }
    catch (ProjectConflictException)
    {
        // Two pushes raced for the same new path, or an earlier interrupted attempt left a pending row
        // holding it. Read back rather than fail: if the other attempt published, this push joins it, and
        // if it did not, this is an ordinary miss and the caller is refused.
        return await store.FindReadyProjectAsync(projectNamespace, slug, context.RequestAborted);
    }
    catch (UnauthorizedAccessException) { return null; }
    catch (ArgumentException) { return null; }
}

async Task<AuthenticatedIdentity?> Authenticate(HttpContext context)
{
    string header = context.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
    try
    {
        string value = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
        int separator = value.IndexOf(':');
        if (separator <= 0) return null;
        string username = value[..separator];
        string token = value[(separator + 1)..];
        AuthenticatedIdentity? identity = await store.ResolveTokenAsync(token, context.RequestAborted);
        return identity is not null && string.Equals(identity.Username, username, StringComparison.OrdinalIgnoreCase)
            ? identity
            : null;
    }
    catch (FormatException) { return null; }
}

async Task<AuthenticatedIdentity?> AuthenticateApi(HttpContext context, bool requireApiScope)
{
    string token = context.Request.Headers["PRIVATE-TOKEN"].ToString();
    string authorization = context.Request.Headers.Authorization.ToString();
    if (token.Length == 0 && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        token = authorization[7..];
    AuthenticatedIdentity? identity = await store.ResolveTokenAsync(token, context.RequestAborted);
    if (identity is null)
    {
        await ApiError(context, 401, "401 Unauthorized");
        return null;
    }
    if (requireApiScope && !identity.TokenScopes.HasFlag(TokenScope.Api) && !identity.TokenScopes.HasFlag(TokenScope.ReadApi))
    {
        await ApiError(context, 403, "403 Forbidden: insufficient_scope");
        return null;
    }
    return identity;
}

async Task<ProjectCreateRequest?> ReadProjectCreateRequest(HttpContext context)
{
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return null; }
    try
    {
        using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        { await ApiError(context, 400, "project must be a JSON object"); return null; }
        string[] allowed = ["name", "path", "description", "visibility"];
        string? unsupported = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .FirstOrDefault(name => !allowed.Contains(name, StringComparer.Ordinal));
        if (unsupported is not null)
        { await ApiError(context, 400, "unsupported project property: " + unsupported); return null; }
        ProjectCreateRequest? request = document.RootElement.Deserialize(ApiJson.Default.ProjectCreateRequest);
        if (request is null) { await ApiError(context, 400, "project is required"); return null; }
        return request;
    }
    catch (JsonException)
    {
        await ApiError(context, 400, "invalid JSON");
        return null;
    }
}

async Task CreateProject(HttpContext context, AuthenticatedIdentity identity, ProjectCreateRequest request)
{
    string name = request.Name?.Trim() ?? "";
    string requestedPath = request.Path?.Trim() ?? "";
    string description = request.Description ?? "";
    string visibility = request.Visibility ?? "private";
    if (name.Length == 0 && requestedPath.Length == 0) { await ApiError(context, 400, "name or path is required"); return; }
    if (name.Length == 0) name = requestedPath;
    if (name.Length > 200) { await ApiError(context, 400, "name must be at most 200 characters"); return; }
    if (description.Length > 2_000) { await ApiError(context, 400, "description must be at most 2000 characters"); return; }
    // GitLab's "internal" has no meaning here, because Graft has no concept of an instance-wide signed-in
    // audience distinct from the public: a project is private to its members, reachable by anyone holding
    // the path, or listed for everyone. Matching the three names Graft actually implements is more honest
    // than accepting a fourth and quietly treating it as one of these.
    if (!TryReadVisibility(visibility, out ProjectVisibility requested))
    { await ApiError(context, 400, "visibility must be private, unlisted or public"); return; }
    if (requested != ProjectVisibility.Private && !settings.PublicBrowsing)
    { await ApiError(context, 400, "this server does not allow projects to be published"); return; }
    string path = requestedPath.Length == 0 ? ProjectPathFromName(name) : requestedPath;
    if (!ValidProjectPath(path))
    {
        await ApiError(context, 400, "path must be 2-39 ASCII letters or numbers separated by single dots, underscores or hyphens");
        return;
    }
    try
    {
        ProjectInfo project = await store.CreateProjectWithRepositoryAsync(identity, path, name,
            (created, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = EndpointFor(created);
                return Task.CompletedTask;
            }, description, context.RequestAborted);
        // Applied after creation rather than inside it, so a project is never briefly readable by the world
        // while its repository is still being initialised. Creation publishes nothing on its own.
        if (requested != ProjectVisibility.Private)
            await store.SetProjectVisibilityAsync(identity, project.Id, requested, context.RequestAborted);
        ProjectDetails details = (await store.GetReadyProjectAsync(project.Id, context.RequestAborted))!;
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(await ProjectResponseFor(context, details), ApiJson.Default.ProjectResponse, null, context.RequestAborted);
    }
    catch (ProjectConflictException)
    {
        await ApiError(context, 400, "path has already been taken");
    }
}

static string ProjectPathFromName(string name)
{
    StringBuilder path = new(name.Length);
    bool separator = false;
    foreach (char character in name)
    {
        if (character is >= 'A' and <= 'Z') { path.Append((char)(character + ('a' - 'A'))); separator = false; }
        else if (character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.')
        { path.Append(character); separator = false; }
        else if (char.IsWhiteSpace(character) && path.Length > 0 && !separator)
        { path.Append('-'); separator = true; }
        else return "";
    }
    return path.ToString().TrimEnd('-');
}

static bool ValidProjectPath(string path)
{
    if (path.Length is < 2 or > 39 || !AsciiLetterOrNumber(path[0]) || !AsciiLetterOrNumber(path[^1])) return false;
    bool previousSpecial = false;
    foreach (char character in path)
    {
        if (AsciiLetterOrNumber(character)) { previousSpecial = false; continue; }
        if (character is not ('_' or '-' or '.') || previousSpecial) return false;
        previousSpecial = true;
    }
    return true;
}

static bool AsciiLetterOrNumber(char character) =>
    character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

static bool TryTokenScopes(string[]? values, out TokenScope scopes)
{
    scopes = TokenScope.None;
    if (values is null || values.Length is < 1 or > 4) return false;
    foreach (string value in values)
    {
        TokenScope parsed = value switch
        {
            "read_api" => TokenScope.ReadApi,
            "api" => TokenScope.Api,
            "read_repository" => TokenScope.ReadRepository,
            "write_repository" => TokenScope.WriteRepository,
            _ => TokenScope.None
        };
        if (parsed == TokenScope.None || scopes.HasFlag(parsed)) return false;
        scopes |= parsed;
    }
    return true;
}

static string[] TokenScopeNames(TokenScope scopes)
{
    List<string> names = [];
    if (scopes.HasFlag(TokenScope.ReadApi)) names.Add("read_api");
    if (scopes.HasFlag(TokenScope.Api)) names.Add("api");
    if (scopes.HasFlag(TokenScope.ReadRepository)) names.Add("read_repository");
    if (scopes.HasFlag(TokenScope.WriteRepository)) names.Add("write_repository");
    return names.ToArray();
}

/// <summary>
/// Resolves a token if one was presented, without requiring that one was. A request carrying no token at all
/// is anonymous, which a public project may still satisfy. A request carrying a token that does not resolve
/// is an error rather than an anonymous request, because a caller who presented a credential meant to use it
/// and should be told it is bad instead of quietly seeing less.
/// </summary>
async Task<(bool Failed, AuthenticatedIdentity? Identity)> AuthenticateApiOptional(HttpContext context)
{
    string token = context.Request.Headers["PRIVATE-TOKEN"].ToString();
    string authorization = context.Request.Headers.Authorization.ToString();
    if (token.Length == 0 && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        token = authorization[7..];
    if (token.Length == 0) return (false, null);

    AuthenticatedIdentity? identity = await store.ResolveTokenAsync(token, context.RequestAborted);
    if (identity is null)
    {
        await ApiError(context, 401, "401 Unauthorized");
        return (true, null);
    }
    return (false, identity);
}

async Task<ApiProject?> AuthorizeApiProject(HttpContext context, ProjectAction action)
{
    (bool failed, AuthenticatedIdentity? identity) = await AuthenticateApiOptional(context);
    if (failed) return null;
    string identifier = context.Request.RouteValues["projectId"]?.ToString() ?? "";
    try { identifier = Uri.UnescapeDataString(identifier); }
    catch (UriFormatException) { await ApiError(context, 404, "404 Project Not Found"); return null; }
    ProjectDetails? details;
    if (long.TryParse(identifier, out long id)) details = await store.GetReadyProjectAsync(id, context.RequestAborted);
    else
    {
        string[] path = identifier.Split('/');
        details = path.Length == 2
            ? await store.FindReadyProjectDetailsAsync(path[0], path[1], context.RequestAborted)
            : null;
    }
    if (details is null)
    {
        await ApiError(context, 404, "404 Project Not Found");
        return null;
    }
    PermissionDecision permission = await DecideFor(context, identity, details.Project.Id, action);
    if (!permission.Allowed)
    {
        await ApiError(context, 404, "404 Project Not Found");
        return null;
    }
    return new(details, identity, EndpointFor(details.Project));
}

RepositoryEndpoint EndpointFor(ProjectInfo project)
{
    if (project.StorageId.Length != 32 || project.StorageId.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        throw new InvalidOperationException("Invalid project storage identifier.");
    RepositoryEndpoint endpoint = repositories.GetOrAdd(project.StorageId, storageId =>
        new(() => new(new Repository(new DurableRepositoryStorage(Path.Combine(settings.Repositories, storageId)))),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    // Repositories are cached for the process lifetime while the project row is read per request, so the
    // stored preference is reapplied on every use rather than only when the repository is first opened.
    // Doing it here covers the Git transport as well, which needs it to advertise the right HEAD symref.
    endpoint.Repository.PreferredDefaultBranch = project.DefaultBranch;
    return endpoint;
}

async Task WithRepository(RepositoryEndpoint endpoint, HttpContext context, Func<Repository, Task> action)
{
    if (!await endpoint.Gate.WaitAsync(0, context.RequestAborted))
    {
        await ApiError(context, 429, "429 Too Many Requests");
        return;
    }
    try { await action(endpoint.Repository); }
    finally { endpoint.Gate.Release(); }
}

async Task<ProjectResponse> ProjectResponseFor(HttpContext context, ProjectDetails details)
{
    RepositoryEndpoint endpoint = EndpointFor(details.Project);
    await endpoint.Gate.WaitAsync(context.RequestAborted);
    try { return await ProjectResponseForLocked(context, details, endpoint.Repository); }
    finally { endpoint.Gate.Release(); }
}

async Task<ProjectResponse> ProjectResponseForLocked(HttpContext context, ProjectDetails details, Repository repository)
{
    string? defaultBranch = repository.DefaultBranch;
    string origin = Origin(context);
    string path = details.Project.Namespace + "/" + details.Project.Slug;
    string webUrl = origin + "/" + path;
    // GitLab reports a null avatar_url when none is set, so the field is only populated once one exists.
    string? avatarUrl = await store.HasAvatarAsync(AvatarOwner.Project, details.Project.Id, context.RequestAborted)
        ? AvatarUrl(context, AvatarOwner.Project, details.Project.Id)
        : null;
    return new(details.Project.Id, details.Description, details.Project.Name,
        details.Project.Namespace + " / " + details.Project.Name, details.Project.Slug, path, details.CreatedAtUtc,
        defaultBranch, VisibilityName(details.Project.Visibility), null, webUrl + ".git", webUrl, avatarUrl,
        new(details.Project.OwnerUserId, details.Project.Namespace, details.Project.Namespace, "user", details.Project.Namespace,
            origin + "/" + Uri.EscapeDataString(details.Project.Namespace)));
}

/// <summary>
/// Finds and renders the README at the root of a revision. Images inside it resolve to the repository image
/// route for the same revision, so a README shows the pictures that were committed beside it and nothing
/// else; anything off-site is downgraded by the renderer rather than fetched.
/// </summary>
ReadmeResponse? ReadmeFor(HttpContext context, ProjectDetails details, Repository repository, string revision)
{
    foreach (string name in MarkdownContent.Names)
    {
        GitFile? file;
        try { file = RepositoryView.File(repository, revision, name); }
        catch (GitInputException) { continue; }
        if (file is null || file.Content.Length == 0 || file.Content.Length > MarkdownContent.MaximumBytes) continue;

        string source;
        try { source = new UTF8Encoding(false, true).GetString(file.Content); }
        catch (DecoderFallbackException) { return null; }

        string baseUrl = Origin(context) + "/api/graft/projects/" +
            details.Project.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/image";
        RenderedMarkdown rendered = MarkdownContent.Render(source, path =>
            baseUrl + "?ref=" + Uri.EscapeDataString(revision) + "&path=" + Uri.EscapeDataString(path));
        return new(file.Path, rendered.Html, rendered.HasDiagrams);
    }
    return null;
}

/// <summary>The header a unified diff prints above a hunk. A range of one line omits its count, and a range
/// of none is numbered by the line it would follow, which is zero at the start of a file.</summary>
static string HunkHeader(DiffHunk hunk)
{
    static string Range(int start, int count) => count == 1
        ? start.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : start.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    return "@@ -" + Range(hunk.OldStart, hunk.OldLines) + " +" + Range(hunk.NewStart, hunk.NewLines) + " @@";
}

/// <summary>The wire name for a visibility. These are the three values the API accepts and reports, and the
/// same three the column stores; there is no fourth name that silently means one of these.</summary>
static string VisibilityName(ProjectVisibility visibility) => visibility switch
{
    ProjectVisibility.Public => "public",
    ProjectVisibility.Unlisted => "unlisted",
    _ => "private"
};

static bool TryReadVisibility(string? value, out ProjectVisibility visibility)
{
    switch (value)
    {
        case "public": visibility = ProjectVisibility.Public; return true;
        case "unlisted": visibility = ProjectVisibility.Unlisted; return true;
        case "private" or null or "": visibility = ProjectVisibility.Private; return true;
        default: visibility = ProjectVisibility.Private; return false;
    }
}

string AvatarUrl(HttpContext context, AvatarOwner ownerType, long ownerId) =>
    Origin(context) + "/api/graft/avatars/" + (ownerType == AvatarOwner.Project ? "project/" : "user/") +
    ownerId.ToString(System.Globalization.CultureInfo.InvariantCulture);

CommitResponse CommitResponseFor(HttpContext context, ProjectDetails project, GitCommit commit) => new(
    commit.Id, commit.Id[..8], commit.Committer.When, commit.ParentIds, commit.Title, commit.Message,
    commit.Author.Name, commit.Author.Email, commit.Author.When, commit.Committer.Name, commit.Committer.Email,
    commit.Committer.When, ProjectWebUrl(context, project) + "/-/commit/" + commit.Id);

static FileResponse FileResponseFor(GitFile file, string revision) => new(
    Path.GetFileName(file.Path), file.Path, file.Content.Length, "base64", Convert.ToBase64String(file.Content),
    Convert.ToHexStringLower(SHA256.HashData(file.Content)), revision, file.BlobId, file.CommitId, file.CommitId, file.Mode == "100755");

async Task<(AuthenticatedIdentity Identity, string Secret)?> AuthenticateBrowser(HttpContext context, bool writeError)
{
    string? secret = context.Request.Cookies[SessionCookie];
    ResolvedSession? session = secret is null ? null : await store.ResolveSessionDetailedAsync(secret, context.RequestAborted);
    if (session is not null)
    {
        // A renewed session owes the browser a refreshed cookie. The browser drops the cookie at exactly the
        // moment the server stops honouring it, so if the stored expiry moves and the cookie's does not, the
        // next request arrives anonymous against a session that is still perfectly good.
        if (session.Renewed && !context.Response.HasStarted)
            context.Response.Cookies.Append(SessionCookie, secret!, SessionCookieOptions(session.ExpiresAtUtc));
        return (session.Identity, secret!);
    }
    if (writeError) await ApiError(context, 401, "session");
    return null;
}

string Csrf(string secret) => Convert.ToHexStringLower(HMACSHA256.HashData(csrfKey, Encoding.ASCII.GetBytes(secret)));
bool VerifyCsrf(string secret, string submitted)
{
    string expected = Csrf(secret);
    return submitted.Length == expected.Length && CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(submitted), Encoding.ASCII.GetBytes(expected));
}

static CookieOptions SessionCookieOptions(DateTimeOffset? expires) => new()
{
    HttpOnly = true,
    Secure = true,
    SameSite = SameSiteMode.Strict,
    Path = "/",
    IsEssential = true,
    Expires = expires
};

static async Task<(int Page, int PerPage)?> Pagination(HttpContext context)
{
    int page = 1;
    int perPage = 20;
    string pageText = context.Request.Query["page"].ToString();
    string perPageText = context.Request.Query["per_page"].ToString();
    bool valid = (pageText.Length == 0 || int.TryParse(pageText, out page)) &&
        (perPageText.Length == 0 || int.TryParse(perPageText, out perPage)) && page >= 1 && perPage is >= 1 and <= 100;
    if (!valid) { await ApiError(context, 400, "invalid pagination"); return null; }
    return (page, perPage);
}

static void SetPagination(HttpContext context, int page, int perPage, long total)
{
    long pages = total == 0 ? 1 : (total + perPage - 1) / perPage;
    context.Response.Headers["X-Total"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
    context.Response.Headers["X-Total-Pages"] = pages.ToString(System.Globalization.CultureInfo.InvariantCulture);
    context.Response.Headers["X-Per-Page"] = perPage.ToString(System.Globalization.CultureInfo.InvariantCulture);
    context.Response.Headers["X-Page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture);
    context.Response.Headers["X-Next-Page"] = page < pages ? (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
    context.Response.Headers["X-Prev-Page"] = page > 1 ? (page - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
}

static async Task<bool> OnlyQuery(HttpContext context, params string[] allowed)
{
    if (context.Request.Query.Keys.All(key => allowed.Contains(key, StringComparer.Ordinal))) return true;
    await ApiError(context, 400, "unsupported query parameter");
    return false;
}

static async Task ApiError(HttpContext context, int status, string message)
{
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new ErrorResponse(message), ApiJson.Default.ErrorResponse, null, context.RequestAborted);
}

static string Origin(HttpContext context) => context.Request.Scheme + "://" + context.Request.Host.Value;

/// <summary>
/// Creates a directory holding state that only the service account should read. Staged upload bytes and the
/// data-protection key ring both qualify: the key ring signs antiforgery tokens, and staged bytes are another
/// account's content until they are ingested.
/// </summary>
/// <summary>Reads the avatar owner from the route, accepting only the two kinds the schema allows.</summary>
static bool TryReadAvatarOwner(HttpContext context, out AvatarOwner ownerType, out long ownerId)
{
    ownerType = AvatarOwner.User;
    ownerId = 0;
    string value = context.Request.RouteValues["ownerType"]?.ToString() ?? "";
    if (value == "project") ownerType = AvatarOwner.Project;
    else if (value != "user") return false;
    return long.TryParse(context.Request.RouteValues["ownerId"]?.ToString(),
        System.Globalization.CultureInfo.InvariantCulture, out ownerId) && ownerId > 0;
}

static void CreatePrivateDirectory(string path)
{
    Directory.CreateDirectory(path);
    if (OperatingSystem.IsWindows()) return;
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}

static void NotifySystemd(string message)
{
    if (!OperatingSystem.IsLinux()) return;
    string? path = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
    if (string.IsNullOrEmpty(path)) return;
    if (path[0] == '@') path = "\0" + path[1..];
    using Socket socket = new(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
    socket.SendTo(Encoding.UTF8.GetBytes(message), new UnixDomainSocketEndPoint(path));
}

static string ProjectWebUrl(HttpContext context, ProjectDetails project) =>
    Origin(context) + "/" + Uri.EscapeDataString(project.Project.Namespace) + "/" + Uri.EscapeDataString(project.Project.Slug);

static X509Certificate2 LoadCertificate(Arguments settings)
{
    X509Certificate2 certificate;
    if (settings.Certificate is not null)
    {
        string? password = settings.CertificatePasswordFile is null ? null : ReadPassword(settings.CertificatePasswordFile);
        certificate = X509CertificateLoader.LoadPkcs12FromFile(settings.Certificate, password,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
    }
    else
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        X509Certificate2 generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
        if (!OperatingSystem.IsWindows()) return generated;
        using (generated)
            certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null,
                X509KeyStorageFlags.DefaultKeySet);
    }

    DateTimeOffset now = DateTimeOffset.UtcNow;
    if (!certificate.HasPrivateKey || now < certificate.NotBefore.ToUniversalTime() || now >= certificate.NotAfter.ToUniversalTime())
    {
        certificate.Dispose();
        throw new InvalidOperationException("The TLS certificate must have a private key and be currently valid.");
    }
    X509EnhancedKeyUsageExtension[] usages = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
    if (usages.Length > 0 && !usages.SelectMany(item => item.EnhancedKeyUsages.Cast<Oid>())
        .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1"))
    {
        certificate.Dispose();
        throw new InvalidOperationException("The TLS certificate enhanced key usage does not permit server authentication.");
    }
    if (settings.Origin is not null && !certificate.MatchesHostname(new Uri(settings.Origin).Host,
        allowWildcards: true, allowCommonName: false))
    {
        certificate.Dispose();
        throw new InvalidOperationException("The TLS certificate subject alternative name does not match the configured origin.");
    }
    return certificate;
}

static string ReadPassword(string path)
{
    string password = File.ReadAllText(path, new UTF8Encoding(false, true));
    if (password.EndsWith("\r\n", StringComparison.Ordinal)) password = password[..^2];
    else if (password.EndsWith('\n')) password = password[..^1];
    if (password.Contains('\r') || password.Contains('\n') || password.Contains('\0'))
        throw new InvalidDataException("The certificate password file must contain one UTF-8 line.");
    return password;
}

sealed record AuthorizedRepository(
    ProjectInfo Project,
    /// <summary>Null when the caller presented no credentials and the repository's visibility let them in
    /// anyway. Push never reaches this state, because visibility grants reads only.</summary>
    AuthenticatedIdentity? Identity,
    PermissionDecision Permission,
    RepositoryEndpoint Endpoint);

sealed record ApiProject(ProjectDetails Details, AuthenticatedIdentity? Identity, RepositoryEndpoint Endpoint);

sealed class RepositoryEndpoint(Repository repository)
{
    public Repository Repository { get; } = repository;
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

/// <summary>
/// A fixed-window counter guarding an unauthenticated route. The limit is per server and per minute, not per
/// caller: an attacker chooses their own source address, so counting per address would be counting something
/// they control.
/// </summary>
sealed class RequestThrottle(int perMinute)
{
    private readonly object gate = new();
    private DateTimeOffset window;
    private int attempts;

    public bool TryEnter(DateTimeOffset now)
    {
        lock (gate)
        {
            if (now >= window.AddMinutes(1)) { window = now; attempts = 0; }
            return ++attempts <= perMinute;
        }
    }
}

sealed record Arguments(string Database, string Repositories, IPAddress ListenAddress, int ListenPort, string? Origin,
    string? Certificate, string? CertificatePasswordFile, bool PublicBrowsing, bool PushToCreate, int SessionMinutes)
{
    public static bool TryParse(string[] values, out Arguments? result)
    {
        result = null;
        if (values.Length is < 4 or > 18 || values.Length % 2 != 0) return false;
        string? database = null;
        string? repositories = null;
        string? listen = null;
        string? origin = null;
        string? certificate = null;
        string? certificatePasswordFile = null;
        string? publicBrowsing = null;
        string? pushToCreate = null;
        string? sessionMinutes = null;
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int index = 0; index < values.Length; index += 2)
        {
            if (!seen.Add(values[index])) return false;
            if (values[index] == "--database") database = values[index + 1];
            else if (values[index] == "--repositories") repositories = values[index + 1];
            else if (values[index] == "--listen") listen = values[index + 1];
            else if (values[index] == "--origin") origin = values[index + 1];
            else if (values[index] == "--certificate") certificate = values[index + 1];
            else if (values[index] == "--certificate-password-file") certificatePasswordFile = values[index + 1];
            else if (values[index] == "--public-browsing") publicBrowsing = values[index + 1];
            else if (values[index] == "--push-to-create") pushToCreate = values[index + 1];
            else if (values[index] == "--session-minutes") sessionMinutes = values[index + 1];
            else return false;
        }
        if (string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(repositories)) return false;
        // Takes a value rather than being a bare switch, so the grammar stays one option per pair, and so an
        // operator can state the closed answer explicitly in a unit file or container command.
        if (publicBrowsing is not null and not "true" and not "false") return false;
        if (pushToCreate is not null and not "true" and not "false") return false;
        // How long a signed-in browser stays signed in without being used. Bounded at both ends: below five
        // minutes nobody could get anything done, and the ceiling is the absolute lifetime a session can
        // reach however much it is used, so a larger number here would be a setting that did nothing.
        int window = 720;
        if (sessionMinutes is not null && (!int.TryParse(sessionMinutes, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out window) ||
            window is < 5 or > 43200))
            return false;
        IPAddress address = IPAddress.Loopback;
        int port = 0;
        if (listen is not null)
        {
            if (!Uri.TryCreate(listen, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps ||
                uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
                !IPAddress.TryParse(uri.Host, out IPAddress? parsedAddress) || uri.Port is < 1 or > 65535)
                return false;
            address = parsedAddress;
            port = uri.Port;
        }
        if (origin is not null && (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? originUri) ||
            originUri.Scheme != Uri.UriSchemeHttps || originUri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(originUri.UserInfo) || !string.IsNullOrEmpty(originUri.Query) ||
            !string.IsNullOrEmpty(originUri.Fragment)))
            return false;
        if (certificatePasswordFile is not null && certificate is null) return false;
        if (!IPAddress.IsLoopback(address) && certificate is null) return false;
        if (!IPAddress.IsLoopback(address) && origin is null) return false;
        if (certificate is not null && (string.IsNullOrWhiteSpace(certificate) || !File.Exists(certificate))) return false;
        if (certificatePasswordFile is not null && (string.IsNullOrWhiteSpace(certificatePasswordFile) ||
            !File.Exists(certificatePasswordFile))) return false;
        result = new(Path.GetFullPath(database), Path.GetFullPath(repositories), address, port,
            origin is null ? null : origin.TrimEnd('/'), certificate is null ? null : Path.GetFullPath(certificate),
            certificatePasswordFile is null ? null : Path.GetFullPath(certificatePasswordFile),
            publicBrowsing == "true", pushToCreate == "true", window);
        return true;
    }
}
