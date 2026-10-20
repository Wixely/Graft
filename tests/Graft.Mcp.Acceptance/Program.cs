using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Graft.Core;
using Graft.Git;
using Graft.Storage;

// Drives Graft's Model Context Protocol surface as a client would, against a real Graft: the endpoint the
// forge hosts itself, and the separate Graft.Mcp launcher over both of its transports.
//
// Launching the processes rather than calling their types is deliberate. What matters here is the protocol
// on the wire and the credential handling around it, and neither is exercised by an in-process call. The
// stdio half in particular can only be wrong in ways a reference would hide: a stray line on standard
// output corrupts the stream, and nothing but a real client reading it would notice.

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
List<string> serverLines = [];

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
                // Seeded with real content, so the tools that read a tree, a history and a file are
                // answering about something. Against an empty repository they all legitimately return
                // nothing, and an assertion that accepts nothing would not have caught a tool pointed at
                // the wrong route.
                Repository repository = new(new DurableRepositoryStorage(Path.Combine(repositoriesRoot, created.StorageId)));
                repository.CommitFiles("main",
                    [new FileChange("README.md", System.Text.Encoding.UTF8.GetBytes(
                        "# Shared\n\nSeeded for the MCP suite.\n"))],
                    new GitIdentity("Fixture", "fixture@graft.invalid", DateTimeOffset.UtcNow),
                    "Seed the MCP acceptance fixture", cancellation);
                return Task.CompletedTask;
            }, "Visible to anyone.");
        ProjectInfo project = Required(await fixture.FindReadyProjectAsync("admin", "shared"), "project");
        await fixture.SetProjectVisibilityAsync(identity, project.Id, ProjectVisibility.Public);

        // Stays private, so an anonymous caller can be shown to reach the public one and not this.
        await fixture.CreateProjectWithRepositoryAsync(identity, "private", "Private project",
            (created, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.Combine(repositoriesRoot, created.StorageId));
                return Task.CompletedTask;
            }, "Not for strangers.");
        TokenGrant grant = await fixture.CreateTokenAsync(identity, "mcp", TokenScope.Api, DateTimeOffset.UtcNow.AddHours(1));
        token = grant.Secret;
    }
    await File.WriteAllTextAsync(tokenFile, token);

    // The forge with its own endpoint enabled by the operator. This is the deployment shape.
    string address = await StartServer(mcp: true);
    await ExportCertificateAsync(address, caFile);
    await CheckForge(address, token, password);

    // The separate launcher, pointed at the same forge. Stdio is the transport a client launches directly,
    // where the credential belongs to the process.
    await CheckStdio(address, readOnly: true);
    await CheckStdio(address, readOnly: false);

    // The launcher's own Streamable HTTP listener, where the credential belongs to the caller.
    await CheckHttp(address, token);
    await CheckOverrides(address);

    // And the forge started without the endpoint, which is the default. There has to be nothing to find.
    Stop(server);
    server = null;
    string closed = await StartServer(mcp: false);
    await CheckForgeWithoutEndpoint(closed, token);

    Console.WriteLine($"RESULT: PASS; assertions: {assertions}");
    Console.WriteLine("BOUNDARY: loopback only; the forge uses an ephemeral certificate, pinned by file for the launcher and accepted by the harness's own client.");
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

