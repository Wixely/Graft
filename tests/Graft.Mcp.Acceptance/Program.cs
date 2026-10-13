using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Graft.Core;
using Graft.Storage;

// Drives Graft's MCP server over both transports, as a client would, against a real Graft.
//
// Launching it as a child process rather than calling its types is deliberate. What matters here is the
// protocol on the wire and the credential handling around it, and neither is exercised by an in-process
// call. The stdio half in particular can only be wrong in ways a reference would hide: a stray line on
// standard output corrupts the stream, and nothing but a real client reading it would notice.

if (args.Length != 2 || !File.Exists(args[0]) || !File.Exists(args[1]))
{
    Console.Error.WriteLine("Usage: Graft.Mcp.Acceptance <Graft.Server DLL or executable> <Graft.Mcp DLL or executable>");
    return 2;
}

string serverPath = Path.GetFullPath(args[0]);
string mcpPath = Path.GetFullPath(args[1]);
string root = Path.Combine(Path.GetTempPath(), "graft-mcp-" + Guid.NewGuid().ToString("N"));
string databasePath = Path.Combine(root, "graft.db");
string repositoriesRoot = Path.Combine(root, "repositories");
string tokenFile = Path.Combine(root, "token.txt");
string caFile = Path.Combine(root, "server-ca.pem");
Directory.CreateDirectory(root);
Directory.CreateDirectory(repositoriesRoot);

int assertions = 0;
Process? server = null;
Process? mcp = null;

try
{
    string password = "mcp-acceptance-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
    string token;
    await using (GraftStore fixture = await GraftStore.OpenAsync(databasePath))
    {
        UserAccount admin = await fixture.BootstrapAdministratorAsync("admin", password);
        SessionGrant session = Required(await fixture.LoginAsync(admin.Username, password), "login");
        AuthenticatedIdentity identity = Required(await fixture.ResolveSessionAsync(session.Secret), "identity");
        await fixture.CreateProjectWithRepositoryAsync(identity, "shared", "Shared project",
            (created, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.Combine(repositoriesRoot, created.StorageId));
                return Task.CompletedTask;
            }, "Visible to anyone.");
        ProjectInfo project = Required(await fixture.FindReadyProjectAsync("admin", "shared"), "project");
        await fixture.SetProjectVisibilityAsync(identity, project.Id, ProjectVisibility.Public);
        TokenGrant grant = await fixture.CreateTokenAsync(identity, "mcp", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1));
        token = grant.Secret;
    }
    await File.WriteAllTextAsync(tokenFile, token);

    string address = await StartServer();
    await ExportCertificateAsync(address, caFile);

    // Stdio. The transport a client launches directly, where the credential belongs to the process.
    await CheckStdio(address, readOnly: true);
    await CheckStdio(address, readOnly: false);

    // Streamable HTTP. The transport where the credential belongs to the caller.
    await CheckHttp(address, token);

    Console.WriteLine($"RESULT: PASS; assertions: {assertions}");
    Console.WriteLine("BOUNDARY: loopback only; the MCP endpoint is plain HTTP on loopback and the forge uses an ephemeral certificate pinned by file.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("RESULT: FAIL: " + error.GetType().Name + ": " + error.Message);
    return 1;
}
finally
{
    Stop(mcp);
    Stop(server);
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}

