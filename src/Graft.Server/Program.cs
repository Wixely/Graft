using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Graft.Core;
using Graft.Git;
using Graft.Storage;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

if (!Arguments.TryParse(args, out Arguments? parsedSettings))
{
    Console.Error.WriteLine("Usage: Graft.Server --database <path> --repositories <directory>. The MVP-05 host binds loopback HTTPS only.");
    return 2;
}
Arguments settings = parsedSettings!;

Directory.CreateDirectory(settings.Repositories);
await using GraftStore store = await GraftStore.OpenAsync(settings.Database);
ConcurrentDictionary<string, Lazy<RepositoryEndpoint>> repositories = new(StringComparer.Ordinal);

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
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate));
    options.Limits.MaxRequestBodySize = Limits.RequestBytes;
    options.Limits.MaxConcurrentConnections = 16;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
});

await using WebApplication app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    deadline.CancelAfter(TimeSpan.FromSeconds(30));
    context.RequestAborted = deadline.Token;
    try { await next(context); }
    catch (GitInputException error)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync("Rejected: " + error.Message, CancellationToken.None);
    }
    catch (OperationCanceledException) { context.Abort(); }
    catch (IOException)
    {
        context.Response.StatusCode = StatusCodes.Status507InsufficientStorage;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync("Repository storage failed.", CancellationToken.None);
    }
});

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

await app.StartAsync();
string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
Console.WriteLine("MODE: " + (RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT"));
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
    if (project.StorageId.Length != 32 || project.StorageId.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        throw new InvalidOperationException("Invalid project storage identifier.");
    RepositoryEndpoint endpoint = repositories.GetOrAdd(project.StorageId, storageId =>
        new(() => new(new Repository(new DurableRepositoryStorage(Path.Combine(settings.Repositories, storageId)))),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
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

sealed record AuthorizedRepository(
    ProjectInfo Project,
    AuthenticatedIdentity Identity,
    PermissionDecision Permission,
    RepositoryEndpoint Endpoint);

sealed class RepositoryEndpoint(Repository repository)
{
    public Repository Repository { get; } = repository;
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

sealed record Arguments(string Database, string Repositories)
{
    public static bool TryParse(string[] values, out Arguments? result)
    {
        result = null;
        if (values.Length != 4) return false;
        string? database = null;
        string? repositories = null;
        for (int index = 0; index < values.Length; index += 2)
        {
            if (values[index] == "--database") database = values[index + 1];
            else if (values[index] == "--repositories") repositories = values[index + 1];
            else return false;
        }
        if (string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(repositories)) return false;
        result = new(Path.GetFullPath(database), Path.GetFullPath(repositories));
        return true;
    }
}