/// <summary>
/// The endpoint the forge hosts itself, governed by the administrator's switches. Driven as a client would
/// drive it and as an administrator would change it: over the real admin route with a browser session,
/// not by writing rows, so the whole path from the page to the next agent request is what is asserted.
/// </summary>
async Task CheckForge(string address, string token, string password)
{
    using HttpClient client = Forge(address);
    string posture = serverLines.FirstOrDefault(line => line.StartsWith("MCP: ", StringComparison.Ordinal)) ?? "";
    Check(posture.Contains("/mcp", StringComparison.Ordinal) && posture.Contains("enabled=true", StringComparison.Ordinal) &&
          posture.Contains("credentials=required", StringComparison.Ordinal) && posture.Contains("writes=refused", StringComparison.Ordinal),
        "the forge prints its agent posture on startup, and the defaults are serving, credentials required, no writes");

    // No credential at all. Refused before the protocol is spoken, so the tool list is not a free
    // description of the forge for somebody who has shown nothing.
    using (HttpResponseMessage anonymous = await Post(client, null, Initialize(1)))
    {
        Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "the forge's endpoint refuses a caller with no token");
        Check(anonymous.Headers.WwwAuthenticate.Count > 0, "the refusal says how to authenticate");
        Check((await anonymous.Content.ReadAsStringAsync()).Contains("Agent access", StringComparison.Ordinal),
            "the refusal names the administrator page that would permit anonymous callers");
    }

    using (HttpResponseMessage handshake = await Post(client, token, Initialize(2)))
    {
        Check(handshake.IsSuccessStatusCode, "the forge's endpoint completes the handshake for a caller with a token");
        Check(handshake.Content.Headers.ContentType?.MediaType == "text/event-stream", "the forge's endpoint streams its reply");
        using JsonDocument body = Parse(await handshake.Content.ReadAsStringAsync());
        Check(body.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString() == "graft",
            "the streamed reply carries the server identity");
    }

    using (HttpResponseMessage listing = await Post(client, token, Request(3, "tools/list", "{}")))
    {
        using JsonDocument body = Parse(await listing.Content.ReadAsStringAsync());
        Check(body.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() == 19,
            "the forge's endpoint advertises every tool");
    }

    foreach ((string tool, string arguments, string expected) in ReadTools())
    {
        using HttpResponseMessage response = await Post(client, token, Call(20, tool, arguments));
        using JsonDocument body = Parse(await response.Content.ReadAsStringAsync());
        string result = Text(body);
        Check(result.Contains(expected, StringComparison.Ordinal), tool + " answers through the forge's own endpoint");
        Check(!result.Contains("token was not accepted", StringComparison.Ordinal) &&
              !result.Contains("could not be reached", StringComparison.Ordinal),
            tool + " reaches the API through the forge's own listener");
    }

    // The URLs in a result are built from the request the API saw. The forge calls itself addressed to its
    // public origin, so an agent is handed links a person could open, not the loopback the socket used.
    using (HttpResponseMessage project = await Post(client, token, Call(4, "graft_get_project", """{"project":"admin/shared"}""")))
    {
        using JsonDocument body = Parse(await project.Content.ReadAsStringAsync());
        Check(Text(body).Contains("\"web_url\":\"" + address.TrimEnd('/') + "/admin/shared\"", StringComparison.Ordinal),
            "a result's links are on the public origin");
    }

    // The forge holds no credential for this, so a caller presenting a token that does not resolve must be
    // refused by Graft rather than quietly served under somebody else's.
    using (HttpResponseMessage impostor = await Post(client, "graft_pat_" + new string('0', 64), Call(5, "graft_list_projects", "{}")))
    {
        using JsonDocument body = Parse(await impostor.Content.ReadAsStringAsync());
        Check(Text(body).Contains("token was not accepted", StringComparison.Ordinal),
            "a caller's own token is what is used, so an unusable one is refused");
    }

    using (HttpResponseMessage write = await Post(client, token, Call(6, "graft_set_page_published", """{"project":"admin/shared","published":true}""")))
    {
        using JsonDocument body = Parse(await write.Content.ReadAsStringAsync());
        string result = Text(body);
        Check(result.Contains("read-only", StringComparison.Ordinal) && result.Contains("Agent access", StringComparison.Ordinal),
            "writes are refused by default, and the refusal names the administrator page rather than a flag");
    }

    // How the forge works, carried in the handshake so a client knows before it calls anything. An agent
    // that has to discover addressing and permissions by failing will fail in ways that look like bugs.
    using (HttpResponseMessage handshake = await Post(client, token, Initialize(30)))
    {
        using JsonDocument body = Parse(await handshake.Content.ReadAsStringAsync());
        string instructions = body.RootElement.GetProperty("result").GetProperty("instructions").GetString() ?? "";
        Check(instructions.Contains("graft_whoami", StringComparison.Ordinal),
            "the handshake tells a client where to start");
        Check(instructions.Contains("instance-wide slug", StringComparison.Ordinal),
            "and how a project is addressed, before a tool call is spent finding out");
    }

    // The same material in depth, on demand, so the handshake can stay short.
    using (HttpResponseMessage topics = await Post(client, token, Call(31, "graft_help", """{"topic":""}""")))
    {
        using JsonDocument body = Parse(await topics.Content.ReadAsStringAsync());
        string result = Text(body);
        Check(result.Contains("\"topics\"", StringComparison.Ordinal) && result.Contains("pages", StringComparison.Ordinal),
            "an agent can list the topics the guide covers");
    }
    using (HttpResponseMessage pagesTopic = await Post(client, token, Call(32, "graft_help", """{"topic":"pages"}""")))
    {
        using JsonDocument body = Parse(await pagesTopic.Content.ReadAsStringAsync());
        Check(Text(body).Contains(".graft/project.json", StringComparison.Ordinal), "and read one in full");
    }
    using (HttpResponseMessage missing = await Post(client, token, Call(33, "graft_help", """{"topic":"nonsense"}""")))
    {
        using JsonDocument body = Parse(await missing.Content.ReadAsStringAsync());
        Check(Text(body).Contains("Try one of", StringComparison.Ordinal),
            "and a topic that does not exist is answered with the ones that do");
    }

    // Who the credential belongs to. An agent that cannot ask this has to infer its own permissions from
    // what fails, which is how an agent ends up retrying something it was never allowed to do.
    using (HttpResponseMessage who = await Post(client, token, Call(13, "graft_whoami", "{}")))
    {
        using JsonDocument body = Parse(await who.Content.ReadAsStringAsync());
        string result = Text(body);
        Check(result.Contains("\"username\":\"admin\"", StringComparison.Ordinal), "an agent can ask which account it is acting as");
        Check(result.Contains("\"writes_allowed\":false", StringComparison.Ordinal),
            "and is told whether writes are permitted before it tries one");
    }

    // Project text, through the tool. Refused while the administrator has writes off, like every other
    // mutating tool, and the refusal names the page rather than a flag.
    using (HttpResponseMessage refused = await Post(client, token, Call(14, "graft_update_project",
        """{"project":"admin/shared","name":"","description":"Edited by an agent","defaultBranch":""}""")))
    {
        using JsonDocument body = Parse(await refused.Content.ReadAsStringAsync());
        Check(Text(body).Contains("read-only", StringComparison.Ordinal), "editing project text is refused while writes are off");
    }

    // The administrator's switches, through the browser route. A token is the wrong credential for it.
    using (HttpRequestMessage byToken = new(HttpMethod.Get, "/api/graft/admin/mcp"))
    {
        byToken.Headers.Add("PRIVATE-TOKEN", token);
        using HttpResponseMessage refused = await client.SendAsync(byToken);
        Check(refused.StatusCode == HttpStatusCode.Unauthorized, "the settings route wants a browser session, not a token");
    }

    using HttpClient browser = await SignIn(address, "admin", password);
    using (HttpResponseMessage session = await browser.GetAsync("/api/session"))
    {
        using JsonDocument body = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        Check(body.RootElement.GetProperty("mcp_available").GetBoolean(), "the session tells the client the endpoint is available");
    }

    using (HttpResponseMessage current = await browser.GetAsync("/api/graft/admin/mcp"))
    {
        Check(current.IsSuccessStatusCode, "an administrator reads the agent settings");
        using JsonDocument body = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
        Check(body.RootElement.GetProperty("enabled").GetBoolean() &&
              !body.RootElement.GetProperty("allow_writes").GetBoolean() &&
              !body.RootElement.GetProperty("allow_anonymous").GetBoolean(),
            "the stored defaults are serving, no writes, no anonymous callers");
        Check(body.RootElement.GetProperty("endpoint").GetString() == address.TrimEnd('/') + "/mcp",
            "the settings name the endpoint a client should connect to");
    }

    // Allow writes, then the same write succeeds on the next request with nothing restarted.
    await SetSettings(browser, address, enabled: true, allowWrites: true, allowAnonymous: false);
    using (HttpResponseMessage write = await Post(client, token, Call(7, "graft_set_page_published", """{"project":"admin/shared","published":true}""")))
    {
        using JsonDocument body = Parse(await write.Content.ReadAsStringAsync());
        Check(Text(body).Contains("\"page_published\":true", StringComparison.Ordinal),
            "once an administrator allows writes, a write performs without a restart");
    }

    // And the project text now that writes are permitted. Addressed by the instance-wide slug, which the
    // REST surface accepts wherever it accepts a path, so an agent can use the name it was given.
    using (HttpResponseMessage edited = await Post(client, token, Call(15, "graft_update_project",
        """{"project":"shared","name":"","description":"Edited by an agent","defaultBranch":""}""")))
    {
        using JsonDocument body = Parse(await edited.Content.ReadAsStringAsync());
        Check(Text(body).Contains("Edited by an agent", StringComparison.Ordinal),
            "an agent edits a project's description once writes are allowed");
    }
    using (HttpResponseMessage reread = await Post(client, token, Call(16, "graft_get_project", """{"project":"admin/shared"}""")))
    {
        using JsonDocument body = Parse(await reread.Content.ReadAsStringAsync());
        Check(Text(body).Contains("Edited by an agent", StringComparison.Ordinal), "and the change is what the next read returns");
    }

    // Writing repository content, which is the thing that turns an agent from a reader into something that
    // can propose a change. On a branch of its own so the seeded history stays what the read tools assert.
    using (HttpResponseMessage wrote = await Post(client, token, Call(17, "graft_write_file",
        """{"project":"admin/shared","file":"docs/agent.md","content":"# Written by an agent\n","branch":"agent-work","message":"Add a note"}""")))
    {
        using JsonDocument body = Parse(await wrote.Content.ReadAsStringAsync());
        string result = Text(body);
        Check(result.Contains("\"commit_id\"", StringComparison.Ordinal), "an agent writes a file and is told the commit it made");
        Check(result.Contains("\"created_branch\":true", StringComparison.Ordinal), "and that the branch was created for it");
    }
    using (HttpResponseMessage readBack = await Post(client, token, Call(18, "graft_read_file",
        """{"project":"admin/shared","file":"docs/agent.md","reference":"agent-work"}""")))
    {
        using JsonDocument body = Parse(await readBack.Content.ReadAsStringAsync());
        Check(Text(body).Contains("Written by an agent", StringComparison.Ordinal),
            "and reads back exactly what it wrote, through the tool that was already there");
    }

    // Membership, which an agent could previously neither read nor change.
    using (HttpResponseMessage listed = await Post(client, token, Call(22, "graft_list_members", """{"project":"admin/shared"}""")))
    {
        using JsonDocument body = Parse(await listed.Content.ReadAsStringAsync());
        Check(Text(body).Contains("\"count\"", StringComparison.Ordinal), "an agent reads who may work on a project");
    }
    using (HttpResponseMessage granted = await Post(client, token, Call(23, "graft_set_member",
        """{"project":"admin/shared","username":"admin","role":"maintainer"}""")))
    {
        using JsonDocument body = Parse(await granted.Content.ReadAsStringAsync());
        // The owner already has everything, so Graft refuses this rather than storing a redundant row. What
        // is asserted is that the tool reports the server's reason instead of claiming success.
        string result = Text(body);
        Check(result.Contains("error", StringComparison.Ordinal) || result.Contains("\"role\":\"maintainer\"", StringComparison.Ordinal),
            "granting a role either works or says why, and never silently does nothing");
    }

    // What a commit changed. An agent could already see that a commit exists; this is the first time it can
    // see what it did, which is the only question worth asking about one.
    string seeded;
    using (HttpResponseMessage commits = await Post(client, token, Call(19, "graft_list_commits",
        """{"project":"admin/shared","reference":"main"}""")))
    {
        using JsonDocument body = Parse(await commits.Content.ReadAsStringAsync());
        using JsonDocument listed = JsonDocument.Parse(Text(body));
        seeded = listed.RootElement.GetProperty("commits")[0].GetProperty("id").GetString()!;
    }
    using (HttpResponseMessage diff = await Post(client, token, Call(20, "graft_read_commit",
        $$"""{"project":"admin/shared","commit":"{{seeded}}","parent":0}""")))
    {
        using JsonDocument body = Parse(await diff.Content.ReadAsStringAsync());
        // Parsed rather than string-matched: the tool's payload is JSON, and a serializer escapes '+' as
        // +, so searching the raw text for a diff marker finds nothing however correct the diff is.
        using JsonDocument reported = JsonDocument.Parse(Text(body));
        JsonElement file = reported.RootElement.GetProperty("files")[0];
        Check(file.GetProperty("path").GetString() == "README.md", "an agent reads which files a commit touched");
        Check(file.GetProperty("status").GetString() == "added" && reported.RootElement.GetProperty("added").GetInt32() == 3,
            "with what happened to each and how many lines moved");
        // Unified diff text, because that is the form every tool prints and an agent reads best.
        Check(file.GetProperty("diff").GetString()!.Contains("+# Shared", StringComparison.Ordinal),
            "and the added lines as ordinary unified diff text");
    }
    using (HttpResponseMessage missing = await Post(client, token, Call(21, "graft_read_commit",
        """{"project":"admin/shared","commit":"0000000000000000000000000000000000000000","parent":0}""")))
    {
        using JsonDocument body = Parse(await missing.Content.ReadAsStringAsync());
        Check(Text(body).Contains("error", StringComparison.Ordinal), "a commit that is not there is reported rather than invented");
    }

    // Allow anonymous callers. A stranger reads what is public and nothing else; the forge decides that,
    // not the endpoint, because an anonymous caller is simply a stranger to Graft.
    await SetSettings(browser, address, enabled: true, allowWrites: true, allowAnonymous: true);
    using (HttpResponseMessage handshake = await Post(client, null, Initialize(8)))
        Check(handshake.IsSuccessStatusCode, "an administrator can allow callers with no credential");
    using (HttpResponseMessage read = await Post(client, null, Call(9, "graft_browse", """{"search":""}""")))
    {
        using JsonDocument body = Parse(await read.Content.ReadAsStringAsync());
        Check(Text(body).Contains("admin/shared", StringComparison.Ordinal), "an anonymous caller reads what is public");
    }
    using (HttpResponseMessage denied = await Post(client, null, Call(10, "graft_get_project", """{"project":"admin/private"}""")))
    {
        using JsonDocument body = Parse(await denied.Content.ReadAsStringAsync());
        Check(!Text(body).Contains("admin/private", StringComparison.Ordinal), "an anonymous caller sees nothing that is not public");
    }

    // Turned off by an administrator: the endpoint answers nothing, while the settings route stays
    // reachable so it can be turned on again.
    await SetSettings(browser, address, enabled: false, allowWrites: false, allowAnonymous: false);
    using (HttpResponseMessage off = await Post(client, token, Initialize(11)))
        Check(off.StatusCode == HttpStatusCode.NotFound, "an administrator can pause the endpoint without a restart");
    using (HttpResponseMessage still = await browser.GetAsync("/api/graft/admin/mcp"))
        Check(still.IsSuccessStatusCode, "the settings stay reachable while the endpoint is paused");
    await SetSettings(browser, address, enabled: true, allowWrites: false, allowAnonymous: false);
    using (HttpResponseMessage back = await Post(client, token, Initialize(12)))
        Check(back.IsSuccessStatusCode, "and turn it back on");

    // The settings are audited with the resulting posture, not as a bare "changed".
    await using (Microsoft.Data.Sqlite.SqliteConnection db = new($"Data Source={databasePath};Pooling=False"))
    {
        await db.OpenAsync();
        await using Microsoft.Data.Sqlite.SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM AuditEvents WHERE Action = 'mcp-settings' AND Detail LIKE 'enabled=%allow-writes=%allow-anonymous=%';";
        Check(Convert.ToInt64(await command.ExecuteScalarAsync()) == 4, "every change to the switches is audited with the posture it produced");
    }
}

