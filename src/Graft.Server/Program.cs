using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
    Console.Error.WriteLine("Usage: Graft.Server --database <path> --repositories <directory> [--listen <https URI>] [--origin <https origin>].");
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

using RSA rsa = RSA.Create(2048);
CertificateRequest certificateRequest = new("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
SubjectAlternativeNameBuilder names = new();
names.AddIpAddress(IPAddress.Loopback);
names.AddDnsName("localhost");
certificateRequest.CertificateExtensions.Add(names.Build());
using X509Certificate2 generated = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
using X509Certificate2? imported = OperatingSystem.IsWindows()
    ? X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.DefaultKeySet)
    : null;
X509Certificate2 certificate = imported ?? generated;

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
        ? new(false, null, "")
        : new(true, browser.Value.Identity.Username, Csrf(browser.Value.Secret));
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
    await context.Response.WriteAsJsonAsync(new SessionResponse(true, identity.Username, Csrf(grant.Secret)),
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
        BranchResponse[] branches = RepositoryView.Branches(repository).Select(branch => new BranchResponse(
            branch.Name, false, false, branch.Name == "main", true, true, push.Allowed,
            ProjectWebUrl(context, details) + "/-/tree/" + Uri.EscapeDataString(branch.Name),
            CommitResponseFor(context, details, RepositoryView.ReadCommit(repository, branch.Id)))).ToArray();
        CommitResponse[] commits = RepositoryView.Commits(repository, revision, 0, 20)
            .Select(commit => CommitResponseFor(context, details, commit)).ToArray();
        TreeEntryResponse[] entries = tree.Select(entry => new TreeEntryResponse(entry.Id, entry.Name, entry.Type, entry.Path, entry.Mode)).ToArray();
        FileResponse? fileResponse = file is null ? null : FileResponseFor(file, revision);
        RepositoryPageResponse response = new(project, branches, commits, entries, fileResponse);
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
static string ProjectWebUrl(HttpContext context, ProjectDetails project) =>
    Origin(context) + "/" + Uri.EscapeDataString(project.Project.Namespace) + "/" + Uri.EscapeDataString(project.Project.Slug);

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

sealed record Arguments(string Database, string Repositories, IPAddress ListenAddress, int ListenPort, string? Origin)
{
    public static bool TryParse(string[] values, out Arguments? result)
    {
        result = null;
        if (values.Length is < 4 or > 8 || values.Length % 2 != 0) return false;
        string? database = null;
        string? repositories = null;
        string? listen = null;
        string? origin = null;
        for (int index = 0; index < values.Length; index += 2)
        {
            if (values[index] == "--database") database = values[index + 1];
            else if (values[index] == "--repositories") repositories = values[index + 1];
            else if (values[index] == "--listen") listen = values[index + 1];
            else if (values[index] == "--origin") origin = values[index + 1];
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
        result = new(Path.GetFullPath(database), Path.GetFullPath(repositories), address, port,
            origin is null ? null : origin.TrimEnd('/'));
        return true;
    }
}
