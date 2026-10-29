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
using Graft.Mcp;
using Graft.Operations;
using Graft.Server;
using Graft.Storage;
using DnaX.Uploads;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using ModelContextProtocol.Server;

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
    Console.Error.WriteLine("Usage: Graft.Server --database <path> --repositories <directory> [--listen <https URI>] [--origin <https origin>] [--certificate <PKCS#12 path>] [--certificate-password-file <UTF-8 path>] [--public-browsing true|false] [--push-to-create true|false] [--session-minutes <5-43200>] [--mcp true|false] [--pages-origin <https origin>] [--recoverable-tokens true|false] [--webhooks true|false].");
    Console.Error.WriteLine("       Graft.Server --licences  prints the third-party notices this build redistributes.");
    return 2;
}
Arguments settings = parsedSettings!;

Directory.CreateDirectory(Path.GetTempPath());
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

// Secrets of tokens their owner has just created and not yet copied.
//
// A token's secret exists exactly once: the database holds only a digest, so nothing can produce it again
// afterwards. It used to travel in the creation response and be written into the page so it could be read
// and copied, which put it in the DOM, in a screenshot, and in anything that walks the document.
//
// Instead it waits here, in memory and never on disk, for one read and a few minutes. A token nobody
// copies therefore never reaches a browser at all, and one that is copied goes to the clipboard without
// ever being rendered. Lost on restart by design: a secret that survived a restart would be a secret store,
// and Graft is deliberately not one.
ConcurrentDictionary<string, PendingSecret> pendingSecrets = new(StringComparer.Ordinal);
TimeSpan pendingSecretWindow = TimeSpan.FromMinutes(5);
// The forge's own Model Context Protocol endpoint. Mounted only when the operator asked for it, and then
// governed by the administrator's stored switches, which the middleware in front of it reads per request.
const string McpPath = "/mcp";
// Where a project answers to its instance-wide slug, with no owner in the path. One segment, so the Git
// route template already matches `/projects/<slug>.git/...` and only the lookup inside it differs.
const string GlobalPrefix = "/projects";
const string GlobalNamespace = "projects";
const string McpSettingsItem = "graft.mcp.settings";
const string McpWriteRemedy = "An administrator can allow writes under Administration, Agent access.";
McpEndpoint mcpEndpoint = new();

// Browser uploads are mounted under Graft's own API namespace, never GitLab's. GitLab owns "/uploads/..."
// and "/api/v4/projects/:id/uploads", so squatting either would block later compatibility work. Unmatched
// paths beneath "/api/" already return 404 rather than the SPA shell.
const string UploadPrefix = "/api/graft/uploads";

// The email domain a server-authored merge commit is attributed to. Graft holds no email addresses, and a
// commit needs one, so this says plainly where the identity came from rather than inventing a plausible
// address that would bounce. A `.invalid` domain is reserved for exactly this by RFC 2606.
const string MergeAuthorHost = "graft.invalid";
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
if (settings.Mcp)
{
    // The same tools the launched Graft.Mcp offers, served from inside the forge. They still reach Graft
    // through the HTTP API with the caller's own token, so what an agent can do here is exactly what that
    // token could do with curl; hosting them in-process changes where the socket goes, not who decides.
    //
    // WithTools<T> and never the assembly-scanning overload. Scanning is trim-unsafe and its failure mode is
    // a server that starts cleanly and advertises no tools at all.
    builder.Services.AddSingleton(mcpEndpoint);
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped(provider =>
    {
        // Per request, not per process. Each caller presents their own token and the administrator's live
        // setting decides whether writes are permitted, both of which the middleware in front of the endpoint
        // has already placed on the request by the time a tool is constructed.
        HttpContext context = provider.GetRequiredService<IHttpContextAccessor>().HttpContext
            ?? throw new InvalidOperationException("Graft tools are only constructed for a request.");
        McpSettings live = context.Items[McpSettingsItem] as McpSettings ?? McpSettings.Default with { AllowWrites = false };
        return new GraftClient(mcpEndpoint.Connection, McpToken(context.Request), live.AllowWrites);
    });
    builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "graft", Version = "1.0.0" };
        // Carried in the handshake, so a client knows how this forge works before it calls anything, and
        // without spending a tool call to find out. The longer material is behind graft_help.
        options.ServerInstructions = GraftGuide.Instructions;
    })
        .WithTools<GraftTools>()
        .WithHttpTransport(http => http.Stateless = true);
}
builder.Services.AddDnaXUploads(options =>
{
    options.Root = uploadRoot;
    options.MaximumReservedBytes = UploadLimits.ReservedBytes;
    options.MaximumSessions = UploadLimits.Sessions;
    options.Profiles[UploadLimits.ArchiveProfile] = UploadLimits.Archive;
    options.Profiles[UploadLimits.AvatarProfile] = UploadLimits.Avatar;
    options.Profiles[UploadLimits.RepositoryFileProfile] = UploadLimits.RepositoryFile;
    options.Profiles[UploadLimits.ReleaseAssetProfile] = UploadLimits.ReleaseAsset;
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(settings.ListenAddress, settings.ListenPort, listen => listen.UseHttps(certificate));
    options.Limits.MaxRequestBodySize = 16 * 1024 * 1024;
    // Raised from 16: an upload holds its connection for the length of the transfer, so the previous ceiling
    // let a few concurrent uploads starve Git and API traffic. Still bounded for low-resource hosts.
    options.Limits.MaxConcurrentConnections = 64;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
});

await using WebApplication app = builder.Build();

// How a recoverable token's secret is kept, when an operator has asked for that. The key ring already
// exists for antiforgery and lives beside the database in the state directory, which bounds what this
// protects against: a stolen database alone, or a dump of one, rather than a stolen machine. The honest
// framing is in ADR 0012, and the switch is off unless somebody turned it on.
IDataProtector? tokenProtector = settings.RecoverableTokens
    ? app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("graft.token.recovery.v1")
    : null;

// Outbound notification, when the operator has allowed it. Null is not "off for now": with no sender,
// nothing anywhere queues a delivery, and the routes that manage hooks are absent rather than refusing.
await using WebhookSender? webhooks = settings.Webhooks ? new WebhookSender(store) : null;
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    bool uploading = context.Request.Path.StartsWithSegments(UploadPrefix);
    bool gitTransfer = context.Request.Path.Value?.EndsWith(".git/git-receive-pack", StringComparison.Ordinal) == true ||
        context.Request.Path.Value?.EndsWith(".git/git-upload-pack", StringComparison.Ordinal) == true;
    bool archiveDownload = context.Request.Path.StartsWithSegments("/api") &&
        context.Request.Path.Value!.Contains("/archive", StringComparison.Ordinal);
    bool largeFile = context.Request.Path.Value?.Contains(".git/info/lfs/objects/", StringComparison.Ordinal) == true ||
        context.Request.Path.StartsWithSegments("/v2") ||
        (context.Request.Path.Value?.Contains("/packages/", StringComparison.Ordinal) == true && context.Request.Method is "PUT" or "POST");
    if (context.Request.Path.Value?.EndsWith(".git/git-receive-pack", StringComparison.Ordinal) == true || largeFile)
    {
        IHttpMaxRequestBodySizeFeature? bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false }) bodySize.MaxRequestBodySize = Limits.RequestBytes;
    }
    gitTransfer |= largeFile;
    if (uploading)
    {
        IHttpMaxRequestBodySizeFeature? bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false }) bodySize.MaxRequestBodySize = UploadLimits.MaximumRequestBytes;
    }
    using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    deadline.CancelAfter(gitTransfer || archiveDownload ? TimeSpan.FromHours(2) :
        uploading ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(30));
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
    if (browser is null) response = new(false, null, false, "", 0, null, settings.PublicBrowsing, settings.Mcp, settings.Webhooks);
    else
    {
        long userId = browser.Value.Identity.UserId;
        string? avatarUrl = await store.HasAvatarAsync(AvatarOwner.User, userId, context.RequestAborted)
            ? AvatarUrl(context, AvatarOwner.User, userId)
            : null;
        response = new(true, browser.Value.Identity.Username, browser.Value.Identity.IsAdministrator,
            Csrf(browser.Value.Secret), userId, avatarUrl, settings.PublicBrowsing, settings.Mcp, settings.Webhooks);
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
// How this forge works, for an agent that does not speak the Model Context Protocol. `llms.txt` is the
// convention for exactly this: one plain document at a predictable address that a crawler or an assistant
// can read without discovering an API first.
//
// Unauthenticated, like the licences, because a guide nobody can read without credentials helps nobody, and
// it describes how Graft works rather than what this instance holds. Agents reaching Graft through MCP get
// the same text in their handshake and need not fetch this at all.
app.MapGet("/llms.txt", async context =>
{
    if (!await OnlyQuery(context)) return;
    context.Response.ContentType = "text/plain; charset=utf-8";
    await context.Response.WriteAsync(GraftGuide.Full(), context.RequestAborted);
});

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

// The administrator's switches for the forge's own Model Context Protocol endpoint. Absent, not refused, on a
// server the operator started without the endpoint: there is nothing for the switches to govern, and the
// client hides the page on the same signal, so a 404 here is the honest answer rather than an error.
app.MapGet("/api/graft/admin/mcp", async context =>
{
    if (!settings.Mcp) { await ApiError(context, 404, "the MCP endpoint is not enabled on this server"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!browser.Value.Identity.IsAdministrator) { await ApiError(context, 403, "administrator"); return; }
    if (!await OnlyQuery(context)) return;
    McpSettings current = await store.GetMcpSettingsAsync(context.RequestAborted);
    await context.Response.WriteAsJsonAsync(McpResponse(current), ApiJson.Default.McpSettingsResponse, null, context.RequestAborted);
});

app.MapPost("/api/graft/admin/mcp", async context =>
{
    if (!settings.Mcp) { await ApiError(context, 404, "the MCP endpoint is not enabled on this server"); return; }
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!browser.Value.Identity.IsAdministrator) { await ApiError(context, 403, "administrator"); return; }
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    McpSettingsRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.McpSettingsRequest, context.RequestAborted);
    if (request is null) { await ApiError(context, 400, "settings"); return; }
    McpSettings updated = new(request.Enabled, request.AllowWrites, request.AllowAnonymous);
    try { await store.SetMcpSettingsAsync(browser.Value.Identity, updated, context.RequestAborted); }
    catch (UnauthorizedAccessException) { await ApiError(context, 403, "administrator"); return; }
    await context.Response.WriteAsJsonAsync(McpResponse(updated), ApiJson.Default.McpSettingsResponse, null, context.RequestAborted);
});

// The audit trail. Every sensitive act has written a row since the first migration; until now nothing could
// read one back without opening the database, which made a trail nobody could consult. Administrators only,
// because it spans accounts, tokens and every project at once and cannot be divided by project.
app.MapGet("/api/graft/admin/audit", async context =>
{
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!browser.Value.Identity.IsAdministrator) { await ApiError(context, 403, "administrator"); return; }
    if (!await OnlyQuery(context, "page", "per_page", "action", "target_type")) return;
    (int Page, int PerPage)? pagination = await Pagination(context);
    if (pagination is null) return;
    (int page, int perPage) = pagination.Value;
    string? action = context.Request.Query["action"].ToString();
    string? targetType = context.Request.Query["target_type"].ToString();
    if (action.Length == 0) action = null;
    if (targetType.Length == 0) targetType = null;
    if (action?.Length > 64 || targetType?.Length > 64) { await ApiError(context, 400, "filter is too long"); return; }

    AuditPage entries;
    try
    {
        entries = await store.ListAuditEventsAsync(browser.Value.Identity, (page - 1) * perPage, perPage,
            action, targetType, context.RequestAborted);
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 403, "administrator"); return; }
    AuditEntryResponse[] response = [.. entries.Entries.Select(entry => new AuditEntryResponse(
        entry.Id, entry.ActorUserId, entry.ActorUsername, entry.Action, entry.TargetType, entry.TargetId,
        entry.Detail, entry.CreatedAtUtc))];
    SetPagination(context, page, perPage, entries.Total);
    await context.Response.WriteAsJsonAsync(new AuditPageResponse(response, entries.Total, page, perPage),
        ApiJson.Default.AuditPageResponse, null, context.RequestAborted);
});

app.MapGet("/api/tokens", async context =>
{
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!await OnlyQuery(context)) return;
    IReadOnlyList<PersonalAccessToken> tokens = await store.ListActiveTokensAsync(browser.Value.Identity, context.RequestAborted);
    TokenResponse[] response = tokens.Select(token => new TokenResponse(
        token.Id, token.Name, TokenScopeNames(token.Scopes), token.ExpiresAtUtc, token.CreatedAtUtc,
        token.Recoverable)).ToArray();
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
            DateTimeOffset.UtcNow.AddDays(request.ExpiresInDays), context.RequestAborted,
            tokenProtector is null ? null : tokenProtector.Protect);
        // The secret stays here rather than travelling with the response, and the caller is handed a
        // one-time claim on it. See the note beside pendingSecrets for why.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (KeyValuePair<string, PendingSecret> stale in pendingSecrets)
            if (stale.Value.ExpiresAtUtc <= now) pendingSecrets.TryRemove(stale.Key, out _);
        // A ceiling as well as an expiry, so a script that creates tokens in a loop cannot grow this
        // without bound before the first of them expires.
        if (pendingSecrets.Count >= 512) { await ApiError(context, 429, "too many uncollected tokens; wait a moment"); return; }

        string reveal = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        pendingSecrets[reveal] = new(browser.Value.Identity.UserId, token.Secret, now.Add(pendingSecretWindow));

        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(new TokenCreatedResponse(
            token.Id, request.Name.Trim(), reveal, TokenScopeNames(token.Scopes), token.ExpiresAtUtc),
            ApiJson.Default.TokenCreatedResponse, null, context.RequestAborted);
    }
    catch (ArgumentException) { await ApiError(context, 400, "token"); }
});

// Claims the secret of a token just created, once. The handle is removed before the secret is written, so
// a second attempt finds nothing even if the first never reached the caller; losing a secret to a dropped
// response is the safe failure, and creating another token costs nothing.
app.MapPost("/api/tokens/reveal", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    TokenRevealRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.TokenRevealRequest, context.RequestAborted);
    if (request?.Reveal is not { Length: > 0 } handle) { await ApiError(context, 400, "reveal"); return; }

    // Removed first, so the single use is settled before anything can go wrong with the response.
    if (!pendingSecrets.TryRemove(handle, out PendingSecret? pending))
    { await ApiError(context, 404, "that token's secret is no longer available"); return; }

    // Expiry and ownership are checked after removal, so a handle probed by the wrong account is spent
    // rather than left for its owner to find missing later.
    if (pending.ExpiresAtUtc <= DateTimeOffset.UtcNow || pending.UserId != browser.Value.Identity.UserId)
    { await ApiError(context, 404, "that token's secret is no longer available"); return; }

    await context.Response.WriteAsJsonAsync(new TokenRevealResponse(pending.Secret),
        ApiJson.Default.TokenRevealResponse, null, context.RequestAborted);
});

// Reads back an existing token's secret, where the operator has asked for recoverable tokens and this
// token was issued while that was so. Off by default, and a token issued with it off can never be read
// here however the setting changes afterwards.
//
// The account password is required on top of the session. Without it, anyone holding a signed-in browser
// could harvest every token the account has, silently; with it, the worst a stolen session does is what it
// always did, which is create new tokens that show up in the list and the audit log.
app.MapPost("/api/tokens/{tokenId:long}/secret", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    TokenSecretRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.TokenSecretRequest, context.RequestAborted);
    if (request?.Password is not { Length: > 0 }) { await ApiError(context, 400, "password"); return; }

    // Rate limited on the same throttle as signing in, because this route takes a password and would
    // otherwise be a quieter place to guess one.
    if (!loginThrottle.TryEnter(DateTimeOffset.UtcNow)) { await ApiError(context, 429, "too many attempts"); return; }

    if (tokenProtector is null)
    { await ApiError(context, 404, "this server does not keep token secrets"); return; }

    long tokenId = Convert.ToInt64(context.Request.RouteValues["tokenId"], System.Globalization.CultureInfo.InvariantCulture);
    string? cipher;
    try { cipher = await store.ReadTokenCipherAsync(browser.Value.Identity, tokenId, request.Password, context.RequestAborted); }
    catch (UnauthorizedAccessException) { await ApiError(context, 403, "the password was not accepted"); return; }
    if (cipher is null) { await ApiError(context, 404, "that token's secret was not kept"); return; }

    string secret;
    // A cipher this server cannot unprotect is one encrypted under a key ring it no longer has, which is
    // the same outcome as never having kept it: the token still works, it just cannot be read again.
    try { secret = tokenProtector.Unprotect(cipher); }
    catch (CryptographicException) { await ApiError(context, 404, "that token's secret can no longer be read"); return; }

    await context.Response.WriteAsJsonAsync(new TokenRevealResponse(secret),
        ApiJson.Default.TokenRevealResponse, null, context.RequestAborted);
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
// A project's name and description, after it was created. Until this existed a description could only be
// set at creation and never corrected, which made it the one piece of project text nobody could maintain.
app.MapPost("/api/projects/{projectId:long}/details", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    ProjectDetailsRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.ProjectDetailsRequest, context.RequestAborted);
    if (request is null) { await ApiError(context, 400, "details"); return; }

    long projectId = Convert.ToInt64(context.Request.RouteValues["projectId"], System.Globalization.CultureInfo.InvariantCulture);
    try
    {
        await store.SetProjectDetailsAsync(browser.Value.Identity, projectId, request.Name, request.Description, context.RequestAborted);
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "project"); return; }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); return; }
    context.Response.StatusCode = StatusCodes.Status204NoContent;
});

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
// Deleting a project. Irreversible, and the one project operation a Maintainer cannot do: the store
// requires the owner or an administrator.
//
// The browser asks for the path to be typed back. The API does not, because there the caller is a program
// that already had to name the project in the URL, and a confirmation a script fills in automatically is
// not a confirmation. See ADR 0013.
app.MapDelete("/api/projects/{projectId:long}", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    ProjectDeleteRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.ProjectDeleteRequest, context.RequestAborted);

    long projectId = Convert.ToInt64(context.Request.RouteValues["projectId"], System.Globalization.CultureInfo.InvariantCulture);
    ProjectDetails? details = await store.GetReadyProjectAsync(projectId, context.RequestAborted);
    if (details is null) { await ApiError(context, 404, "project"); return; }
    string expected = details.Project.Namespace + "/" + details.Project.Slug;
    if (!string.Equals(request?.Path?.Trim(), expected, StringComparison.Ordinal))
    { await ApiError(context, 400, "type " + expected + " to confirm"); return; }
    await DeleteProject(context, browser.Value.Identity, projectId);
});

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
            project.Project.Visibility == ProjectVisibility.Public,
            project.Project.GlobalSlug));
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
        // A repository with no branches is not a missing tree, and answering 404 made a project somebody
        // had just created look like one they could not see: the browser reports a refusal as a permission
        // problem, because for every other 404 on this route that is what it is. An empty repository is a
        // state the page can render, so it is reported as one.
        if (repository.Refs.Count == 0)
        {
            ProjectResponse bare = await ProjectResponseForLocked(context, details, repository);
            PermissionDecision bareManage = await DecideFor(context, browser?.Identity, projectId, ProjectAction.ManageProject);
            PermissionDecision barePush = await DecideFor(context, browser?.Identity, projectId, ProjectAction.PushRepository);
            PermissionDecision bareParticipate = await DecideFor(context, browser?.Identity, projectId, ProjectAction.WriteCollaboration);
            await context.Response.WriteAsJsonAsync(new RepositoryPageResponse(bare, bareManage.Allowed,
                barePush.Allowed, [], [], [], null, null, details.Project.PagePublished, settings.PublicBrowsing,
                bareParticipate.Allowed,
                bareManage.Allowed ? await store.GetApprovalsRequiredAsync(projectId, context.RequestAborted) : 0,
                bareManage.Allowed && await store.GetRequireCodeOwnerApprovalAsync(projectId, context.RequestAborted)),
                ApiJson.Default.RepositoryPageResponse, null, context.RequestAborted);
            return;
        }

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
        // Reported separately from pushing because it is a weaker permission: a Reporter may comment and
        // open an issue without being able to push, so a page that gated its comment box on CanPush would
        // hide it from exactly the people it is for.
        PermissionDecision participate = await DecideFor(context, browser?.Identity, projectId, ProjectAction.WriteCollaboration);
        // Only for somebody who can change it. A reader has no form to fill in, and the rule is of no
        // interest to them until they open a proposal, where it is reported on the proposal itself.
        int approvalsRequired = manage.Allowed ? await store.GetApprovalsRequiredAsync(projectId, context.RequestAborted) : 0;
        RepositoryPageResponse response = new(project, manage.Allowed, push.Allowed, branches, commits, entries,
            fileResponse, readme, details.Project.PagePublished, settings.PublicBrowsing, participate.Allowed,
            approvalsRequired, manage.Allowed && await store.GetRequireCodeOwnerApprovalAsync(projectId, context.RequestAborted));
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

// The published project page: the curated surface, for anybody the page is published to. A separate read
// path from the repository view, never a filter over it, because a filter is where a leak hides: a pack is
// reachability-shaped rather than path-shaped, and a diff prints files the page never named. So this route
// reads named files and nothing that walks the graph.
app.MapGet("/api/graft/pages/{globalSlug}", async context =>
{
    if (!await OnlyQuery(context)) return;
    ProjectInfo? found = await store.FindReadyProjectByGlobalSlugAsync(
        context.Request.RouteValues["globalSlug"]?.ToString() ?? "", context.RequestAborted);
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: false);
    if (found is null || !(await DecideFor(context, browser?.Identity, found.Id, ProjectAction.ReadPage)).Allowed)
    { await ApiError(context, 404, "page"); return; }

    ProjectDetails details = (await store.GetReadyProjectAsync(found.Id, context.RequestAborted))!;
    await WithRepository(EndpointFor(found), context, async repository =>
    {
        PageConfigurationResult config = PageConfiguration.Read(ReadRepositoryFile(repository, PageConfiguration.Path));
        // An invalid config means nobody has said what may be served, so the page is not served at all
        // rather than served with defaults. The owner finds out from the project's own settings tab.
        if (!config.Ok) { await ApiError(context, 404, "page"); return; }
        PageSettings page = config.Settings!;

        // Only the readme the config named, and only if the allowlist admits it. A project's actual
        // README.md is not offered by default: on a page, nothing is offered by default.
        ReadmeResponse? readme = null;
        if (page.Readme is not null)
        {
            GitFile? file;
            try { file = RepositoryView.File(repository, "HEAD", page.Readme); }
            catch (GitInputException) { file = null; }
            if (file is not null && file.Content.Length > 0 && file.Content.Length <= MarkdownContent.MaximumBytes)
            {
                string? source = null;
                try { source = new UTF8Encoding(false, true).GetString(file.Content); }
                catch (DecoderFallbackException) { }
                if (source is not null)
                {
                    string baseUrl = Origin(context) + "/api/graft/projects/" +
                        details.Project.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/image";
                    RenderedMarkdown rendered = MarkdownContent.Render(source, path =>
                        baseUrl + "?ref=HEAD&path=" + Uri.EscapeDataString(path));
                    readme = new(file.Path, rendered.Html, rendered.HasDiagrams);
                }
            }
        }

        // The listing is produced by asking the allowlist about each path the repository actually holds,
        // rather than by expanding patterns, so a pattern can never name something that is not there and a
        // file can never appear that the matcher would refuse.
        List<string> files = [];
        CollectPagePaths(repository, page, "", files);
        files.Sort(StringComparer.Ordinal);

        ProjectPageResponse response = new(
            page.Title is { Length: > 0 } ? page.Title : details.Project.Name,
            page.Description is { Length: > 0 } ? page.Description : details.Description,
            details.Project.GlobalSlug,
            readme,
            files,
            page.Site is null || settings.PagesOrigin is null
                ? null
                : settings.PagesOrigin.TrimEnd('/') + "/" + Uri.EscapeDataString(details.Project.GlobalSlug) + "/");
        await context.Response.WriteAsJsonAsync(response, ApiJson.Default.ProjectPageResponse, null, context.RequestAborted);
    });
});

// One file a published page offers. Named paths only: the allowlist is asked before the object store is,
// so a path the config did not admit is indistinguishable from one that does not exist.
app.MapGet("/api/graft/pages/{globalSlug}/files/{**filePath}", async context =>
{
    if (!await OnlyQuery(context)) return;
    ProjectInfo? found = await store.FindReadyProjectByGlobalSlugAsync(
        context.Request.RouteValues["globalSlug"]?.ToString() ?? "", context.RequestAborted);
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: false);
    if (found is null || !(await DecideFor(context, browser?.Identity, found.Id, ProjectAction.ReadPage)).Allowed)
    { await ApiError(context, 404, "page"); return; }

    string filePath = context.Request.RouteValues["filePath"]?.ToString() ?? "";
    try { filePath = Uri.UnescapeDataString(filePath); }
    catch (UriFormatException) { await ApiError(context, 404, "file"); return; }

    await WithRepository(EndpointFor(found), context, async repository =>
    {
        PageConfigurationResult config = PageConfiguration.Read(ReadRepositoryFile(repository, PageConfiguration.Path));
        if (!config.Ok || !PageConfiguration.AllowsFile(config.Settings!, filePath))
        { await ApiError(context, 404, "file"); return; }

        byte[]? content = ReadRepositoryFile(repository, filePath);
        if (content is null) { await ApiError(context, 404, "file"); return; }
        // Served as text or refused. A page's file list is for reading, and handing a browser an arbitrary
        // content type from somebody else's repository is the thing the whole surface is built to avoid.
        string? text = null;
        try { text = new UTF8Encoding(false, true).GetString(content); }
        catch (DecoderFallbackException) { }
        if (text is null) { await ApiError(context, 415, "that file is not text"); return; }

        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(text, context.RequestAborted);
    });
});

// The same page, reached by the project's instance-wide slug. It lives under Graft's own prefix rather than
// beside the namespaced route because a literal segment there would shadow any account of that name, and
// account names are not a list this server gets to shorten.
app.MapGet("/api/graft/slugs/{globalSlug}", async context => await browseProject(context,
    await store.FindReadyProjectDetailsByGlobalSlugAsync(
        context.Request.RouteValues["globalSlug"]?.ToString() ?? "",
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

// Creating and deleting a branch without a Git client. Both need the permission a push needs, because
// both are what a push would otherwise have done.
//
// Neither writes an object: creating points a new reference at a commit the repository already holds, and
// deleting removes a reference while the objects beneath it stay. That is Git's own behaviour, and it is
// why deleting a branch cannot lose work another branch can still reach.
app.MapPost("/api/v4/projects/{projectId}/repository/branches", async context =>
{
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.PushRepository);
    if (project is null) return;
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    BranchCreateRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.BranchCreateRequest, context.RequestAborted);
    if (request?.Branch is not { Length: > 0 }) { await ApiError(context, 400, "branch is required"); return; }
    await CreateBranch(context, project.Details, project.Identity, request.Branch, request.Ref);
});

app.MapDelete("/api/v4/projects/{projectId}/repository/branches/{**branch}", async context =>
{
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.PushRepository);
    if (project is null) return;
    if (!await OnlyQuery(context)) return;
    await DeleteBranch(context, project.Details, project.Identity, context.Request.RouteValues["branch"]?.ToString() ?? "");
});

app.MapPost("/api/projects/{projectId:long}/branches", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null) return;
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    BranchCreateRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.BranchCreateRequest, context.RequestAborted);
    if (request?.Branch is not { Length: > 0 }) { await ApiError(context, 400, "branch is required"); return; }
    await CreateBranch(context, scope.Value.Details, scope.Value.Identity, request.Branch, request.Ref);
});

app.MapDelete("/api/projects/{projectId:long}/branches/{**branch}", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null) return;
    await DeleteBranch(context, scope.Value.Details, scope.Value.Identity, context.Request.RouteValues["branch"]?.ToString() ?? "");
});

// One file, as bytes.
//
// Always served as an attachment of unknown type, never as the type the file looks like. Repository content
// is somebody else's HTML and script, and this origin holds the session cookie and the API; that is the
// whole reason project sites live on a hostname of their own. A raw route that guessed a content type would
// undo that in one line. The browser therefore downloads it and never renders it, and a command-line client
// still prints it to the terminal exactly as before.
// The path is one URL-encoded segment here, not a catch-all, because a catch-all cannot be followed by
// anything and `/raw` has to come after it. That is GitLab's own documented form for this route.
app.MapGet("/api/v4/projects/{projectId}/repository/files/{filePath}/raw", async context =>
{
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.BrowseRepository);
    if (project is null) return;
    if (!await OnlyQuery(context, "ref")) return;
    string revision = context.Request.Query["ref"].ToString();
    if (revision.Length == 0) revision = "HEAD";
    string filePath;
    try { filePath = Uri.UnescapeDataString(context.Request.RouteValues["filePath"]?.ToString() ?? ""); }
    catch (UriFormatException) { await ApiError(context, 400, "file path"); return; }

    await WithRepository(project.Endpoint, context, async repository =>
    {
        GitFile? file = RepositoryView.File(repository, revision, filePath);
        if (file is null) { await ApiError(context, 404, "404 File Not Found"); return; }
        if (await WriteLargeFile(context, project.Details.Project, file)) return;
        await WriteDownload(context, file.Content, Path.GetFileName(file.Path), "application/octet-stream");
    });
});

// The whole tree at a revision, as one file. Two formats: a gzipped tar and a zip, chosen by the extension
// on the route, because a client that asked for one and received the other would have no way to tell until
// it failed to open it.
// The same file, for a browser. The GitLab-shaped route above answers a token and nothing else, so a
// signed-in reader could not use it. Here the path is the catch-all because nothing follows it.
app.MapGet("/api/projects/{projectId:long}/raw/{**filePath}", async context =>
{
    var scope = await BrowserCollaboration(context, write: false);
    if (scope is null) return;
    PermissionDecision browse = await DecideFor(context, scope.Value.Identity, scope.Value.Details.Project.Id,
        ProjectAction.BrowseRepository);
    if (!browse.Allowed) { await ApiError(context, 404, "project"); return; }
    if (!await OnlyQuery(context, "ref")) return;
    string revision = context.Request.Query["ref"].ToString();
    if (revision.Length == 0) revision = "HEAD";
    string filePath;
    try { filePath = Uri.UnescapeDataString(context.Request.RouteValues["filePath"]?.ToString() ?? ""); }
    catch (UriFormatException) { await ApiError(context, 400, "file path"); return; }

    await WithRepository(EndpointFor(scope.Value.Details.Project), context, async repository =>
    {
        GitFile? file = RepositoryView.File(repository, revision, filePath);
        if (file is null) { await ApiError(context, 404, "404 File Not Found"); return; }
        if (await WriteLargeFile(context, scope.Value.Details.Project, file)) return;
        await WriteDownload(context, file.Content, Path.GetFileName(file.Path), "application/octet-stream");
    });
});

