using System.Net;
using System.Runtime.CompilerServices;
using Graft.ManagedGit;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

if (args is ["--self-test"]) return SelfChecks.Run();
if (args.Length != 0)
{
    Console.Error.WriteLine("Usage: Graft.ManagedGit [--self-test]. Disposable loopback-only probe; no authentication or persistence.");
    return 2;
}

var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, 0);
    options.Limits.MaxRequestBodySize = Limits.RequestBytes;
    options.Limits.MaxConcurrentConnections = 4;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
});
await using var app = builder.Build();
var repository = new Repository();
using var gate = new SemaphoreSlim(1);
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!await gate.WaitAsync(0, context.RequestAborted))
    {
        context.Response.StatusCode = 429; return;
    }
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    deadline.CancelAfter(TimeSpan.FromSeconds(20));
    context.RequestAborted = deadline.Token;
    try { await next(context); }
    catch (GitInputException ex)
    {
        context.Response.StatusCode = 400;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync("Rejected: " + ex.Message, CancellationToken.None);
    }
    catch (OperationCanceledException) { context.Abort(); }
    finally { gate.Release(); }
});
app.MapGet("/fixture.git/info/refs", async context =>
{
    var service = context.Request.Query["service"].ToString();
    var data = repository.Advertise(service);
    context.Response.ContentType = "application/x-" + service + "-advertisement";
    await context.Response.Body.WriteAsync(data, context.RequestAborted);
});
app.MapPost("/fixture.git/{service}", async context =>
{
    var service = context.Request.RouteValues["service"]?.ToString();
    Limits.Require(service is "git-receive-pack" or "git-upload-pack", "service");
    Limits.Require(context.Request.ContentType == "application/x-" + service + "-request", "content-type");
    Limits.Require(!context.Request.Headers.ContainsKey("Content-Encoding"), "content-encoding");
    using var input = new MemoryStream();
    var buffer = new byte[8192];
    int count;
    while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
    {
        Limits.Require(input.Length + count <= Limits.RequestBytes, "request-limit");
        input.Write(buffer, 0, count);
    }
    var data = service == "git-receive-pack"
        ? repository.Receive(input.ToArray(), context.RequestAborted)
        : repository.Upload(input.ToArray(), context.RequestAborted);
    context.Response.ContentType = "application/x-" + service + "-result";
    await context.Response.Body.WriteAsync(data, context.RequestAborted);
    if (service == "git-receive-pack")
        Console.WriteLine($"RECEIVE objects={repository.Objects.Count} ofs={repository.OffsetDeltas} ref={repository.ReferenceDeltas}");
});
await app.StartAsync();
Console.WriteLine("MODE: " + (RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT"));
Console.WriteLine("READY: " + app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
await app.WaitForShutdownAsync();
return 0;
