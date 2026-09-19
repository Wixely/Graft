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
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

if (!Arguments.TryParse(args, out Arguments? parsedSettings))
{
    Console.Error.WriteLine("Usage: Graft.Server --database <path> --repositories <directory> [--listen <https URI>] [--origin <https origin>] [--certificate <PKCS#12 path>] [--certificate-password-file <UTF-8 path>].");
    return 2;
}
Arguments settings = parsedSettings!;

Directory.CreateDirectory(settings.Repositories);
using MaintenanceLease maintenanceLease = MaintenanceLease.Acquire(settings.Database, settings.Repositories);
await using GraftStore store = await GraftStore.OpenAsync(settings.Database);
WebAssets assets = new();
ConcurrentDictionary<string, Lazy<RepositoryEndpoint>> repositories = new(StringComparer.Ordinal);
byte[] csrfKey = RandomNumberGenerator.GetBytes(32);
LoginThrottle loginThrottle = new();
string? serverOrigin = null;
const string SessionCookie = "__Host-graft-session";

using X509Certificate2 certificate = LoadCertificate(settings);

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
builder.Host.UseWindowsService(options => options.ServiceName = "Graft");
builder.Host.UseSystemd();
builder.Logging.ClearProviders();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJson.Default));
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(settings.ListenAddress, settings.ListenPort, listen => listen.UseHttps(certificate));
    options.Limits.MaxRequestBodySize = Limits.RequestBytes;
    options.Limits.MaxConcurrentConnections = 16;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
});

await using WebApplication app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    deadline.CancelAfter(TimeSpan.FromSeconds(30));
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

app.MapGet("/api/session", async context =>
{
    if (!await OnlyQuery(context)) return;
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: false);
    SessionResponse response = browser is null
        ? new(false, null, false, "")
        : new(true, browser.Value.Identity.Username, browser.Value.Identity.IsAdministrator, Csrf(browser.Value.Secret));
    await context.Response.WriteAsJsonAsync(response, ApiJson.Default.SessionResponse, null, context.RequestAborted);
});

app.MapGet("/healthz", async context =>
{
    if (!await OnlyQuery(context)) return;
    context.Response.ContentType = "text/plain; charset=utf-8";
    await context.Response.WriteAsync("healthy", context.RequestAborted);
});