// A pointer in the tree stands for a large file kept beside the repository. The raw routes serve the file
// it stands for when the repository holds it, which is what a reader following "download" expects, and the
// pointer itself otherwise, which is at least the truth about what is there.
async Task<bool> WriteLargeFile(HttpContext context, ProjectInfo project, GitFile file)
{
    LfsPointer? pointer = LfsPointer.TryParse(file.Content);
    if (pointer is null) return false;
    LargeFileStore large = new(Path.Combine(settings.Repositories, project.StorageId));
    long? size = large.Size(pointer.Oid);
    if (size is null) return false;
    context.Response.ContentType = "application/octet-stream";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    string safe = new([.. Path.GetFileName(file.Path).Where(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')]);
    context.Response.Headers.ContentDisposition = "attachment; filename=\"" + (safe.Length is 0 or > 100 ? "download" : safe) + "\"";
    context.Response.Headers["X-Graft-Lfs-Oid"] = pointer.Oid;
    context.Response.ContentLength = size;
    using Stream content = large.OpenRead(pointer.Oid);
    await content.CopyToAsync(context.Response.Body, context.RequestAborted);
    return true;
}

// One route per extension rather than one with the extension as a parameter. A parameter inside a segment
// matches by backtracking over a literal, which is exactly the kind of route that works in testing and
// surprises somebody later; three literals cannot.
foreach ((string suffix, string archiveFormat) in new[] { ("", "tar.gz"), (".tar.gz", "tar.gz"), (".zip", "zip") })
{
    string chosen = archiveFormat;
    app.MapGet("/api/v4/projects/{projectId}/repository/archive" + suffix, async context =>
    {
        ApiProject? project = await AuthorizeApiProject(context, ProjectAction.BrowseRepository);
        if (project is null) return;
        if (!await OnlyQuery(context, "sha")) return;
        string revision = context.Request.Query["sha"].ToString();
        if (revision.Length == 0) revision = "HEAD";
        await WriteArchive(context, project.Details, project.Endpoint, revision, chosen);
    });

    app.MapGet("/api/projects/{projectId:long}/archive" + suffix, async context =>
    {
        var scope = await BrowserCollaboration(context, write: false);
        if (scope is null) return;
        PermissionDecision browse = await DecideFor(context, scope.Value.Identity, scope.Value.Details.Project.Id,
            ProjectAction.BrowseRepository);
        if (!browse.Allowed) { await ApiError(context, 404, "project"); return; }
        string revision = context.Request.Query["sha"].ToString();
        if (revision.Length == 0) revision = "HEAD";
        await WriteArchive(context, scope.Value.Details, EndpointFor(scope.Value.Details.Project), revision, chosen);
    });
}

app.MapGet("/api/v4/projects/{projectId}/repository/tags", async context =>
{
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.BrowseRepository);
    if (project is null) return;
    if (!await OnlyQuery(context, "page", "per_page")) return;
    (int Page, int PerPage)? pagination = await Pagination(context);
    if (pagination is null) return;
    (int page, int perPage) = pagination.Value;
    await WithRepository(project.Endpoint, context, async repository =>
    {
        (string Name, string Id)[] tags = RepositoryView.Tags(repository).ToArray();
        List<TagResponse> result = [];
        foreach ((string name, string target) in tags.Skip((page - 1) * perPage).Take(perPage))
        {
            // The reference may point at an annotated tag object rather than at the commit, so the commit
            // is resolved through the name. A tag whose target has gone is skipped rather than failing the
            // listing: one unreadable tag should not hide the rest.
            string? commitId = RepositoryView.ResolveCommit(repository, name);
            if (commitId is null) continue;
            result.Add(new(name, target,
                ProjectWebUrl(context, project.Details) + "/-/tree/" + Uri.EscapeDataString(name),
                CommitResponseFor(context, project.Details, RepositoryView.ReadCommit(repository, commitId))));
        }
        SetPagination(context, page, perPage, tags.Length);
        await context.Response.WriteAsJsonAsync(result.ToArray(), ApiJson.Default.TagResponseArray, null, context.RequestAborted);
    });
});

app.MapPost("/api/v4/projects/{projectId}/repository/tags", async context =>
{
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.PushRepository);
    if (project is null) return;
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    TagCreateRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.TagCreateRequest, context.RequestAborted);
    if (request?.TagName is not { Length: > 0 }) { await ApiError(context, 400, "tag_name is required"); return; }
    await CreateTag(context, project.Details, project.Identity, request.TagName, request.Ref);
});

app.MapDelete("/api/v4/projects/{projectId}/repository/tags/{**tag}", async context =>
{
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.PushRepository);
    if (project is null) return;
    if (!await OnlyQuery(context)) return;
    await DeleteTag(context, project.Details, project.Identity, context.Request.RouteValues["tag"]?.ToString() ?? "");
});

app.MapGet("/api/projects/{projectId:long}/tags", async context =>
{
    var scope = await BrowserCollaboration(context, write: false);
    if (scope is null) return;
    await BrowserTags(context, scope.Value.Details, scope.Value.Identity);
});

app.MapPost("/api/projects/{projectId:long}/tags", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null) return;
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    TagCreateRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.TagCreateRequest, context.RequestAborted);
    if (request?.TagName is not { Length: > 0 }) { await ApiError(context, 400, "tag_name is required"); return; }
    await CreateTag(context, scope.Value.Details, scope.Value.Identity, request.TagName, request.Ref);
});

app.MapDelete("/api/projects/{projectId:long}/tags/{**tag}", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null) return;
    await DeleteTag(context, scope.Value.Details, scope.Value.Identity, context.Request.RouteValues["tag"]?.ToString() ?? "");
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

// A project's avatar, for a token. The browser sets one through the resumable upload transport, which
// carries an upload receipt rather than bytes and needs a browser antiforgery token, so no API client could
// set an avatar at all. This is the plain-HTTP half ADR 0005 describes: the image arrives as the request
// body, bounded before it is read, and is validated by exactly the same code the browser route uses.
app.MapMethods("/api/v4/projects/{projectId}/avatar", ["POST", "PUT"], async context =>
{
    (bool unresolved, AuthenticatedIdentity? caller) = await AuthenticateApiOptional(context);
    if (unresolved) return;
    if (caller is null) { await ApiError(context, 401, "401 Unauthorized"); return; }
    if (!caller.TokenScopes.HasFlag(TokenScope.Api))
    { await ApiError(context, 403, "403 Forbidden: insufficient_scope"); return; }

    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.ManageProject);
    if (project is null) return;

    // Refused on the declared length before a byte is read, so an oversized body is not streamed into
    // memory first. The read is still bounded in case no length was declared.
    if (context.Request.ContentLength > ImageContent.MaximumBytes)
    { await ApiError(context, 413, "avatar exceeds the image limit"); return; }

    byte[] bytes;
    using (MemoryStream buffer = new())
    {
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        if (buffer.Length > ImageContent.MaximumBytes)
        { await ApiError(context, 413, "avatar exceeds the image limit"); return; }
        bytes = buffer.ToArray();
    }

    ValidatedImage? image = ImageContent.Validate(bytes);
    if (image is null) { await ApiError(context, 400, "avatar must be a PNG, GIF, JPEG or WebP image"); return; }

    try { await store.SetAvatarAsync(caller, AvatarOwner.Project, project.Details.Project.Id, image, context.RequestAborted); }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "404 Project Not Found"); return; }

    ProjectDetails refreshed = (await store.GetReadyProjectAsync(project.Details.Project.Id, context.RequestAborted))!;
    await context.Response.WriteAsJsonAsync(await ProjectResponseFor(context, refreshed), ApiJson.Default.ProjectResponse, null, context.RequestAborted);
});

// Who may work on a project, for a token. GitLab shapes these as /members with an access_level; Graft has
// three named roles and reports both, so a GitLab client reads a number it understands and a person reads
// a word. Membership is a Maintainer decision, which the store enforces, not this route.
app.MapGet("/api/v4/projects/{projectId}/members", async context =>
{
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.ManageProject);
    if (project is null) return;
    if (!await OnlyQuery(context)) return;
    try
    {
        IReadOnlyList<ProjectMember> members = await store.ListProjectMembersAsync(
            project.Identity!, project.Details.Project.Id, context.RequestAborted);
        await context.Response.WriteAsJsonAsync(
            members.Select(member => new ProjectMemberResponse(member.UserId, member.Username, member.Role.ToString().ToLowerInvariant())).ToArray(),
            ApiJson.Default.ProjectMemberResponseArray, null, context.RequestAborted);
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "404 Project Not Found"); }
});

app.MapMethods("/api/v4/projects/{projectId}/members", ["POST", "PUT"], async context =>
{
    (bool unresolved, AuthenticatedIdentity? caller) = await AuthenticateApiOptional(context);
    if (unresolved) return;
    if (caller is null) { await ApiError(context, 401, "401 Unauthorized"); return; }
    if (!caller.TokenScopes.HasFlag(TokenScope.Api))
    { await ApiError(context, 403, "403 Forbidden: insufficient_scope"); return; }

    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.ManageProject);
    if (project is null) return;
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }

    MembershipSetRequest? request;
    try { request = await context.Request.ReadFromJsonAsync(ApiJson.Default.MembershipSetRequest, context.RequestAborted); }
    catch (JsonException) { await ApiError(context, 400, "invalid JSON"); return; }
    if (request is null || !Enum.TryParse(request.Role, ignoreCase: true, out ProjectRole role) || !Enum.IsDefined(role))
    { await ApiError(context, 400, "role must be reporter, developer or maintainer"); return; }

    try
    {
        UserAccount? user = await store.FindActiveUserAsync(request.Username, context.RequestAborted);
        // A username that is not there is a bad request, not a 404: the project was found, and refusing
        // with the project's own "not found" would say something untrue about the project.
        if (user is null) { await ApiError(context, 400, "no such account"); return; }
        await store.SetMembershipAsync(caller, project.Details.Project.Id, user.Id, role, context.RequestAborted);
        await context.Response.WriteAsJsonAsync(new ProjectMemberResponse(user.Id, user.Username, role.ToString().ToLowerInvariant()),
            ApiJson.Default.ProjectMemberResponse, null, context.RequestAborted);
    }
    catch (ArgumentException) { await ApiError(context, 400, "username"); }
    catch (InvalidOperationException error) { await ApiError(context, 400, error.Message); }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "404 Project Not Found"); }
});

app.MapDelete("/api/v4/projects/{projectId}/members/{username}", async context =>
{
    (bool unresolved, AuthenticatedIdentity? caller) = await AuthenticateApiOptional(context);
    if (unresolved) return;
    if (caller is null) { await ApiError(context, 401, "401 Unauthorized"); return; }
    if (!caller.TokenScopes.HasFlag(TokenScope.Api))
    { await ApiError(context, 403, "403 Forbidden: insufficient_scope"); return; }

    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.ManageProject);
    if (project is null) return;

    string username = context.Request.RouteValues["username"]?.ToString() ?? "";
    try
    {
        UserAccount? user = await store.FindActiveUserAsync(username, context.RequestAborted);
        if (user is null) { await ApiError(context, 400, "no such account"); return; }
        await store.RevokeMembershipAsync(caller, project.Details.Project.Id, user.Id, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (ArgumentException) { await ApiError(context, 400, "username"); }
    catch (InvalidOperationException error) { await ApiError(context, 400, error.Message); }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "404 Project Not Found"); }
});

// What one commit changed, for a token. The browser has had this since commits became linkable; without it
// an agent can see that a commit exists and never what it did, which is the one question worth asking
// about a commit.
app.MapGet("/api/v4/projects/{projectId}/repository/commits/{sha}/diff", async context =>
{
    // BrowseRepository, not ReadRest: a diff prints the contents of the files a commit touched, so a page
    // grant can never reach it. The same rule the browser route applies.
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.BrowseRepository);
    if (project is null) return;
    if (!await OnlyQuery(context, "parent")) return;

    string sha = context.Request.RouteValues["sha"]?.ToString() ?? "";
    // A merge has more than one parent and no single answer to "what changed", so the caller says which
    // side it wants rather than the server picking one silently.
    int parent = int.TryParse(context.Request.Query["parent"].ToString(),
        System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int requested)
        ? requested : 0;
    if (parent is < 0 or > 15) { await ApiError(context, 400, "parent must be 0 to 15"); return; }

    await WithRepository(project.Endpoint, context, async repository =>
    {
        string? commitId = Limits.IsId(sha) && repository.Objects.ContainsKey(sha) ? sha : null;
        if (commitId is null) { await ApiError(context, 404, "404 Commit Not Found"); return; }

        CommitDiff diff;
        try { diff = RepositoryDiff.ForCommit(repository, commitId, parent); }
        catch (GitInputException) { await ApiError(context, 404, "404 Commit Not Found"); return; }

        DiffFileResponse[] files = diff.Files.Select(file => new DiffFileResponse(
            file.Path, file.Status, file.OldMode, file.NewMode, file.IsBinary, file.TooLarge,
            file.Added, file.Removed,
            file.Hunks.Select(hunk => new DiffHunkResponse(HunkHeader(hunk),
                hunk.Lines.Select(line => new DiffLineResponse(
                    line.Kind.ToString(), line.Text, line.OldLine, line.NewLine, line.NoNewline)).ToArray()))
                .ToArray())).ToArray();

        CommitDiffResponse response = new(await ProjectResponseForLocked(context, project.Details, repository),
            CommitResponseFor(context, project.Details, diff.Commit),
            diff.ParentId, files, diff.Truncated, files.Sum(file => file.Added), files.Sum(file => file.Removed));
        await context.Response.WriteAsJsonAsync(response, ApiJson.Default.CommitDiffResponse, null, context.RequestAborted);
    });
});

// Writing one file, in GitLab's shape, with the content in the request rather than as an upload receipt.
// ADR 0005 put the resumable transport in the browser and plain HTTP in front of API clients; this is that
// second half for repository content, and it is what lets an agent propose a change at all.
//
// POST and PUT behave identically. GitLab distinguishes creating a file from updating one and refuses the
// wrong verb; Graft commits what it was given either way, which is recorded as a deviation rather than
// pretended away.
app.MapMethods("/api/v4/projects/{projectId}/repository/files/{**filePath}", ["POST", "PUT"], async context =>
{
    // PushRepository, so the token's scopes decide exactly as they do for a git push: api or
    // write_repository, and Developer-equivalent access to the project.
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.PushRepository);
    if (project is null) return;
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }

    string filePath = context.Request.RouteValues["filePath"]?.ToString() ?? "";
    try { filePath = Uri.UnescapeDataString(filePath); }
    catch (UriFormatException) { await ApiError(context, 400, "file path is not valid"); return; }
    if (filePath.Length == 0) { await ApiError(context, 400, "file path is missing"); return; }

    RepositoryFileWriteRequest? request;
    try { request = await context.Request.ReadFromJsonAsync(ApiJson.Default.RepositoryFileWriteRequest, context.RequestAborted); }
    catch (JsonException) { await ApiError(context, 400, "invalid JSON"); return; }
    if (request is null) { await ApiError(context, 400, "file is missing"); return; }
    if (string.IsNullOrWhiteSpace(request.Branch)) { await ApiError(context, 400, "branch is required"); return; }
    if (string.IsNullOrWhiteSpace(request.CommitMessage)) { await ApiError(context, 400, "commit_message is required"); return; }
    if (request.Encoding is not (null or "text" or "base64")) { await ApiError(context, 400, "encoding must be text or base64"); return; }

    byte[] content;
    try
    {
        content = request.Encoding == "base64"
            ? Convert.FromBase64String(request.Content ?? "")
            : Encoding.UTF8.GetBytes(request.Content ?? "");
    }
    catch (FormatException) { await ApiError(context, 400, "content is not valid base64"); return; }
    if (content.Length > Limits.ObjectBytes) { await ApiError(context, 413, "file exceeds the repository object limit"); return; }

    // Graft accounts carry no email address, so attribution uses the reserved .invalid domain rather than
    // inventing a deliverable one, exactly as the browser route does.
    GitIdentity author = new(project.Identity!.Username, project.Identity.Username + "@graft.invalid", DateTimeOffset.UtcNow);
    try
    {
        await WithRepository(project.Endpoint, context, async repository =>
        {
            AuthoredCommit authored = repository.CommitFiles(request.Branch!,
                [new FileChange(filePath, content, request.ExecuteFilemode)], author, request.CommitMessage!, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status201Created;
            await context.Response.WriteAsJsonAsync(
                new RepositoryCommitResponse(authored.CommitId, authored.TreeId, authored.Branch, authored.CreatedBranch),
                ApiJson.Default.RepositoryCommitResponse, null, context.RequestAborted);
        });
    }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
});

// Deleting one file, which is a commit like writing one. The branch and the message travel in a JSON body
// as GitLab documents, or on the query string for a client whose DELETE cannot carry a body.
app.MapDelete("/api/v4/projects/{projectId}/repository/files/{**filePath}", async context =>
{
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.PushRepository);
    if (project is null) return;
    if (!await OnlyQuery(context, "branch", "commit_message")) return;
    await DeleteRepositoryFile(context, project.Details, project.Identity, context.Request.RouteValues["filePath"]?.ToString() ?? "");
});

app.MapDelete("/api/projects/{projectId:long}/repository/files/{**filePath}", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null) return;
    if (!await OnlyQuery(context, "branch", "commit_message")) return;
    await DeleteRepositoryFile(context, scope.Value.Details, scope.Value.Identity, context.Request.RouteValues["filePath"]?.ToString() ?? "");
});

app.MapGet("/api/v4/projects/{projectId}/repository/files/{**filePath}", async context =>
{
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.BrowseRepository);
    if (project is null) return;
    if (!await OnlyQuery(context, "ref")) return;
    string revision = context.Request.Query["ref"].ToString();
    if (revision.Length == 0) { await ApiError(context, 400, "ref is missing"); return; }
    // GitLab documents this path as URL-encoded, so `docs%2Fthing.md` and `docs/thing.md` have to name the
    // same file. Unescaping here matches what the write route does, so a file can be read back by the same
    // spelling it was written with.
    string filePath = context.Request.RouteValues["filePath"]?.ToString() ?? "";
    try { filePath = Uri.UnescapeDataString(filePath); }
    catch (UriFormatException) { await ApiError(context, 400, "file path is not valid"); return; }
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

    // The attributes this route understands, each a GitLab attribute with GitLab's meaning. Everything
    // else is still refused by name, so a caller is never told it succeeded at something that did not
    // happen. `name` and `description` are text and move nothing; `default_branch` is a preference the
    // engine resolves; `visibility` is the only one that can widen who sees the project.
    ProjectVisibility? visibility = null;
    string? newName = null;
    string? newDescription = null;
    string? newDefaultBranch = null;
    bool defaultBranchGiven = false;
    try
    {
        using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        { await ApiError(context, 400, "project must be a JSON object"); return; }
        string? unsupported = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .FirstOrDefault(name => name is not ("visibility" or "name" or "description" or "default_branch"));
        if (unsupported is not null)
        { await ApiError(context, 400, "unsupported project property: " + unsupported); return; }

        if (document.RootElement.TryGetProperty("visibility", out JsonElement visibilityValue))
        {
            if (visibilityValue.ValueKind != JsonValueKind.String || !TryReadVisibility(visibilityValue.GetString(), out ProjectVisibility parsed))
            { await ApiError(context, 400, "visibility must be private, unlisted or public"); return; }
            visibility = parsed;
        }
        if (document.RootElement.TryGetProperty("name", out JsonElement nameValue))
        {
            newName = nameValue.ValueKind == JsonValueKind.String ? nameValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(newName) || newName.Length > 200)
            { await ApiError(context, 400, "name is 1 to 200 characters"); return; }
        }
        if (document.RootElement.TryGetProperty("description", out JsonElement descriptionValue))
        {
            newDescription = descriptionValue.ValueKind == JsonValueKind.String ? descriptionValue.GetString() : null;
            if (newDescription is null || newDescription.Length > 2_000)
            { await ApiError(context, 400, "description is text of at most 2000 characters"); return; }
        }
        if (document.RootElement.TryGetProperty("default_branch", out JsonElement branchValue))
        {
            // Null clears the preference and lets the repository fall back to convention, which is what the
            // browser's own control does; the store takes null to mean exactly that.
            if (branchValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            { await ApiError(context, 400, "default_branch must be a branch name or null"); return; }
            newDefaultBranch = branchValue.ValueKind == JsonValueKind.Null ? null : branchValue.GetString();
            defaultBranchGiven = true;
        }
        if (visibility is null && newName is null && newDescription is null && !defaultBranchGiven)
        { await ApiError(context, 400, "no supported project property was given"); return; }
    }
    catch (JsonException) { await ApiError(context, 400, "invalid JSON"); return; }

    if (visibility is not null and not ProjectVisibility.Private && !settings.PublicBrowsing)
    { await ApiError(context, 400, "this server does not allow projects to be published"); return; }

    try
    {
        if (visibility is not null)
            await store.SetProjectVisibilityAsync(caller, project.Details.Project.Id, visibility.Value, context.RequestAborted);
        if (newName is not null || newDescription is not null)
            await store.SetProjectDetailsAsync(caller, project.Details.Project.Id, newName, newDescription, context.RequestAborted);
        if (defaultBranchGiven)
            await store.SetDefaultBranchAsync(caller, project.Details.Project.Id, newDefaultBranch, context.RequestAborted);
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "404 Project Not Found"); return; }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); return; }

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

// ---------------------------------------------------------------------------------------------------
// Collaboration: issues, merge proposals and the comments on both.
//
// Every operation below exists twice, as a GitLab-compatible route under /api/v4 answering to a token and
// as a Graft route under /api/projects answering to a browser session. The two differ only in how the
// caller is identified and in the CSRF and Origin checks a cookie makes necessary; the body of each is one
// shared function, so a rule cannot hold on one surface and not the other.
// ---------------------------------------------------------------------------------------------------

// Reading is allowed wherever the repository can be read, including anonymously on a public project. A
// refusal is a 404 like every other project refusal, because distinguishing "no" from "does not exist"
// would make the existence of a private project readable.
async Task<(ProjectDetails Details, AuthenticatedIdentity? Identity)?> BrowserCollaboration(
    HttpContext context, bool write)
{
    if (write && context.Request.Headers.Origin.ToString() != serverOrigin)
    { await ApiError(context, 403, "origin"); return null; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: write);
    if (write && browser is null) return null;
    if (write && !VerifyCsrf(browser!.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return null; }

    long projectId = Convert.ToInt64(context.Request.RouteValues["projectId"], System.Globalization.CultureInfo.InvariantCulture);
    ProjectDetails? details = await store.GetReadyProjectAsync(projectId, context.RequestAborted);
    AuthenticatedIdentity? identity = browser?.Identity;
    if (details is null) { await ApiError(context, 404, "project"); return null; }
    PermissionDecision permission = await DecideFor(context, identity, projectId, ProjectAction.ReadCollaboration);
    if (!permission.Allowed) { await ApiError(context, 404, "project"); return null; }
    return (details, identity);
}

// The same for a token caller. Reading is checked here; writing is checked by the store, so a caller who
// can read but not write is told so with a 403 rather than being hidden from.
async Task<(ProjectDetails Details, AuthenticatedIdentity? Identity)?> ApiCollaboration(HttpContext context)
{
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.ReadCollaboration);
    return project is null ? null : (project.Details, project.Identity);
}

long Number(HttpContext context) =>
    Convert.ToInt64(context.Request.RouteValues["iid"], System.Globalization.CultureInfo.InvariantCulture);

async Task<UserResponse> AuthorFor(HttpContext context, long userId, string username)
{
    string? avatarUrl = await store.HasAvatarAsync(AvatarOwner.User, userId, context.RequestAborted)
        ? AvatarUrl(context, AvatarOwner.User, userId)
        : null;
    return new(userId, username, username, "active", false,
        Origin(context) + "/" + Uri.EscapeDataString(username), avatarUrl);
}

async Task<IssueResponse> IssueResponseFor(HttpContext context, ProjectDetails project, Issue issue) => new(
    issue.Id, issue.Iid, issue.ProjectId, issue.Title, issue.Description, StateText(issue.State),
    await AuthorFor(context, issue.AuthorUserId, issue.AuthorUsername),
    issue.CreatedAtUtc, issue.UpdatedAtUtc, issue.ClosedAtUtc,
    ProjectWebUrl(context, project) + "/-/issues/" + issue.Iid.ToString(System.Globalization.CultureInfo.InvariantCulture),
    issue.Comments,
    [.. (issue.Labels ?? []).Select(label => label.Name)],
    [.. (issue.Labels ?? []).Select(LabelResponseFor)],
    issue.Milestone is null ? null : new MilestoneReferenceResponse(issue.Milestone.Id, issue.Milestone.Iid, issue.Milestone.Title),
    await PeopleFor(context, issue.Assignees),
    issue.DueDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
    issue.Confidential,
    TimeStatsFor(issue.TimeEstimateSeconds, issue.TimeSpentSeconds));

async Task<UserResponse[]> PeopleFor(HttpContext context, IReadOnlyList<Person>? people)
{
    List<UserResponse> result = [];
    foreach (Person person in people ?? []) result.Add(await AuthorFor(context, person.UserId, person.Username));
    return [.. result];
}

static LabelResponse LabelResponseFor(Label label) => new(label.Id, label.Name, label.Color, TextColorFor(label.Color), label.Description);

// Relative luminance of the stored colour decides whether dark or light text reads against it. The colour
// is six validated hex digits, so the parse cannot fail.
static string TextColorFor(string color)
{
    int r = Convert.ToInt32(color.Substring(1, 2), 16), g = Convert.ToInt32(color.Substring(3, 2), 16), b = Convert.ToInt32(color.Substring(5, 2), 16);
    double luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
    return luminance > 0.55 ? "#1f2328" : "#ffffff";
}

static TimeStatsResponse TimeStatsFor(long estimate, long spent) =>
    new(estimate, spent, TimeTracking.Humanize(estimate), TimeTracking.Humanize(spent));

MilestoneResponse MilestoneResponseFor(HttpContext context, ProjectDetails project, Milestone milestone) => new(
    milestone.Id, milestone.Iid, milestone.ProjectId, milestone.Title, milestone.Description,
    milestone.State == MilestoneState.Closed ? "closed" : "active",
    milestone.DueDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
    milestone.StartDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
    milestone.CreatedAtUtc, milestone.UpdatedAtUtc,
    ProjectWebUrl(context, project) + "/-/milestones/" + milestone.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
    milestone.OpenIssues, milestone.ClosedIssues, milestone.MergeRequests);

static BoardResponse BoardResponseFor(Board board) => new(board.Id, board.Name, board.ProjectId,
    [.. board.Lists.Select(list => new BoardListResponse(list.Id, LabelResponseFor(list.Label), list.Position))]);

// GitLab's own convention for labels in a request: one string, comma-separated. An empty string is an empty
// set, which on an update means "remove them all"; an absent field is null and means "leave them".
static string[]? LabelList(string? value) => value is null
    ? null
    : [.. value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

// A date field: absent leaves it alone, empty clears it, otherwise YYYY-MM-DD. Returns false for anything else.
static bool TryReadDay(string? value, out Change<DateOnly?> change)
{
    change = default;
    if (value is null) return true;
    if (value.Length == 0) { change = Change<DateOnly?>.To(null); return true; }
    if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out DateOnly day)) return false;
    change = Change<DateOnly?>.To(day);
    return true;
}

// GitLab clears a milestone with zero. Null is absent and leaves it alone.
static Change<long?> MilestoneChange(long? value) => value is null ? default : Change<long?>.To(value == 0 ? null : value);

/// <summary>
/// Re-reads the proposal's approvals against the branch tip the analysis just resolved. The store cannot
/// know that tip without walking the repository, so it loads every approval as stale and this decides
/// which are current, with no second query and no second lock.
/// </summary>
static MergeProposal AgainstTip(MergeProposal proposal, string? tip) =>
    proposal.Approvals is null ? proposal : proposal with
    {
        Approvals = [.. proposal.Approvals.Select(approval => approval with
        {
            Current = tip is not null && string.Equals(approval.SourceCommitSha, tip, StringComparison.Ordinal)
        })]
    };

// `analysis` is null in a listing. Deciding whether a proposal merges means flattening three trees, and
// doing that for every row of a page would make a list cost as much as the work it lists. GitLab answers
// the same way, with a merge_status a client is expected to treat as not yet known.
async Task<MergeRequestResponse> MergeResponseFor(HttpContext context, ProjectDetails project,
    MergeProposal input, MergeAnalysis? analysis)
{
    MergeProposal proposal = analysis is null ? input : AgainstTip(input, analysis.SourceCommitId);
    string status = proposal.State switch
    {
        CollaborationState.Merged => "can_be_merged",
        CollaborationState.Closed => "cannot_be_merged",
        _ => analysis is null ? "unchecked" : analysis.CanMerge ? "can_be_merged" : "cannot_be_merged"
    };
    string method = proposal.State == CollaborationState.Merged ? "already_merged" : analysis switch
    {
        null => "unresolved",
        { Kind: MergeKind.FastForward } => "fast_forward",
        { Kind: MergeKind.ThreeWay } => "three_way",
        { Kind: MergeKind.Conflict } => "conflict",
        { Kind: MergeKind.AlreadyMerged } => "already_merged",
        _ => "unresolved"
    };
    int current = proposal.Approvals?.Count(approval => approval.Current) ?? 0;
    return new(proposal.Id, proposal.Iid, proposal.ProjectId, proposal.Title, proposal.Description,
        StateText(proposal.State), proposal.SourceBranch, proposal.TargetBranch,
        await AuthorFor(context, proposal.AuthorUserId, proposal.AuthorUsername),
        proposal.CreatedAtUtc, proposal.UpdatedAtUtc, proposal.ClosedAtUtc,
        proposal.MergeCommitSha, proposal.MergedByUsername, status, analysis?.SourceCommitId,
        ProjectWebUrl(context, project) + "/-/merge-requests/" + proposal.Iid.ToString(System.Globalization.CultureInfo.InvariantCulture),
        method, analysis?.Conflicts, proposal.Comments,
        proposal.ApprovalsRequired, Math.Max(0, proposal.ApprovalsRequired - current),
        proposal.Approvals is null ? null : [.. proposal.Approvals.Select(approval => new ApprovalResponse(
            approval.UserId, approval.Username, approval.SourceCommitSha, approval.Current, approval.CreatedAtUtc))],
        [.. (proposal.Labels ?? []).Select(label => label.Name)],
        [.. (proposal.Labels ?? []).Select(LabelResponseFor)],
        proposal.Milestone is null ? null : new MilestoneReferenceResponse(proposal.Milestone.Id, proposal.Milestone.Iid, proposal.Milestone.Title),
        await PeopleFor(context, proposal.Assignees),
        await PeopleFor(context, proposal.Reviewers),
        proposal.Draft,
        TimeStatsFor(proposal.TimeEstimateSeconds, proposal.TimeSpentSeconds),
        SourceProjectId: proposal.SourceProjectId ?? proposal.ProjectId,
        SourceProjectPath: proposal.SourceProjectPath);
}

