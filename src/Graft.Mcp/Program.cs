using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Graft.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// Graft's Model Context Protocol server. It is a separate process on purpose, rather than another route on
// the forge.
//
// Graft.Server publishes NativeAOT and that is a release claim, so an MCP library whose native behaviour
// nobody has yet verified does not belong inside it. Keeping it out here means the forge's dependency
// closure is unchanged, this can be pointed at an already-running Graft without redeploying anything, and
// an operator who wants no agent access simply does not run it.
//
// It speaks to Graft over the ordinary HTTP API with a personal access token, so it can reach exactly what
// that token's owner can reach and nothing else.

// The Apache and MIT terms this build redistributes under bind the binary rather than the source tree, so
// the binary has to be able to produce them. Checked before anything else, because it must work without a
// server, a token, or a network.
if (args.Length == 1 && args[0] == "--licences")
{
    try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { /* output is redirected */ }
    using Stream? notices = typeof(McpArguments).Assembly.GetManifestResourceStream("graft/THIRD-PARTY-NOTICES.md");
    Console.WriteLine(notices is null
        ? "Third-party notices were not embedded in this build."
        : await new StreamReader(notices).ReadToEndAsync());
    return 0;
}

if (!McpArguments.TryParse(args, out McpArguments? parsed))
{
    await Console.Error.WriteLineAsync(
        "Usage: Graft.Mcp --server <https origin> [--transport stdio|http] [--allow-writes true|false] [--ca-certificate <path>]");
    await Console.Error.WriteLineAsync(
        "         stdio: --token-file <UTF-8 path> is required. The token is read from a file, never from the");
    await Console.Error.WriteLineAsync(
        "                command line, which is visible to every process and lands in shell history.");
    await Console.Error.WriteLineAsync(
        "          http: --listen <uri> [--certificate <PKCS#12 path>] [--certificate-password-file <path>].");
    await Console.Error.WriteLineAsync(
        "                Each caller sends its own Graft token as PRIVATE-TOKEN or Authorization: Bearer, so");
    await Console.Error.WriteLineAsync(
        "                this server stores no credential and --token-file is refused.");
    await Console.Error.WriteLineAsync(
        "       Graft.Mcp --licences  prints the third-party notices this build redistributes.");
    return 2;
}
McpArguments settings = parsed!;

X509Certificate2? trustedRoot = null;
if (settings.CaCertificate is not null)
{
    try { trustedRoot = X509CertificateLoader.LoadCertificateFromFile(settings.CaCertificate); }
    catch (Exception error) when (error is CryptographicException or IOException)
    {
        await Console.Error.WriteLineAsync("Could not read the CA certificate: " + error.Message);
        return 2;
    }
}

using GraftConnection connection = new(settings.Server, settings.AllowWrites, trustedRoot);

return settings.Transport == McpTransport.Http
    ? await RunHttp(settings, connection)
    : await RunStdio(settings, connection);

async Task<int> RunStdio(McpArguments options, GraftConnection graft)
{
    string token;
    try { token = (await File.ReadAllTextAsync(options.TokenFile!)).Trim(); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        await Console.Error.WriteLineAsync("Could not read the token file: " + error.Message);
        return 2;
    }
    if (token.Length == 0)
    {
        await Console.Error.WriteLineAsync("The token file is empty.");
        return 2;
    }

    HostApplicationBuilder builder = Host.CreateApplicationBuilder();

    // Standard output carries the protocol. Anything written there that is not a JSON-RPC message corrupts
    // the stream, so logging goes to standard error and nowhere else.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

    builder.Services.AddSingleton(graft);
    builder.Services.AddSingleton(new GraftClient(graft, token));
    Register(builder.Services).WithStdioServerTransport();

    await builder.Build().RunAsync();
    return 0;
}