app.MapPost("/api/login", async context =>
{
    if (context.Request.Headers.Origin.ToString() != serverOrigin) { await ApiError(context, 403, "origin"); return; }
    if (!context.Request.HasJsonContentType()) { await ApiError(context, 415, "content-type"); return; }
    if (!loginThrottle.TryEnter(DateTimeOffset.UtcNow)) { await ApiError(context, 429, "login-rate-limit"); return; }
    LoginRequest? login = await context.Request.ReadFromJsonAsync(ApiJson.Default.LoginRequest, context.RequestAborted);
    if (login?.Username is not { Length: >= 2 and <= 39 } || login.Password is not { Length: >= 16 and <= 256 })
    { await ApiError(context, 400, "credentials"); return; }
    SessionGrant? grant = await store.LoginAsync(login.Username, login.Password, context.RequestAborted);
    if (grant is null) { await ApiError(context, 401, "credentials"); return; }
    context.Response.Cookies.Append(SessionCookie, grant.Secret, SessionCookieOptions(grant.ExpiresAtUtc));
    AuthenticatedIdentity identity = (await store.ResolveSessionAsync(grant.Secret, context.RequestAborted))!;
    await context.Response.WriteAsJsonAsync(new SessionResponse(true, identity.Username, identity.IsAdministrator, Csrf(grant.Secret)),
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
    if (request?.Username is not { Length: >= 2 and <= 39 } || request.Password is not { Length: >= 16 and <= 256 })
    { await ApiError(context, 400, "user"); return; }
    try
    {
        UserAccount user = await store.CreateUserAsync(browser.Value.Identity, request.Username, request.Password, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(new UserAccountResponse(user.Id, user.Username, user.IsAdministrator),
            ApiJson.Default.UserAccountResponse, null, context.RequestAborted);
    }
    catch (ArgumentException) { await ApiError(context, 400, "username"); }
    catch (UserConflictException) { await ApiError(context, 400, "username has already been taken"); }
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

app.MapGet("/api/browser/projects/{projectId:long}", async context =>
{
    (AuthenticatedIdentity Identity, string Secret)? browser = await AuthenticateBrowser(context, writeError: true);
    if (browser is null) return;
    if (!await OnlyQuery(context, "ref", "path", "file")) return;
    long projectId = Convert.ToInt64(context.Request.RouteValues["projectId"], System.Globalization.CultureInfo.InvariantCulture);
    ProjectDetails? details = await store.GetReadyProjectAsync(projectId, context.RequestAborted);
    if (details is null || !(await store.GetPermissionAsync(browser.Value.Identity, projectId, ProjectAction.BrowseRepository, context.RequestAborted)).Allowed)
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
        PermissionDecision push = await store.GetPermissionAsync(browser.Value.Identity, projectId, ProjectAction.PushRepository, context.RequestAborted);
        PermissionDecision manage = await store.GetPermissionAsync(browser.Value.Identity, projectId, ProjectAction.ManageProject, context.RequestAborted);
        BranchResponse[] branches = RepositoryView.Branches(repository).Select(branch => new BranchResponse(
            branch.Name, false, false, branch.Name == "main", true, true, push.Allowed,
            ProjectWebUrl(context, details) + "/-/tree/" + Uri.EscapeDataString(branch.Name),
            CommitResponseFor(context, details, RepositoryView.ReadCommit(repository, branch.Id)))).ToArray();
        CommitResponse[] commits = RepositoryView.Commits(repository, revision, 0, 20)
            .Select(commit => CommitResponseFor(context, details, commit)).ToArray();
        TreeEntryResponse[] entries = tree.Select(entry => new TreeEntryResponse(entry.Id, entry.Name, entry.Type, entry.Path, entry.Mode)).ToArray();
        FileResponse? fileResponse = file is null ? null : FileResponseFor(file, revision);
        RepositoryPageResponse response = new(project, manage.Allowed, branches, commits, entries, fileResponse);
        await context.Response.WriteAsJsonAsync(response, ApiJson.Default.RepositoryPageResponse, null, context.RequestAborted);
    });
});

app.MapGet("/api/v4/user", async context =>
{
    AuthenticatedIdentity? identity = await AuthenticateApi(context, requireApiScope: true);
    if (identity is null) return;
    if (!await OnlyQuery(context)) return;
    string origin = Origin(context);
    await context.Response.WriteAsJsonAsync(new UserResponse(
        identity.UserId, identity.Username, identity.Username, "active", false, origin + "/" + Uri.EscapeDataString(identity.Username)),
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
        PermissionDecision push = await store.GetPermissionAsync(project.Identity, project.Details.Project.Id, ProjectAction.PushRepository, context.RequestAborted);
        BranchResponse[] result = branches.Skip((page - 1) * perPage).Take(perPage).Select(branch =>
            new BranchResponse(branch.Name, false, false, branch.Name == "main", true, true, push.Allowed,
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
        byte[] data = service == "git-receive-pack"
            ? authorized.Endpoint.Repository.Receive(input.ToArray(), context.RequestAborted)
            : authorized.Endpoint.Repository.Upload(input.ToArray(), context.RequestAborted);
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
Console.WriteLine($"ASSETS: {assets.Count}; bytes={assets.Bytes}");
Console.WriteLine("READY: " + address);
await app.WaitForShutdownAsync();
return 0;

async Task<AuthorizedRepository?> Authorize(HttpContext context, ProjectAction action)
{
    string projectNamespace = context.Request.RouteValues["projectNamespace"]?.ToString() ?? "";
    string projectSlug = context.Request.RouteValues["project"]?.ToString() ?? "";
    ProjectInfo? project = await store.FindReadyProjectAsync(projectNamespace, projectSlug, context.RequestAborted);
    AuthenticatedIdentity? identity = await Authenticate(context);
    if (identity is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Graft Git\", charset=\"UTF-8\"";
        return null;
    }
    if (project is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return null;
    }
    PermissionDecision decision = await store.GetPermissionAsync(identity, project.Id, action, context.RequestAborted);
    if (!decision.Allowed)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return null;
    }
    RepositoryEndpoint endpoint = EndpointFor(project);
    return new(project, identity, decision, endpoint);
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
    if (!string.Equals(visibility, "private", StringComparison.Ordinal))
    { await ApiError(context, 400, "visibility must be private"); return; }
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

async Task<ApiProject?> AuthorizeApiProject(HttpContext context, ProjectAction action)
{
    AuthenticatedIdentity? identity = await AuthenticateApi(context, requireApiScope: false);
    if (identity is null) return null;
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
    PermissionDecision permission = await store.GetPermissionAsync(identity, details.Project.Id, action, context.RequestAborted);
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
    return repositories.GetOrAdd(project.StorageId, storageId =>
        new(() => new(new Repository(new DurableRepositoryStorage(Path.Combine(settings.Repositories, storageId)))),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
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

Task<ProjectResponse> ProjectResponseForLocked(HttpContext context, ProjectDetails details, Repository repository)
{
    string? defaultBranch = repository.Refs.ContainsKey("refs/heads/main") ? "main" : null;
    string origin = Origin(context);
    string path = details.Project.Namespace + "/" + details.Project.Slug;
    string webUrl = origin + "/" + path;
    ProjectResponse response = new(details.Project.Id, details.Description, details.Project.Name,
        details.Project.Namespace + " / " + details.Project.Name, details.Project.Slug, path, details.CreatedAtUtc,
        defaultBranch, "private", null, webUrl + ".git", webUrl,
        new(details.Project.OwnerUserId, details.Project.Namespace, details.Project.Namespace, "user", details.Project.Namespace,
            origin + "/" + Uri.EscapeDataString(details.Project.Namespace)));
    return Task.FromResult(response);
}

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
    AuthenticatedIdentity? identity = secret is null ? null : await store.ResolveSessionAsync(secret, context.RequestAborted);
    if (identity is not null) return (identity, secret!);
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
    AuthenticatedIdentity Identity,
    PermissionDecision Permission,
    RepositoryEndpoint Endpoint);

sealed record ApiProject(ProjectDetails Details, AuthenticatedIdentity Identity, RepositoryEndpoint Endpoint);

sealed class RepositoryEndpoint(Repository repository)
{
    public Repository Repository { get; } = repository;
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

sealed class LoginThrottle
{
    private readonly object gate = new();
    private DateTimeOffset window;
    private int attempts;

    public bool TryEnter(DateTimeOffset now)
    {
        lock (gate)
        {
            if (now >= window.AddMinutes(1)) { window = now; attempts = 0; }
            return ++attempts <= 20;
        }
    }
}

sealed record Arguments(string Database, string Repositories, IPAddress ListenAddress, int ListenPort, string? Origin,
    string? Certificate, string? CertificatePasswordFile)
{
    public static bool TryParse(string[] values, out Arguments? result)
    {
        result = null;
        if (values.Length is < 4 or > 12 || values.Length % 2 != 0) return false;
        string? database = null;
        string? repositories = null;
        string? listen = null;
        string? origin = null;
        string? certificate = null;
        string? certificatePasswordFile = null;
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
            else return false;
        }
        if (string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(repositories)) return false;
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
            certificatePasswordFile is null ? null : Path.GetFullPath(certificatePasswordFile));
        return true;
    }
}