/// <summary>
/// Brings a fork's branch tip into the target repository before the engine looks at it, so the proposal
/// reads as the fork's branch stands now. A proposal from the project's own branch needs nothing. The
/// fork's gate is taken first and released before the target's, so two gates are never held at once.
/// </summary>
async Task EnsureSource(HttpContext context, ProjectDetails project, MergeProposal proposal)
{
    if (!proposal.FromFork || proposal.State != CollaborationState.Opened) return;
    ProjectDetails? fork = await store.GetReadyProjectAsync(proposal.SourceProjectId!.Value, context.RequestAborted);
    if (fork is null) return;
    RepositoryEndpoint source = EndpointFor(fork.Project);
    Repository from;
    string? tip;
    await source.Gate.WaitAsync(context.RequestAborted);
    try { from = source.Repository; tip = from.Refs.GetValueOrDefault("refs/heads/" + proposal.SourceBranch); }
    finally { source.Gate.Release(); }
    RepositoryEndpoint target = EndpointFor(project.Project);
    await target.Gate.WaitAsync(context.RequestAborted);
    try
    {
        if (tip is null) target.Repository.DropServerRef(proposal.SourceReference, context.RequestAborted);
        else target.Repository.Mirror(from, tip, proposal.SourceReference, context.RequestAborted);
    }
    catch (GitInputException) { }
    finally { target.Gate.Release(); }
}

async Task ForkProject(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required to fork"); return; }
    try
    {
        ProjectInfo fork = await store.ForkProjectAsync(identity, project.Project.Id, async (upstream, created, cancellationToken) =>
        {
            RepositoryEndpoint source = EndpointFor(upstream);
            await source.Gate.WaitAsync(cancellationToken);
            try
            {
                DurableRepositoryStorage.Copy(Path.Combine(settings.Repositories, upstream.StorageId),
                    Path.Combine(settings.Repositories, created.StorageId), cancellationToken);
            }
            finally { source.Gate.Release(); }
        }, context.RequestAborted);
        ProjectDetails details = (await store.GetReadyProjectAsync(fork.Id, context.RequestAborted))!;
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(await ProjectResponseFor(context, details), ApiJson.Default.ProjectResponse, null, context.RequestAborted);
    }
    catch (CollaborationConflictException error) { await ApiError(context, 409, error.Message); }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "404 Project Not Found"); }
    catch (IOException error) { await ApiError(context, 409, "the fork could not be created: " + error.Message); }
}

async Task ListForks(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context, "page", "per_page")) return;
    try
    {
        IReadOnlyList<ProjectDetails> forks = await store.ListForksAsync(identity, project.Project.Id, settings.PublicBrowsing, context.RequestAborted);
        List<ProjectResponse> response = [];
        foreach (ProjectDetails fork in forks) response.Add(await ProjectResponseFor(context, fork));
        await context.Response.WriteAsJsonAsync(response.ToArray(), ApiJson.Default.ProjectResponseArray, null, context.RequestAborted);
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "404 Project Not Found"); }
}

// `tip` is the source branch as it stands now. A comment anchored to any other commit is outdated: the
// line it names may no longer be the line it was about, so the page shows it beside the file rather than
// inside the diff.
async Task<NoteResponse> NoteResponseFor(HttpContext context, Comment comment, string? tip = null) => new(
    comment.Id, comment.Body, await AuthorFor(context, comment.AuthorUserId, comment.AuthorUsername),
    comment.CreatedAtUtc, comment.Path, comment.Line,
    comment.Side is null ? null : comment.Side == DiffSide.Old ? "old" : "new",
    comment.AnchorCommitSha,
    comment.AnchorCommitSha is not null && !string.Equals(comment.AnchorCommitSha, tip, StringComparison.Ordinal),
    comment.ParentId, comment.EditedAtUtc, comment.ResolvedAtUtc is not null, comment.ResolvedByUsername, comment.ResolvedAtUtc);

/// <summary>
/// Where a proposal's source branch points right now, or null when it no longer resolves. This is the
/// value an approval is recorded against and an anchored comment is measured by, so it is read from the
/// repository rather than taken from a caller.
/// </summary>
async Task<string?> SourceTip(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity, long iid)
{
    MergeProposal? proposal = await store.FindMergeProposalAsync(identity, project.Project.Id, iid, null, context.RequestAborted);
    if (proposal is null) return null;
    await EnsureSource(context, project, proposal);
    RepositoryEndpoint endpoint = EndpointFor(project.Project);
    await endpoint.Gate.WaitAsync(context.RequestAborted);
    try { return endpoint.Repository.Refs.GetValueOrDefault(proposal.FromFork ? proposal.SourceReference : "refs/heads/" + proposal.SourceBranch); }
    finally { endpoint.Gate.Release(); }
}

// Analyses a proposal against the repository as it stands now. Taken under the repository's gate like
// every other read of it, because a merge analysis reads the whole object graph.
async Task<MergeAnalysis> AnalyseProposal(HttpContext context, ProjectDetails project, MergeProposal proposal)
{
    await EnsureSource(context, project, proposal);
    RepositoryEndpoint endpoint = EndpointFor(project.Project);
    await endpoint.Gate.WaitAsync(context.RequestAborted);
    try { return RepositoryMerge.Analyse(endpoint.Repository, proposal.SourceReference, proposal.TargetBranch, context.RequestAborted); }
    finally { endpoint.Gate.Release(); }
}

async Task ListIssues(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context, "state", "page", "per_page", "labels", "without_labels", "milestone_id", "assignee_username", "search", "confidential")) return;
    if (!TryReadState(context.Request.Query["state"].ToString(), out CollaborationState? state, merged: false))
    { await ApiError(context, 400, "state must be opened, closed or all"); return; }
    (int Page, int PerPage)? pagination = await Pagination(context);
    if (pagination is null) return;
    (int page, int perPage) = pagination.Value;
    bool? confidential = context.Request.Query["confidential"].ToString() switch
    {
        "" => null,
        "true" => true,
        "false" => false,
        _ => (bool?)null
    };
    if (context.Request.Query["confidential"].ToString() is not ("" or "true" or "false"))
    { await ApiError(context, 400, "confidential must be true or false"); return; }
    IssueFilter filter = new(state,
        LabelList(Optional(context, "labels")), LabelList(Optional(context, "without_labels")),
        Optional(context, "milestone_id"), Optional(context, "assignee_username"), Optional(context, "search"), confidential);

    IssuePage issues;
    try { issues = await store.ListIssuesAsync(identity, project.Project.Id, filter, (page - 1) * perPage, perPage, context.RequestAborted); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); return; }
    List<IssueResponse> response = [];
    foreach (Issue issue in issues.Issues) response.Add(await IssueResponseFor(context, project, issue));
    SetPagination(context, page, perPage, issues.Total);
    await context.Response.WriteAsJsonAsync(new IssuePageResponse(response, issues.Total, page, perPage),
        ApiJson.Default.IssuePageResponse, null, context.RequestAborted);
}

static string? Optional(HttpContext context, string name) =>
    context.Request.Query[name].ToString() is { Length: > 0 } value ? value : null;

async Task CreateIssue(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required to open an issue"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    IssueCreateRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.IssueCreateRequest, context.RequestAborted);
    if (request?.Title is not { Length: > 0 }) { await ApiError(context, 400, "title is required"); return; }
    if (!TryReadDay(request.DueDate, out Change<DateOnly?> dueDate)) { await ApiError(context, 400, "due_date is YYYY-MM-DD"); return; }
    IssueChanges changes = new(LabelList(request.Labels), null, null, MilestoneChange(request.MilestoneId),
        request.AssigneeUsernames, dueDate, request.Confidential);
    try
    {
        Issue issue = await store.CreateIssueAsync(identity, project.Project.Id, request.Title,
            request.Description ?? "", changes.Any ? changes : null, context.RequestAborted);
        await Notify(context, project.Project, WebhookEvent.Issue, payload =>
        {
            payload["action"] = "opened";
            payload["issue"] = new System.Text.Json.Nodes.JsonObject
            {
                ["iid"] = issue.Iid,
                ["title"] = issue.Title,
                ["state"] = "opened",
                ["author"] = issue.AuthorUsername
            };
        });
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(await IssueResponseFor(context, project, issue),
            ApiJson.Default.IssueResponse, null, context.RequestAborted);
    }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task ReadIssue(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context)) return;
    Issue? issue = await store.FindIssueAsync(identity, project.Project.Id, Number(context), context.RequestAborted);
    if (issue is null) { await ApiError(context, 404, "404 Issue Not Found"); return; }
    await context.Response.WriteAsJsonAsync(await IssueResponseFor(context, project, issue),
        ApiJson.Default.IssueResponse, null, context.RequestAborted);
}