/// <summary>The default: the operator did not enable the endpoint, and nothing about it is reachable.</summary>
async Task CheckForgeWithoutEndpoint(string address, string token)
{
    using HttpClient client = Forge(address);
    Check(serverLines.Any(line => line == "MCP: off"), "a forge started without the endpoint says so");
    using (HttpResponseMessage absent = await Post(client, token, Initialize(1)))
    {
        Check(absent.StatusCode == HttpStatusCode.NotFound, "without --mcp true the endpoint is a 404, not the browser shell");
        Check((await absent.Content.ReadAsStringAsync()).Contains("--mcp true", StringComparison.Ordinal),
            "and the refusal names the flag that would enable it");
    }
    using (HttpResponseMessage session = await client.GetAsync("/api/session"))
    {
        using JsonDocument body = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        Check(!body.RootElement.GetProperty("mcp_available").GetBoolean(), "the session tells the client there is nothing to configure");
    }
}

async Task SetSettings(HttpClient browser, string address, bool enabled, bool allowWrites, bool allowAnonymous)
{
    using HttpRequestMessage request = new(HttpMethod.Post, "/api/graft/admin/mcp");
    request.Headers.Add("Origin", address.TrimEnd('/'));
    request.Headers.Add("X-Graft-CSRF", browser.DefaultRequestHeaders.GetValues("X-Graft-CSRF").Single());
    request.Content = JsonContent.Create(new { enabled, allow_writes = allowWrites, allow_anonymous = allowAnonymous });
    using HttpResponseMessage response = await browser.SendAsync(request);
    Check(response.IsSuccessStatusCode, "an administrator saves the agent settings");
}