async Task CheckStdio(string address, bool readOnly)
{
    string label = readOnly ? "stdio read-only" : "stdio with writes";
    ProcessStartInfo start = Launch(mcpPath,
        ["--server", address, "--token-file", tokenFile, "--ca-certificate", caFile,
         "--allow-writes", readOnly ? "false" : "true"]);
    start.RedirectStandardInput = true;
    using Process client = Process.Start(start)!;
    try
    {
        JsonDocument initialize = await Exchange(client, Request(1, "initialize", """
            {"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"acceptance","version":"1"}}
            """));
        Check(initialize.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString() == "graft",
            label + " completes the handshake");
        initialize.Dispose();

        await client.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await client.StandardInput.FlushAsync();

        using JsonDocument tools = await Exchange(client, Request(2, "tools/list", "{}"));
        int count = tools.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength();
        Check(count == 10, label + " advertises every tool");

        using JsonDocument read = await Exchange(client, Call(3, "graft_browse", """{"search":""}"""));
        Check(Text(read).Contains("admin/shared", StringComparison.Ordinal), label + " reads the public listing");

        using JsonDocument write = await Exchange(client, Call(4, "graft_set_page_published",
            """{"project":"admin/shared","published":true}"""));
        string result = Text(write);
        if (readOnly)
            Check(result.Contains("read-only", StringComparison.Ordinal) &&
                result.Contains("--allow-writes", StringComparison.Ordinal),
                "a read-only server refuses a write and names the switch that would permit it");
        else
            Check(result.Contains("\"page_published\":true", StringComparison.Ordinal),
                "a server started with writes performs one");
    }
    finally { Stop(client); }
}

async Task CheckHttp(string address, string token)
{
    int port = FreePort();
    string endpoint = "http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    mcp = Process.Start(Launch(mcpPath,
        ["--server", address, "--transport", "http", "--listen", endpoint,
         "--ca-certificate", caFile, "--allow-writes", "true"]))!;
    await WaitForListener(port);

    using HttpClient client = new() { BaseAddress = new(endpoint), Timeout = TimeSpan.FromSeconds(30) };

    // No credential at all. Refused before the protocol is spoken, so the tool list is not a free
    // description of the forge for somebody who has shown nothing.
    using (HttpResponseMessage anonymous = await Post(client, null, Request(1, "initialize", """
        {"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"acceptance","version":"1"}}
        """)))
    {
        Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "the HTTP endpoint refuses a caller with no token");
        Check(anonymous.Headers.WwwAuthenticate.Count > 0, "the refusal says how to authenticate");
    }

    using (HttpResponseMessage handshake = await Post(client, token, Request(2, "initialize", """
        {"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"acceptance","version":"1"}}
        """)))
    {
        Check(handshake.IsSuccessStatusCode, "the HTTP endpoint completes the handshake for a caller with a token");
        // Streamable HTTP answers as an event stream rather than a plain body, which is the whole point of
        // this transport and the thing a plain JSON response would quietly fail to be.
        Check(handshake.Content.Headers.ContentType?.MediaType == "text/event-stream",
            "the HTTP endpoint streams its reply");
        using JsonDocument body = Parse(await handshake.Content.ReadAsStringAsync());
        Check(body.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString() == "graft",
            "the streamed reply carries the server identity");
    }

    using (HttpResponseMessage listing = await Post(client, token, Request(3, "tools/list", "{}")))
    {
        using JsonDocument body = Parse(await listing.Content.ReadAsStringAsync());
        Check(body.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() == 10,
            "the HTTP endpoint advertises every tool");
    }

    using (HttpResponseMessage read = await Post(client, token, Call(4, "graft_get_project", """{"project":"admin/shared"}""")))
    {
        using JsonDocument body = Parse(await read.Content.ReadAsStringAsync());
        Check(Text(body).Contains("\"visibility\":\"public\"", StringComparison.Ordinal),
            "a tool call over HTTP reaches the forge");
    }

    // The decisive one for this transport. The server holds no credential, so a caller presenting a token
    // that does not resolve must be refused by Graft rather than quietly served under somebody else's.
    using (HttpResponseMessage impostor = await Post(client, "graft_pat_" + new string('0', 64),
        Call(5, "graft_list_projects", "{}")))
    {
        using JsonDocument body = Parse(await impostor.Content.ReadAsStringAsync());
        Check(Text(body).Contains("token was not accepted", StringComparison.Ordinal),
            "a caller's own token is what is used, so an unusable one is refused");
    }
}