async Task UpdateIssue(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    IssueUpdateRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.IssueUpdateRequest, context.RequestAborted);
    if (request is null) { await ApiError(context, 400, "issue"); return; }
    if (!TryReadStateEvent(request.StateEvent, out CollaborationState? state))
    { await ApiError(context, 400, "state_event must be close or reopen"); return; }
    if (!TryReadDay(request.DueDate, out Change<DateOnly?> dueDate)) { await ApiError(context, 400, "due_date is YYYY-MM-DD"); return; }
    if (request.ReviewerUsernames is not null || request.Draft is not null)
    { await ApiError(context, 400, "an issue has no reviewers and no draft state"); return; }
    IssueChanges changes = new(LabelList(request.Labels), LabelList(request.AddLabels), LabelList(request.RemoveLabels),
        MilestoneChange(request.MilestoneId), request.AssigneeUsernames, dueDate, request.Confidential);
    if (request.Title is null && request.Description is null && state is null && !changes.Any)
    { await ApiError(context, 400, "nothing to change"); return; }
    try
    {
        Issue issue = await store.UpdateIssueAsync(identity, project.Project.Id, Number(context),
            request.Title, request.Description, state, changes.Any ? changes : null, context.RequestAborted);
        if (state is not null)
            await Notify(context, project.Project, WebhookEvent.Issue, payload =>
            {
                payload["action"] = state == CollaborationState.Closed ? "closed" : "reopened";
                payload["issue"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["iid"] = issue.Iid,
                    ["title"] = issue.Title,
                    ["state"] = StateText(issue.State),
                    ["author"] = issue.AuthorUsername
                };
            });
        await context.Response.WriteAsJsonAsync(await IssueResponseFor(context, project, issue),
            ApiJson.Default.IssueResponse, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Issue Not Found"); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task ListNotes(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity, CommentSubject subject)
{
    if (!await OnlyQuery(context, "page", "per_page")) return;
    (int Page, int PerPage)? pagination = await Pagination(context);
    if (pagination is null) return;
    (int page, int perPage) = pagination.Value;
    try
    {
        IReadOnlyList<Comment> comments = await store.ListCommentsAsync(identity, project.Project.Id, subject,
            Number(context), (page - 1) * perPage, perPage, context.RequestAborted);
        // Only a merge proposal has anchored comments, and only it needs the branch tip to tell a current
        // anchor from an outdated one. An issue's comments never carry one, so the walk is skipped.
        string? tip = subject == CommentSubject.MergeRequest && comments.Any(comment => comment.Path is not null)
            ? await SourceTip(context, project, identity, Number(context))
            : null;
        List<NoteResponse> response = [];
        foreach (Comment comment in comments) response.Add(await NoteResponseFor(context, comment, tip));
        await context.Response.WriteAsJsonAsync(response.ToArray(), ApiJson.Default.NoteResponseArray, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException error) { await ApiError(context, 404, error.Message); }
}

async Task CreateNote(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity, CommentSubject subject)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required to comment"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    NoteCreateRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.NoteCreateRequest, context.RequestAborted);
    if (request?.Body is not { Length: > 0 }) { await ApiError(context, 400, "body is required"); return; }

    // A comment either names a line or it does not. Half a position is a client bug, and accepting it would
    // store an anchor nothing could render.
    bool anchored = request.Path is not null || request.Line is not null || request.Side is not null;
    if (anchored && (request.Path is not { Length: > 0 } || request.Line is null || request.Side is null))
    { await ApiError(context, 400, "a comment on a line needs path, line and side together"); return; }
    if (anchored && request.Side is not ("old" or "new"))
    { await ApiError(context, 400, "side must be old or new"); return; }
    if (anchored && subject != CommentSubject.MergeRequest)
    { await ApiError(context, 400, "only a merge request has a diff to comment on"); return; }

    // The anchor is the branch tip as the server sees it now, not a value the client supplies. A client
    // that sent its own could anchor a comment to a commit it never read.
    string? anchor = anchored ? await SourceTip(context, project, identity, Number(context)) : null;
    if (anchored && anchor is null)
    { await ApiError(context, 409, "the source branch no longer resolves, so there is no diff to anchor to"); return; }

    try
    {
        Comment comment = await store.AddCommentAsync(identity, project.Project.Id, subject, Number(context),
            request.Body, request.Path, request.Line,
            anchored ? request.Side == "old" ? DiffSide.Old : DiffSide.New : null,
            anchor, request.ParentId, context.RequestAborted);
        await Notify(context, project.Project, WebhookEvent.Comment, payload =>
        {
            payload["action"] = "created";
            payload["subject"] = subject == CommentSubject.Issue ? "issue" : "merge_request";
            payload["iid"] = Number(context);
            payload["author"] = comment.AuthorUsername;
            payload["path"] = comment.Path;
            payload["line"] = comment.Line;
        });
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(await NoteResponseFor(context, comment, anchor),
            ApiJson.Default.NoteResponse, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException error) { await ApiError(context, 404, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task ListMergeRequests(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context, "state", "page", "per_page", "labels", "milestone_id", "assignee_username", "reviewer_username", "search")) return;
    if (!TryReadState(context.Request.Query["state"].ToString(), out CollaborationState? state, merged: true))
    { await ApiError(context, 400, "state must be opened, closed, merged or all"); return; }
    (int Page, int PerPage)? pagination = await Pagination(context);
    if (pagination is null) return;
    (int page, int perPage) = pagination.Value;
    MergeFilter filter = new(state, LabelList(Optional(context, "labels")), Optional(context, "milestone_id"),
        Optional(context, "assignee_username"), Optional(context, "reviewer_username"), Optional(context, "search"));

    MergeProposalPage proposals;
    try { proposals = await store.ListMergeProposalsAsync(identity, project.Project.Id, filter, (page - 1) * perPage, perPage, context.RequestAborted); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); return; }
    List<MergeRequestResponse> response = [];
    foreach (MergeProposal proposal in proposals.Proposals)
        response.Add(await MergeResponseFor(context, project, proposal, null));
    SetPagination(context, page, perPage, proposals.Total);
    await context.Response.WriteAsJsonAsync(new MergeRequestPageResponse(response, proposals.Total, page, perPage),
        ApiJson.Default.MergeRequestPageResponse, null, context.RequestAborted);
}

async Task CreateMergeRequest(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required to propose a merge"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    MergeRequestCreateRequest? request = await context.Request.ReadFromJsonAsync(
        ApiJson.Default.MergeRequestCreateRequest, context.RequestAborted);
    if (request?.SourceBranch is not { Length: > 0 } || request.TargetBranch is not { Length: > 0 })
    { await ApiError(context, 400, "source_branch and target_branch are required"); return; }
    if (request.Title is not { Length: > 0 }) { await ApiError(context, 400, "title is required"); return; }

    // Both branches must exist before the proposal does. A proposal naming a branch that was never there
    // is a typo rather than a plan, and catching it here is the difference between an error at the moment
    // of the mistake and a proposal that can never be merged for reasons nobody can see. The source branch
    // lives in the fork when one is named.
    long? sourceProjectId = request.SourceProjectId is long given && given != project.Project.Id ? given : null;
    ProjectDetails? sourceProject = project;
    if (sourceProjectId is long forkId)
    {
        sourceProject = await store.GetReadyProjectAsync(forkId, context.RequestAborted);
        if (sourceProject is null || !(await DecideFor(context, identity, forkId, ProjectAction.BrowseRepository)).Allowed)
        { await ApiError(context, 400, "source_project_id does not name a project you can read"); return; }
    }
    RepositoryEndpoint sourceEndpoint = EndpointFor(sourceProject!.Project);
    await sourceEndpoint.Gate.WaitAsync(context.RequestAborted);
    try
    {
        if (!sourceEndpoint.Repository.Refs.ContainsKey("refs/heads/" + request.SourceBranch))
        { await ApiError(context, 400, "source_branch does not exist"); return; }
    }
    finally { sourceEndpoint.Gate.Release(); }
    RepositoryEndpoint endpoint = EndpointFor(project.Project);
    await endpoint.Gate.WaitAsync(context.RequestAborted);
    try
    {
        if (!endpoint.Repository.Refs.ContainsKey("refs/heads/" + request.TargetBranch))
        { await ApiError(context, 400, "target_branch does not exist"); return; }
    }
    finally { endpoint.Gate.Release(); }

    ProposalChanges changes = new(LabelList(request.Labels), null, null, MilestoneChange(request.MilestoneId),
        request.AssigneeUsernames, request.ReviewerUsernames, request.Draft);
    try
    {
        MergeProposal proposal = await store.CreateMergeProposalAsync(identity, project.Project.Id, sourceProjectId,
            request.SourceBranch, request.TargetBranch, request.Title, request.Description ?? "",
            changes.Any ? changes : null, context.RequestAborted);
        await EnsureSource(context, project, proposal);
        await Notify(context, project.Project, WebhookEvent.MergeRequest, payload =>
        {
            payload["action"] = "opened";
            payload["merge_request"] = MergeNode(proposal);
        });
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(
            await FullMergeResponse(context, project, proposal),
            ApiJson.Default.MergeRequestResponse, null, context.RequestAborted);
    }
    catch (CollaborationConflictException error) { await ApiError(context, 409, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task ReadMergeRequest(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context)) return;
    MergeProposal? proposal = await store.FindMergeProposalAsync(identity, project.Project.Id, Number(context), null, context.RequestAborted);
    if (proposal is null) { await ApiError(context, 404, "404 Merge Request Not Found"); return; }
    await context.Response.WriteAsJsonAsync(await FullMergeResponse(context, project, proposal),
        ApiJson.Default.MergeRequestResponse, null, context.RequestAborted);
}

async Task UpdateMergeRequest(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    IssueUpdateRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.IssueUpdateRequest, context.RequestAborted);
    if (request is null) { await ApiError(context, 400, "merge request"); return; }
    if (!TryReadStateEvent(request.StateEvent, out CollaborationState? state))
    { await ApiError(context, 400, "state_event must be close or reopen"); return; }
    if (request.DueDate is not null || request.Confidential is not null)
    { await ApiError(context, 400, "a merge request has no due date and cannot be confidential"); return; }
    ProposalChanges changes = new(LabelList(request.Labels), LabelList(request.AddLabels), LabelList(request.RemoveLabels),
        MilestoneChange(request.MilestoneId), request.AssigneeUsernames, request.ReviewerUsernames, request.Draft);
    if (request.Title is null && request.Description is null && state is null && !changes.Any)
    { await ApiError(context, 400, "nothing to change"); return; }
    try
    {
        MergeProposal proposal = await store.UpdateMergeProposalAsync(identity, project.Project.Id, Number(context),
            request.Title, request.Description, state, changes.Any ? changes : null, context.RequestAborted);
        await context.Response.WriteAsJsonAsync(
            await FullMergeResponse(context, project, proposal),
            ApiJson.Default.MergeRequestResponse, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Merge Request Not Found"); }
    catch (CollaborationConflictException error) { await ApiError(context, 409, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task MergeRequestChanges(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context)) return;
    MergeProposal? proposal = await store.FindMergeProposalAsync(identity, project.Project.Id, Number(context), null, context.RequestAborted);
    if (proposal is null) { await ApiError(context, 404, "404 Merge Request Not Found"); return; }
    await EnsureSource(context, project, proposal);

    RepositoryEndpoint endpoint = EndpointFor(project.Project);
    await endpoint.Gate.WaitAsync(context.RequestAborted);
    MergeAnalysis analysis;
    IReadOnlyList<DiffFile> files = [];
    bool truncated = false;
    try
    {
        analysis = RepositoryMerge.Analyse(endpoint.Repository, proposal.SourceReference, proposal.TargetBranch, context.RequestAborted);
        // Measured from where the branches last agreed, which is what review wants: the source's own
        // changes, not everything the target has gained since. For a merged proposal the branches have
        // converged and the base is the source itself, so this correctly shows nothing.
        if (analysis.SourceCommitId is not null)
        {
            try
            {
                (files, truncated) = RepositoryDiff.BetweenCommits(endpoint.Repository,
                    analysis.BaseCommitId, analysis.SourceCommitId);
            }
            catch (GitInputException) { files = []; truncated = true; }
        }
    }
    finally { endpoint.Gate.Release(); }

    DiffFileResponse[] response = [.. files.Select(file => new DiffFileResponse(file.Path, file.Status,
        file.OldMode, file.NewMode, file.IsBinary, file.TooLarge, file.Added, file.Removed,
        [.. file.Hunks.Select(hunk => new DiffHunkResponse(HunkHeader(hunk),
            [.. hunk.Lines.Select(line => new DiffLineResponse(line.Kind.ToString(), line.Text, line.OldLine, line.NewLine, line.NoNewline))]))]))];
    await context.Response.WriteAsJsonAsync(new MergeRequestChangesResponse(
        await FullMergeResponse(context, project, proposal),
        analysis.BaseCommitId, analysis.SourceCommitId, analysis.TargetCommitId,
        response, truncated, response.Sum(file => file.Added), response.Sum(file => file.Removed)),
        ApiJson.Default.MergeRequestChangesResponse, null, context.RequestAborted);
}

// Merging writes to the target branch, so it needs exactly what a push to that branch needs. The store
// re-checks that; this checks it first so a caller who may read but not push is refused before the Git
// engine does any work.
async Task AcceptMergeRequest(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required to merge"); return; }
    PermissionDecision push = await DecideFor(context, identity, project.Project.Id, ProjectAction.PushRepository);
    if (!push.Allowed) { await ApiError(context, 403, push.Explanation); return; }

    // The body is optional: GitLab's merge route takes none. Graft reads whether to remove the source
    // branch afterwards, and whether to squash.
    bool removeSource = false;
    bool squash = false;
    string? squashMessage = null;
    if (context.Request.HasJsonContentType())
    {
        MergeAcceptRequest? body;
        try { body = await context.Request.ReadFromJsonAsync(ApiJson.Default.MergeAcceptRequest, context.RequestAborted); }
        catch (JsonException) { await ApiError(context, 400, "invalid JSON"); return; }
        removeSource = body?.ShouldRemoveSourceBranch == true;
        squash = body?.Squash == true;
        squashMessage = body?.SquashCommitMessage;
        if (squashMessage is { Length: > 10_000 }) { await ApiError(context, 400, "squash_commit_message is too long"); return; }
    }

    MergeProposal? proposal = await store.FindMergeProposalAsync(identity, project.Project.Id, Number(context),
        null, context.RequestAborted);
    if (proposal is null) { await ApiError(context, 404, "404 Merge Request Not Found"); return; }
    if (proposal.State != CollaborationState.Opened)
    { await ApiError(context, 409, "the proposal is not open"); return; }
    await EnsureSource(context, project, proposal);

    // A draft is the author saying it is not ready. The branches may well merge; the person did not ask.
    if (proposal.Draft) { await ApiError(context, 409, "this proposal is marked as a draft; mark it ready before merging"); return; }

    // The approval gate, checked here rather than only in the browser, so a client that simply does not
    // ask cannot merge past it. Counted against the branch as it stands now, so an approval given before
    // the latest push does not satisfy it.
    string? currentTip = await SourceTip(context, project, identity, proposal.Iid);
    int outstanding = await store.ApprovalsOutstandingAsync(project.Project.Id, proposal.Iid,
        currentTip ?? "", context.RequestAborted);
    if (outstanding > 0)
    {
        await ApiError(context, 409, $"this project requires {proposal.ApprovalsRequired} approvals of the " +
            $"current branch and {outstanding} more are outstanding");
        return;
    }

    // The code owner gate, decided against the approvals that stand right now and the CODEOWNERS file on
    // the target branch, which is the branch whose owners have the say.
    bool ownersRequired = await store.GetRequireCodeOwnerApprovalAsync(project.Project.Id, context.RequestAborted);
    IReadOnlyList<string> approvers = currentTip is null ? [] :
        await store.CurrentApproversAsync(project.Project.Id, proposal.Iid, currentTip, context.RequestAborted);

    string message = squash
        ? (string.IsNullOrWhiteSpace(squashMessage) ? proposal.Title : squashMessage.Trim())
        : $"Merge branch '{proposal.SourceBranch}' into '{proposal.TargetBranch}'\n\n{proposal.Title}";
    GitIdentity author = new(identity.Username, identity.Username + "@" + MergeAuthorHost, DateTimeOffset.UtcNow);

    RepositoryEndpoint endpoint = EndpointFor(project.Project);
    await endpoint.Gate.WaitAsync(context.RequestAborted);
    MergeOutcome outcome;
    bool sourceRemoved = false;
    try
    {
        MergeAnalysis analysis = RepositoryMerge.Analyse(endpoint.Repository, proposal.SourceReference,
            proposal.TargetBranch, context.RequestAborted);
        if (!analysis.CanMerge)
        {
            string reason = analysis.Kind switch
            {
                MergeKind.AlreadyMerged => "the target branch already contains the source",
                MergeKind.Conflict => "both branches changed: " + string.Join(", ", analysis.Conflicts),
                _ => "a branch this proposal names no longer exists"
            };
            await ApiError(context, 409, reason);
            return;
        }
        if (ownersRequired)
        {
            (IReadOnlyList<DiffFile> changed, _) = RepositoryDiff.BetweenCommits(endpoint.Repository, analysis.BaseCommitId, analysis.SourceCommitId!);
            List<string> missing = MissingOwnerApprovals(CodeOwnersFor(endpoint.Repository, analysis, changed.Select(file => file.Path)), approvers);
            if (missing.Count > 0)
            {
                await ApiError(context, 409, "this project requires a code owner's approval, and none stands for: " + string.Join(", ", missing));
                return;
            }
        }
        outcome = squash
            ? RepositoryMerge.Squash(endpoint.Repository, proposal.SourceReference, proposal.TargetBranch, author, message, context.RequestAborted)
            : RepositoryMerge.Merge(endpoint.Repository, proposal.SourceReference, proposal.TargetBranch, author, message, context.RequestAborted);

        // Inside the same lock as the merge, so nothing can push to the source branch between the two and
        // have that push silently removed. Refused only when the source is the branch the project opens
        // at, which is not an error worth failing a completed merge over.
        if (removeSource && !proposal.FromFork)
        {
            try
            {
                endpoint.Repository.DeleteBranch(proposal.SourceBranch, context.RequestAborted);
                sourceRemoved = true;
            }
            catch (GitInputException) { sourceRemoved = false; }
        }
    }
    catch (GitInputException error) { await ApiError(context, 409, "merge refused: " + error.Message); return; }
    finally { endpoint.Gate.Release(); }

    // The branch has moved. Recording it cannot be undone from here, so a failure after this point leaves a
    // merged branch and an open proposal, which is the safe direction to fail in: the work is not lost and
    // the proposal can be looked at.
    MergeProposal merged = await store.RecordMergeAsync(identity, project.Project.Id, proposal.Iid,
        outcome.CommitId, context.RequestAborted);
    await Notify(context, project.Project, WebhookEvent.MergeRequest, payload =>
    {
        payload["action"] = "merged";
        payload["merge_request"] = MergeNode(merged);
        payload["merge_commit_sha"] = outcome.CommitId;
        payload["source_branch_removed"] = sourceRemoved;
    });
    // A branch moved, which is the thing a build server is actually waiting for, so it is reported as a
    // push as well. A receiver listening only for pushes would otherwise never hear about a merge.
    await Notify(context, project.Project, WebhookEvent.Push, payload =>
    {
        payload["branch"] = merged.TargetBranch;
        payload["commit"] = outcome.CommitId;
        payload["by"] = identity.Username;
        payload["via"] = "merge";
    });
    await context.Response.WriteAsJsonAsync(new MergeResultResponse(
        await MergeResponseFor(context, project, merged, await AnalyseProposal(context, project, merged)),
        outcome.Kind switch { MergeKind.FastForward => "fast_forward", MergeKind.Squash => "squash", _ => "three_way" },
        outcome.CommitId, sourceRemoved),
        ApiJson.Default.MergeResultResponse, null, context.RequestAborted);
}

// The permission a push needs is re-checked here as well as at the route, because the browser family
// authorizes reading and leaves writing to whatever the operation itself requires.
async Task CreateBranch(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity,
    string branch, string? revision)
{
    PermissionDecision push = await DecideFor(context, identity, project.Project.Id, ProjectAction.PushRepository);
    if (!push.Allowed) { await ApiError(context, 403, push.Explanation); return; }

    RepositoryEndpoint endpoint = EndpointFor(project.Project);
    await WithRepository(endpoint, context, async repository =>
    {
        string from = string.IsNullOrWhiteSpace(revision) ? "HEAD" : revision.Trim();
        try
        {
            string commitId = repository.CreateBranch(branch, from, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status201Created;
            await context.Response.WriteAsJsonAsync(new BranchResponse(branch, false, false,
                branch == repository.DefaultBranch, true, true, true,
                ProjectWebUrl(context, project) + "/-/tree/" + Uri.EscapeDataString(branch),
                CommitResponseFor(context, project, RepositoryView.ReadCommit(repository, commitId))),
                ApiJson.Default.BranchResponse, null, context.RequestAborted);
        }
        catch (GitInputException error)
        {
            await ApiError(context, error.Message == "branch-exists" ? 409 : 400, error.Message switch
            {
                "branch-exists" => "a branch with this name already exists",
                "branch-revision" => "the revision to branch from could not be resolved",
                "ref-prefix-conflict" => "another branch's name is a prefix of this one, which Git cannot store",
                _ => "the branch name is not valid"
            });
        }
    });
}

/// <summary>
/// Writes bytes the caller is meant to save rather than look at.
///
/// <para>
/// The type is always one the browser will not render and the disposition is always an attachment, with
/// <c>nosniff</c> so the browser does not decide otherwise from the content. Repository bytes reaching this
/// origin as a document would be script running where the session cookie lives.
/// </para>
/// </summary>
static async Task WriteDownload(HttpContext context, byte[] content, string fileName, string contentType)
{
    using MemoryStream source = new(content, false);
    await WriteDownloadStream(context, source, fileName, contentType);
}

static async Task WriteDownloadStream(HttpContext context, Stream content, string fileName, string contentType)
{
    context.Response.ContentType = contentType;
    context.Response.Headers.XContentTypeOptions = "nosniff";
    // Only the characters a header can carry without quoting, so a repository path cannot inject one. A
    // name that loses everything is replaced rather than sent empty.
    string safe = new([.. fileName.Where(character =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')]);
    if (safe.Length is 0 or > 100) safe = "download";
    context.Response.Headers.ContentDisposition = "attachment; filename=\"" + safe + "\"";
    context.Response.ContentLength = content.Length;
    content.Position = 0;
    await content.CopyToAsync(context.Response.Body, context.RequestAborted);
}

/// <summary>
/// Writes the whole tree at a revision as a gzipped tar or a zip.
///
/// </summary>
async Task WriteArchive(HttpContext context, ProjectDetails project, RepositoryEndpoint endpoint,
    string revision, string format)
{
    using TemporaryStorage staging = new();
    using FileStream archive = staging.CreateFile();
    string? commitId = null;
    await WithRepository(endpoint, context, async repository =>
    {
        commitId = RepositoryView.ResolveCommit(repository, revision);
        if (commitId is null) { await ApiError(context, 404, "404 Revision Not Found"); return; }
        IReadOnlyList<GitTreeEntry> files = RepositoryView.FlatTree(repository, commitId)!;
        Limits.Require(files.Sum(file => (long)repository.Objects[file.Id].Size) <= Limits.ExpandedBytes, "archive-budget");
        // Git's own convention: everything sits under one directory named for the project and the commit,
        // so unpacking two archives in one place cannot merge them.
        string prefix = project.Project.Slug + "-" + commitId[..8] + "/";
        if (format == "zip") BuildZip(archive, repository, files, prefix, context.RequestAborted);
        else BuildTarGz(archive, repository, files, prefix, project.CreatedAtUtc, context.RequestAborted);
    });
    if (commitId is null) return;
    await WriteDownloadStream(context, archive, project.Project.Slug + "-" + commitId[..8] +
        (format == "zip" ? ".zip" : ".tar.gz"), "application/octet-stream");
}

static void BuildZip(Stream output, Repository repository, IReadOnlyList<GitTreeEntry> files, string prefix,
    CancellationToken cancellationToken)
{
    using (System.IO.Compression.ZipArchive zip = new(output, System.IO.Compression.ZipArchiveMode.Create, true))
    {
        foreach (GitTreeEntry file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            System.IO.Compression.ZipArchiveEntry entry = zip.CreateEntry(prefix + file.Path,
                System.IO.Compression.CompressionLevel.Optimal);
            using Stream writing = entry.Open();
            using Stream source = repository.Objects[file.Id].OpenRead();
            source.CopyTo(writing);
        }
    }
}

static void BuildTarGz(Stream output, Repository repository, IReadOnlyList<GitTreeEntry> files, string prefix,
    DateTimeOffset when, CancellationToken cancellationToken)
{
    using (System.IO.Compression.GZipStream compressed =
        new(output, System.IO.Compression.CompressionLevel.Optimal, true))
    using (System.Formats.Tar.TarWriter writer = new(compressed, System.Formats.Tar.TarEntryFormat.Pax, true))
    {
        foreach (GitTreeEntry file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Stream content = repository.Objects[file.Id].OpenRead();
            System.Formats.Tar.PaxTarEntry entry = new(System.Formats.Tar.TarEntryType.RegularFile, prefix + file.Path)
            {
                // Git stores one bit of permission that matters, and this carries it: an executable file
                // unpacks executable. Everything else is the ordinary mode rather than whatever the host
                // running the server happens to use.
                Mode = file.Mode == "100755"
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                      UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite |
                      UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                ModificationTime = when,
                DataStream = content
            };
            writer.WriteEntry(entry);
        }
    }
}

async Task BrowserTags(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    PermissionDecision browse = await DecideFor(context, identity, project.Project.Id, ProjectAction.BrowseRepository);
    if (!browse.Allowed) { await ApiError(context, 404, "project"); return; }
    RepositoryEndpoint endpoint = EndpointFor(project.Project);
    await WithRepository(endpoint, context, async repository =>
    {
        List<TagResponse> result = [];
        foreach ((string name, string target) in RepositoryView.Tags(repository))
        {
            string? commitId = RepositoryView.ResolveCommit(repository, name);
            if (commitId is null) continue;
            result.Add(new(name, target,
                ProjectWebUrl(context, project) + "/-/tree/" + Uri.EscapeDataString(name),
                CommitResponseFor(context, project, RepositoryView.ReadCommit(repository, commitId))));
        }
        await context.Response.WriteAsJsonAsync(result.ToArray(), ApiJson.Default.TagResponseArray, null, context.RequestAborted);
    });
}

async Task CreateTag(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity,
    string tag, string? revision)
{
    PermissionDecision push = await DecideFor(context, identity, project.Project.Id, ProjectAction.PushRepository);
    if (!push.Allowed) { await ApiError(context, 403, push.Explanation); return; }

    RepositoryEndpoint endpoint = EndpointFor(project.Project);
    await WithRepository(endpoint, context, async repository =>
    {
        string from = string.IsNullOrWhiteSpace(revision) ? "HEAD" : revision.Trim();
        try
        {
            string commitId = repository.CreateTag(tag, from, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status201Created;
            await context.Response.WriteAsJsonAsync(new TagResponse(tag, commitId,
                ProjectWebUrl(context, project) + "/-/tree/" + Uri.EscapeDataString(tag),
                CommitResponseFor(context, project, RepositoryView.ReadCommit(repository, commitId))),
                ApiJson.Default.TagResponse, null, context.RequestAborted);
        }
        catch (GitInputException error)
        {
            await ApiError(context, error.Message == "tag-exists" ? 409 : 400, error.Message switch
            {
                "tag-exists" => "a tag with this name already exists",
                "tag-revision" => "the revision to tag could not be resolved",
                "ref-prefix-conflict" => "another tag's name is a prefix of this one, which Git cannot store",
                _ => "the tag name is not valid"
            });
        }
    });
}

async Task DeleteTag(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity, string tag)
{
    PermissionDecision push = await DecideFor(context, identity, project.Project.Id, ProjectAction.PushRepository);
    if (!push.Allowed) { await ApiError(context, 403, push.Explanation); return; }
    try { tag = Uri.UnescapeDataString(tag); }
    catch (UriFormatException) { await ApiError(context, 400, "tag"); return; }
    if (tag.Length == 0) { await ApiError(context, 400, "tag is required"); return; }

    RepositoryEndpoint endpoint = EndpointFor(project.Project);
    await WithRepository(endpoint, context, async repository =>
    {
        try
        {
            repository.DeleteTag(tag, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        catch (GitInputException error)
        {
            await ApiError(context, error.Message == "tag-missing" ? 404 : 400,
                error.Message == "tag-missing" ? "404 Tag Not Found" : "the tag name is not valid");
        }
    });
}

async Task DeleteBranch(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity, string branch)
{
    PermissionDecision push = await DecideFor(context, identity, project.Project.Id, ProjectAction.PushRepository);
    if (!push.Allowed) { await ApiError(context, 403, push.Explanation); return; }
    try { branch = Uri.UnescapeDataString(branch); }
    catch (UriFormatException) { await ApiError(context, 400, "branch"); return; }
    if (branch.Length == 0) { await ApiError(context, 400, "branch is required"); return; }

    RepositoryEndpoint endpoint = EndpointFor(project.Project);
    await WithRepository(endpoint, context, async repository =>
    {
        try
        {
            repository.DeleteBranch(branch, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        catch (GitInputException error)
        {
            await ApiError(context, error.Message == "branch-missing" ? 404 : 409, error.Message switch
            {
                "branch-missing" => "404 Branch Not Found",
                "branch-is-default" => "this is the branch the project opens at; choose another default first",
                _ => "the branch name is not valid"
            });
        }
    });
}

// Deleting a file is a commit, authored exactly as a write is, so it needs what a push needs. The whole
// batch is one file here because that is the route's shape; the engine would take a hundred.
async Task DeleteRepositoryFile(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity, string filePath)
{
    PermissionDecision push = await DecideFor(context, identity, project.Project.Id, ProjectAction.PushRepository);
    if (!push.Allowed) { await ApiError(context, 403, push.Explanation); return; }
    try { filePath = Uri.UnescapeDataString(filePath); }
    catch (UriFormatException) { await ApiError(context, 400, "file path is not valid"); return; }
    if (filePath.Length == 0) { await ApiError(context, 400, "file path is missing"); return; }

    string? branch = context.Request.Query["branch"].ToString();
    string? commitMessage = context.Request.Query["commit_message"].ToString();
    if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
    {
        if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
        RepositoryFileDeleteRequest? request;
        try { request = await context.Request.ReadFromJsonAsync(ApiJson.Default.RepositoryFileDeleteRequest, context.RequestAborted); }
        catch (JsonException) { await ApiError(context, 400, "invalid JSON"); return; }
        if (!string.IsNullOrWhiteSpace(request?.Branch)) branch = request.Branch;
        if (!string.IsNullOrWhiteSpace(request?.CommitMessage)) commitMessage = request.CommitMessage;
    }
    if (string.IsNullOrWhiteSpace(branch)) { await ApiError(context, 400, "branch is required"); return; }
    if (string.IsNullOrWhiteSpace(commitMessage)) { await ApiError(context, 400, "commit_message is required"); return; }

    GitIdentity author = new(identity!.Username, identity.Username + "@graft.invalid", DateTimeOffset.UtcNow);
    await WithRepository(EndpointFor(project.Project), context, async repository =>
    {
        try
        {
            repository.DeleteFiles(branch, [filePath], author, commitMessage, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        catch (GitInputException error)
        {
            (int status, string reason) = error.Message switch
            {
                "path-missing" => (404, "404 File Not Found"),
                "branch-missing" => (400, "branch does not exist"),
                "path-is-directory" => (400, "the path names a directory, not a file"),
                _ => (400, error.Message)
            };
            await ApiError(context, status, reason);
        }
    });
}

// Approving records the commit approved, so the approval can be recognised as stale once the branch moves.
// The commit comes from the repository rather than from the caller, for the same reason an anchor does.
async Task SetApproval(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity, bool approved)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required to approve"); return; }
    if (!await OnlyQuery(context)) return;
    string? tip = await SourceTip(context, project, identity, Number(context));
    if (approved && tip is null)
    { await ApiError(context, 409, "the source branch no longer resolves, so there is nothing to approve"); return; }

    try
    {
        MergeProposal proposal = await store.SetApprovalAsync(identity, project.Project.Id, Number(context),
            approved, tip ?? "", context.RequestAborted);
        await context.Response.WriteAsJsonAsync(
            await FullMergeResponse(context, project, proposal),
            ApiJson.Default.MergeRequestResponse, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Merge Request Not Found"); }
    catch (CollaborationConflictException error) { await ApiError(context, 409, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task SetApprovalsRequired(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    ApprovalsRequiredRequest? request = await context.Request.ReadFromJsonAsync(
        ApiJson.Default.ApprovalsRequiredRequest, context.RequestAborted);
    if (request is null || (request.ApprovalsBeforeMerge is null && request.RequireCodeOwnerApproval is null))
    { await ApiError(context, 400, "approvals_before_merge or require_code_owner_approval is required"); return; }
    if (request.ApprovalsBeforeMerge is < 0 or > 10)
    { await ApiError(context, 400, "approvals_before_merge must be between 0 and 10"); return; }
    try
    {
        if (request.ApprovalsBeforeMerge is int count)
            await store.SetApprovalsRequiredAsync(identity, project.Project.Id, count, context.RequestAborted);
        if (request.RequireCodeOwnerApproval is bool owners)
            await store.SetRequireCodeOwnerApprovalAsync(identity, project.Project.Id, owners, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
}

/// <summary>
/// Who owns what a proposal changes, read from the CODEOWNERS file on the target branch, which is the
/// branch the change would land on and whose owners therefore have the say. Paths the file does not name
/// are left out: an unowned path needs nobody.
/// </summary>
static List<CodeOwnerResponse> CodeOwnersFor(Repository repository, MergeAnalysis analysis, IEnumerable<string> changedPaths)
{
    if (analysis.TargetCommitId is null) return [];
    IReadOnlyList<CodeOwnerRule> rules = CodeOwners.Read(repository, analysis.TargetCommitId);
    if (rules.Count == 0) return [];
    List<CodeOwnerResponse> owners = [];
    foreach (string path in changedPaths)
    {
        IReadOnlyList<string> who = CodeOwners.OwnersOf(rules, path);
        if (who.Count > 0) owners.Add(new(path, who));
    }
    return owners;
}

/// <summary>The paths in <paramref name="owners"/> that no listed owner has currently approved.</summary>
static List<string> MissingOwnerApprovals(IReadOnlyList<CodeOwnerResponse> owners, IReadOnlyList<string> approvers) =>
    [.. owners.Where(entry => !entry.Owners.Any(owner => approvers.Contains(owner, StringComparer.OrdinalIgnoreCase)))
        .Select(entry => entry.Path)];

/// <summary>
/// A proposal's full picture: analysis, changed paths and who owns them, with the code owner gate decided
/// against the approvals that stand. Reads the repository under its gate; the store afterwards.
/// </summary>
async Task<MergeRequestResponse> FullMergeResponse(HttpContext context, ProjectDetails project, MergeProposal proposal)
{
    await EnsureSource(context, project, proposal);
    RepositoryEndpoint endpoint = EndpointFor(project.Project);
    MergeAnalysis analysis;
    List<CodeOwnerResponse> owners = [];
    await endpoint.Gate.WaitAsync(context.RequestAborted);
    try
    {
        analysis = RepositoryMerge.Analyse(endpoint.Repository, proposal.SourceReference, proposal.TargetBranch, context.RequestAborted);
        if (analysis.SourceCommitId is not null && proposal.State == CollaborationState.Opened)
        {
            try
            {
                (IReadOnlyList<DiffFile> files, _) = RepositoryDiff.BetweenCommits(endpoint.Repository, analysis.BaseCommitId, analysis.SourceCommitId);
                owners = CodeOwnersFor(endpoint.Repository, analysis, files.Select(file => file.Path));
            }
            catch (GitInputException) { owners = []; }
        }
    }
    finally { endpoint.Gate.Release(); }
    bool required = await store.GetRequireCodeOwnerApprovalAsync(project.Project.Id, context.RequestAborted);
    List<string> missing = required && owners.Count > 0 && analysis.SourceCommitId is not null
        ? MissingOwnerApprovals(owners, await store.CurrentApproversAsync(project.Project.Id, proposal.Iid, analysis.SourceCommitId, context.RequestAborted))
        : [];
    return await MergeResponseFor(context, project, proposal, analysis) with
    {
        CodeOwners = owners,
        CodeOwnerApprovalRequired = required,
        CodeOwnerApprovalsMissing = missing
    };
}

// A rebase rewrites the source branch onto the target's tip. It needs what a push needs, because it is a
// forced push, and the response says plainly that approvals and line comments were about commits that are
// no longer on the branch.
async Task RebaseMergeRequest(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required to rebase"); return; }
    PermissionDecision push = await DecideFor(context, identity, project.Project.Id, ProjectAction.PushRepository);
    if (!push.Allowed) { await ApiError(context, 403, push.Explanation); return; }
    MergeProposal? proposal = await store.FindMergeProposalAsync(identity, project.Project.Id, Number(context), null, context.RequestAborted);
    if (proposal is null) { await ApiError(context, 404, "404 Merge Request Not Found"); return; }
    if (proposal.State != CollaborationState.Opened) { await ApiError(context, 409, "the proposal is not open"); return; }
    if (proposal.FromFork) { await ApiError(context, 409, "a proposal from a fork is rebased in the fork, by its author"); return; }

    GitIdentity committer = new(identity.Username, identity.Username + "@" + MergeAuthorHost, DateTimeOffset.UtcNow);
    RepositoryEndpoint endpoint = EndpointFor(project.Project);
    RebaseOutcome outcome;
    await endpoint.Gate.WaitAsync(context.RequestAborted);
    try { outcome = RepositoryMerge.Rebase(endpoint.Repository, proposal.SourceBranch, proposal.TargetBranch, committer, context.RequestAborted); }
    catch (GitInputException error)
    {
        await ApiError(context, 409, error.Message switch
        {
            "rebase-merge-commit" => "the branch contains a merge commit, which a rebase would have to flatten; rebase it locally",
            "rebase-too-many" => "too many commits to rebase here; rebase it locally",
            "merge-unresolved" => "a branch this proposal names no longer exists",
            _ when error.Message.StartsWith("rebase-conflict:", StringComparison.Ordinal) =>
                "both branches changed " + error.Message["rebase-conflict:".Length..] + "; rebase it locally, where the conflict can be seen",
            _ => "rebase refused: " + error.Message
        });
        return;
    }
    finally { endpoint.Gate.Release(); }

    if (outcome.Rewritten > 0)
        await Notify(context, project.Project, WebhookEvent.Push, payload =>
        {
            payload["branch"] = proposal.SourceBranch;
            payload["commit"] = outcome.NewTip;
            payload["by"] = identity.Username;
            payload["via"] = "rebase";
            payload["forced"] = true;
        });
    MergeProposal refreshed = (await store.FindMergeProposalAsync(identity, project.Project.Id, proposal.Iid, null, context.RequestAborted))!;
    await context.Response.WriteAsJsonAsync(new RebaseResultResponse(
        await FullMergeResponse(context, project, refreshed), outcome.PreviousTip, outcome.NewTip, outcome.Rewritten),
        ApiJson.Default.RebaseResultResponse, null, context.RequestAborted);
}

// Hooks are managed only where the operator allowed outbound requests. Absent rather than refused on a
// server without the switch: there is nothing for a hook to do there, and a 404 is the honest answer.
async Task ListWebhooks(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!await OnlyQuery(context)) return;
    try
    {
        IReadOnlyList<Webhook> hooks = await store.ListWebhooksAsync(identity, project.Project.Id, context.RequestAborted);
        await context.Response.WriteAsJsonAsync([.. hooks.Select(WebhookResponseFor)],
            ApiJson.Default.WebhookResponseArray, null, context.RequestAborted);
    }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
}

async Task CreateWebhook(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    WebhookCreateRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.WebhookCreateRequest, context.RequestAborted);
    if (request?.Url is not { Length: > 0 }) { await ApiError(context, 400, "url is required"); return; }
    if (request.Secret is not { Length: > 0 }) { await ApiError(context, 400, "secret is required"); return; }
    if (request.Events is not { Length: > 0 }) { await ApiError(context, 400, "at least one event is required"); return; }
    if (!WebhookEvents.TryParse(string.Join(",", request.Events), out WebhookEvent events))
    { await ApiError(context, 400, "events must be push, issue, merge_request or comment"); return; }
    // An absolute http or https address and nothing else. A scheme this server could be talked into
    // treating as a file or a socket is the one thing an address field must not accept.
    if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? target) ||
        (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps) ||
        !string.IsNullOrEmpty(target.UserInfo))
    { await ApiError(context, 400, "url must be an absolute http or https address with no credentials in it"); return; }

    try
    {
        Webhook hook = await store.CreateWebhookAsync(identity, project.Project.Id, request.Url,
            request.Secret, events, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(WebhookResponseFor(hook), ApiJson.Default.WebhookResponse, null, context.RequestAborted);
    }
    catch (CollaborationConflictException error) { await ApiError(context, 409, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task DeleteWebhook(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    long hookId = Convert.ToInt64(context.Request.RouteValues["hookId"], System.Globalization.CultureInfo.InvariantCulture);
    try
    {
        await store.DeleteWebhookAsync(identity, project.Project.Id, hookId, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Webhook Not Found"); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
}

/// <summary>
/// Queues an event for a project's hooks, if the operator allowed any. Returns as soon as it is queued;
/// nothing a person does waits for somebody else's server to answer.
///
/// <para>
/// The payload is built here as a JSON node tree rather than serialized from a type, for the same reason
/// the agent tools are: reflection-based serialization is off, so a shape invented here would work under a
/// debugger and fail in the published build.
/// </para>
/// </summary>
async Task Notify(HttpContext context, ProjectInfo project, WebhookEvent single,
    Action<System.Text.Json.Nodes.JsonObject> describe)
{
    if (webhooks is null) return;
    System.Text.Json.Nodes.JsonObject payload = new()
    {
        ["event"] = WebhookEvents.Name(single),
        ["project"] = new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = project.Id,
            ["path"] = project.Namespace + "/" + project.Slug,
            ["web_url"] = ProjectUrlFor(context, project)
        },
        ["at"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
    };
    describe(payload);
    await webhooks.PublishAsync(project.Id, single, payload.ToJsonString(), context.RequestAborted);
}

static System.Text.Json.Nodes.JsonObject MergeNode(MergeProposal proposal) => new()
{
    ["iid"] = proposal.Iid,
    ["title"] = proposal.Title,
    ["state"] = StateText(proposal.State),
    ["source_branch"] = proposal.SourceBranch,
    ["target_branch"] = proposal.TargetBranch,
    ["author"] = proposal.AuthorUsername
};

static WebhookResponse WebhookResponseFor(Webhook hook) => new(
    hook.Id, hook.Url,
    [.. WebhookEvents.All.Where(entry => hook.Events.HasFlag(entry.Value)).Select(entry => entry.Name)],
    hook.Active, hook.CreatedByUsername, hook.CreatedAtUtc, hook.LastStatus, hook.LastError, hook.LastDeliveredAtUtc);

// `subject` is set on the GitLab-shaped route, where the comment is addressed beneath an issue or a
// proposal; a comment that is not on that one is a 404 rather than being reached through the wrong door.
async Task DeleteNote(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity,
    CommentSubject? subject = null)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    long commentId = Convert.ToInt64(context.Request.RouteValues["commentId"], System.Globalization.CultureInfo.InvariantCulture);
    if (subject is not null && !await store.CommentBelongsToAsync(project.Project.Id, subject.Value, Number(context), commentId, context.RequestAborted))
    { await ApiError(context, 404, "404 Note Not Found"); return; }
    try
    {
        await store.DeleteCommentAsync(identity, project.Project.Id, commentId, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (CollaborationNotFoundException error) { await ApiError(context, 404, error.Message); }
    catch (CollaborationConflictException error) { await ApiError(context, 409, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
}

// Editing the words and settling the thread arrive through one route because GitLab's note update does
// both, and because the two rules differ: the words belong to the author or a Maintainer, the thread to
// anybody who could push or whoever started it. The store applies each rule to its own half.
async Task UpdateNote(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity,
    CommentSubject? subject = null)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    NoteUpdateRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.NoteUpdateRequest, context.RequestAborted);
    if (request is null || (request.Body is null && request.Resolved is null))
    { await ApiError(context, 400, "body or resolved is required"); return; }
    long commentId = Convert.ToInt64(context.Request.RouteValues["commentId"], System.Globalization.CultureInfo.InvariantCulture);
    if (subject is not null && !await store.CommentBelongsToAsync(project.Project.Id, subject.Value, Number(context), commentId, context.RequestAborted))
    { await ApiError(context, 404, "404 Note Not Found"); return; }
    try
    {
        Comment? comment = null;
        if (request.Body is not null)
            comment = await store.EditCommentAsync(identity, project.Project.Id, commentId, request.Body, context.RequestAborted);
        if (request.Resolved is not null)
            comment = await store.SetThreadResolvedAsync(identity, project.Project.Id, commentId, request.Resolved.Value, context.RequestAborted);
        // The anchor's currency needs the branch tip, which only a proposal has; the edited comment is
        // reported against it so the response says the same thing a fresh listing would.
        string? tip = comment!.Path is not null && subject is not CommentSubject.Issue
            ? await SourceTipOfComment(context, project, identity, commentId)
            : null;
        await context.Response.WriteAsJsonAsync(await NoteResponseFor(context, comment, tip),
            ApiJson.Default.NoteResponse, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException error) { await ApiError(context, 404, error.Message); }
    catch (CollaborationConflictException error) { await ApiError(context, 409, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

// ---------------------------------------------------------------------------------------------------
// Planning: labels, milestones, boards, and the people who can be put on a piece of work.
// Reading follows the conversation; organising needs the permission to push. The store decides both.
// ---------------------------------------------------------------------------------------------------

async Task ListLabels(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context, "page", "per_page", "search")) return;
    IReadOnlyList<Label> labels = await store.ListLabelsAsync(identity, project.Project.Id, context.RequestAborted);
    string search = context.Request.Query["search"].ToString();
    if (search.Length > 0) labels = [.. labels.Where(label => label.Name.Contains(search, StringComparison.OrdinalIgnoreCase))];
    await context.Response.WriteAsJsonAsync([.. labels.Select(LabelResponseFor)], ApiJson.Default.LabelResponseArray, null, context.RequestAborted);
}

async Task CreateLabel(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    LabelRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.LabelRequest, context.RequestAborted);
    if (request?.Name is not { Length: > 0 }) { await ApiError(context, 400, "name is required"); return; }
    if (request.Color is not { Length: > 0 }) { await ApiError(context, 400, "color is required"); return; }
    try
    {
        Label label = await store.CreateLabelAsync(identity, project.Project.Id, request.Name, request.Color,
            request.Description ?? "", context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(LabelResponseFor(label), ApiJson.Default.LabelResponse, null, context.RequestAborted);
    }
    catch (CollaborationConflictException error) { await ApiError(context, 409, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

// GitLab addresses a label by id or by name in the path. Both are accepted; a name is matched the way the
// store matches it, without regard to case.
async Task<long?> LabelIdFromRoute(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    string key = context.Request.RouteValues["labelId"]?.ToString() ?? "";
    try { key = Uri.UnescapeDataString(key); } catch (UriFormatException) { return null; }
    if (long.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long id)) return id;
    IReadOnlyList<Label> labels = await store.ListLabelsAsync(identity, project.Project.Id, context.RequestAborted);
    return labels.FirstOrDefault(label => string.Equals(label.Name, key.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;
}

async Task UpdateLabel(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    LabelRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.LabelRequest, context.RequestAborted);
    if (request is null || (request.Name is null && request.Color is null && request.Description is null))
    { await ApiError(context, 400, "nothing to change"); return; }
    long? labelId = await LabelIdFromRoute(context, project, identity);
    if (labelId is null) { await ApiError(context, 404, "404 Label Not Found"); return; }
    try
    {
        Label label = await store.UpdateLabelAsync(identity, project.Project.Id, labelId.Value, request.Name, request.Color,
            request.Description, context.RequestAborted);
        await context.Response.WriteAsJsonAsync(LabelResponseFor(label), ApiJson.Default.LabelResponse, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Label Not Found"); }
    catch (CollaborationConflictException error) { await ApiError(context, 409, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task DeleteLabel(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    long? labelId = await LabelIdFromRoute(context, project, identity);
    if (labelId is null) { await ApiError(context, 404, "404 Label Not Found"); return; }
    try
    {
        await store.DeleteLabelAsync(identity, project.Project.Id, labelId.Value, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Label Not Found"); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
}

async Task ListMilestones(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context, "state", "page", "per_page")) return;
    MilestoneState? state = context.Request.Query["state"].ToString() switch
    {
        "" or "all" => null,
        "active" => MilestoneState.Active,
        "closed" => MilestoneState.Closed,
        _ => (MilestoneState?)MilestoneState.Active
    };
    if (context.Request.Query["state"].ToString() is not ("" or "all" or "active" or "closed"))
    { await ApiError(context, 400, "state must be active, closed or all"); return; }
    IReadOnlyList<Milestone> milestones = await store.ListMilestonesAsync(identity, project.Project.Id, state, context.RequestAborted);
    await context.Response.WriteAsJsonAsync([.. milestones.Select(milestone => MilestoneResponseFor(context, project, milestone))],
        ApiJson.Default.MilestoneResponseArray, null, context.RequestAborted);
}

long MilestoneId(HttpContext context) =>
    Convert.ToInt64(context.Request.RouteValues["milestoneId"], System.Globalization.CultureInfo.InvariantCulture);

async Task ReadMilestone(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context)) return;
    Milestone? milestone = await store.FindMilestoneAsync(identity, project.Project.Id, MilestoneId(context), context.RequestAborted);
    if (milestone is null) { await ApiError(context, 404, "404 Milestone Not Found"); return; }
    await context.Response.WriteAsJsonAsync(MilestoneResponseFor(context, project, milestone), ApiJson.Default.MilestoneResponse, null, context.RequestAborted);
}

async Task CreateMilestone(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    MilestoneRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.MilestoneRequest, context.RequestAborted);
    if (request?.Title is not { Length: > 0 }) { await ApiError(context, 400, "title is required"); return; }
    if (!TryReadDay(request.DueDate, out Change<DateOnly?> due) || !TryReadDay(request.StartDate, out Change<DateOnly?> start))
    { await ApiError(context, 400, "dates are YYYY-MM-DD"); return; }
    try
    {
        Milestone milestone = await store.CreateMilestoneAsync(identity, project.Project.Id, request.Title, request.Description ?? "",
            due.Value, start.Value, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(MilestoneResponseFor(context, project, milestone), ApiJson.Default.MilestoneResponse, null, context.RequestAborted);
    }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task UpdateMilestone(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    MilestoneRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.MilestoneRequest, context.RequestAborted);
    if (request is null) { await ApiError(context, 400, "milestone"); return; }
    MilestoneState? state = request.StateEvent switch
    {
        null or "" => null,
        "close" => MilestoneState.Closed,
        "activate" => MilestoneState.Active,
        _ => (MilestoneState?)MilestoneState.Active
    };
    if (request.StateEvent is not (null or "" or "close" or "activate"))
    { await ApiError(context, 400, "state_event must be close or activate"); return; }
    if (!TryReadDay(request.DueDate, out Change<DateOnly?> due) || !TryReadDay(request.StartDate, out Change<DateOnly?> start))
    { await ApiError(context, 400, "dates are YYYY-MM-DD"); return; }
    if (request.Title is null && request.Description is null && state is null && !due.Set && !start.Set)
    { await ApiError(context, 400, "nothing to change"); return; }
    try
    {
        Milestone milestone = await store.UpdateMilestoneAsync(identity, project.Project.Id, MilestoneId(context),
            request.Title, request.Description, state, due, start, context.RequestAborted);
        await context.Response.WriteAsJsonAsync(MilestoneResponseFor(context, project, milestone), ApiJson.Default.MilestoneResponse, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Milestone Not Found"); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task DeleteMilestone(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    try
    {
        await store.DeleteMilestoneAsync(identity, project.Project.Id, MilestoneId(context), context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Milestone Not Found"); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
}

async Task ListBoards(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context, "page", "per_page")) return;
    IReadOnlyList<Board> boards = await store.ListBoardsAsync(identity, project.Project.Id, context.RequestAborted);
    await context.Response.WriteAsJsonAsync([.. boards.Select(BoardResponseFor)], ApiJson.Default.BoardResponseArray, null, context.RequestAborted);
}

long BoardId(HttpContext context) =>
    Convert.ToInt64(context.Request.RouteValues["boardId"], System.Globalization.CultureInfo.InvariantCulture);

async Task ReadBoard(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context)) return;
    Board? board = await store.FindBoardAsync(identity, project.Project.Id, BoardId(context), context.RequestAborted);
    if (board is null) { await ApiError(context, 404, "404 Board Not Found"); return; }
    await context.Response.WriteAsJsonAsync(BoardResponseFor(board), ApiJson.Default.BoardResponse, null, context.RequestAborted);
}

async Task CreateBoard(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    BoardRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.BoardRequest, context.RequestAborted);
    if (request?.Name is not { Length: > 0 }) { await ApiError(context, 400, "name is required"); return; }
    try
    {
        Board board = await store.CreateBoardAsync(identity, project.Project.Id, request.Name, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(BoardResponseFor(board), ApiJson.Default.BoardResponse, null, context.RequestAborted);
    }
    catch (CollaborationConflictException error) { await ApiError(context, 409, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task UpdateBoard(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    BoardRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.BoardRequest, context.RequestAborted);
    if (request?.Name is not { Length: > 0 }) { await ApiError(context, 400, "name is required"); return; }
    try
    {
        Board board = await store.RenameBoardAsync(identity, project.Project.Id, BoardId(context), request.Name, context.RequestAborted);
        await context.Response.WriteAsJsonAsync(BoardResponseFor(board), ApiJson.Default.BoardResponse, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Board Not Found"); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task DeleteBoard(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    try
    {
        await store.DeleteBoardAsync(identity, project.Project.Id, BoardId(context), context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Board Not Found"); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
}

async Task CreateBoardList(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    BoardListRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.BoardListRequest, context.RequestAborted);
    long? labelId = request?.LabelId;
    if (labelId is null && request?.Label is { Length: > 0 } name)
        labelId = (await store.ListLabelsAsync(identity, project.Project.Id, context.RequestAborted))
            .FirstOrDefault(label => string.Equals(label.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;
    if (labelId is null) { await ApiError(context, 400, "label_id or label is required and must name an existing label"); return; }
    try
    {
        Board board = await store.AddBoardListAsync(identity, project.Project.Id, BoardId(context), labelId.Value, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(BoardResponseFor(board), ApiJson.Default.BoardResponse, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException error) { await ApiError(context, 404, error.Message); }
    catch (CollaborationConflictException error) { await ApiError(context, 409, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
}

long ListId(HttpContext context) =>
    Convert.ToInt64(context.Request.RouteValues["listId"], System.Globalization.CultureInfo.InvariantCulture);

async Task UpdateBoardList(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    BoardListRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.BoardListRequest, context.RequestAborted);
    if (request?.Position is not >= 1) { await ApiError(context, 400, "position is required, counted from one"); return; }
    try
    {
        Board board = await store.MoveBoardListAsync(identity, project.Project.Id, BoardId(context), ListId(context),
            request.Position.Value, context.RequestAborted);
        await context.Response.WriteAsJsonAsync(BoardResponseFor(board), ApiJson.Default.BoardResponse, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException error) { await ApiError(context, 404, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
}

async Task DeleteBoardList(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    try
    {
        await store.DeleteBoardListAsync(identity, project.Project.Id, BoardId(context), ListId(context), context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (CollaborationNotFoundException error) { await ApiError(context, 404, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
}

// The people who can be put on a piece of work. GitLab serves this as /projects/:id/users; it is the
// membership seen from the inside, readable by anybody who may write on the project.
async Task ListAssignable(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!await OnlyQuery(context, "page", "per_page", "search")) return;
    try
    {
        IReadOnlyList<Person> people = await store.ListAssignableAsync(identity, project.Project.Id, context.RequestAborted);
        await context.Response.WriteAsJsonAsync(await PeopleFor(context, people), ApiJson.Default.UserResponseArray, null, context.RequestAborted);
    }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
}

// ---------------------------------------------------------------------------------------------------
// Releases: notes, links and files on a tag. Reading follows the repository; publishing needs a push.
// ---------------------------------------------------------------------------------------------------

async Task<ReleaseResponse> ReleaseResponseFor(HttpContext context, ProjectDetails project, Release release)
{
    RepositoryEndpoint endpoint = EndpointFor(project.Project);
    CommitResponse? commit = null;
    await endpoint.Gate.WaitAsync(context.RequestAborted);
    try
    {
        if (endpoint.Repository.Refs.ContainsKey("refs/tags/" + release.TagName))
        {
            string? commitId = RepositoryView.ResolveCommit(endpoint.Repository, release.TagName);
            if (commitId is not null) commit = CommitResponseFor(context, project, RepositoryView.ReadCommit(endpoint.Repository, commitId));
        }
    }
    finally { endpoint.Gate.Release(); }
    string tag = Uri.EscapeDataString(release.TagName);
    string api = Origin(context) + "/api/v4/projects/" + project.Project.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/releases/" + tag;
    string browser = ProjectWebUrl(context, project) + "/-/releases/" + tag;
    List<ReleaseSourceResponse> sources = commit is null ? [] :
    [
        new("tar.gz", Origin(context) + "/api/v4/projects/" + project.Project.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/repository/archive.tar.gz?sha=" + tag),
        new("zip", Origin(context) + "/api/v4/projects/" + project.Project.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/repository/archive.zip?sha=" + tag)
    ];
    return new(release.TagName, release.Name, release.Description, release.CreatedAtUtc, release.ReleasedAtUtc,
        await AuthorFor(context, release.AuthorUserId, release.AuthorUsername), commit, commit is not null,
        new(release.Links.Count + release.Assets.Count + sources.Count, sources,
            [.. release.Links.Select(link => new ReleaseLinkResponse(link.Id, link.Name, link.Url, link.LinkType))],
            [.. release.Assets.Select(asset => new ReleaseFileResponse(asset.Id, asset.FileName, asset.Size, asset.Sha256,
                api + "/assets/files/" + Uri.EscapeDataString(asset.FileName), asset.UploadedByUsername, asset.CreatedAtUtc))]),
        browser);
}

string TagOf(HttpContext context)
{
    string tag = context.Request.RouteValues["tag"]?.ToString() ?? "";
    try { return Uri.UnescapeDataString(tag); } catch (UriFormatException) { return tag; }
}

async Task ListReleases(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context, "page", "per_page")) return;
    try
    {
        IReadOnlyList<Release> releases = await store.ListReleasesAsync(identity, project.Project.Id, context.RequestAborted);
        List<ReleaseResponse> response = [];
        foreach (Release release in releases) response.Add(await ReleaseResponseFor(context, project, release));
        await context.Response.WriteAsJsonAsync(response.ToArray(), ApiJson.Default.ReleaseResponseArray, null, context.RequestAborted);
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "project"); }
}

async Task ReadRelease(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context)) return;
    try
    {
        Release? release = await store.FindReleaseAsync(identity, project.Project.Id, TagOf(context), context.RequestAborted);
        if (release is null) { await ApiError(context, 404, "404 Release Not Found"); return; }
        await context.Response.WriteAsJsonAsync(await ReleaseResponseFor(context, project, release), ApiJson.Default.ReleaseResponse, null, context.RequestAborted);
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "project"); }
}

// A release needs its tag to exist when it is made. Afterwards the tag may go, and the release says so,
// because deleting a tag and deleting what was said about it are different acts.
async Task CreateRelease(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    ReleaseRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.ReleaseRequest, context.RequestAborted);
    if (request?.TagName is not { Length: > 0 }) { await ApiError(context, 400, "tag_name is required"); return; }
    RepositoryEndpoint endpoint = EndpointFor(project.Project);
    await endpoint.Gate.WaitAsync(context.RequestAborted);
    bool exists;
    try { exists = endpoint.Repository.Refs.ContainsKey("refs/tags/" + request.TagName.Trim()); }
    finally { endpoint.Gate.Release(); }
    if (!exists) { await ApiError(context, 400, "tag_name does not name a tag in this repository; create the tag first"); return; }
    try
    {
        Release release = await store.CreateReleaseAsync(identity, project.Project.Id, request.TagName, request.Name,
            request.Description ?? "", request.ReleasedAt, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(await ReleaseResponseFor(context, project, release), ApiJson.Default.ReleaseResponse, null, context.RequestAborted);
    }
    catch (CollaborationConflictException error) { await ApiError(context, 409, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task UpdateRelease(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    ReleaseRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.ReleaseRequest, context.RequestAborted);
    if (request is null || (request.Name is null && request.Description is null && request.ReleasedAt is null))
    { await ApiError(context, 400, "nothing to change"); return; }
    try
    {
        Release release = await store.UpdateReleaseAsync(identity, project.Project.Id, TagOf(context), request.Name,
            request.Description, request.ReleasedAt, context.RequestAborted);
        await context.Response.WriteAsJsonAsync(await ReleaseResponseFor(context, project, release), ApiJson.Default.ReleaseResponse, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Release Not Found"); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task DeleteRelease(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    try
    {
        await store.DeleteReleaseAsync(identity, project.Project.Id, TagOf(context), context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Release Not Found"); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
}

async Task AddReleaseLink(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    ReleaseLinkRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.ReleaseLinkRequest, context.RequestAborted);
    if (request?.Name is not { Length: > 0 } || request.Url is not { Length: > 0 }) { await ApiError(context, 400, "name and url are required"); return; }
    try
    {
        Release release = await store.AddReleaseLinkAsync(identity, project.Project.Id, TagOf(context), request.Name, request.Url,
            request.LinkType ?? "other", context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(await ReleaseResponseFor(context, project, release), ApiJson.Default.ReleaseResponse, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Release Not Found"); }
    catch (CollaborationConflictException error) { await ApiError(context, 409, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task DeleteReleaseLink(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    long linkId = Convert.ToInt64(context.Request.RouteValues["linkId"], System.Globalization.CultureInfo.InvariantCulture);
    try
    {
        Release release = await store.DeleteReleaseLinkAsync(identity, project.Project.Id, TagOf(context), linkId, context.RequestAborted);
        await context.Response.WriteAsJsonAsync(await ReleaseResponseFor(context, project, release), ApiJson.Default.ReleaseResponse, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException error) { await ApiError(context, 404, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
}

string FileNameOf(HttpContext context)
{
    string name = context.Request.RouteValues["fileName"]?.ToString() ?? "";
    try { return Uri.UnescapeDataString(name); } catch (UriFormatException) { return name; }
}

// The plain-HTTP half for an API client: the file is the request body, bounded before it is read. The
// browser's half arrives as an upload receipt through the transport ADR 0005 chose for it.
async Task PutReleaseAssetBytes(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (context.Request.ContentLength > GraftStore.MaximumAssetBytes) { await ApiError(context, 413, "the file exceeds the 32 MiB asset limit"); return; }
    byte[] bytes;
    using (MemoryStream buffer = new())
    {
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        if (buffer.Length > GraftStore.MaximumAssetBytes) { await ApiError(context, 413, "the file exceeds the 32 MiB asset limit"); return; }
        bytes = buffer.ToArray();
    }
    await StoreReleaseAsset(context, project, identity, FileNameOf(context), bytes);
}

async Task StoreReleaseAsset(HttpContext context, ProjectDetails project, AuthenticatedIdentity identity, string fileName, byte[] bytes)
{
    try
    {
        Release release = await store.PutReleaseAssetAsync(identity, project.Project.Id, TagOf(context), fileName, bytes, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(await ReleaseResponseFor(context, project, release), ApiJson.Default.ReleaseResponse, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Release Not Found"); }
    catch (CollaborationConflictException error) { await ApiError(context, 409, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

// Served exactly as a raw repository file is: an attachment of unknown type, with nosniff. An uploaded
// asset is somebody else's bytes on the origin that holds the session, and ADR 0017's reasoning applies
// without change.
async Task DownloadReleaseAsset(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context)) return;
    try
    {
        (ReleaseAsset Asset, byte[] Bytes)? found = await store.ReadReleaseAssetAsync(identity, project.Project.Id, TagOf(context), FileNameOf(context), context.RequestAborted);
        if (found is null) { await ApiError(context, 404, "404 File Not Found"); return; }
        context.Response.Headers.ETag = "\"" + found.Value.Asset.Sha256 + "\"";
        await WriteDownload(context, found.Value.Bytes, found.Value.Asset.FileName, "application/octet-stream");
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "project"); }
}

async Task DeleteReleaseAsset(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    try
    {
        Release release = await store.DeleteReleaseAssetAsync(identity, project.Project.Id, TagOf(context), FileNameOf(context), context.RequestAborted);
        await context.Response.WriteAsJsonAsync(await ReleaseResponseFor(context, project, release), ApiJson.Default.ReleaseResponse, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException error) { await ApiError(context, 404, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
}

// ---------------------------------------------------------------------------------------------------
// Search. One scope per request. A project search reads the project; a global search reads whatever the
// caller may see, which for a stranger is what the operator publishes and for a member is their projects.
// Nothing here is indexed: a search reads what is there, bounded, and says when it stopped early.
// ---------------------------------------------------------------------------------------------------

const int SearchLimit = 50;

static bool TryReadSearch(HttpContext context, string[] scopes, out string scope, out string search, out string error)
{
    scope = context.Request.Query["scope"].ToString();
    search = context.Request.Query["search"].ToString().Trim();
    error = "";
    if (!scopes.Contains(scope, StringComparer.Ordinal)) { error = "scope must be one of " + string.Join(", ", scopes); return false; }
    if (search.Length is 0 or > 200) { error = "search is required and at most 200 characters"; return false; }
    return true;
}

async Task ProjectSearch(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context, "scope", "search", "ref")) return;
    if (!TryReadSearch(context, ["issues", "merge_requests", "blobs", "wiki_blobs", "commits"], out string scope, out string search, out string error))
    { await ApiError(context, 400, error); return; }
    string revision = context.Request.Query["ref"].ToString() is { Length: > 0 } wanted ? wanted : "HEAD";

    List<IssueResponse> issues = [];
    List<MergeRequestResponse> proposals = [];
    List<BlobSearchResponse> blobs = [];
    List<WikiPageResponse> wiki = [];
    List<CommitResponse> commits = [];
    try
    {
        switch (scope)
        {
            case "issues":
                foreach (Issue issue in (await store.ListIssuesAsync(identity, project.Project.Id, new IssueFilter(Search: search), 0, SearchLimit, context.RequestAborted)).Issues)
                    issues.Add(await IssueResponseFor(context, project, issue));
                break;
            case "merge_requests":
                foreach (MergeProposal proposal in (await store.ListMergeProposalsAsync(identity, project.Project.Id, new MergeFilter(Search: search), 0, SearchLimit, context.RequestAborted)).Proposals)
                    proposals.Add(await MergeResponseFor(context, project, proposal, null));
                break;
            case "wiki_blobs":
                foreach (WikiPage page in await store.SearchWikiAsync(identity, project.Project.Id, search, SearchLimit, context.RequestAborted))
                    wiki.Add(WikiResponseFor(context, project, page, false));
                break;
            case "blobs":
            case "commits":
                // Repository content needs the repository grant, which a reader with only the conversation
                // grant does not hold; the two are the same for every caller today, and checked separately
                // so they stay separate.
                PermissionDecision browse = await DecideFor(context, identity, project.Project.Id, ProjectAction.BrowseRepository);
                if (!browse.Allowed) { await ApiError(context, 404, "project"); return; }
                RepositoryEndpoint endpoint = EndpointFor(project.Project);
                await endpoint.Gate.WaitAsync(context.RequestAborted);
                try
                {
                    if (scope == "blobs")
                    {
                        string? commitId = RepositoryView.ResolveCommit(endpoint.Repository, revision);
                        foreach (BlobMatch match in RepositorySearch.Blobs(endpoint.Repository, revision, search, SearchLimit, context.RequestAborted))
                            blobs.Add(new(Path.GetFileName(match.Path), match.Text, match.Path, match.Path, match.BlobId, commitId ?? revision,
                                match.Line, project.Project.Id,
                                ProjectWebUrl(context, project) + "?ref=" + Uri.EscapeDataString(revision) + "&file=" + Uri.EscapeDataString(match.Path)));
                    }
                    else
                        foreach (GitCommit commit in RepositorySearch.Commits(endpoint.Repository, revision, search, SearchLimit, context.RequestAborted))
                            commits.Add(CommitResponseFor(context, project, commit));
                }
                finally { endpoint.Gate.Release(); }
                break;
        }
    }
    catch (UnauthorizedAccessException) { await ApiError(context, 404, "project"); return; }
    catch (GitInputException error2) { await ApiError(context, 400, "search refused: " + error2.Message); return; }
    int count = issues.Count + proposals.Count + blobs.Count + wiki.Count + commits.Count;
    await context.Response.WriteAsJsonAsync(new SearchResponse(scope, search, count, count >= SearchLimit, [], issues, proposals, blobs, wiki, commits),
        ApiJson.Default.SearchResponse, null, context.RequestAborted);
}

async Task GlobalSearch(HttpContext context, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context, "scope", "search")) return;
    if (!TryReadSearch(context, ["projects", "issues", "merge_requests"], out string scope, out string search, out string error))
    { await ApiError(context, 400, error); return; }
    if (identity is null && !settings.PublicBrowsing) { await ApiError(context, 401, "authentication is required to search"); return; }
    bool includePublic = settings.PublicBrowsing;

    List<ProjectResponse> projects = [];
    List<IssueResponse> issues = [];
    List<MergeRequestResponse> proposals = [];
    switch (scope)
    {
        case "projects":
            foreach (ProjectDetails project in await store.SearchProjectsAsync(identity, search, includePublic, SearchLimit, context.RequestAborted))
                projects.Add(await ProjectResponseFor(context, project));
            break;
        case "issues":
            foreach (IssueHit hit in await store.SearchIssuesAsync(identity, search, includePublic, SearchLimit, context.RequestAborted))
            {
                ProjectDetails? project = await store.GetReadyProjectAsync(hit.Issue.ProjectId, context.RequestAborted);
                if (project is not null) issues.Add(await IssueResponseFor(context, project, hit.Issue));
            }
            break;
        case "merge_requests":
            foreach (ProposalHit hit in await store.SearchMergeProposalsAsync(identity, search, includePublic, SearchLimit, context.RequestAborted))
            {
                ProjectDetails? project = await store.GetReadyProjectAsync(hit.Proposal.ProjectId, context.RequestAborted);
                if (project is not null) proposals.Add(await MergeResponseFor(context, project, hit.Proposal, null));
            }
            break;
    }
    int count = projects.Count + issues.Count + proposals.Count;
    await context.Response.WriteAsJsonAsync(new SearchResponse(scope, search, count, count >= SearchLimit, projects, issues, proposals, [], [], []),
        ApiJson.Default.SearchResponse, null, context.RequestAborted);
}

app.MapGet("/api/search", async context =>
{
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: false);
    await GlobalSearch(context, browser?.Identity);
});

app.MapGet("/api/v4/search", async context =>
{
    (bool failed, AuthenticatedIdentity? identity) = await AuthenticateApiOptional(context);
    if (failed) return;
    await GlobalSearch(context, identity);
});

app.MapGet("/api/projects/{projectId:long}/search", async context =>
{
    var scope = await BrowserCollaboration(context, write: false);
    if (scope is not null) await ProjectSearch(context, scope.Value.Details, scope.Value.Identity);
});

app.MapGet("/api/v4/projects/{projectId}/search", async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is not null) await ProjectSearch(context, scope.Value.Details, scope.Value.Identity);
});

// ---------------------------------------------------------------------------------------------------
// Wiki, snippets and time tracking.
// ---------------------------------------------------------------------------------------------------

// A wiki page is rendered by the README renderer under the README's rules: raw HTML disabled, every link
// and image destination checked on the tree, output scanned before it is sent. Images cannot resolve to a
// repository path from a wiki page, so every image is downgraded to a link.
WikiPageResponse WikiResponseFor(HttpContext context, ProjectDetails project, WikiPage page, bool render)
{
    RenderedMarkdown? rendered = render && page.Content.Length > 0 ? MarkdownContent.Render(page.Content, _ => null) : null;
    return new(page.Slug, page.Title, page.Content, "markdown", rendered?.Html, page.CreatedByUsername, page.UpdatedByUsername,
        page.CreatedAtUtc, page.UpdatedAtUtc, page.Versions,
        ProjectWebUrl(context, project) + "/-/wiki/" + Uri.EscapeDataString(page.Slug), rendered?.HasDiagrams ?? false);
}

string SlugOf(HttpContext context)
{
    string slug = context.Request.RouteValues["slug"]?.ToString() ?? "";
    try { return Uri.UnescapeDataString(slug); } catch (UriFormatException) { return slug; }
}

async Task ListWiki(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context, "with_content")) return;
    IReadOnlyList<WikiPage> pages = await store.ListWikiPagesAsync(identity, project.Project.Id, context.RequestAborted);
    await context.Response.WriteAsJsonAsync([.. pages.Select(page => WikiResponseFor(context, project, page, false))],
        ApiJson.Default.WikiPageResponseArray, null, context.RequestAborted);
}

async Task ReadWiki(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context, "render_html", "version")) return;
    string version = context.Request.Query["version"].ToString();
    if (version.Length > 0)
    {
        if (!long.TryParse(version, out long versionId)) { await ApiError(context, 400, "version is an id"); return; }
        WikiPageVersion? old = await store.FindWikiVersionAsync(identity, project.Project.Id, SlugOf(context), versionId, context.RequestAborted);
        if (old is null) { await ApiError(context, 404, "404 Wiki Page Not Found"); return; }
        await context.Response.WriteAsJsonAsync(new WikiVersionResponse(old.Id, old.Title, old.Content, old.Message, old.AuthorUsername, old.CreatedAtUtc),
            ApiJson.Default.WikiVersionResponse, null, context.RequestAborted);
        return;
    }
    WikiPage? page = await store.FindWikiPageAsync(identity, project.Project.Id, SlugOf(context), context.RequestAborted);
    if (page is null) { await ApiError(context, 404, "404 Wiki Page Not Found"); return; }
    await context.Response.WriteAsJsonAsync(WikiResponseFor(context, project, page, true), ApiJson.Default.WikiPageResponse, null, context.RequestAborted);
}

async Task ListWikiVersions(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context)) return;
    IReadOnlyList<WikiPageVersion> versions = await store.ListWikiVersionsAsync(identity, project.Project.Id, SlugOf(context), context.RequestAborted);
    await context.Response.WriteAsJsonAsync([.. versions.Select(version => new WikiVersionResponse(version.Id, version.Title, "", version.Message, version.AuthorUsername, version.CreatedAtUtc))],
        ApiJson.Default.WikiVersionResponseArray, null, context.RequestAborted);
}

async Task CreateWiki(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    WikiPageRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.WikiPageRequest, context.RequestAborted);
    if (request?.Title is not { Length: > 0 }) { await ApiError(context, 400, "title is required"); return; }
    try
    {
        WikiPage page = await store.CreateWikiPageAsync(identity, project.Project.Id, request.Title, request.Content ?? "", request.Message ?? "", context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(WikiResponseFor(context, project, page, true), ApiJson.Default.WikiPageResponse, null, context.RequestAborted);
    }
    catch (CollaborationConflictException error) { await ApiError(context, 409, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task UpdateWiki(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    WikiPageRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.WikiPageRequest, context.RequestAborted);
    if (request is null || (request.Title is null && request.Content is null)) { await ApiError(context, 400, "title or content is required"); return; }
    try
    {
        WikiPage page = await store.UpdateWikiPageAsync(identity, project.Project.Id, SlugOf(context), request.Title, request.Content, request.Message ?? "", context.RequestAborted);
        await context.Response.WriteAsJsonAsync(WikiResponseFor(context, project, page, true), ApiJson.Default.WikiPageResponse, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Wiki Page Not Found"); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task DeleteWiki(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    try
    {
        await store.DeleteWikiPageAsync(identity, project.Project.Id, SlugOf(context), context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Wiki Page Not Found"); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
}

async Task<SnippetResponse> SnippetResponseFor(HttpContext context, ProjectDetails project, Snippet snippet, bool withContent)
{
    string id = snippet.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    return new(snippet.Id, snippet.Title, snippet.FileName, snippet.Description, withContent ? snippet.Content : null,
        await AuthorFor(context, snippet.AuthorUserId, snippet.AuthorUsername), snippet.CreatedAtUtc, snippet.UpdatedAtUtc,
        ProjectWebUrl(context, project) + "/-/snippets/" + id,
        Origin(context) + "/api/v4/projects/" + project.Project.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/snippets/" + id + "/raw");
}

long SnippetId(HttpContext context) => Convert.ToInt64(context.Request.RouteValues["snippetId"], System.Globalization.CultureInfo.InvariantCulture);

async Task ListSnippets(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context, "page", "per_page")) return;
    IReadOnlyList<Snippet> snippets = await store.ListSnippetsAsync(identity, project.Project.Id, context.RequestAborted);
    List<SnippetResponse> response = [];
    foreach (Snippet snippet in snippets) response.Add(await SnippetResponseFor(context, project, snippet, false));
    await context.Response.WriteAsJsonAsync(response.ToArray(), ApiJson.Default.SnippetResponseArray, null, context.RequestAborted);
}

async Task ReadSnippet(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity, bool raw)
{
    if (!await OnlyQuery(context)) return;
    Snippet? snippet = await store.FindSnippetAsync(identity, project.Project.Id, SnippetId(context), context.RequestAborted);
    if (snippet is null) { await ApiError(context, 404, "404 Snippet Not Found"); return; }
    if (raw)
    {
        // The same rule as a raw repository file: bytes to save, never a document on this origin.
        await WriteDownload(context, Encoding.UTF8.GetBytes(snippet.Content), snippet.FileName, "application/octet-stream");
        return;
    }
    await context.Response.WriteAsJsonAsync(await SnippetResponseFor(context, project, snippet, true), ApiJson.Default.SnippetResponse, null, context.RequestAborted);
}

async Task CreateSnippet(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    SnippetRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.SnippetRequest, context.RequestAborted);
    if (request?.Title is not { Length: > 0 } || request.FileName is not { Length: > 0 } || request.Content is null)
    { await ApiError(context, 400, "title, file_name and content are required"); return; }
    try
    {
        Snippet snippet = await store.CreateSnippetAsync(identity, project.Project.Id, request.Title, request.FileName, request.Content, request.Description ?? "", context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(await SnippetResponseFor(context, project, snippet, true), ApiJson.Default.SnippetResponse, null, context.RequestAborted);
    }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task UpdateSnippet(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    SnippetRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.SnippetRequest, context.RequestAborted);
    if (request is null || (request.Title is null && request.FileName is null && request.Content is null && request.Description is null))
    { await ApiError(context, 400, "nothing to change"); return; }
    try
    {
        Snippet snippet = await store.UpdateSnippetAsync(identity, project.Project.Id, SnippetId(context), request.Title, request.FileName, request.Content, request.Description, context.RequestAborted);
        await context.Response.WriteAsJsonAsync(await SnippetResponseFor(context, project, snippet, true), ApiJson.Default.SnippetResponse, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Snippet Not Found"); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task DeleteSnippet(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    try
    {
        await store.DeleteSnippetAsync(identity, project.Project.Id, SnippetId(context), context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Snippet Not Found"); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
}

// Time tracking, in GitLab's shape: four verbs on an issue or proposal and a stats read. A duration is
// GitLab's own vocabulary, `3h 30m`, and a negative one on spent time takes time back.
async Task<(long Estimate, long Spent)?> TimeTotals(ProjectDetails project, AuthenticatedIdentity? identity, CommentSubject subject, long iid, CancellationToken cancellationToken)
{
    if (subject == CommentSubject.Issue)
    {
        Issue? issue = await store.FindIssueAsync(identity, project.Project.Id, iid, cancellationToken);
        return issue is null ? null : (issue.TimeEstimateSeconds, issue.TimeSpentSeconds);
    }
    MergeProposal? proposal = await store.FindMergeProposalAsync(identity, project.Project.Id, iid, null, cancellationToken);
    return proposal is null ? null : (proposal.TimeEstimateSeconds, proposal.TimeSpentSeconds);
}

async Task WriteTimeStats(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity, CommentSubject subject)
{
    (long Estimate, long Spent)? totals = await TimeTotals(project, identity, subject, Number(context), context.RequestAborted);
    if (totals is null) { await ApiError(context, 404, subject == CommentSubject.Issue ? "404 Issue Not Found" : "404 Merge Request Not Found"); return; }
    await context.Response.WriteAsJsonAsync(TimeStatsFor(totals.Value.Estimate, totals.Value.Spent), ApiJson.Default.TimeStatsResponse, null, context.RequestAborted);
}

async Task TimeAction(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity, CommentSubject subject, string action)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    TimeRequest? request = null;
    if (context.Request.HasJsonContentType())
        request = await context.Request.ReadFromJsonAsync(ApiJson.Default.TimeRequest, context.RequestAborted);
    long seconds = 0;
    bool negative = false;
    if (action is "time_estimate" or "add_spent_time")
    {
        string duration = (request?.Duration ?? context.Request.Query["duration"].ToString()).Trim();
        if (duration.StartsWith('-')) { negative = true; duration = duration[1..]; }
        if (!TimeTracking.TryParse(duration, out seconds)) { await ApiError(context, 400, "duration is like 3h 30m, 2d or 1w"); return; }
    }
    DateOnly? spentOn = null;
    if (request?.SpentAt is { Length: > 0 } when)
    {
        if (!DateOnly.TryParseExact(when.Length >= 10 ? when[..10] : when, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out DateOnly day)) { await ApiError(context, 400, "spent_at is YYYY-MM-DD"); return; }
        spentOn = day;
    }
    try
    {
        switch (action)
        {
            case "time_estimate":
                if (negative) { await ApiError(context, 400, "an estimate is not negative"); return; }
                await store.SetTimeEstimateAsync(identity, project.Project.Id, subject, Number(context), seconds, context.RequestAborted);
                break;
            case "reset_time_estimate":
                await store.SetTimeEstimateAsync(identity, project.Project.Id, subject, Number(context), 0, context.RequestAborted);
                break;
            case "add_spent_time":
                await store.AddTimeSpentAsync(identity, project.Project.Id, subject, Number(context), negative ? -seconds : seconds, spentOn, request?.Summary ?? "", context.RequestAborted);
                break;
            case "reset_spent_time":
                await store.ResetTimeSpentAsync(identity, project.Project.Id, subject, Number(context), context.RequestAborted);
                break;
        }
        await WriteTimeStats(context, project, identity, subject);
    }
    catch (CollaborationNotFoundException error) { await ApiError(context, 404, error.Message); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentOutOfRangeException) { await ApiError(context, 400, "duration is out of range"); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
}

async Task ListTime(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity, CommentSubject subject)
{
    if (!await OnlyQuery(context)) return;
    try
    {
        IReadOnlyList<TimeEntry> entries = await store.ListTimeEntriesAsync(identity, project.Project.Id, subject, Number(context), context.RequestAborted);
        await context.Response.WriteAsJsonAsync([.. entries.Select(entry => new TimeEntryResponse(entry.Id, entry.Username, entry.Seconds,
            TimeTracking.Humanize(Math.Abs(entry.Seconds)) is { } human ? (entry.Seconds < 0 ? "-" + human : human) : null,
            entry.SpentOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), entry.Summary, entry.CreatedAtUtc))],
            ApiJson.Default.TimeEntryResponseArray, null, context.RequestAborted);
    }
    catch (CollaborationNotFoundException error) { await ApiError(context, 404, error.Message); }
}

// ---------------------------------------------------------------------------------------------------
// Package feeds and the container registry. Bytes live beside the repository; the store keeps what a
// listing needs. Reading follows the repository, publishing needs a push. Package clients authenticate in
// their own ways, so every way a token can arrive is accepted here.
// ---------------------------------------------------------------------------------------------------

async Task<AuthenticatedIdentity?> AuthenticateAnyToken(HttpContext context)
{
    string token = context.Request.Headers["PRIVATE-TOKEN"].ToString();
    if (token.Length == 0) token = context.Request.Headers["X-NuGet-ApiKey"].ToString();
    string authorization = context.Request.Headers.Authorization.ToString();
    if (token.Length == 0 && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = authorization[7..];
    if (token.Length > 0) return await store.ResolveTokenAsync(token, context.RequestAborted);
    return await Authenticate(context);
}

/// <summary>Resolves the project a package route names and decides the action, challenging with Basic when
/// nobody is identified, because NuGet and Docker send credentials only once asked.</summary>
async Task<(ProjectDetails Details, AuthenticatedIdentity? Identity)?> PackageProject(HttpContext context, ProjectAction action, string realm)
{
    AuthenticatedIdentity? identity = await AuthenticateAnyToken(context);
    string identifier = context.Request.RouteValues["projectId"]?.ToString() ?? "";
    try { identifier = Uri.UnescapeDataString(identifier); } catch (UriFormatException) { identifier = ""; }
    ProjectDetails? details;
    if (long.TryParse(identifier, out long id)) details = await store.GetReadyProjectAsync(id, context.RequestAborted);
    else
    {
        string[] path = identifier.Split('/');
        details = path.Length switch
        {
            2 => await store.FindReadyProjectDetailsAsync(path[0], path[1], context.RequestAborted),
            1 => await store.FindReadyProjectDetailsByGlobalSlugAsync(path[0], context.RequestAborted),
            _ => null
        };
    }
    PermissionDecision? decision = details is null ? null : await DecideFor(context, identity, details.Project.Id, action);
    if (decision is { Allowed: true }) return (details!, identity);
    if (identity is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"" + realm + "\", charset=\"UTF-8\"";
        return null;
    }
    await ApiError(context, details is null || action is ProjectAction.BrowseRepository or ProjectAction.FetchRepository ? 404 : 403,
        details is null ? "404 Project Not Found" : decision!.Explanation);
    return null;
}

ProjectFiles FilesOf(ProjectInfo project) => new(Path.Combine(settings.Repositories, project.StorageId));

PackageResponse PackageResponseFor(HttpContext context, ProjectDetails project, Package package)
{
    string api = Origin(context) + "/api/v4/projects/" + project.Project.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/packages/";
    return new(package.Id, package.Name, package.Version, package.PackageType, package.Description, package.Authors, package.CreatedByUsername,
        package.CreatedAtUtc, package.UpdatedAtUtc, ProjectWebUrl(context, project) + "/-/packages",
        [.. package.Files.Select(file => new PackageFileResponse(file.Id, file.FileName, file.Size, file.Sha256, file.CreatedAtUtc,
            package.PackageType == "nuget"
                ? api + "nuget/download/" + Uri.EscapeDataString(package.Name.ToLowerInvariant()) + "/" + Uri.EscapeDataString(package.Version.ToLowerInvariant()) + "/" + Uri.EscapeDataString(file.FileName)
                : api + "generic/" + Uri.EscapeDataString(package.Name) + "/" + Uri.EscapeDataString(package.Version) + "/" + Uri.EscapeDataString(file.FileName)))]);
}

async Task ListPackages(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context, "page", "per_page", "package_type", "package_name", "order_by", "sort")) return;
    string type = context.Request.Query["package_type"].ToString();
    if (type is not ("" or "generic" or "nuget")) { await ApiError(context, 400, "package_type must be generic or nuget"); return; }
    IReadOnlyList<Package> packages = await store.ListPackagesAsync(identity, project.Project.Id, type.Length == 0 ? null : type,
        Optional(context, "package_name"), context.RequestAborted);
    await context.Response.WriteAsJsonAsync([.. packages.Select(package => PackageResponseFor(context, project, package))], ApiJson.Default.PackageResponseArray, null, context.RequestAborted);
}

async Task ReadPackage(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    long packageId = Convert.ToInt64(context.Request.RouteValues["packageId"], System.Globalization.CultureInfo.InvariantCulture);
    Package? package = await store.FindPackageAsync(identity, project.Project.Id, packageId, context.RequestAborted);
    if (package is null) { await ApiError(context, 404, "404 Package Not Found"); return; }
    await context.Response.WriteAsJsonAsync(PackageResponseFor(context, project, package), ApiJson.Default.PackageResponse, null, context.RequestAborted);
}

async Task DeletePackage(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (identity is null) { await ApiError(context, 401, "authentication is required"); return; }
    long packageId = Convert.ToInt64(context.Request.RouteValues["packageId"], System.Globalization.CultureInfo.InvariantCulture);
    try
    {
        Package package = await store.DeletePackageAsync(identity, project.Project.Id, packageId, context.RequestAborted);
        FilesOf(project.Project).DeletePackageDirectory(package.PackageType, package.Name, package.Version);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (CollaborationNotFoundException) { await ApiError(context, 404, "404 Package Not Found"); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
}

async Task DownloadPackageFile(HttpContext context, ProjectDetails project, Package package, string fileName)
{
    PackageFile? file = package.Files.FirstOrDefault(candidate => string.Equals(candidate.FileName, fileName, StringComparison.OrdinalIgnoreCase));
    string path = file is null ? "" : FilesOf(project.Project).PackagePath(package.PackageType, package.Name, package.Version, file.FileName);
    if (file is null || !File.Exists(path)) { await ApiError(context, 404, "404 File Not Found"); return; }
    context.Response.ContentType = "application/octet-stream";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.ContentDisposition = "attachment; filename=\"" + new string([.. file.FileName.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')]) + "\"";
    context.Response.Headers.ETag = "\"" + file.Sha256 + "\"";
    using FileStream content = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
    context.Response.ContentLength = content.Length;
    await content.CopyToAsync(context.Response.Body, context.RequestAborted);
}

// Generic packages: PUT a file under a name and a version, GET it back. GitLab's own routes.
async Task PutGenericPackage(HttpContext context, ProjectDetails project, AuthenticatedIdentity identity)
{
    string name = Uri.UnescapeDataString(context.Request.RouteValues["name"]?.ToString() ?? "");
    string version = Uri.UnescapeDataString(context.Request.RouteValues["version"]?.ToString() ?? "");
    string fileName = Uri.UnescapeDataString(context.Request.RouteValues["fileName"]?.ToString() ?? "");
    if (context.Request.ContentLength > ProjectFiles.MaximumPackageBytes) { await ApiError(context, 413, "the file exceeds the 2 GiB package limit"); return; }
    ProjectFiles files = FilesOf(project.Project);
    string destination;
    try { destination = files.PackagePath("generic", name, version, fileName); }
    catch (ArgumentException) { await ApiError(context, 400, "package path"); return; }
    if (name.Contains("..", StringComparison.Ordinal) || version.Contains('/') || fileName.Contains('/') || fileName.Contains('\\') || fileName.StartsWith('.'))
    { await ApiError(context, 400, "package name, version and file name are not paths"); return; }
    (string sha, long size) = await files.WriteAsync(destination, context.Request.Body, ProjectFiles.MaximumPackageBytes, context.RequestAborted);
    try
    {
        await store.PutPackageFileAsync(identity, project.Project.Id, "generic", name, version, fileName, size, sha, cancellationToken: context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(new ErrorResponse("201 Created"), ApiJson.Default.ErrorResponse, null, context.RequestAborted);
    }
    catch (UnauthorizedAccessException error) { File.Delete(destination); await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { File.Delete(destination); await ApiError(context, 400, error.Message); }
}

async Task GetGenericPackage(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    string name = Uri.UnescapeDataString(context.Request.RouteValues["name"]?.ToString() ?? "");
    string version = Uri.UnescapeDataString(context.Request.RouteValues["version"]?.ToString() ?? "");
    string fileName = Uri.UnescapeDataString(context.Request.RouteValues["fileName"]?.ToString() ?? "");
    Package? package = await store.FindPackageAsync(identity, project.Project.Id, "generic", name, version, context.RequestAborted);
    if (package is null) { await ApiError(context, 404, "404 Package Not Found"); return; }
    await DownloadPackageFile(context, project, package, fileName);
}

// NuGet v3: a service index naming four resources, all served here.
string NuGetBase(HttpContext context, ProjectDetails project) =>
    Origin(context) + "/api/v4/projects/" + project.Project.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/packages/nuget";

async Task NuGetIndex(HttpContext context, ProjectDetails project)
{
    string root = NuGetBase(context, project);
    await context.Response.WriteAsJsonAsync(new NuGetServiceIndex("3.0.0",
    [
        new(root, "PackagePublish/2.0.0"),
        new(root + "/download/", "PackageBaseAddress/3.0.0"),
        new(root + "/metadata/", "RegistrationsBaseUrl/3.6.0"),
        new(root + "/metadata/", "RegistrationsBaseUrl/3.4.0"),
        new(root + "/metadata/", "RegistrationsBaseUrl"),
        new(root + "/query", "SearchQueryService/3.5.0"),
        new(root + "/query", "SearchQueryService/3.0.0-beta"),
        new(root + "/query", "SearchQueryService")
    ]), ApiJson.Default.NuGetServiceIndex, null, context.RequestAborted);
}

async Task NuGetPush(HttpContext context, ProjectDetails project, AuthenticatedIdentity identity)
{
    if (!context.Request.HasFormContentType) { await ApiError(context, 400, "a package is pushed as multipart form data"); return; }
    IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted);
    IFormFile? upload = form.Files.Count == 0 ? null : form.Files[0];
    if (upload is null || upload.Length == 0) { await ApiError(context, 400, "the form carries no package"); return; }
    if (upload.Length > ProjectFiles.MaximumPackageBytes) { await ApiError(context, 413, "the package exceeds the 2 GiB limit"); return; }
    ProjectFiles files = FilesOf(project.Project);
    string staging = files.UploadPath("nuget-" + Guid.NewGuid().ToString("N"));
    try
    {
        await using (Stream source = upload.OpenReadStream())
            await files.WriteAsync(staging, source, ProjectFiles.MaximumPackageBytes, context.RequestAborted);
        NuGetManifest? manifest = NuGetManifest.Read(staging);
        if (manifest is null) { await ApiError(context, 400, "the file is not a NuGet package with a .nuspec"); return; }
        string fileName = manifest.Id.ToLowerInvariant() + "." + manifest.Version.ToLowerInvariant() + ".nupkg";
        string destination = files.PackagePath("nuget", manifest.Id, manifest.Version, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(staging, destination, overwrite: true);
        FileInfo stored = new(destination);
        string sha;
        using (FileStream read = stored.OpenRead()) sha = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(read, context.RequestAborted));
        await store.PutPackageFileAsync(identity, project.Project.Id, "nuget", manifest.Id, manifest.Version, fileName, stored.Length, sha,
            manifest.Description, manifest.Authors, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
    }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); }
    catch (ArgumentException error) { await ApiError(context, 400, error.Message); }
    finally { if (File.Exists(staging)) File.Delete(staging); }
}

async Task NuGetVersions(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    string id = context.Request.RouteValues["packageName"]?.ToString() ?? "";
    IReadOnlyList<Package> packages = await store.ListPackagesAsync(identity, project.Project.Id, "nuget", id, context.RequestAborted);
    if (packages.Count == 0) { await ApiError(context, 404, "404 Package Not Found"); return; }
    await context.Response.WriteAsJsonAsync(new NuGetVersions([.. packages.Select(package => package.Version.ToLowerInvariant()).OrderBy(v => v, StringComparer.Ordinal)]),
        ApiJson.Default.NuGetVersions, null, context.RequestAborted);
}

async Task NuGetDownload(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    string id = context.Request.RouteValues["packageName"]?.ToString() ?? "";
    string version = context.Request.RouteValues["version"]?.ToString() ?? "";
    string fileName = context.Request.RouteValues["fileName"]?.ToString() ?? "";
    Package? package = await store.FindPackageAsync(identity, project.Project.Id, "nuget", id, version, context.RequestAborted);
    if (package is null) { await ApiError(context, 404, "404 Package Not Found"); return; }
    if (fileName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
    {
        // The manifest alone, read out of the package it came in.
        PackageFile? nupkg = package.Files.FirstOrDefault(file => file.FileName.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase));
        string path = nupkg is null ? "" : FilesOf(project.Project).PackagePath("nuget", package.Name, package.Version, nupkg.FileName);
        if (nupkg is null || !File.Exists(path)) { await ApiError(context, 404, "404 File Not Found"); return; }
        using System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(path);
        System.IO.Compression.ZipArchiveEntry? spec = zip.Entries.FirstOrDefault(entry => !entry.FullName.Contains('/') && entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        if (spec is null) { await ApiError(context, 404, "404 File Not Found"); return; }
        context.Response.ContentType = "application/xml";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        using Stream specStream = spec.Open();
        await specStream.CopyToAsync(context.Response.Body, context.RequestAborted);
        return;
    }
    await DownloadPackageFile(context, project, package, fileName);
}

NuGetCatalogEntry NuGetEntryFor(HttpContext context, ProjectDetails project, Package package)
{
    string root = NuGetBase(context, project);
    string id = package.Name.ToLowerInvariant();
    string version = package.Version.ToLowerInvariant();
    string content = root + "/download/" + Uri.EscapeDataString(id) + "/" + Uri.EscapeDataString(version) + "/" + Uri.EscapeDataString(id + "." + version + ".nupkg");
    PackageFile? nupkg = package.Files.FirstOrDefault(file => file.FileName.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase));
    string path = nupkg is null ? "" : FilesOf(project.Project).PackagePath("nuget", package.Name, package.Version, nupkg.FileName);
    NuGetManifest? manifest = nupkg is null || !File.Exists(path) ? null : NuGetManifest.Read(path);
    return new(root + "/metadata/" + Uri.EscapeDataString(id) + "/" + Uri.EscapeDataString(version) + ".json",
        package.Authors, package.Description, package.Name, package.Version, content, true, package.CreatedAtUtc,
        [.. (manifest?.DependencyGroups ?? []).Select(group => new NuGetDependencyGroup(group.Framework,
            [.. group.Dependencies.Select(dependency => new NuGetDependency(dependency.Id, dependency.Range))]))]);
}

async Task NuGetRegistration(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    string id = context.Request.RouteValues["packageName"]?.ToString() ?? "";
    string? version = context.Request.RouteValues["version"]?.ToString();
    IReadOnlyList<Package> packages = await store.ListPackagesAsync(identity, project.Project.Id, "nuget", id, context.RequestAborted);
    if (packages.Count == 0) { await ApiError(context, 404, "404 Package Not Found"); return; }
    List<NuGetRegistrationLeaf> leaves = [.. packages.OrderBy(package => package.Version, StringComparer.OrdinalIgnoreCase).Select(package =>
    {
        NuGetCatalogEntry entry = NuGetEntryFor(context, project, package);
        return new NuGetRegistrationLeaf(entry.Id, entry.PackageContent, entry);
    })];
    if (version is not null)
    {
        NuGetRegistrationLeaf? leaf = leaves.FirstOrDefault(candidate => string.Equals(candidate.CatalogEntry.Version, version, StringComparison.OrdinalIgnoreCase));
        if (leaf is null) { await ApiError(context, 404, "404 Package Not Found"); return; }
        await context.Response.WriteAsJsonAsync(new NuGetRegistrationIndex(1, [new(leaf.Id, 1, leaf.CatalogEntry.Version, leaf.CatalogEntry.Version, [leaf])]),
            ApiJson.Default.NuGetRegistrationIndex, null, context.RequestAborted);
        return;
    }
    string indexUrl = NuGetBase(context, project) + "/metadata/" + Uri.EscapeDataString(id.ToLowerInvariant()) + "/index.json";
    await context.Response.WriteAsJsonAsync(new NuGetRegistrationIndex(1,
        [new(indexUrl, leaves.Count, leaves[0].CatalogEntry.Version, leaves[^1].CatalogEntry.Version, leaves)]),
        ApiJson.Default.NuGetRegistrationIndex, null, context.RequestAborted);
}

async Task NuGetQuery(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    string query = context.Request.Query["q"].ToString().Trim().ToLowerInvariant();
    int skip = int.TryParse(context.Request.Query["skip"], out int s) && s >= 0 ? s : 0;
    int take = int.TryParse(context.Request.Query["take"], out int t) && t is > 0 and <= 100 ? t : 20;
    IReadOnlyList<Package> packages = await store.ListPackagesAsync(identity, project.Project.Id, "nuget", null, context.RequestAborted);
    List<NuGetSearchResult> results = [];
    foreach (IGrouping<string, Package> group in packages.GroupBy(package => package.Name.ToLowerInvariant()))
    {
        if (query.Length > 0 && !group.Key.Contains(query, StringComparison.Ordinal) && !group.Any(package => package.Description.Contains(query, StringComparison.OrdinalIgnoreCase))) continue;
        Package latest = group.OrderBy(package => package.CreatedAtUtc).Last();
        results.Add(new(latest.Name, latest.Version, latest.Description, latest.Authors, 0, false,
            [.. group.Select(package => new NuGetSearchVersion(NuGetEntryFor(context, project, package).Id, package.Version, 0))]));
    }
    await context.Response.WriteAsJsonAsync(new NuGetSearchResponse(results.Count, [.. results.Skip(skip).Take(take)]), ApiJson.Default.NuGetSearchResponse, null, context.RequestAborted);
}

// Browser routes for a project's packages and images.
ContainerRepositoryResponse ImageResponseFor(HttpContext context, ProjectDetails project, ContainerRepository image)
{
    string path = project.Project.Namespace + "/" + project.Project.Slug + (image.Name.Length == 0 ? "" : "/" + image.Name);
    string host = context.Request.Host.Value ?? "";
    return new(image.Id, image.Name, path, host + "/" + path, image.CreatedAtUtc, image.Tags.Count,
        [.. image.Tags.Select(tag => new ContainerTagResponse(tag.Tag, path + ":" + tag.Tag, host + "/" + path + ":" + tag.Tag, tag.Digest, tag.MediaType, tag.Size, tag.UpdatedAtUtc))]);
}

async Task ListImages(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity)
{
    if (!await OnlyQuery(context, "page", "per_page", "tags")) return;
    IReadOnlyList<ContainerRepository> images = await store.ListContainerRepositoriesAsync(identity, project.Project.Id, context.RequestAborted);
    await context.Response.WriteAsJsonAsync([.. images.Select(image => ImageResponseFor(context, project, image))], ApiJson.Default.ContainerRepositoryResponseArray, null, context.RequestAborted);
}

foreach ((string root, bool browserFamily) in new[] { ("/api/projects/{projectId:long}", true), ("/api/v4/projects/{projectId}", false) })
{
    async Task<(ProjectDetails Details, AuthenticatedIdentity? Identity)?> Scope(HttpContext context, bool write) =>
        browserFamily ? await BrowserCollaboration(context, write) : await PackageProject(context, write ? ProjectAction.PushRepository : ProjectAction.BrowseRepository, "Graft Packages");

    app.MapGet(root + "/packages", async context => { var scope = await Scope(context, false); if (scope is not null) await ListPackages(context, scope.Value.Details, scope.Value.Identity); });
    app.MapGet(root + "/packages/{packageId:long}", async context => { var scope = await Scope(context, false); if (scope is not null) await ReadPackage(context, scope.Value.Details, scope.Value.Identity); });
    app.MapDelete(root + "/packages/{packageId:long}", async context => { var scope = await Scope(context, true); if (scope is not null) await DeletePackage(context, scope.Value.Details, scope.Value.Identity); });
    app.MapGet(root + "/packages/generic/{name}/{version}/{fileName}", async context => { var scope = await Scope(context, false); if (scope is not null) await GetGenericPackage(context, scope.Value.Details, scope.Value.Identity); });
    app.MapGet(root + "/registry/repositories", async context => { var scope = await Scope(context, false); if (scope is not null) await ListImages(context, scope.Value.Details, scope.Value.Identity); });
    if (browserFamily)
    {
        app.MapGet(root + "/packages/{packageId:long}/files/{fileName}", async context =>
        {
            var scope = await Scope(context, false);
            if (scope is null) return;
            long packageId = Convert.ToInt64(context.Request.RouteValues["packageId"], System.Globalization.CultureInfo.InvariantCulture);
            Package? package = await store.FindPackageAsync(scope.Value.Identity, scope.Value.Details.Project.Id, packageId, context.RequestAborted);
            if (package is null) { await ApiError(context, 404, "404 Package Not Found"); return; }
            await DownloadPackageFile(context, scope.Value.Details, package, Uri.UnescapeDataString(context.Request.RouteValues["fileName"]?.ToString() ?? ""));
        });
        continue;
    }
    app.MapMethods(root + "/packages/generic/{name}/{version}/{fileName}", ["PUT"], async context =>
    {
        var scope = await PackageProject(context, ProjectAction.PushRepository, "Graft Packages");
        if (scope is not null) await PutGenericPackage(context, scope.Value.Details, scope.Value.Identity!);
    });
    app.MapGet(root + "/packages/nuget/index.json", async context => { var scope = await Scope(context, false); if (scope is not null) await NuGetIndex(context, scope.Value.Details); });
    // One mapping: ASP.NET already matches the trailing slash a NuGet client sends, and mapping both spellings
    // makes the request ambiguous rather than doubly served.
    app.MapMethods(root + "/packages/nuget", ["PUT"], async context => { var scope = await PackageProject(context, ProjectAction.PushRepository, "Graft Packages"); if (scope is not null) await NuGetPush(context, scope.Value.Details, scope.Value.Identity!); });
    app.MapGet(root + "/packages/nuget/download/{packageName}/index.json", async context => { var scope = await Scope(context, false); if (scope is not null) await NuGetVersions(context, scope.Value.Details, scope.Value.Identity); });
    app.MapGet(root + "/packages/nuget/download/{packageName}/{version}/{fileName}", async context => { var scope = await Scope(context, false); if (scope is not null) await NuGetDownload(context, scope.Value.Details, scope.Value.Identity); });
    app.MapGet(root + "/packages/nuget/metadata/{packageName}/index.json", async context => { var scope = await Scope(context, false); if (scope is not null) await NuGetRegistration(context, scope.Value.Details, scope.Value.Identity); });
    app.MapGet(root + "/packages/nuget/metadata/{packageName}/{version}.json", async context => { var scope = await Scope(context, false); if (scope is not null) await NuGetRegistration(context, scope.Value.Details, scope.Value.Identity); });
    app.MapGet(root + "/packages/nuget/query", async context => { var scope = await Scope(context, false); if (scope is not null) await NuGetQuery(context, scope.Value.Details, scope.Value.Identity); });
}

// ---------------------------------------------------------------------------------------------------
// The container registry: the Docker registry HTTP API, version 2. An image name is a project path and
// optionally more segments beneath it. Blobs are content-addressed per project and shared by its images.
// ---------------------------------------------------------------------------------------------------

static async Task RegistryError(HttpContext context, int status, string code, string message)
{
    context.Response.StatusCode = status;
    context.Response.Headers["Docker-Distribution-Api-Version"] = "registry/2.0";
    await context.Response.WriteAsJsonAsync(new RegistryErrors([new(code, message)]), ApiJson.Default.RegistryErrors, null, context.RequestAborted);
}

async Task<(ProjectDetails Details, AuthenticatedIdentity? Identity, string Image)?> RegistryProject(HttpContext context, string[] nameSegments, ProjectAction action)
{
    AuthenticatedIdentity? identity = await AuthenticateAnyToken(context);
    ProjectDetails? details = null;
    string image = "";
    if (nameSegments.Length >= 2)
    {
        details = string.Equals(nameSegments[0], GlobalNamespace, StringComparison.OrdinalIgnoreCase)
            ? await store.FindReadyProjectDetailsByGlobalSlugAsync(nameSegments[1], context.RequestAborted)
            : await store.FindReadyProjectDetailsAsync(nameSegments[0], nameSegments[1], context.RequestAborted);
        image = string.Join('/', nameSegments.Skip(2));
    }
    PermissionDecision? decision = details is null ? null : await DecideFor(context, identity, details.Project.Id, action);
    if (decision is { Allowed: true }) return (details!, identity, image);
    if (identity is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers["Docker-Distribution-Api-Version"] = "registry/2.0";
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Graft Registry\"";
        await context.Response.WriteAsJsonAsync(new RegistryErrors([new("UNAUTHORIZED", "authentication required")]), ApiJson.Default.RegistryErrors, null, context.RequestAborted);
        return null;
    }
    await RegistryError(context, details is null ? 404 : 403, details is null ? "NAME_UNKNOWN" : "DENIED", details is null ? "repository name not known to registry" : decision!.Explanation);
    return null;
}

static bool IsDigest(string value) => value.StartsWith("sha256:", StringComparison.Ordinal) && value.Length == 71 && value[7..].All(char.IsAsciiHexDigitLower);

app.MapGet("/v2/", async context =>
{
    AuthenticatedIdentity? identity = await AuthenticateAnyToken(context);
    context.Response.Headers["Docker-Distribution-Api-Version"] = "registry/2.0";
    if (identity is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Graft Registry\"";
        await context.Response.WriteAsJsonAsync(new RegistryErrors([new("UNAUTHORIZED", "authentication required")]), ApiJson.Default.RegistryErrors, null, context.RequestAborted);
        return;
    }
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync("{}", context.RequestAborted);
});

app.MapGet("/v2/_catalog", async context =>
{
    AuthenticatedIdentity? identity = await AuthenticateAnyToken(context);
    context.Response.Headers["Docker-Distribution-Api-Version"] = "registry/2.0";
    if (identity is null) { await RegistryError(context, 401, "UNAUTHORIZED", "authentication required"); return; }
    List<string> names = [];
    foreach (ProjectDetails project in (await store.ListAccessibleProjectsAsync(identity, 0, 100, context.RequestAborted)).Projects)
        foreach (ContainerRepository image in await store.ListContainerRepositoriesAsync(identity, project.Project.Id, context.RequestAborted))
            names.Add(project.Project.Namespace + "/" + project.Project.Slug + (image.Name.Length == 0 ? "" : "/" + image.Name));
    await context.Response.WriteAsJsonAsync(new RegistryCatalog(names), ApiJson.Default.RegistryCatalog, null, context.RequestAborted);
});

app.MapMethods("/v2/{**rest}", ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"], async context =>
{
    context.Response.Headers["Docker-Distribution-Api-Version"] = "registry/2.0";
    string rest = context.Request.RouteValues["rest"]?.ToString() ?? "";
    string[] segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
    // The operation is the last keyword segment; everything before it is the image name.
    int keyword = -1;
    for (int index = segments.Length - 1; index >= 0; index--)
        if (segments[index] is "blobs" or "manifests" or "tags") { keyword = index; break; }
    if (keyword < 2) { await RegistryError(context, 404, "NAME_UNKNOWN", "repository name not known to registry"); return; }
    string[] name = segments[..keyword];
    string[] operation = segments[(keyword + 1)..];
    string verb = segments[keyword];
    string method = context.Request.Method;
    bool write = method is "POST" or "PUT" or "PATCH" or "DELETE";
    var scope = await RegistryProject(context, name, write ? ProjectAction.PushRepository : ProjectAction.FetchRepository);
    if (scope is null) return;
    (ProjectDetails project, AuthenticatedIdentity? identity, string image) = scope.Value;
    ProjectFiles files = FilesOf(project.Project);
    string location = "/v2/" + string.Join('/', name);

    if (verb == "tags" && operation is ["list"] && method == "GET")
    {
        ContainerRepository? repository = await store.FindContainerRepositoryAsync(identity, project.Project.Id, image, context.RequestAborted);
        await context.Response.WriteAsJsonAsync(new RegistryTagList(string.Join('/', name), [.. (repository?.Tags ?? []).Select(tag => tag.Tag)]),
            ApiJson.Default.RegistryTagList, null, context.RequestAborted);
        return;
    }

    if (verb == "blobs" && operation.Length == 1 && method is "GET" or "HEAD")
    {
        string digest = operation[0];
        string path = IsDigest(digest) ? files.BlobPath(digest[7..]) : "";
        if (path.Length == 0 || !File.Exists(path)) { await RegistryError(context, 404, "BLOB_UNKNOWN", "blob unknown to registry"); return; }
        FileInfo blob = new(path);
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = blob.Length;
        context.Response.Headers["Docker-Content-Digest"] = digest;
        if (method == "HEAD") return;
        using FileStream content = blob.OpenRead();
        await content.CopyToAsync(context.Response.Body, context.RequestAborted);
        return;
    }

    if (verb == "blobs" && operation is ["uploads"] && method == "POST")
    {
        string uploadId = Guid.NewGuid().ToString("N");
        string? digest = Optional(context, "digest");
        if (digest is not null)
        {
            // Monolithic: the whole blob in this request.
            if (!IsDigest(digest)) { await RegistryError(context, 400, "DIGEST_INVALID", "provided digest did not match uploaded content"); return; }
            try
            {
                await files.AppendUploadAsync(uploadId, context.Request.Body, context.RequestAborted);
                (string sha, _) = await files.CompleteUploadAsync(uploadId, context.RequestAborted);
                // The bytes are kept under what they actually hash to, which is harmless and content-addressed, and
                // the client is told. Deleting them would delete a blob an honest push may already have stored.
                if ("sha256:" + sha != digest) { await RegistryError(context, 400, "DIGEST_INVALID", "provided digest did not match uploaded content"); return; }
            }
            catch (InvalidDataException) { files.AbandonUpload(uploadId); await RegistryError(context, 413, "BLOB_UPLOAD_INVALID", "blob exceeds the 8 GiB limit"); return; }
            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.Headers.Location = location + "/blobs/" + digest;
            context.Response.Headers["Docker-Content-Digest"] = digest;
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(files.UploadPath(uploadId))!);
        await File.WriteAllBytesAsync(files.UploadPath(uploadId), [], context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status202Accepted;
        context.Response.Headers.Location = location + "/blobs/uploads/" + uploadId;
        context.Response.Headers["Docker-Upload-UUID"] = uploadId;
        context.Response.Headers["Range"] = "0-0";
        return;
    }

    if (verb == "blobs" && operation is ["uploads", string uploadKey] && ProjectFiles.IsUploadId(uploadKey))
    {
        if (!File.Exists(files.UploadPath(uploadKey))) { await RegistryError(context, 404, "BLOB_UPLOAD_UNKNOWN", "blob upload unknown to registry"); return; }
        switch (method)
        {
            case "PATCH":
            case "PUT":
                long length;
                try { length = await files.AppendUploadAsync(uploadKey, context.Request.Body, context.RequestAborted); }
                catch (InvalidDataException) { files.AbandonUpload(uploadKey); await RegistryError(context, 413, "BLOB_UPLOAD_INVALID", "blob exceeds the 8 GiB limit"); return; }
                if (method == "PATCH")
                {
                    context.Response.StatusCode = StatusCodes.Status202Accepted;
                    context.Response.Headers.Location = location + "/blobs/uploads/" + uploadKey;
                    context.Response.Headers["Docker-Upload-UUID"] = uploadKey;
                    context.Response.Headers["Range"] = "0-" + Math.Max(0, length - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return;
                }
                string? digest = Optional(context, "digest");
                if (digest is null || !IsDigest(digest)) { await RegistryError(context, 400, "DIGEST_INVALID", "digest is required to complete an upload"); return; }
                (string sha, _) = await files.CompleteUploadAsync(uploadKey, context.RequestAborted);
                if ("sha256:" + sha != digest)
                {
                    // The bytes are kept under what they actually hash to, which is harmless, and the client is told.
                    await RegistryError(context, 400, "DIGEST_INVALID", "provided digest did not match uploaded content");
                    return;
                }
                context.Response.StatusCode = StatusCodes.Status201Created;
                context.Response.Headers.Location = location + "/blobs/" + digest;
                context.Response.Headers["Docker-Content-Digest"] = digest;
                return;
            case "GET":
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                context.Response.Headers["Docker-Upload-UUID"] = uploadKey;
                context.Response.Headers["Range"] = "0-" + Math.Max(0, new FileInfo(files.UploadPath(uploadKey)).Length - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return;
            case "DELETE":
                files.AbandonUpload(uploadKey);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
        }
    }

    if (verb == "manifests" && operation.Length == 1)
    {
        string reference = operation[0];
        if (method == "PUT")
        {
            if (identity is null) { await RegistryError(context, 401, "UNAUTHORIZED", "authentication required"); return; }
            if (context.Request.ContentLength > 4 * 1024 * 1024) { await RegistryError(context, 413, "MANIFEST_INVALID", "manifest too large"); return; }
            byte[] body;
            using (MemoryStream buffer = new())
            {
                await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
                if (buffer.Length > 4 * 1024 * 1024) { await RegistryError(context, 413, "MANIFEST_INVALID", "manifest too large"); return; }
                body = buffer.ToArray();
            }
            if (body.Length == 0) { await RegistryError(context, 400, "MANIFEST_INVALID", "manifest is empty"); return; }
            string mediaType = context.Request.ContentType ?? "application/vnd.docker.distribution.manifest.v2+json";
            if (mediaType.Length > 200 || mediaType.Any(c => c is '\r' or '\n')) { await RegistryError(context, 400, "MANIFEST_INVALID", "content type"); return; }
            string digest = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body));
            if (IsDigest(reference) && reference != digest) { await RegistryError(context, 400, "DIGEST_INVALID", "manifest digest does not match reference"); return; }
            string path = files.ManifestPath(digest[7..]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, body, context.RequestAborted);
            try
            {
                ContainerRepository repository = await store.EnsureContainerRepositoryAsync(identity, project.Project.Id, image, context.RequestAborted);
                await store.PutManifestAsync(identity, project.Project.Id, repository.Id, digest, mediaType, body.Length, IsDigest(reference) ? null : reference, context.RequestAborted);
            }
            catch (UnauthorizedAccessException error) { await RegistryError(context, 403, "DENIED", error.Message); return; }
            catch (ArgumentException error) { await RegistryError(context, 400, "NAME_INVALID", error.Message); return; }
            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.Headers.Location = location + "/manifests/" + digest;
            context.Response.Headers["Docker-Content-Digest"] = digest;
            return;
        }
        ContainerRepository? found = await store.FindContainerRepositoryAsync(identity, project.Project.Id, image, context.RequestAborted);
        if (method is "GET" or "HEAD")
        {
            ContainerManifest? manifest = found is null ? null : await store.FindManifestAsync(found.Id, reference, context.RequestAborted);
            string path = manifest is null ? "" : files.ManifestPath(manifest.Digest[7..]);
            if (manifest is null || !File.Exists(path)) { await RegistryError(context, 404, "MANIFEST_UNKNOWN", "manifest unknown to registry"); return; }
            context.Response.ContentType = manifest.MediaType;
            context.Response.ContentLength = manifest.Size;
            context.Response.Headers["Docker-Content-Digest"] = manifest.Digest;
            if (method == "HEAD") return;
            using FileStream content = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            await content.CopyToAsync(context.Response.Body, context.RequestAborted);
            return;
        }
        if (method == "DELETE")
        {
            if (identity is null || found is null) { await RegistryError(context, 404, "MANIFEST_UNKNOWN", "manifest unknown to registry"); return; }
            try
            {
                bool removed = await store.DeleteManifestAsync(identity, project.Project.Id, found.Id, reference, context.RequestAborted);
                if (!removed) { await RegistryError(context, 404, "MANIFEST_UNKNOWN", "manifest unknown to registry"); return; }
                context.Response.StatusCode = StatusCodes.Status202Accepted;
            }
            catch (UnauthorizedAccessException error) { await RegistryError(context, 403, "DENIED", error.Message); }
            return;
        }
    }
    await RegistryError(context, 404, "UNSUPPORTED", "the operation is not supported");
});

// The branch tip behind an anchored comment addressed by id alone, which the browser route does. Null when
// the comment is not on a proposal or the branch is gone, both of which leave the anchor outdated.
async Task<string?> SourceTipOfComment(HttpContext context, ProjectDetails project, AuthenticatedIdentity? identity, long commentId)
{
    (CommentSubject Subject, long Iid)? home = await store.FindCommentSubjectAsync(project.Project.Id, commentId, context.RequestAborted);
    return home is { Subject: CommentSubject.MergeRequest } ? await SourceTip(context, project, identity, home.Value.Iid) : null;
}

static string StateText(CollaborationState state) => state switch
{
    CollaborationState.Closed => "closed",
    CollaborationState.Merged => "merged",
    _ => "opened"
};

// An absent state means every state, which is what a browser's first request wants. GitLab defaults the
// same way.
static bool TryReadState(string? value, out CollaborationState? state, bool merged)
{
    state = null;
    if (string.IsNullOrEmpty(value) || value == "all") return true;
    switch (value)
    {
        case "opened": state = CollaborationState.Opened; return true;
        case "closed": state = CollaborationState.Closed; return true;
        case "merged" when merged: state = CollaborationState.Merged; return true;
        default: return false;
    }
}

// GitLab names the act rather than the result, and so does this: `close` and `reopen` rather than a state
// to assert. A caller asserting a state would have to be told that the state they asked for is the one
// they were already in, which is not an error worth having.
static bool TryReadStateEvent(string? value, out CollaborationState? state)
{
    state = null;
    if (string.IsNullOrEmpty(value)) return true;
    switch (value)
    {
        case "close": state = CollaborationState.Closed; return true;
        case "reopen": state = CollaborationState.Opened; return true;
        default: return false;
    }
}

app.MapGet("/api/projects/{projectId:long}/issues", async context =>
{
    var scope = await BrowserCollaboration(context, write: false);
    if (scope is null) return;
    await ListIssues(context, scope.Value.Details, scope.Value.Identity);
});

app.MapPost("/api/projects/{projectId:long}/issues", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null) return;
    await CreateIssue(context, scope.Value.Details, scope.Value.Identity);
});

app.MapGet("/api/projects/{projectId:long}/issues/{iid:long}", async context =>
{
    var scope = await BrowserCollaboration(context, write: false);
    if (scope is null) return;
    await ReadIssue(context, scope.Value.Details, scope.Value.Identity);
});

app.MapPost("/api/projects/{projectId:long}/issues/{iid:long}", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null) return;
    await UpdateIssue(context, scope.Value.Details, scope.Value.Identity);
});

app.MapGet("/api/projects/{projectId:long}/issues/{iid:long}/comments", async context =>
{
    var scope = await BrowserCollaboration(context, write: false);
    if (scope is null) return;
    await ListNotes(context, scope.Value.Details, scope.Value.Identity, CommentSubject.Issue);
});

app.MapPost("/api/projects/{projectId:long}/issues/{iid:long}/comments", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null) return;
    await CreateNote(context, scope.Value.Details, scope.Value.Identity, CommentSubject.Issue);
});

app.MapGet("/api/projects/{projectId:long}/merge-requests", async context =>
{
    var scope = await BrowserCollaboration(context, write: false);
    if (scope is null) return;
    await ListMergeRequests(context, scope.Value.Details, scope.Value.Identity);
});

app.MapPost("/api/projects/{projectId:long}/merge-requests", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null) return;
    await CreateMergeRequest(context, scope.Value.Details, scope.Value.Identity);
});

app.MapGet("/api/projects/{projectId:long}/merge-requests/{iid:long}", async context =>
{
    var scope = await BrowserCollaboration(context, write: false);
    if (scope is null) return;
    await ReadMergeRequest(context, scope.Value.Details, scope.Value.Identity);
});

app.MapPost("/api/projects/{projectId:long}/merge-requests/{iid:long}", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null) return;
    await UpdateMergeRequest(context, scope.Value.Details, scope.Value.Identity);
});

app.MapGet("/api/projects/{projectId:long}/merge-requests/{iid:long}/changes", async context =>
{
    var scope = await BrowserCollaboration(context, write: false);
    if (scope is null) return;
    await MergeRequestChanges(context, scope.Value.Details, scope.Value.Identity);
});

app.MapGet("/api/projects/{projectId:long}/merge-requests/{iid:long}/comments", async context =>
{
    var scope = await BrowserCollaboration(context, write: false);
    if (scope is null) return;
    await ListNotes(context, scope.Value.Details, scope.Value.Identity, CommentSubject.MergeRequest);
});

app.MapPost("/api/projects/{projectId:long}/merge-requests/{iid:long}/comments", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null) return;
    await CreateNote(context, scope.Value.Details, scope.Value.Identity, CommentSubject.MergeRequest);
});

app.MapPost("/api/projects/{projectId:long}/merge-requests/{iid:long}/merge", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null) return;
    await AcceptMergeRequest(context, scope.Value.Details, scope.Value.Identity);
});

app.MapPost("/api/projects/{projectId:long}/merge-requests/{iid:long}/rebase", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null) return;
    await RebaseMergeRequest(context, scope.Value.Details, scope.Value.Identity);
});

app.MapMethods("/api/v4/projects/{projectId}/merge_requests/{iid:long}/rebase", ["PUT"], async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is null) return;
    await RebaseMergeRequest(context, scope.Value.Details, scope.Value.Identity);
});

app.MapPost("/api/projects/{projectId:long}/merge-requests/{iid:long}/approve", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null) return;
    await SetApproval(context, scope.Value.Details, scope.Value.Identity, approved: true);
});

app.MapPost("/api/projects/{projectId:long}/merge-requests/{iid:long}/unapprove", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null) return;
    await SetApproval(context, scope.Value.Details, scope.Value.Identity, approved: false);
});

app.MapPost("/api/projects/{projectId:long}/approvals", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null) return;
    await SetApprovalsRequired(context, scope.Value.Details, scope.Value.Identity);
});

if (settings.Webhooks)
{
    app.MapGet("/api/projects/{projectId:long}/hooks", async context =>
    {
        var scope = await BrowserCollaboration(context, write: false);
        if (scope is null) return;
        await ListWebhooks(context, scope.Value.Details, scope.Value.Identity);
    });

    app.MapPost("/api/projects/{projectId:long}/hooks", async context =>
    {
        var scope = await BrowserCollaboration(context, write: true);
        if (scope is null) return;
        await CreateWebhook(context, scope.Value.Details, scope.Value.Identity);
    });

    app.MapDelete("/api/projects/{projectId:long}/hooks/{hookId:long}", async context =>
    {
        var scope = await BrowserCollaboration(context, write: true);
        if (scope is null) return;
        await DeleteWebhook(context, scope.Value.Details, scope.Value.Identity);
    });

    app.MapGet("/api/v4/projects/{projectId}/hooks", async context =>
    {
        var scope = await ApiCollaboration(context);
        if (scope is null) return;
        await ListWebhooks(context, scope.Value.Details, scope.Value.Identity);
    });

    app.MapPost("/api/v4/projects/{projectId}/hooks", async context =>
    {
        var scope = await ApiCollaboration(context);
        if (scope is null) return;
        await CreateWebhook(context, scope.Value.Details, scope.Value.Identity);
    });

    app.MapDelete("/api/v4/projects/{projectId}/hooks/{hookId:long}", async context =>
    {
        var scope = await ApiCollaboration(context);
        if (scope is null) return;
        await DeleteWebhook(context, scope.Value.Details, scope.Value.Identity);
    });
}

app.MapDelete("/api/projects/{projectId:long}/comments/{commentId:long}", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null) return;
    await DeleteNote(context, scope.Value.Details, scope.Value.Identity);
});

app.MapPost("/api/projects/{projectId:long}/comments/{commentId:long}", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null) return;
    await UpdateNote(context, scope.Value.Details, scope.Value.Identity);
});

// Planning routes, twice like everything else in this family. The browser family keeps the hyphenated
// spelling it already uses; the API family is GitLab's.
foreach ((string root, bool browserFamily) in new[] { ("/api/projects/{projectId:long}", true), ("/api/v4/projects/{projectId}", false) })
{
    async Task<(ProjectDetails Details, AuthenticatedIdentity? Identity)?> Scope(HttpContext context, bool write) =>
        browserFamily ? await BrowserCollaboration(context, write) : await ApiCollaboration(context);

    app.MapGet(root + "/labels", async context => { var scope = await Scope(context, false); if (scope is not null) await ListLabels(context, scope.Value.Details, scope.Value.Identity); });
    app.MapPost(root + "/labels", async context => { var scope = await Scope(context, true); if (scope is not null) await CreateLabel(context, scope.Value.Details, scope.Value.Identity); });
    app.MapMethods(root + "/labels/{labelId}", [browserFamily ? "POST" : "PUT"], async context => { var scope = await Scope(context, true); if (scope is not null) await UpdateLabel(context, scope.Value.Details, scope.Value.Identity); });
    app.MapDelete(root + "/labels/{labelId}", async context => { var scope = await Scope(context, true); if (scope is not null) await DeleteLabel(context, scope.Value.Details, scope.Value.Identity); });

    app.MapGet(root + "/milestones", async context => { var scope = await Scope(context, false); if (scope is not null) await ListMilestones(context, scope.Value.Details, scope.Value.Identity); });
    app.MapPost(root + "/milestones", async context => { var scope = await Scope(context, true); if (scope is not null) await CreateMilestone(context, scope.Value.Details, scope.Value.Identity); });
    app.MapGet(root + "/milestones/{milestoneId:long}", async context => { var scope = await Scope(context, false); if (scope is not null) await ReadMilestone(context, scope.Value.Details, scope.Value.Identity); });
    app.MapMethods(root + "/milestones/{milestoneId:long}", [browserFamily ? "POST" : "PUT"], async context => { var scope = await Scope(context, true); if (scope is not null) await UpdateMilestone(context, scope.Value.Details, scope.Value.Identity); });
    app.MapDelete(root + "/milestones/{milestoneId:long}", async context => { var scope = await Scope(context, true); if (scope is not null) await DeleteMilestone(context, scope.Value.Details, scope.Value.Identity); });

    app.MapGet(root + "/boards", async context => { var scope = await Scope(context, false); if (scope is not null) await ListBoards(context, scope.Value.Details, scope.Value.Identity); });
    app.MapPost(root + "/boards", async context => { var scope = await Scope(context, true); if (scope is not null) await CreateBoard(context, scope.Value.Details, scope.Value.Identity); });
    app.MapGet(root + "/boards/{boardId:long}", async context => { var scope = await Scope(context, false); if (scope is not null) await ReadBoard(context, scope.Value.Details, scope.Value.Identity); });
    app.MapMethods(root + "/boards/{boardId:long}", [browserFamily ? "POST" : "PUT"], async context => { var scope = await Scope(context, true); if (scope is not null) await UpdateBoard(context, scope.Value.Details, scope.Value.Identity); });
    app.MapDelete(root + "/boards/{boardId:long}", async context => { var scope = await Scope(context, true); if (scope is not null) await DeleteBoard(context, scope.Value.Details, scope.Value.Identity); });
    app.MapPost(root + "/boards/{boardId:long}/lists", async context => { var scope = await Scope(context, true); if (scope is not null) await CreateBoardList(context, scope.Value.Details, scope.Value.Identity); });
    app.MapMethods(root + "/boards/{boardId:long}/lists/{listId:long}", [browserFamily ? "POST" : "PUT"], async context => { var scope = await Scope(context, true); if (scope is not null) await UpdateBoardList(context, scope.Value.Details, scope.Value.Identity); });
    app.MapDelete(root + "/boards/{boardId:long}/lists/{listId:long}", async context => { var scope = await Scope(context, true); if (scope is not null) await DeleteBoardList(context, scope.Value.Details, scope.Value.Identity); });

    app.MapGet(root + "/users", async context => { var scope = await Scope(context, false); if (scope is not null) await ListAssignable(context, scope.Value.Details, scope.Value.Identity); });

    app.MapPost(root + "/fork", async context => { var scope = await Scope(context, true); if (scope is not null) await ForkProject(context, scope.Value.Details, scope.Value.Identity); });
    app.MapGet(root + "/forks", async context => { var scope = await Scope(context, false); if (scope is not null) await ListForks(context, scope.Value.Details, scope.Value.Identity); });

    app.MapGet(root + "/releases", async context => { var scope = await Scope(context, false); if (scope is not null) await ListReleases(context, scope.Value.Details, scope.Value.Identity); });
    app.MapPost(root + "/releases", async context => { var scope = await Scope(context, true); if (scope is not null) await CreateRelease(context, scope.Value.Details, scope.Value.Identity); });
    app.MapGet(root + "/releases/{tag}", async context => { var scope = await Scope(context, false); if (scope is not null) await ReadRelease(context, scope.Value.Details, scope.Value.Identity); });
    app.MapMethods(root + "/releases/{tag}", [browserFamily ? "POST" : "PUT"], async context => { var scope = await Scope(context, true); if (scope is not null) await UpdateRelease(context, scope.Value.Details, scope.Value.Identity); });
    app.MapDelete(root + "/releases/{tag}", async context => { var scope = await Scope(context, true); if (scope is not null) await DeleteRelease(context, scope.Value.Details, scope.Value.Identity); });
    app.MapPost(root + "/releases/{tag}/assets/links", async context => { var scope = await Scope(context, true); if (scope is not null) await AddReleaseLink(context, scope.Value.Details, scope.Value.Identity); });
    app.MapDelete(root + "/releases/{tag}/assets/links/{linkId:long}", async context => { var scope = await Scope(context, true); if (scope is not null) await DeleteReleaseLink(context, scope.Value.Details, scope.Value.Identity); });
    app.MapGet(root + "/releases/{tag}/assets/files/{fileName}", async context => { var scope = await Scope(context, false); if (scope is not null) await DownloadReleaseAsset(context, scope.Value.Details, scope.Value.Identity); });
    app.MapDelete(root + "/releases/{tag}/assets/files/{fileName}", async context => { var scope = await Scope(context, true); if (scope is not null) await DeleteReleaseAsset(context, scope.Value.Details, scope.Value.Identity); });
    app.MapGet(root + "/wikis", async context => { var scope = await Scope(context, false); if (scope is not null) await ListWiki(context, scope.Value.Details, scope.Value.Identity); });
    app.MapPost(root + "/wikis", async context => { var scope = await Scope(context, true); if (scope is not null) await CreateWiki(context, scope.Value.Details, scope.Value.Identity); });
    app.MapGet(root + "/wikis/{slug}", async context => { var scope = await Scope(context, false); if (scope is not null) await ReadWiki(context, scope.Value.Details, scope.Value.Identity); });
    app.MapGet(root + "/wikis/{slug}/versions", async context => { var scope = await Scope(context, false); if (scope is not null) await ListWikiVersions(context, scope.Value.Details, scope.Value.Identity); });
    app.MapMethods(root + "/wikis/{slug}", [browserFamily ? "POST" : "PUT"], async context => { var scope = await Scope(context, true); if (scope is not null) await UpdateWiki(context, scope.Value.Details, scope.Value.Identity); });
    app.MapDelete(root + "/wikis/{slug}", async context => { var scope = await Scope(context, true); if (scope is not null) await DeleteWiki(context, scope.Value.Details, scope.Value.Identity); });

    app.MapGet(root + "/snippets", async context => { var scope = await Scope(context, false); if (scope is not null) await ListSnippets(context, scope.Value.Details, scope.Value.Identity); });
    app.MapPost(root + "/snippets", async context => { var scope = await Scope(context, true); if (scope is not null) await CreateSnippet(context, scope.Value.Details, scope.Value.Identity); });
    app.MapGet(root + "/snippets/{snippetId:long}", async context => { var scope = await Scope(context, false); if (scope is not null) await ReadSnippet(context, scope.Value.Details, scope.Value.Identity, raw: false); });
    app.MapGet(root + "/snippets/{snippetId:long}/raw", async context => { var scope = await Scope(context, false); if (scope is not null) await ReadSnippet(context, scope.Value.Details, scope.Value.Identity, raw: true); });
    app.MapMethods(root + "/snippets/{snippetId:long}", [browserFamily ? "POST" : "PUT"], async context => { var scope = await Scope(context, true); if (scope is not null) await UpdateSnippet(context, scope.Value.Details, scope.Value.Identity); });
    app.MapDelete(root + "/snippets/{snippetId:long}", async context => { var scope = await Scope(context, true); if (scope is not null) await DeleteSnippet(context, scope.Value.Details, scope.Value.Identity); });

    foreach ((string segment, CommentSubject timeSubject) in new[] { (browserFamily ? "issues" : "issues", CommentSubject.Issue), (browserFamily ? "merge-requests" : "merge_requests", CommentSubject.MergeRequest) })
    {
        app.MapGet(root + "/" + segment + "/{iid:long}/time_stats", async context => { var scope = await Scope(context, false); if (scope is not null) await WriteTimeStats(context, scope.Value.Details, scope.Value.Identity, timeSubject); });
        app.MapGet(root + "/" + segment + "/{iid:long}/time_entries", async context => { var scope = await Scope(context, false); if (scope is not null) await ListTime(context, scope.Value.Details, scope.Value.Identity, timeSubject); });
        foreach (string action in new[] { "time_estimate", "reset_time_estimate", "add_spent_time", "reset_spent_time" })
        {
            string chosenAction = action;
            app.MapPost(root + "/" + segment + "/{iid:long}/" + action, async context => { var scope = await Scope(context, true); if (scope is not null) await TimeAction(context, scope.Value.Details, scope.Value.Identity, timeSubject, chosenAction); });
        }
    }

    if (!browserFamily)
        app.MapMethods(root + "/releases/{tag}/assets/files/{fileName}", ["PUT"], async context =>
        {
            // The token route carries the bytes. Bounded on the declared length before anything is read.
            var scope = await ApiCollaboration(context);
            if (scope is null) return;
            await PutReleaseAssetBytes(context, scope.Value.Details, scope.Value.Identity);
        });
}

// The browser's half of a release file: an upload receipt, re-authenticated, re-authorized, and confirmed
// to be the caller's own upload under the release-asset profile.
app.MapPost("/api/graft/projects/{projectId:long}/releases/{tag}/assets", async context =>
{
    var scope = await BrowserCollaboration(context, write: true);
    if (scope is null || scope.Value.Identity is null) return;
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    ReleaseAssetIngestRequest? request = await context.Request.ReadFromJsonAsync(ApiJson.Default.ReleaseAssetIngestRequest, context.RequestAborted);
    if (request is null || !Guid.TryParse(request.UploadId, out Guid uploadId)) { await ApiError(context, 400, "upload-id"); return; }
    DiskUploadStore uploads = context.RequestServices.GetRequiredService<DiskUploadStore>();
    string owner = GraftSessionAuthenticationHandler.Owner(scope.Value.Identity);
    byte[] bytes = [];
    string fileName;
    try
    {
        UploadStatus status = uploads.Get(owner, uploadId);
        if (!string.Equals(status.Profile, UploadLimits.ReleaseAssetProfile, StringComparison.Ordinal))
        { await ApiError(context, 400, "upload was not created for release files"); return; }
        if (status.Length > GraftStore.MaximumAssetBytes) { await ApiError(context, 413, "the file exceeds the 32 MiB asset limit"); return; }
        fileName = Path.GetFileName(status.FileName);
        await uploads.ReadCompletedAsync(owner, uploadId, async (source, cancellationToken) =>
        {
            using MemoryStream buffer = new();
            await source.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }, context.RequestAborted);
    }
    catch (UploadException error) { await ApiError(context, error.StatusCode, error.Message); return; }
    await StoreReleaseAsset(context, scope.Value.Details, scope.Value.Identity, fileName, bytes);
});

foreach ((string segment, CommentSubject noteSubject) in new[] { ("issues", CommentSubject.Issue), ("merge_requests", CommentSubject.MergeRequest) })
{
    app.MapMethods("/api/v4/projects/{projectId}/" + segment + "/{iid:long}/notes/{commentId:long}", ["PUT"], async context =>
    {
        var scope = await ApiCollaboration(context);
        if (scope is null) return;
        await UpdateNote(context, scope.Value.Details, scope.Value.Identity, noteSubject);
    });

    app.MapDelete("/api/v4/projects/{projectId}/" + segment + "/{iid:long}/notes/{commentId:long}", async context =>
    {
        var scope = await ApiCollaboration(context);
        if (scope is null) return;
        await DeleteNote(context, scope.Value.Details, scope.Value.Identity, noteSubject);
    });
}

app.MapGet("/api/v4/projects/{projectId}/issues", async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is null) return;
    await ListIssues(context, scope.Value.Details, scope.Value.Identity);
});

app.MapPost("/api/v4/projects/{projectId}/issues", async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is null) return;
    await CreateIssue(context, scope.Value.Details, scope.Value.Identity);
});

app.MapGet("/api/v4/projects/{projectId}/issues/{iid:long}", async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is null) return;
    await ReadIssue(context, scope.Value.Details, scope.Value.Identity);
});

app.MapMethods("/api/v4/projects/{projectId}/issues/{iid:long}", ["PUT"], async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is null) return;
    await UpdateIssue(context, scope.Value.Details, scope.Value.Identity);
});

app.MapGet("/api/v4/projects/{projectId}/issues/{iid:long}/notes", async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is null) return;
    await ListNotes(context, scope.Value.Details, scope.Value.Identity, CommentSubject.Issue);
});

app.MapPost("/api/v4/projects/{projectId}/issues/{iid:long}/notes", async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is null) return;
    await CreateNote(context, scope.Value.Details, scope.Value.Identity, CommentSubject.Issue);
});

app.MapGet("/api/v4/projects/{projectId}/merge_requests", async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is null) return;
    await ListMergeRequests(context, scope.Value.Details, scope.Value.Identity);
});

app.MapPost("/api/v4/projects/{projectId}/merge_requests", async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is null) return;
    await CreateMergeRequest(context, scope.Value.Details, scope.Value.Identity);
});

app.MapGet("/api/v4/projects/{projectId}/merge_requests/{iid:long}", async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is null) return;
    await ReadMergeRequest(context, scope.Value.Details, scope.Value.Identity);
});

app.MapMethods("/api/v4/projects/{projectId}/merge_requests/{iid:long}", ["PUT"], async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is null) return;
    await UpdateMergeRequest(context, scope.Value.Details, scope.Value.Identity);
});

app.MapGet("/api/v4/projects/{projectId}/merge_requests/{iid:long}/changes", async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is null) return;
    await MergeRequestChanges(context, scope.Value.Details, scope.Value.Identity);
});

app.MapGet("/api/v4/projects/{projectId}/merge_requests/{iid:long}/notes", async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is null) return;
    await ListNotes(context, scope.Value.Details, scope.Value.Identity, CommentSubject.MergeRequest);
});

app.MapPost("/api/v4/projects/{projectId}/merge_requests/{iid:long}/notes", async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is null) return;
    await CreateNote(context, scope.Value.Details, scope.Value.Identity, CommentSubject.MergeRequest);
});

app.MapMethods("/api/v4/projects/{projectId}/merge_requests/{iid:long}/merge", ["PUT"], async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is null) return;
    await AcceptMergeRequest(context, scope.Value.Details, scope.Value.Identity);
});

app.MapPost("/api/v4/projects/{projectId}/merge_requests/{iid:long}/approve", async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is null) return;
    await SetApproval(context, scope.Value.Details, scope.Value.Identity, approved: true);
});

app.MapPost("/api/v4/projects/{projectId}/merge_requests/{iid:long}/unapprove", async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is null) return;
    await SetApproval(context, scope.Value.Details, scope.Value.Identity, approved: false);
});

app.MapMethods("/api/v4/projects/{projectId}/approvals", ["POST", "PUT"], async context =>
{
    var scope = await ApiCollaboration(context);
    if (scope is null) return;
    await SetApprovalsRequired(context, scope.Value.Details, scope.Value.Identity);
});

// GitLab answers this with 202 Accepted because its deletion is scheduled and happens later. Graft's is
// immediate and complete when the response is written, so it answers 204: a client that polls for the
// project to disappear would be polling for something that has already happened.
app.MapDelete("/api/v4/projects/{**projectId}", async context =>
{
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.ManageProject);
    if (project is null) return;
    if (project.Identity is null) { await ApiError(context, 401, "401 Unauthorized"); return; }
    if (!await OnlyQuery(context)) return;
    await DeleteProject(context, project.Identity, project.Details.Project.Id);
});

// Housekeeping: every object into one pack, loose files and older packs removed. GitLab schedules this and
// answers 201 with nothing; Graft does it before answering and says what it did. Maintainer-equivalent, the
// same standing the other project settings need, because it rewrites the repository's files even though it
// changes none of its content.
app.MapPost("/api/v4/projects/{**projectId}", async context =>
{
    string projectId = context.Request.RouteValues["projectId"]?.ToString() ?? "";
    if (!projectId.EndsWith("/housekeeping", StringComparison.Ordinal))
    { await ApiError(context, 404, "404 API endpoint not found"); return; }
    context.Request.RouteValues["projectId"] = projectId[..^"/housekeeping".Length];
    ApiProject? project = await AuthorizeApiProject(context, ProjectAction.ManageProject);
    if (project is null) return;
    if (project.Identity is null) { await ApiError(context, 401, "401 Unauthorized"); return; }
    if (!await OnlyQuery(context, "task")) return;
    await WithRepository(EndpointFor(project.Details.Project), context, async repository =>
    {
        RepackResult result = repository.Repack(context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(new HousekeepingResponse(result.Packed, result.LooseRemoved, result.PacksRemoved),
            ApiJson.Default.HousekeepingResponse, null, context.RequestAborted);
    });
});

app.MapPost("/api/projects/{projectId:long}/housekeeping", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!VerifyCsrf(browser.Value.Secret, context.Request.Headers["X-Graft-CSRF"].ToString()))
    { await ApiError(context, 403, "csrf"); return; }
    long projectId = Convert.ToInt64(context.Request.RouteValues["projectId"], System.Globalization.CultureInfo.InvariantCulture);
    ProjectDetails? details = await store.GetReadyProjectAsync(projectId, context.RequestAborted);
    if (details is null) { await ApiError(context, 404, "project"); return; }
    PermissionDecision manage = await DecideFor(context, browser.Value.Identity, projectId, ProjectAction.ManageProject);
    if (!manage.Allowed) { await ApiError(context, 404, "project"); return; }
    await WithRepository(EndpointFor(details.Project), context, async repository =>
    {
        RepackResult result = repository.Repack(context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(new HousekeepingResponse(result.Packed, result.LooseRemoved, result.PacksRemoved),
            ApiJson.Default.HousekeepingResponse, null, context.RequestAborted);
    });
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
        // Protocol version 2 is spoken when the client asks for it by header; a push has no version 2 and
        // falls back to the version 0 advertisement whatever the header says, as Git expects.
        byte[] data = WantsProtocolV2(context) && service == "git-upload-pack"
            ? authorized.Endpoint.Repository.AdvertiseV2(service)
            : authorized.Endpoint.Repository.Advertise(service);
        context.Response.ContentType = "application/x-" + service + "-advertisement";
        await context.Response.Body.WriteAsync(data, context.RequestAborted);
    }
    finally { authorized.Endpoint.Gate.Release(); }
});

// ---------------------------------------------------------------------------------------------------
// Git LFS: the batch API and the basic transfer. A large file is content-addressed by SHA-256 beside the
// repository's objects; a pointer in the tree names it. The same credential and the same permission as the
// Git transport: downloading needs what a fetch needs, uploading what a push needs.
// ---------------------------------------------------------------------------------------------------

const string LfsMediaType = "application/vnd.git-lfs+json";

app.MapPost("/{projectNamespace}/{project}.git/info/lfs/objects/batch", async context =>
{
    LfsBatchRequest? request;
    try { request = await context.Request.ReadFromJsonAsync(ApiJson.Default.LfsBatchRequest, context.RequestAborted); }
    catch (JsonException) { await LfsError(context, 400, "the batch request is not valid JSON"); return; }
    if (request is null || request.Operation is not ("download" or "upload")) { await LfsError(context, 400, "operation must be download or upload"); return; }
    if (request.Transfers is { Length: > 0 } && !request.Transfers.Contains("basic")) { await LfsError(context, 422, "only the basic transfer is supported"); return; }
    if (request.Objects is not { Length: > 0 and <= 1000 }) { await LfsError(context, 422, "objects is required, at most a thousand at a time"); return; }

    AuthorizedRepository? authorized = await Authorize(context, request.Operation == "upload" ? ProjectAction.PushRepository : ProjectAction.FetchRepository);
    if (authorized is null) return;
    LargeFileStore store2 = new(Path.Combine(settings.Repositories, authorized.Project.StorageId));
    string origin = Origin(context) + "/" + Uri.EscapeDataString(authorized.Project.Namespace) + "/" + Uri.EscapeDataString(authorized.Project.Slug) + ".git/info/lfs/objects/";
    // The transfer requests carry the credential the batch arrived with, so a client that was told the
    // action is already authenticated does not go back to its credential helper for every object.
    string authorization = context.Request.Headers.Authorization.ToString();
    Dictionary<string, string>? header = authorization.Length == 0 ? null : new() { ["Authorization"] = authorization };

    List<LfsObjectResponse> objects = [];
    foreach (LfsObjectRequest wanted in request.Objects)
    {
        if (wanted.Oid is null || !LargeFileStore.IsOid(wanted.Oid) || wanted.Size < 0)
        { objects.Add(new(wanted.Oid ?? "", wanted.Size, null, null, new(422, "oid is a lowercase SHA-256 and size is not negative"))); continue; }
        long? stored = store2.Size(wanted.Oid);
        if (request.Operation == "download")
        {
            objects.Add(stored is null
                ? new(wanted.Oid, wanted.Size, null, null, new(404, "the object is not in this repository"))
                : new(wanted.Oid, stored.Value, true, new(new(origin + wanted.Oid, header, 3600), null), null));
        }
        else
        {
            if (wanted.Size > LargeFileStore.MaximumBytes) { objects.Add(new(wanted.Oid, wanted.Size, null, null, new(422, "the object exceeds the 4 GiB limit"))); continue; }
            // Already here: nothing to upload, which the protocol says by omitting the action.
            objects.Add(stored is not null
                ? new(wanted.Oid, wanted.Size, true, null, null)
                : new(wanted.Oid, wanted.Size, true, new(null, new(origin + wanted.Oid, header, 3600)), null));
        }
    }
    context.Response.ContentType = LfsMediaType;
    await context.Response.WriteAsJsonAsync(new LfsBatchResponse("basic", objects.ToArray()), ApiJson.Default.LfsBatchResponse, LfsMediaType, context.RequestAborted);
});

app.MapMethods("/{projectNamespace}/{project}.git/info/lfs/objects/{oid}", ["PUT"], async context =>
{
    string oid = context.Request.RouteValues["oid"]?.ToString() ?? "";
    if (!LargeFileStore.IsOid(oid)) { await LfsError(context, 404, "no such object"); return; }
    AuthorizedRepository? authorized = await Authorize(context, ProjectAction.PushRepository);
    if (authorized is null) return;
    long? declared = context.Request.ContentLength;
    if (declared is null) { await LfsError(context, 411, "Content-Length is required"); return; }
    if (declared > LargeFileStore.MaximumBytes) { await LfsError(context, 413, "the object exceeds the 4 GiB limit"); return; }
    LargeFileStore store2 = new(Path.Combine(settings.Repositories, authorized.Project.StorageId));
    try
    {
        await store2.StoreAsync(oid, declared.Value, context.Request.Body, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status200OK;
    }
    catch (GitInputException error)
    {
        await LfsError(context, error.Message == "lfs-hash-mismatch" ? 422 : 400, error.Message switch
        {
            "lfs-hash-mismatch" => "the bytes do not hash to the oid they were sent as",
            "lfs-size-mismatch" or "lfs-size-exceeded" => "the bytes are not the size that was declared",
            _ => "the object was refused: " + error.Message
        });
    }
});

app.MapGet("/{projectNamespace}/{project}.git/info/lfs/objects/{oid}", async context =>
{
    string oid = context.Request.RouteValues["oid"]?.ToString() ?? "";
    if (!LargeFileStore.IsOid(oid)) { await LfsError(context, 404, "no such object"); return; }
    AuthorizedRepository? authorized = await Authorize(context, ProjectAction.FetchRepository);
    if (authorized is null) return;
    LargeFileStore store2 = new(Path.Combine(settings.Repositories, authorized.Project.StorageId));
    long? size = store2.Size(oid);
    if (size is null) { await LfsError(context, 404, "no such object"); return; }
    context.Response.ContentType = "application/octet-stream";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.ContentLength = size;
    using Stream content = store2.OpenRead(oid);
    await content.CopyToAsync(context.Response.Body, context.RequestAborted);
});

static async Task LfsError(HttpContext context, int status, string message)
{
    context.Response.StatusCode = status;
    context.Response.ContentType = LfsMediaType;
    await context.Response.WriteAsJsonAsync(new ErrorResponse(message), ApiJson.Default.ErrorResponse, LfsMediaType, context.RequestAborted);
}

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
    List<(string Branch, string? From, string? To)> pushed = [];
    Limits.Require(context.Request.ContentType == "application/x-" + service + "-request", "content-type");

    // Git gzips a fetch negotiation body once it passes about a kilobyte, and offers no setting to stop it.
    // Refusing every Content-Encoding therefore broke exactly the fetches that need several rounds of
    // negotiation, which is what happens when two histories have diverged: the first rounds are small and
    // succeed, and the round that grows past a kilobyte is compressed and refused. A small clone never
    // reaches that point, so this looked like it worked until a repository had been used for a while.
    //
    // Only gzip, because that is the only encoding Git sends. Anything else is still refused by name.
    string encoding = context.Request.Headers.ContentEncoding.ToString();
    bool gzipped = string.Equals(encoding, "gzip", StringComparison.OrdinalIgnoreCase);
    Limits.Require(encoding.Length == 0 || gzipped, "content-encoding: " + encoding);
    if (!await authorized.Endpoint.Gate.WaitAsync(0, context.RequestAborted))
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return;
    }
    try
    {
        using TemporaryStorage staging = new();
        using FileStream input = staging.CreateFile();
        long requestLimit = service == "git-receive-pack" ? Limits.RequestBytes : 16 * 1024 * 1024;
        byte[] buffer = new byte[64 * 1024];
        int count;
        // The limit is applied to what comes out rather than to what arrived, which is the whole reason a
        // compressed body can be accepted at all: a body that expands past the ceiling is refused at the
        // moment it passes it, so a small input cannot become a large one here.
        Stream body = gzipped
            ? new System.IO.Compression.GZipStream(context.Request.Body, System.IO.Compression.CompressionMode.Decompress)
            : context.Request.Body;
        try
        {
            while ((count = await body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                Limits.Require(input.Length + count <= requestLimit, "request-limit");
                await input.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
            }
        }
        catch (InvalidDataException) { throw new GitInputException("content-encoding: malformed gzip"); }
        finally { if (gzipped) await body.DisposeAsync(); }
        input.Position = 0;

        // A lone flush packet is the client settling authentication, not a request to act on. Git sends one
        // before any body it would be unable to send twice: once the body is streamed with chunked encoding
        // it cannot be replayed after a 401, so the client spends an empty request on the challenge first
        // and only then streams the real one.
        //
        // Answering that probe with an error ends the operation before it begins. It is why a push larger
        // than the client's http.postBuffer, a megabyte by default, failed here while a smaller one went
        // through: only the large one is chunked, and only the chunked one is probed. Real Git answers an
        // empty request with an empty result, so this does too.
        if (input.Length == 4 && new Cursor(input, 0, 4).Take(4).AsSpan().SequenceEqual("0000"u8))
        {
            context.Response.ContentType = "application/x-" + service + "-result";
            return;
        }

        // The refs before and after, so a push can report which branches moved. Taken inside the lock, so
        // nothing else can move one between the two readings.
        Dictionary<string, string>? before = service == "git-receive-pack" && webhooks is not null
            ? new(authorized.Endpoint.Repository.Refs, StringComparer.Ordinal)
            : null;

        input.Position = 0;
        context.Response.ContentType = "application/x-" + service + "-result";
        if (service == "git-receive-pack")
        {
            byte[] data = authorized.Endpoint.Repository.Receive(input, context.RequestAborted);
            await context.Response.Body.WriteAsync(data, context.RequestAborted);
        }
        else
        {
            using FileStream output = staging.CreateFile();
            if (WantsProtocolV2(context)) authorized.Endpoint.Repository.ServeV2(input, output, context.RequestAborted);
            else authorized.Endpoint.Repository.Upload(input, output, context.RequestAborted);
            output.Position = 0;
            await output.CopyToAsync(context.Response.Body, context.RequestAborted);
        }

        if (before is not null) pushed = Moved(before, authorized.Endpoint.Repository.Refs);
    }
    finally { authorized.Endpoint.Gate.Release(); }

    // Queued after the lock is released and after the client has its answer, because a push is finished
    // when the objects are durable and not when somebody else has been told about it.
    foreach ((string branch, string? from, string? to) in pushed)
    {
        await Notify(context, authorized.Project, WebhookEvent.Push, payload =>
        {
            payload["branch"] = branch;
            payload["before"] = from;
            payload["commit"] = to;
            payload["by"] = authorized.Identity?.Username;
            payload["via"] = "push";
        });
    }
});

// The pages origin: a project's static site, served from a different hostname and nothing else.
//
// Why a second hostname rather than a path on this one. A site is repository-authored HTML, which the
// README pipeline refuses to serve precisely because same-origin script reaches the session cookie's
// privileges, the CSRF token and every authenticated route. A separate origin is what contains it: the
// session cookie is `__Host-` prefixed and cannot travel there, and this branch short-circuits before
// authentication, the API, the uploads and the browser shell, so none of them exist on that host at all.
//
// Addressed by the project's instance-wide slug, one segment, so a shared site URL names no account.
if (settings.PagesOrigin is not null)
{
    string pagesHost = new Uri(settings.PagesOrigin).Host;
    app.Use(async (context, next) =>
    {
        if (!string.Equals(context.Request.Host.Host, pagesHost, StringComparison.OrdinalIgnoreCase))
        { await next(context); return; }

        // Nothing on this host answers to anything but a read. A site is files.
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }

        // The site's own containment. No API and no session live here, so the policy's job is to stop the
        // site being framed and to stop a browser second-guessing a declared content type.
        context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";

        string[] segments = context.Request.Path.Value?.Trim('/').Split('/', 2) ?? [];
        if (segments.Length == 0 || segments[0].Length == 0)
        {
            // The root of the pages origin belongs to no project, and a reader who trimmed a site's URL
            // back to it is looking for whatever this host is. Send them to the forge rather than
            // explaining the URL shape to somebody who cannot act on it.
            //
            // Found rather than Moved Permanently: this address may yet serve something of its own, and a
            // permanent redirect is the one thing a browser will not let us take back.
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = serverOrigin;
            return;
        }

        ProjectInfo? project = await store.FindReadyProjectByGlobalSlugAsync(segments[0], context.RequestAborted);
        // Anonymous, always: there is no session on this host to resolve. A site is therefore reachable
        // only through a published page, and a project that published nothing is simply not here.
        if (project is null ||
            !(await DecideFor(context, null, project.Id, ProjectAction.ReadPage)).Allowed)
        { await PagesMiss(context, "No site is published at that address."); return; }

        // A directory asks for the configured index, which is what makes links inside a site work.
        string requested = segments.Length > 1 ? segments[1] : "";
        bool directory = requested.Length == 0 || requested.EndsWith('/');

        // Links back to the forge, resolved here rather than written into the repository.
        //
        // A site that hard-codes the forge's hostname is wrong the day that hostname changes, and the whole
        // point of a site is that it is content somebody wrote once. These paths let an author link to the
        // project without naming a host: Graft answers with wherever the forge actually is now.
        //
        // Under `-`, which is already Graft's namespace for its own routes, and resolved before any file
        // lookup so a repository cannot shadow them with a file of the same name.
        if (requested is "-/repo" or "-/repo/" or "-/graft.json")
        {
            string projectUrl = serverOrigin + GlobalPrefix + "/" + Uri.EscapeDataString(project.GlobalSlug);
            string pageUrl = serverOrigin + "/-/p/" + Uri.EscapeDataString(project.GlobalSlug);
            if (requested == "-/graft.json")
            {
                // For a site that would rather render the link itself than follow a redirect. Same origin,
                // so a page can simply fetch it.
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(new System.Text.Json.Nodes.JsonObject
                {
                    ["slug"] = project.GlobalSlug,
                    ["name"] = project.Name,
                    ["forge"] = serverOrigin,
                    ["project"] = projectUrl,
                    ["page"] = pageUrl
                }.ToJsonString(), context.RequestAborted);
                return;
            }
            // The page rather than the repository view, because a site exists only where a page is
            // published, and a page-only project would refuse the repository view to the reader who
            // followed the link. The page links onward to the source for anyone allowed to see it.
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = pageUrl;
            return;
        }

        await WithRepository(EndpointFor(project), context, async repository =>
        {
            PageConfigurationResult config = PageConfiguration.Read(ReadRepositoryFile(repository, PageConfiguration.Path));
            // An invalid config unpublishes the page rather than falling back to defaults: a parse failure
            // means nobody has said what may be served, and the safe reading of that is nothing.
            if (!config.Ok || config.Settings!.Site is null)
            { await PagesMiss(context, "No site is published at that address."); return; }

            PageSite site = config.Settings.Site;
            string path = site.Root + "/" + (directory ? requested + site.Index : requested);
            // Checked against the configured root even though it was just built from it. The matcher is the
            // one authority on what a page may serve, and a path that reached here another way must still
            // pass it.
            if (!PageConfiguration.AllowsSiteFile(config.Settings, path))
            { await PagesMiss(context, "That file is not part of this site."); return; }

            // The type comes from the extension, never from the bytes. A repository can name a file
            // anything, and letting the file choose its own type is how a site serves script where the
            // operator expected an image.
            string? contentType = SiteContentTypes.For(path);
            if (contentType is null) { await PagesMiss(context, "That file type is not served."); return; }

            byte[]? content = ReadRepositoryFile(repository, path);
            if (content is null) { await PagesMiss(context, "That file is not part of this site."); return; }

            context.Response.ContentType = contentType;
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.ContentLength = content.Length;
            if (!HttpMethods.IsHead(context.Request.Method))
                await context.Response.Body.WriteAsync(content, context.RequestAborted);
        });
    });
}