/// <summary>A signed-in browser, as the client would be: the session cookie and the CSRF secret the session
/// route hands back, which every mutation has to echo.</summary>
async Task<HttpClient> SignIn(string address, string username, string password)
{
    HttpClientHandler handler = new()
    {
        CookieContainer = new CookieContainer(),
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        UseProxy = false
    };
    HttpClient browser = new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(30) };
    using (HttpRequestMessage login = new(HttpMethod.Post, "/api/login"))
    {
        login.Headers.Add("Origin", address.TrimEnd('/'));
        login.Content = JsonContent.Create(new { username, password });
        using HttpResponseMessage response = await browser.SendAsync(login);
        Check(response.IsSuccessStatusCode, "the administrator signs in");
    }
    using (HttpResponseMessage session = await browser.GetAsync("/api/session"))
    {
        using JsonDocument body = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        browser.DefaultRequestHeaders.Add("X-Graft-CSRF", body.RootElement.GetProperty("csrf").GetString());
    }
    return browser;
}

static HttpClient Forge(string address)
{
    HttpClientHandler handler = new()
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        UseProxy = false
    };
    return new(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(30) };
}

static string Initialize(int id) => Request(id, "initialize",
    """{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"acceptance","version":"1"}}""");

/// <summary>Every read tool, not a representative one. The listing tool was once pointed at a browser-session
/// route that refuses a token outright, and no assertion noticed because nothing called it.</summary>
static (string Tool, string Arguments, string Expected)[] ReadTools() =>
[
    ("graft_list_projects", "{}", "admin/shared"),
    ("graft_browse", """{"search":""}""", "admin/shared"),
    ("graft_get_project", """{"project":"admin/shared"}""", "admin/shared"),
    ("graft_list_branches", """{"project":"admin/shared"}""", "main"),
    ("graft_list_commits", """{"project":"admin/shared","reference":""}""", "Seed the MCP acceptance fixture"),
    ("graft_list_tree", """{"project":"admin/shared","path":"","reference":""}""", "README.md"),
    ("graft_read_file", """{"project":"admin/shared","file":"README.md","reference":""}""", "Seeded for the MCP suite")
];

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
        JsonDocument initialize = await Exchange(client, Initialize(1));
        Check(initialize.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString() == "graft",
            label + " completes the handshake");
        initialize.Dispose();

        await client.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await client.StandardInput.FlushAsync();

        using JsonDocument tools = await Exchange(client, Request(2, "tools/list", "{}"));
        int count = tools.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength();
        Check(count == 19, label + " advertises every tool");

        using JsonDocument read = await Exchange(client, Call(3, "graft_browse", """{"search":""}"""));
        Check(Text(read).Contains("admin/shared", StringComparison.Ordinal), label + " reads the public listing");

        using JsonDocument write = await Exchange(client, Call(4, "graft_set_page_published",
            """{"project":"admin/shared","published":true}"""));
        string result = Text(write);
        if (readOnly)
            Check(result.Contains("read-only", StringComparison.Ordinal) &&
                result.Contains("--allow-writes", StringComparison.Ordinal),
                "a read-only launcher refuses a write and names the switch that would permit it");
        else
            Check(result.Contains("\"page_published\":true", StringComparison.Ordinal),
                "a launcher started with writes performs one");
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

    using (HttpResponseMessage anonymous = await Post(client, null, Initialize(1)))
    {
        Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "the launcher's HTTP endpoint refuses a caller with no token");
        Check(anonymous.Headers.WwwAuthenticate.Count > 0, "the refusal says how to authenticate");
        Check((await anonymous.Content.ReadAsStringAsync()).Contains("--allow-anonymous", StringComparison.Ordinal),
            "the refusal names the switch that would permit it rather than leaving it to be guessed");
    }

    using (HttpResponseMessage handshake = await Post(client, token, Initialize(2)))
    {
        Check(handshake.IsSuccessStatusCode, "the launcher's HTTP endpoint completes the handshake for a caller with a token");
        // Streamable HTTP answers as an event stream rather than a plain body, which is the whole point of
        // this transport and the thing a plain JSON response would quietly fail to be.
        Check(handshake.Content.Headers.ContentType?.MediaType == "text/event-stream",
            "the launcher's HTTP endpoint streams its reply");
        using JsonDocument body = Parse(await handshake.Content.ReadAsStringAsync());
        Check(body.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString() == "graft",
            "the streamed reply carries the server identity");
    }

    using (HttpResponseMessage listing = await Post(client, token, Request(3, "tools/list", "{}")))
    {
        using JsonDocument body = Parse(await listing.Content.ReadAsStringAsync());
        Check(body.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() == 19,
            "the launcher's HTTP endpoint advertises every tool");
    }

    foreach ((string tool, string arguments, string expected) in ReadTools())
    {
        using HttpResponseMessage response = await Post(client, token, Call(20, tool, arguments));
        using JsonDocument body = Parse(await response.Content.ReadAsStringAsync());
        string result = Text(body);
        Check(result.Contains(expected, StringComparison.Ordinal), tool + " answers through the launcher's endpoint");
        Check(!result.Contains("token was not accepted", StringComparison.Ordinal) &&
              !result.Contains("could not be reached", StringComparison.Ordinal),
            tool + " reaches a route a token can actually use");
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

/// <summary>
/// The launcher's two overrides that loosen the default posture. Both are off unless asked for, and the
/// point of asserting them is that the closed answer is the default rather than something an operator has
/// to remember to choose.
/// </summary>
async Task CheckOverrides(string address)
{
    int anonymousPort = FreePort();
    string anonymousEndpoint = "http://127.0.0.1:" + anonymousPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
    Process open = Process.Start(Launch(mcpPath,
        ["--server", address, "--transport", "http", "--listen", anonymousEndpoint,
         "--ca-certificate", caFile, "--allow-anonymous", "true"]))!;
    try
    {
        await WaitForListener(anonymousPort);
        using HttpClient client = new() { BaseAddress = new(anonymousEndpoint), Timeout = TimeSpan.FromSeconds(30) };

        using (HttpResponseMessage handshake = await Post(client, null, Initialize(1)))
            Check(handshake.IsSuccessStatusCode, "an operator can allow callers with no credential");

        using (HttpResponseMessage read = await Post(client, null, Call(2, "graft_browse", """{"search":""}""")))
        {
            using JsonDocument body = Parse(await read.Content.ReadAsStringAsync());
            Check(Text(body).Contains("admin/shared", StringComparison.Ordinal),
                "an anonymous caller reads what is public");
        }

        using (HttpResponseMessage denied = await Post(client, null, Call(3, "graft_get_project", """{"project":"admin/private"}""")))
        {
            using JsonDocument body = Parse(await denied.Content.ReadAsStringAsync());
            Check(!Text(body).Contains("admin/private", StringComparison.Ordinal),
                "an anonymous caller sees nothing that is not public");
        }
    }
    finally { Stop(open); }

    // Plaintext off loopback. Refused unless asked for, which is the assertion that matters: a deployment
    // that forgets the override fails to start rather than quietly serving tokens in the clear.
    int plaintextPort = FreePort();
    string offLoopback = "http://0.0.0.0:" + plaintextPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
    Process refused = Process.Start(Launch(mcpPath,
        ["--server", address, "--transport", "http", "--listen", offLoopback, "--ca-certificate", caFile]))!;
    await refused.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
    Check(refused.ExitCode == 2, "a plaintext listener off loopback is refused without the override");
    refused.Dispose();

    Process permitted = Process.Start(Launch(mcpPath,
        ["--server", address, "--transport", "http", "--listen", offLoopback,
         "--ca-certificate", caFile, "--allow-plaintext", "true"]))!;
    try
    {
        await WaitForListener(plaintextPort);
        Check(true, "the same listener starts once an operator allows plaintext");
    }
    finally { Stop(permitted); }
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

async Task<string> StartServer(bool mcp)
{
    TaskCompletionSource<string> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    serverLines = [];
    List<string> lines = serverLines;
    server = Process.Start(Launch(serverPath,
        ["--database", databasePath, "--repositories", repositoriesRoot, "--public-browsing", "true", "--mcp", mcp ? "true" : "false"]))!;
    Process active = server;
    _ = Task.Run(async () =>
    {
        while (await active.StandardOutput.ReadLineAsync() is string line)
        {
            lock (lines) lines.Add(line);
            if (line.StartsWith("READY: ", StringComparison.Ordinal)) ready.TrySetResult(line[7..]);
        }
    });
    return await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
}

/// <summary>
/// Writes the forge's own certificate out so the launcher can be pointed at it. The forge generates an
/// ephemeral one on loopback, and the launcher has no option to skip verification, so pinning it is how
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