async Task<HttpResponseMessage> Post(HttpClient client, string? token, string payload)
{
    HttpRequestMessage request = new(HttpMethod.Post, "/mcp")
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
    if (token is not null) request.Headers.Add("PRIVATE-TOKEN", token);
    HttpResponseMessage response = await client.SendAsync(request);
    request.Dispose();
    return response;
}

/// <summary>Reads one JSON-RPC reply, from a plain body or from the data line of an event stream.</summary>
JsonDocument Parse(string body)
{
    foreach (string line in body.Split('\n'))
        if (line.StartsWith("data:", StringComparison.Ordinal))
            return JsonDocument.Parse(line[5..].Trim());
    return JsonDocument.Parse(body);
}

async Task<JsonDocument> Exchange(Process client, string payload)
{
    await client.StandardInput.WriteLineAsync(payload);
    await client.StandardInput.FlushAsync();
    Task<string?> line = client.StandardOutput.ReadLineAsync();
    string? reply = await line.WaitAsync(TimeSpan.FromSeconds(30));
    if (reply is null) throw new InvalidOperationException("The MCP server closed its output without replying.");
    return JsonDocument.Parse(reply);
}

static string Request(int id, string method, string parameters) =>
    $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""";

static string Call(int id, string tool, string arguments) =>
    Request(id, "tools/call", $$"""{"name":"{{tool}}","arguments":{{arguments}}}""");

static string Text(JsonDocument document) =>
    document.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() ?? "";

async Task<string> StartServer()
{
    TaskCompletionSource<string> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    server = Process.Start(Launch(serverPath,
        ["--database", databasePath, "--repositories", repositoriesRoot, "--public-browsing", "true"]))!;
    Process active = server;
    _ = Task.Run(async () =>
    {
        while (await active.StandardOutput.ReadLineAsync() is string line)
            if (line.StartsWith("READY: ", StringComparison.Ordinal)) ready.TrySetResult(line[7..]);
    });
    return await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
}

/// <summary>
/// Writes the forge's own certificate out so the MCP server can be pointed at it. The forge generates an
/// ephemeral one on loopback, and the MCP server has no option to skip verification, so pinning it is how
/// this test runs with checking switched on rather than off.
/// </summary>
static async Task ExportCertificateAsync(string address, string path)
{
    Uri uri = new(address);
    using System.Net.Sockets.TcpClient tcp = new();
    await tcp.ConnectAsync(uri.Host, uri.Port);
    await using System.Net.Security.SslStream stream = new(tcp.GetStream(), false, (_, _, _, _) => true);
    await stream.AuthenticateAsClientAsync(uri.Host);
    System.Security.Cryptography.X509Certificates.X509Certificate2 certificate = new(stream.RemoteCertificate!);
    await File.WriteAllTextAsync(path, certificate.ExportCertificatePem());
    certificate.Dispose();
}

static int FreePort()
{
    using System.Net.Sockets.Socket probe = new(System.Net.Sockets.AddressFamily.InterNetwork,
        System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
    probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    return ((IPEndPoint)probe.LocalEndPoint!).Port;
}

static async Task WaitForListener(int port)
{
    for (int attempt = 0; attempt < 120; attempt++)
    {
        try
        {
            using System.Net.Sockets.TcpClient probe = new();
            await probe.ConnectAsync(IPAddress.Loopback, port);
            return;
        }
        catch (System.Net.Sockets.SocketException) { await Task.Delay(250); }
    }
    throw new InvalidOperationException("The MCP server never began listening.");
}

static ProcessStartInfo Launch(string path, string[] arguments)
{
    bool dll = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    ProcessStartInfo start = new(dll ? "dotnet" : path)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    if (dll) start.ArgumentList.Add(path);
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    return start;
}

static void Stop(Process? process)
{
    if (process is null) return;
    try
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        process.WaitForExit(10_000);
    }
    catch (InvalidOperationException) { }
    process.Dispose();
}

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("Assertion failed: " + name);
    assertions++;
}

static T Required<T>(T? value, string name) where T : class =>
    value ?? throw new InvalidOperationException("Required value missing: " + name);