// The agent endpoint. Mounted ahead of the fallback so a request for it never receives the browser shell,
// and answered here rather than left absent when the operator did not enable it, because the fallback would
// otherwise serve HTML with a 200 to a client that asked for a protocol.
if (settings.Mcp)
{
    app.Use(async (context, next) =>
    {
        if (!context.Request.Path.StartsWithSegments(McpPath)) { await next(context); return; }

        McpSettings live = await store.GetMcpSettingsAsync(context.RequestAborted);
        if (!live.Enabled) { await ApiError(context, 404, "the MCP endpoint is turned off by an administrator"); return; }

        // A caller with no credential is refused before the protocol is spoken, so the tool list is not a
        // free description of the forge for somebody who has shown nothing. Graft re-checks the token on
        // every call the tools make; the refusal is in front, not instead. An administrator can lift it, and
        // then an uncredentialed caller reaches Graft as a stranger and sees only what is public.
        if (!live.AllowAnonymous && McpToken(context.Request).Length == 0)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"Graft MCP\"";
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(
                "Send a Graft personal access token as PRIVATE-TOKEN or Authorization: Bearer. " +
                "This server does not serve anonymous callers; an administrator can allow that under Administration, Agent access.",
                context.RequestAborted);
            return;
        }
        context.Items[McpSettingsItem] = live;
        await next(context);
    });
    app.MapMcp(McpPath);
}
else
{
    app.Map(McpPath, context => ApiError(context, 404, "the MCP endpoint is not enabled on this server; start Graft with --mcp true"));
}

