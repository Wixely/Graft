using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Graft.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
        "Usage: Graft.Mcp --server <https origin> --token-file <UTF-8 path> [--allow-writes true|false] [--ca-certificate <PEM or DER path>]");
    await Console.Error.WriteLineAsync(
        "       The token is read from a file, never from the command line, because a command line is visible");
    await Console.Error.WriteLineAsync(
        "       to every process on the machine and lands in shell history.");
    await Console.Error.WriteLineAsync(
        "       Graft.Mcp --licences  prints the third-party notices this build redistributes.");
    return 2;
}
McpArguments settings = parsed!;

string token;
try { token = (await File.ReadAllTextAsync(settings.TokenFile)).Trim(); }
catch (IOException error)
{
    await Console.Error.WriteLineAsync("Could not read the token file: " + error.Message);
    return 2;
}
catch (UnauthorizedAccessException error)
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

// Standard output carries the protocol. Anything written there that is not a JSON-RPC message corrupts the
// stream, so logging goes to standard error and nowhere else.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

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

builder.Services.AddSingleton(new GraftClient(settings.Server, token, settings.AllowWrites, trustedRoot));

// WithTools<T> and never the assembly-scanning overload. Scanning is trim-unsafe and its failure mode is a
// server that starts cleanly and advertises no tools at all, which looks like a configuration problem for
// as long as it takes somebody to find this comment.
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "graft", Version = "1.0.0" };
    })
    .WithStdioServerTransport()
    .WithTools<GraftTools>();

await builder.Build().RunAsync();
return 0;

namespace Graft.Mcp
{
    /// <summary>
    /// The server's own settings, in the same one-option-per-pair grammar the rest of Graft uses.
    /// </summary>
    internal sealed record McpArguments(Uri Server, string TokenFile, bool AllowWrites, string? CaCertificate)
    {
        public static bool TryParse(string[] values, out McpArguments? result)
        {
            result = null;
            if (values.Length is < 4 or > 8 || values.Length % 2 != 0) return false;

            string? server = null;
            string? tokenFile = null;
            string? allowWrites = null;
            string? caCertificate = null;
            HashSet<string> seen = new(StringComparer.Ordinal);
            for (int index = 0; index < values.Length; index += 2)
            {
                if (!seen.Add(values[index])) return false;
                if (values[index] == "--server") server = values[index + 1];
                else if (values[index] == "--token-file") tokenFile = values[index + 1];
                else if (values[index] == "--allow-writes") allowWrites = values[index + 1];
                else if (values[index] == "--ca-certificate") caCertificate = values[index + 1];
                else return false;
            }

            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(tokenFile)) return false;
            if (allowWrites is not null and not "true" and not "false") return false;

            // HTTPS only. The token is sent on every request as a bearer-style header, and Graft itself
            // refuses to listen without TLS off loopback, so a plain-HTTP target here would be either a
            // typo or something impersonating the forge.
            if (!Uri.TryCreate(server, UriKind.Absolute, out Uri? address) ||
                address.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(address.Query) ||
                !string.IsNullOrEmpty(address.Fragment) || !string.IsNullOrEmpty(address.UserInfo))
                return false;

            if (!File.Exists(tokenFile)) return false;
            if (caCertificate is not null && !File.Exists(caCertificate)) return false;

            // A trailing slash matters: a base address without one drops its last path segment when a
            // relative request is combined with it.
            string text = address.ToString();
            if (!text.EndsWith('/')) address = new Uri(text + "/");

            result = new(address, Path.GetFullPath(tokenFile), allowWrites == "true",
                caCertificate is null ? null : Path.GetFullPath(caCertificate));
            return true;
        }
    }
}