async Task<int> RunHttp(McpArguments options, GraftConnection graft)
{
    WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.Limits.MaxRequestBodySize = 1024 * 1024;
        kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        kestrel.Listen(options.ListenAddress!, options.ListenPort, listen =>
        {
            if (options.Certificate is not null) listen.UseHttps(LoadCertificate(options));
        });
    });

    builder.Services.AddSingleton(graft);
    builder.Services.AddHttpContextAccessor();

    // Per request, not per process. Over HTTP each caller presents their own Graft token and this server
    // stores none, so two callers reaching the same endpoint get precisely the access their own credentials
    // carry. A server holding one token would instead lend its owner's access to anyone who could reach the
    // port, which is the wrong thing to be one firewall rule away from.
    builder.Services.AddScoped(provider =>
    {
        HttpContext? context = provider.GetRequiredService<IHttpContextAccessor>().HttpContext;
        return new GraftClient(graft, context is null ? "" : ReadToken(context.Request));
    });

    Register(builder.Services).WithHttpTransport(http => http.Stateless = true);

    await using WebApplication app = builder.Build();

    // Refused before the protocol is spoken, so an unauthenticated caller cannot even enumerate the tools.
    // Graft re-checks the token on every call this forwards; this only stops the endpoint being a free
    // description of what is available to somebody who has not shown a credential yet.
    app.Use(async (context, next) =>
    {
        if (ReadToken(context.Request).Length == 0)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"Graft MCP\"";
            await context.Response.WriteAsync("Send a Graft personal access token as PRIVATE-TOKEN or Authorization: Bearer.");
            return;
        }
        await next(context);
    });

    app.MapMcp("/mcp");

    await app.StartAsync();
    foreach (string address in app.Urls) Console.WriteLine("READY: " + address + "/mcp");
    await app.WaitForShutdownAsync();
    return 0;
}

// WithTools<T> and never the assembly-scanning overload. Scanning is trim-unsafe and its failure mode is a
// server that starts cleanly and advertises no tools at all, which looks like a configuration problem for
// as long as it takes somebody to find this comment.
static IMcpServerBuilder Register(IServiceCollection services) =>
    services.AddMcpServer(options => options.ServerInfo = new() { Name = "graft", Version = "1.0.0" })
        .WithTools<GraftTools>();

/// <summary>
/// The caller's Graft token. Both spellings are accepted because Graft itself accepts both, and an MCP
/// client that can only set one of them should not be the thing that decides whether this works.
/// </summary>
static string ReadToken(HttpRequest request)
{
    string token = request.Headers["PRIVATE-TOKEN"].ToString();
    if (token.Length > 0) return token;
    string authorization = request.Headers.Authorization.ToString();
    return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : "";
}

static X509Certificate2 LoadCertificate(McpArguments options)
{
    string? password = options.CertificatePasswordFile is null
        ? null
        : File.ReadAllText(options.CertificatePasswordFile).Trim();
    return X509CertificateLoader.LoadPkcs12FromFile(options.Certificate!, password);
}

namespace Graft.Mcp
{
    internal enum McpTransport { Stdio, Http }