app.MapFallback("/{**path}", context => assets.Serve(context));

await app.StartAsync();
string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
serverOrigin = settings.Origin ?? address;
if (settings.Mcp)
{
    // Addressed to the public origin, connected to the listener this process actually bound, trusting only
    // the certificate it is itself serving. The port is read back from the server rather than from the
    // settings because a loopback test server listens on whatever port it was given.
    int listenPort = new Uri(address).Port;
    IPAddress target = settings.ListenAddress.Equals(IPAddress.Any) ? IPAddress.Loopback
        : settings.ListenAddress.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback
        : settings.ListenAddress;
    mcpEndpoint.Open(GraftConnection.ToOwnListener(new Uri(serverOrigin + "/"), new IPEndPoint(target, listenPort), certificate, McpWriteRemedy));
}
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
// Printed every time rather than only when unusual, so the posture a server is actually running in is
// visible in the logs of a deployment nobody has looked at for six months. The administrator's switches are
// read at this moment; they can change afterwards without a restart, which is the point of them.
if (settings.Mcp)
{
    McpSettings posture = await store.GetMcpSettingsAsync();
    Console.WriteLine("MCP: " + serverOrigin + McpPath +
        "; enabled=" + (posture.Enabled ? "true" : "false") +
        "; credentials=" + (posture.AllowAnonymous ? "optional" : "required") +
        "; writes=" + (posture.AllowWrites ? "allowed" : "refused"));
}
else Console.WriteLine("MCP: off");
Console.WriteLine("TOKENS: " + (settings.RecoverableTokens
    ? "secrets kept and readable by their owner with a password"
    : "secrets unrecoverable once issued"));
