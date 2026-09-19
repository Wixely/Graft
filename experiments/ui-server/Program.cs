using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Graft.Ui;
using Graft.Ui.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

if (args is ["--self-test"]) { Sessions.SelfCheck(); return 0; }
var password = Environment.GetEnvironmentVariable("GRAFT_UI_PROBE_PASSWORD");
if (args is not ["--fixture"] || password is not { Length: >= 16 and <= 256 })
{
    Console.Error.WriteLine("Disposable probe: pass --fixture and set GRAFT_UI_PROBE_PASSWORD (16-256 characters). No default credentials.");
    return 2;
}
var sessions = new Sessions(password);
Environment.SetEnvironmentVariable("GRAFT_UI_PROBE_PASSWORD", null);
password = null;
var assets = new WebAssets();
using var rsa = RSA.Create(2048);
var certificateRequest = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); names.AddDnsName("localhost");
certificateRequest.CertificateExtensions.Add(names.Build());
using var generated = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
using var imported = OperatingSystem.IsWindows() ? X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.DefaultKeySet) : null;
var certificate = imported ?? generated;
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate));
    options.Limits.MaxRequestBodySize = 4096;
    options.Limits.MaxConcurrentConnections = 32;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
});
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, UiJson.Default));
await using var app = builder.Build();
string? origin = null;
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (origin != "https://" + context.Request.Host.Value) { context.Response.StatusCode = 400; return; }
    if (HttpMethods.IsPost(context.Request.Method))
    {
        if (context.Request.Headers.Origin.ToString() != origin) { await Error(context, 403, "origin"); return; }
        var session = sessions.Find(context.Request.Cookies[Sessions.CookieName]);
        if (session is null) { await Error(context, 401, "session"); return; }
        if (!sessions.VerifyCsrf(session, context.Request.Headers["X-Graft-CSRF"])) { await Error(context, 403, "csrf"); return; }
    }
    try { await next(context); }
    catch (SessionLimitException) { await Error(context, 429, "limit"); }
    catch (JsonException) { await Error(context, 400, "json"); }
});
app.MapGet("/api/session", async context =>
{
    var view = sessions.Find(context.Request.Cookies[Sessions.CookieName]);
    if (view is null) { var issued = sessions.Issue(); SetCookie(context, issued.Token, issued.View); view = issued.View; }
    await context.Response.WriteAsJsonAsync(view, UiJson.Default.SessionView);
});
app.MapPost("/api/login", async context =>
{
    if (!context.Request.HasJsonContentType()) { await Error(context, 415, "content-type"); return; }
    var login = await context.Request.ReadFromJsonAsync(UiJson.Default.LoginRequest, context.RequestAborted);
    if (login?.Username is not { Length: > 0 and <= 80 } || login.Password is not { Length: > 0 and <= 256 })
    { await Error(context, 400, "credentials"); return; }
    var result = sessions.Login(context.Request.Cookies[Sessions.CookieName]!, login.Username, login.Password);
    if (result is null) { await Error(context, 401, "credentials"); return; }
    SetCookie(context, result.Value.Token, result.Value.View);
    await context.Response.WriteAsJsonAsync(result.Value.View, UiJson.Default.SessionView);
});
app.MapGet("/api/projects", async context =>
{
    if (sessions.Find(context.Request.Cookies[Sessions.CookieName])?.Authenticated != true) { await Error(context, 401, "session"); return; }
    ProjectView[] projects = [new("Graft sandbox", "A private place to test the first workflow.")];
    await context.Response.WriteAsJsonAsync(projects, UiJson.Default.ProjectViewArray);
});
app.MapPost("/api/logout", context =>
{
    sessions.Revoke(context.Request.Cookies[Sessions.CookieName]!);
    context.Response.Cookies.Delete(Sessions.CookieName, CookieOptions());
    context.Response.StatusCode = 204;
    return Task.CompletedTask;
});
app.MapFallback("/{**path}", async context =>
{
    var path = context.Request.Path.Value ?? "/";
    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || path.Equals("/api", StringComparison.OrdinalIgnoreCase) || path.Contains(".git", StringComparison.OrdinalIgnoreCase))
    { await Error(context, 404, "not-found"); return; }
    await assets.Serve(context);
});
await app.StartAsync();
origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
Console.WriteLine("MODE: " + (RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT"));
Console.WriteLine($"ASSETS: {assets.Count}; bytes={assets.Bytes}");
Console.WriteLine("CERT-SHA256: " + certificate.GetCertHashString(HashAlgorithmName.SHA256));
Console.WriteLine("READY: " + origin);
await app.WaitForShutdownAsync();
return 0;

static CookieOptions CookieOptions() => new() { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true };
static void SetCookie(HttpContext context, string token, SessionView view)
{
    var options = CookieOptions(); options.Expires = view.Expires;
    context.Response.Cookies.Append(Sessions.CookieName, token, options);
}
static Task Error(HttpContext context, int status, string error)
{
    context.Response.StatusCode = status;
    return context.Response.WriteAsJsonAsync(new ErrorView(error), UiJson.Default.ErrorView);
}