    /// <summary>
    /// The server's own settings, in the same one-option-per-pair grammar the rest of Graft uses.
    /// </summary>
    internal sealed record McpArguments(
        Uri Server,
        McpTransport Transport,
        string? TokenFile,
        bool AllowWrites,
        string? CaCertificate,
        IPAddress? ListenAddress,
        int ListenPort,
        string? Certificate,
        string? CertificatePasswordFile)
    {
        public static bool TryParse(string[] values, out McpArguments? result)
        {
            result = null;
            if (values.Length is < 2 or > 16 || values.Length % 2 != 0) return false;

            string? server = null;
            string? transport = null;
            string? tokenFile = null;
            string? allowWrites = null;
            string? caCertificate = null;
            string? listen = null;
            string? certificate = null;
            string? certificatePasswordFile = null;
            HashSet<string> seen = new(StringComparer.Ordinal);
            for (int index = 0; index < values.Length; index += 2)
            {
                if (!seen.Add(values[index])) return false;
                if (values[index] == "--server") server = values[index + 1];
                else if (values[index] == "--transport") transport = values[index + 1];
                else if (values[index] == "--token-file") tokenFile = values[index + 1];
                else if (values[index] == "--allow-writes") allowWrites = values[index + 1];
                else if (values[index] == "--ca-certificate") caCertificate = values[index + 1];
                else if (values[index] == "--listen") listen = values[index + 1];
                else if (values[index] == "--certificate") certificate = values[index + 1];
                else if (values[index] == "--certificate-password-file") certificatePasswordFile = values[index + 1];
                else return false;
            }

            if (string.IsNullOrWhiteSpace(server)) return false;
            if (allowWrites is not null and not "true" and not "false") return false;
            if (transport is not null and not "stdio" and not "http") return false;
            McpTransport chosen = transport == "http" ? McpTransport.Http : McpTransport.Stdio;

            // HTTPS only for the forge itself. The token is sent on every request as a header, and Graft
            // refuses to listen without TLS off loopback, so a plain-HTTP target would be either a typo or
            // something impersonating the forge.
            if (!Uri.TryCreate(server, UriKind.Absolute, out Uri? address) ||
                address.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(address.Query) ||
                !string.IsNullOrEmpty(address.Fragment) || !string.IsNullOrEmpty(address.UserInfo))
                return false;

            if (caCertificate is not null && !File.Exists(caCertificate)) return false;

            IPAddress? listenAddress = null;
            int listenPort = 0;
            if (chosen == McpTransport.Stdio)
            {
                // One credential for one process, read from a file. Over stdio there is no caller to carry a
                // token: the client is whoever launched this.
                if (string.IsNullOrWhiteSpace(tokenFile) || !File.Exists(tokenFile)) return false;
                if (listen is not null || certificate is not null || certificatePasswordFile is not null) return false;
            }
            else
            {
                // Refused rather than ignored. A stored token here would be lent to anyone who reached the
                // port, so accepting the option and quietly not using it would be the dangerous reading.
                if (tokenFile is not null) return false;
                if (string.IsNullOrWhiteSpace(listen)) return false;
                if (!Uri.TryCreate(listen, UriKind.Absolute, out Uri? listenUri) ||
                    (listenUri.Scheme != Uri.UriSchemeHttps && listenUri.Scheme != Uri.UriSchemeHttp) ||
                    listenUri.AbsolutePath != "/" || !string.IsNullOrEmpty(listenUri.Query) ||
                    !string.IsNullOrEmpty(listenUri.Fragment) ||
                    !IPAddress.TryParse(listenUri.Host, out listenAddress) || listenUri.Port is < 1 or > 65535)
                    return false;
                listenPort = listenUri.Port;

                // Off loopback the tokens callers send cross a network, so the endpoint must be TLS and must
                // have a certificate to be it. The same rule the forge applies to itself.
                if (!IPAddress.IsLoopback(listenAddress) && listenUri.Scheme != Uri.UriSchemeHttps) return false;
                if (listenUri.Scheme == Uri.UriSchemeHttps && certificate is null) return false;
                if (certificatePasswordFile is not null && certificate is null) return false;
                if (certificate is not null && !File.Exists(certificate)) return false;
                if (certificatePasswordFile is not null && !File.Exists(certificatePasswordFile)) return false;
            }

            // A trailing slash matters: a base address without one drops its last path segment when a
            // relative request is combined with it.
            string text = address.ToString();
            if (!text.EndsWith('/')) address = new Uri(text + "/");

            result = new(address, chosen,
                tokenFile is null ? null : Path.GetFullPath(tokenFile),
                allowWrites == "true",
                caCertificate is null ? null : Path.GetFullPath(caCertificate),
                listenAddress, listenPort,
                certificate is null ? null : Path.GetFullPath(certificate),
                certificatePasswordFile is null ? null : Path.GetFullPath(certificatePasswordFile));
            return true;
        }
    }
}