Console.WriteLine(settings.PagesOrigin is null ? "PAGES: off" : "PAGES: " + settings.PagesOrigin + "/<project-slug>/");
Console.WriteLine("WEBHOOKS: " + (settings.Webhooks
    ? "on; a project Maintainer can make this server call an address of their choosing"
    : "off"));
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

    // `/projects/<slug>.git` is the same two segments as `/<owner>/<slug>.git`, so this route already
    // matches it and only the lookup changes. Everything after this point is identical: the same
    // credential, the same permission decision, the same refusals. A slug is a second name for a project,
    // never a second set of rules.
    bool byGlobalSlug = string.Equals(projectNamespace, GlobalNamespace, StringComparison.OrdinalIgnoreCase);
    ProjectInfo? project = byGlobalSlug
        ? await store.FindReadyProjectByGlobalSlugAsync(projectSlug, context.RequestAborted)
        : await store.FindReadyProjectAsync(projectNamespace, projectSlug, context.RequestAborted);
    AuthenticatedIdentity? identity = await Authenticate(context);

    // Push to a path that does not exist yet, and the project is created to receive it. Only on a push:
    // a fetch of a missing repository stays a miss, because creating something in order to report that it
    // is empty would be absurd. Creation happens on the ref advertisement rather than on the pack upload,
    // because the advertisement is the first request Git makes and it would otherwise fail before the push
    // began.
    //
    // Never through a slug: that path names no owner, and a project has to be created in somebody's
    // namespace. A push to an unused slug is simply a push to something that is not there.
    if (project is null && !byGlobalSlug && identity is not null && action == ProjectAction.PushRepository && settings.PushToCreate)
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
        // Two segments is GitLab's `owner/slug`. One segment that is not a number is Graft's instance-wide
        // slug, which GitLab has no meaning for at all, so reading it here takes nothing away from a
        // GitLab client and lets an API caller use the same name it was given to share.
        details = path.Length switch
        {
            2 => await store.FindReadyProjectDetailsAsync(path[0], path[1], context.RequestAborted),
            1 => await store.FindReadyProjectDetailsByGlobalSlugAsync(path[0], context.RequestAborted),
            _ => null
        };
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

/// <summary>
/// Removes a project and then its repository, in that order.
///
/// <para>
/// The database first, because that is what makes the project inaccessible, and the directory afterwards.
/// If the directory removal fails the instance is left with an unreferenced directory whose name is a
/// random identifier that will never be issued again, which is harmless and is named in the audit row the
/// deletion wrote. The other order could leave a project whose repository had already gone.
/// </para>
///
/// <para>
/// The cached repository is evicted and its gate taken first, so a request already inside the Git engine
/// finishes before the files under it are removed. A caller that will not yield within the wait keeps its
/// directory: the row is gone either way, so nothing can reach it.
/// </para>
/// </summary>
async Task DeleteProject(HttpContext context, AuthenticatedIdentity identity, long projectId)
{
    DeletedProject deleted;
    try { deleted = await store.DeleteProjectAsync(identity, projectId, context.RequestAborted); }
    catch (UnauthorizedAccessException error) { await ApiError(context, 403, error.Message); return; }
    catch (InvalidOperationException error) { await ApiError(context, 409, error.Message); return; }

    if (repositories.TryRemove(deleted.StorageId, out Lazy<RepositoryEndpoint>? cached) && cached.IsValueCreated)
    {
        if (await cached.Value.Gate.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None))
            cached.Value.Gate.Release();
        else
        {
            Console.Error.WriteLine($"DELETE PROJECT: repository {deleted.StorageId} was busy; its directory remains.");
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
    }

    string directory = Path.Combine(settings.Repositories, deleted.StorageId);
    try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"DELETE PROJECT: repository {deleted.StorageId} could not be removed: {error.Message}");
    }
    context.Response.StatusCode = StatusCodes.Status204NoContent;
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
    ForkInfo forks = await store.GetForkInfoAsync(details.Project.Id, context.RequestAborted);
    return new(details.Project.Id, details.Description, details.Project.Name,
        details.Project.Namespace + " / " + details.Project.Name, details.Project.Slug, path, details.CreatedAtUtc,
        defaultBranch, VisibilityName(details.Project.Visibility), null, webUrl + ".git", webUrl, avatarUrl,
        new(details.Project.OwnerUserId, details.Project.Namespace, details.Project.Namespace, "user", details.Project.Namespace,
            origin + "/" + Uri.EscapeDataString(details.Project.Namespace)),
        details.Project.GlobalSlug,
        details.Project.GlobalSlug.Length == 0 ? "" : origin + GlobalPrefix + "/" + Uri.EscapeDataString(details.Project.GlobalSlug),
        forks.UpstreamId is long upstreamId ? new ForkedFromResponse(upstreamId, forks.UpstreamPath!, origin + "/" + forks.UpstreamPath) : null,
        forks.ForksCount);
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

/// <summary>Git names the protocol version it can speak in a header; the value may carry other keys separated by colons.</summary>
static bool WantsProtocolV2(HttpContext context) =>
    context.Request.Headers["Git-Protocol"].ToString().Split(':').Any(part => part.Trim() == "version=2");

static string Origin(HttpContext context) => context.Request.Scheme + "://" + context.Request.Host.Value;

/// <summary>
/// Every refusal on the pages origin, in one shape. Plain text and always 404: a site that is not there, a
/// project that published nothing, and a file outside the configured root must be indistinguishable, or
/// the pages host becomes a way to enumerate what an instance holds.
/// </summary>
static async Task PagesMiss(HttpContext context, string message)
{
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    context.Response.ContentType = "text/plain; charset=utf-8";
    await context.Response.WriteAsync(message, context.RequestAborted);
}

/// <summary>
/// Collects the paths a page's allowlist admits, by walking what the repository actually holds and asking
/// the matcher about each one. The other direction, expanding patterns into paths, would invent names that
/// are not there and would be a second implementation of the only rule that matters.
///
/// <para>
/// Bounded in both breadth and depth: a page lists at most 500 files and descends at most 16 directories,
/// so a repository cannot make listing its own page expensive.
/// </para>
/// </summary>
static void CollectPagePaths(Repository repository, PageSettings page, string directory, List<string> found, int depth = 0)
{
    if (found.Count >= 500 || depth > 16) return;
    IReadOnlyList<GitTreeEntry>? entries;
    try { entries = RepositoryView.Tree(repository, "HEAD", directory); }
    catch (GitInputException) { return; }
    if (entries is null) return;

    foreach (GitTreeEntry entry in entries)
    {
        if (found.Count >= 500) return;
        // The config's own directory is never walked, at any visibility.
        if (entry.Path.Equals(PageConfiguration.ReservedDirectory, StringComparison.OrdinalIgnoreCase)) continue;
        if (entry.Type == "tree") CollectPagePaths(repository, page, entry.Path, found, depth + 1);
        else if (PageConfiguration.AllowsFile(page, entry.Path)) found.Add(entry.Path);
    }
}

/// <summary>Reads one file from a repository's default branch, or null when it is not there.</summary>
static byte[]? ReadRepositoryFile(Repository repository, string path)
{
    try { return RepositoryView.File(repository, "HEAD", path)?.Content; }
    catch (GitInputException) { return null; }
}

McpSettingsResponse McpResponse(McpSettings current) =>
    new(current.Enabled, current.AllowWrites, current.AllowAnonymous, serverOrigin + McpPath);

/// <summary>
/// The caller's Graft token for the agent endpoint. Both spellings are accepted because the API accepts
/// both, and an MCP client that can only set one of them should not be the thing that decides whether this
/// works. Empty means no credential was presented, which is a different thing from a bad one.
/// </summary>
static string McpToken(HttpRequest request)
{
    string token = request.Headers["PRIVATE-TOKEN"].ToString();
    if (token.Length > 0) return token;
    string authorization = request.Headers.Authorization.ToString();
    return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : "";
}

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

static string ProjectWebUrl(HttpContext context, ProjectDetails project) => ProjectUrlFor(context, project.Project);

// The same address from the lighter record. Local functions cannot be overloaded, hence the second name.
static string ProjectUrlFor(HttpContext context, ProjectInfo project) =>
    Origin(context) + "/" + Uri.EscapeDataString(project.Namespace) + "/" + Uri.EscapeDataString(project.Slug);

/// <summary>
/// Which branches a push moved, by comparing the references before and after. A branch that was created
/// reports a null previous commit and one that was deleted reports a null new one, so a receiver can tell
/// the three cases apart without guessing from the commit ids.
/// </summary>
static List<(string Branch, string? From, string? To)> Moved(
    Dictionary<string, string> before, Dictionary<string, string> after)
{
    List<(string Branch, string? From, string? To)> moved = [];
    foreach (string name in before.Keys.Union(after.Keys, StringComparer.Ordinal))
    {
        if (!name.StartsWith("refs/heads/", StringComparison.Ordinal)) continue;
        before.TryGetValue(name, out string? from);
        after.TryGetValue(name, out string? to);
        if (!string.Equals(from, to, StringComparison.Ordinal)) moved.Add((name[11..], from, to));
    }
    return moved;
}

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

/// <summary>
/// The forge's connection to its own listener, for the agent endpoint. A holder rather than the connection
/// itself because the tools are registered before the server starts and the port is only known after it.
/// Disposed with the service container, which outlives every request that could use it.
/// </summary>
sealed class McpEndpoint : IDisposable
{
    private GraftConnection? connection;

    public GraftConnection Connection => connection
        ?? throw new InvalidOperationException("The agent endpoint is not open until the server has started.");

    public void Open(GraftConnection opened) => connection = opened;

    public void Dispose() => connection?.Dispose();
}

/// <summary>
/// A token secret waiting to be collected: whose it is, what it is, and when it stops existing. Memory
/// only, so a restart discards every one of them.
/// </summary>
sealed record PendingSecret(long UserId, string Secret, DateTimeOffset ExpiresAtUtc);

sealed record Arguments(string Database, string Repositories, IPAddress ListenAddress, int ListenPort, string? Origin,
    string? Certificate, string? CertificatePasswordFile, bool PublicBrowsing, bool PushToCreate, int SessionMinutes, bool Mcp,
    string? PagesOrigin, bool RecoverableTokens, bool Webhooks)
{
    public static bool TryParse(string[] values, out Arguments? result)
    {
        result = null;
        if (values.Length is < 4 or > 26 || values.Length % 2 != 0) return false;
        string? database = null;
        string? repositories = null;
        string? listen = null;
        string? origin = null;
        string? certificate = null;
        string? certificatePasswordFile = null;
        string? publicBrowsing = null;
        string? pushToCreate = null;
        string? sessionMinutes = null;
        string? mcp = null;
        string? pagesOrigin = null;
        string? recoverableTokens = null;
        string? webhooks = null;
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
            else if (values[index] == "--mcp") mcp = values[index + 1];
            else if (values[index] == "--pages-origin") pagesOrigin = values[index + 1];
            else if (values[index] == "--recoverable-tokens") recoverableTokens = values[index + 1];
            else if (values[index] == "--webhooks") webhooks = values[index + 1];
            else return false;
        }
        if (string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(repositories)) return false;
        // Takes a value rather than being a bare switch, so the grammar stays one option per pair, and so an
        // operator can state the closed answer explicitly in a unit file or container command.
        if (publicBrowsing is not null and not "true" and not "false") return false;
        if (pushToCreate is not null and not "true" and not "false") return false;
        // Whether the forge hosts its Model Context Protocol endpoint at all. The operator's gate; the
        // administrator's switches beneath it live in the database and are edited in the browser.
        if (mcp is not null and not "true" and not "false") return false;
        // Whether a token's secret stays readable after it is issued. Off by default, and off is the only
        // state in which losing a token is not also a way to read somebody else's: see ADR 0012.
        if (recoverableTokens is not null and not "true" and not "false") return false;
        // Whether this server may make outbound HTTP requests on a project's behalf. Off by default, because
        // turning it on lets a project Maintainer cause the server to issue a request to any address it can
        // reach, which is an egress decision the operator owns rather than the project.
        if (webhooks is not null and not "true" and not "false") return false;
        // Where project sites are served, on a hostname of their own. Repository-authored HTML cannot share
        // an origin with the session cookie and the API, so this names a second host or the feature stays
        // off. An https origin with no path, for the same reasons --origin takes one.
        // Empty means unset, not malformed. A container passes every option it knows about and fills the
        // value from the environment, so an operator who never configured a pages origin sends an empty
        // string; refusing that would turn "I am not using this feature" into a server that will not start.
        if (string.IsNullOrWhiteSpace(pagesOrigin)) pagesOrigin = null;
        if (pagesOrigin is not null && (!Uri.TryCreate(pagesOrigin, UriKind.Absolute, out Uri? pagesUri) ||
            pagesUri.Scheme != Uri.UriSchemeHttps || pagesUri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(pagesUri.UserInfo) || !string.IsNullOrEmpty(pagesUri.Query) ||
            !string.IsNullOrEmpty(pagesUri.Fragment)))
            return false;
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
            publicBrowsing == "true", pushToCreate == "true", window, mcp == "true",
            pagesOrigin, recoverableTokens == "true", webhooks == "true");
        return true;
    }
}
