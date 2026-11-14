using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Graft.TestSupport;

/// <summary>
/// A stand-in for an application store that speaks the upload-session contract of ADR 0052, on loopback. It
/// keeps sessions, chunks, builds, a revision and channels the way the contract describes, refuses what the
/// contract says is refused, and can be told to misbehave: drop a connection, redirect, issue an address on
/// another origin, reject a file, expire a session, or change the application under a publisher's feet.
///
/// <para>
/// Compiled into each suite that needs it. Everything it was sent is recorded, so a test can assert on what
/// did not happen as well as what did.
/// </para>
/// </summary>
public sealed class FakeAppStore : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly object gate = new();
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Build> builds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string BuildId, string Version)> channels = new(StringComparer.Ordinal);
    private int revision = 1;

    public FakeAppStore()
    {
        using (TcpListener probe = new(IPAddress.Loopback, 0))
        {
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        Origin = "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture);
        listener.Prefixes.Add(Origin + "/");
        listener.Start();
        _ = Task.Run(RunAsync);
    }

    public int Port { get; }
    public string Origin { get; }
    /// <summary>The part of the address a real store keeps private.</summary>
    public string Opaque { get; } = "opaque-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    public string Endpoint => Origin + "/" + Opaque + "/api/v1";
    public string ApiKey { get; } = "key-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
    public string ApplicationId { get; } = Guid.NewGuid().ToString("D");

    public long MaximumArtifactBytes { get; set; } = 8 * 1024 * 1024;
    public int ChunkSize { get; set; } = 1024;
    public string[] Formats { get; set; } = ["apk", "file"];
    public string[] Platforms { get; set; } = ["Android", "Windows", "Linux", "MacOS", "iOS", "Unknown"];
    public bool ProductionPromotionAvailable { get; set; } = true;
    public bool KeyMayPromote { get; set; } = true;
    /// <summary>How many times a session answers Validating before it settles.</summary>
    public int ValidationPolls { get; set; } = 1;
    /// <summary>File names validation rejects.</summary>
    public HashSet<string> Reject { get; } = new(StringComparer.Ordinal);
    /// <summary>A signing store changes an Android package's bytes, and so its hash.</summary>
    public bool SignAndroidPackages { get; set; } = true;
    /// <summary>Chunk index to how many times the connection is dropped before that chunk is taken.</summary>
    public ConcurrentDictionary<int, int> DropChunk { get; } = new();
    /// <summary>Chunk index to how many times the chunk is taken and the answer then lost, so the sender cannot know.</summary>
    public ConcurrentDictionary<int, int> LoseAnswer { get; } = new();
    /// <summary>Answers that many chunk requests with 429 before taking one.</summary>
    public int BusyChunks { get; set; }
    /// <summary>When set, issued instead of this store's own chunk path.</summary>
    public string? ChunkPathTemplate { get; set; }
    /// <summary>When set, every chunk request is redirected here.</summary>
    public string? RedirectChunksTo { get; set; }
    /// <summary>When set, every API request is redirected here.</summary>
    public string? RedirectApiTo { get; set; }
    /// <summary>Expires every session that is opened again, as a store does to one left too long.</summary>
    public bool ExpireOnResume { get; set; }
    /// <summary>Changes the application once, between a publisher reading its revision and using it.</summary>
    public bool ChangeApplicationAfterRead { get; set; }

    /// <summary>Every request, as "METHOD path". Never the bodies, which may be large.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();
    /// <summary>How many times each chunk of each file arrived whole and was taken or matched.</summary>
    public ConcurrentDictionary<string, int> ChunkArrivals { get; } = new(StringComparer.Ordinal);
    /// <summary>Anything the contract forbids that a client did anyway.</summary>
    public ConcurrentQueue<string> Violations { get; } = new();
    public int UploadSessionsOpened;
    public int ReleaseRequests;

    public IReadOnlyList<(string Id, string FileName, string Platform, string Architecture, string Format, string Version, string? Channel, bool Published, string Sha256, string? PackageName)> Builds
    {
        get { lock (gate) return [.. builds.Values.Select(build => (build.Id, build.FileName, build.Platform, build.Architecture, build.Format, build.Version, build.Channel, build.Published, build.Sha256, build.PackageName))]; }
    }

    public (string BuildId, string Version)? Channel(string name)
    {
        lock (gate) return channels.TryGetValue(name, out var current) ? current : null;
    }

    public int Arrivals(string fileName, int index) => ChunkArrivals.TryGetValue(fileName + "#" + index.ToString(CultureInfo.InvariantCulture), out int count) ? count : 0;

    private async Task RunAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            try { await HandleAsync(context); }
            catch (Exception error) when (error is HttpListenerException or IOException or ObjectDisposedException) { }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        string path = request.Url!.AbsolutePath;
        Requests.Enqueue(request.HttpMethod + " " + path);
        string api = "/" + Opaque + "/api/v1/";
        string chunks = "/" + Opaque + "/upload/v1/upload-sessions/";
        string bearer = request.Headers["Authorization"] is { } header && header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";

        if (path.StartsWith(chunks, StringComparison.Ordinal)) { await HandleChunkAsync(context, path[chunks.Length..], bearer); return; }
        if (!path.StartsWith(api, StringComparison.Ordinal)) { await Error(context, 404, "not_found", "no such route"); return; }
        if (RedirectApiTo is not null) { context.Response.Redirect(RedirectApiTo); context.Response.StatusCode = 307; context.Response.Close(); return; }
        if (bearer != ApiKey) { await Error(context, 401, "unauthorized", "the key is not valid"); return; }
        if (request.Headers["Accept"]?.Contains("application/json", StringComparison.Ordinal) != true) Violations.Enqueue("no Accept: application/json on " + path);
        string route = path[api.Length..];
        byte[] body = await ReadAsync(request);
        if (body.Length > 0 && request.ContentType?.StartsWith("application/json", StringComparison.Ordinal) != true) Violations.Enqueue("a JSON body without its content type on " + path);

        if (request.HttpMethod == "GET" && route == "capabilities")
        {
            await Json(context, 200, json =>
            {
                json.WriteStartArray("formats"); foreach (string format in Formats) json.WriteStringValue(format); json.WriteEndArray();
                json.WriteNumber("maxArtifactBytes", MaximumArtifactBytes);
                json.WriteNumber("chunkSizeBytes", ChunkSize);
                json.WriteStartArray("grantableScopes"); json.WriteStringValue("read"); json.WriteStringValue("upload:test"); json.WriteStringValue("promote:production"); json.WriteEndArray();
                json.WriteBoolean("productionPromotionAvailable", ProductionPromotionAvailable);
                json.WriteStartArray("platforms"); foreach (string platform in Platforms) json.WriteStringValue(platform); json.WriteEndArray();
                json.WriteStartArray("architectures"); foreach (string architecture in new[] { "any", "x64", "x86", "arm64", "arm" }) json.WriteStringValue(architecture); json.WriteEndArray();
            });
            return;
        }
        if (request.HttpMethod == "POST" && route == "upload-sessions") { await OpenSessionAsync(context, body); return; }
        if (route.StartsWith("upload-sessions/", StringComparison.Ordinal))
        {
            string[] parts = route["upload-sessions/".Length..].Split('/');
            Session? session;
            lock (gate) session = sessions.Values.FirstOrDefault(candidate => candidate.Id == parts[0]);
            if (session is null) { await Error(context, 404, "session_not_found", "no such upload session"); return; }
            if (request.HttpMethod == "POST" && parts is [_, "finalize"])
            {
                bool incomplete;
                lock (gate)
                {
                    incomplete = session.State == "Uploading" && session.Chunks.Count != session.TotalChunks;
                    if (!incomplete && session.State == "Uploading") { session.State = "Validating"; session.PollsLeft = ValidationPolls; }
                }
                if (incomplete) { await Error(context, 409, "upload_incomplete", "not every chunk has arrived"); return; }
                await WriteSession(context, session, withTransfer: false);
                return;
            }
            if (request.HttpMethod == "GET" && parts.Length == 1)
            {
                lock (gate) Settle(session);
                await WriteSession(context, session, withTransfer: false);
                return;
            }
            if (request.HttpMethod == "DELETE" && parts.Length == 1)
            {
                lock (gate) if (session.State is "Uploading" or "Validating") session.State = "Aborted";
                await WriteSession(context, session, withTransfer: false);
                return;
            }
        }
        if (request.HttpMethod == "GET" && route == "applications/" + ApplicationId)
        {
            int read;
            lock (gate)
            {
                read = revision;
                if (ChangeApplicationAfterRead) { ChangeApplicationAfterRead = false; revision++; }
            }
            await Json(context, 200, json => { json.WriteString("id", ApplicationId); json.WriteString("revision", "rev-" + read.ToString(CultureInfo.InvariantCulture)); });
            return;
        }
        if (request.HttpMethod == "GET" && route.StartsWith("applications/", StringComparison.Ordinal)) { await Error(context, 404, "application_not_found", "no such application"); return; }
        if (request.HttpMethod == "PUT" && route.StartsWith("builds/", StringComparison.Ordinal) && route.EndsWith("/release", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref ReleaseRequests);
            string buildId = route["builds/".Length..^"/release".Length];
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement asked = document.RootElement;
            if (!KeyMayPromote) { await Error(context, 403, "missing_scope", "promote:production is required"); return; }
            int status = 200;
            string code = "", detail = "";
            lock (gate)
            {
                if (!builds.TryGetValue(buildId, out Build? build)) { status = 404; code = "build_not_found"; detail = "no such build"; }
                else if (asked.GetProperty("confirmation").GetString() != "SET BUILD RELEASE " + buildId) { status = 400; code = "confirmation_mismatch"; detail = "the confirmation does not name the build"; }
                else if (asked.GetProperty("expectedRevision").GetString() != "rev-" + revision.ToString(CultureInfo.InvariantCulture)) { status = 409; code = "revision_conflict"; detail = "the application has changed"; }
                else
                {
                    build.Published = asked.GetProperty("published").GetBoolean();
                    build.Channel = asked.GetProperty("beta").GetBoolean() ? "beta" : "stable";
                    build.Notes = asked.GetProperty("releaseNotes").GetString() ?? "";
                    channels[build.Channel] = (build.Id, build.Version);
                    revision++;
                }
            }
            if (status != 200) { await Error(context, status, code, detail); return; }
            await Json(context, 200, json => json.WriteString("id", buildId));
            return;
        }
        if (request.HttpMethod == "GET" && route == "builds")
        {
            if (request.QueryString["applicationId"] != ApplicationId) { await Error(context, 404, "application_not_found", "no such application"); return; }
            Build[] listed;
            lock (gate) listed = [.. builds.Values];
            await JsonArray(context, json =>
            {
                foreach (Build build in listed)
                {
                    json.WriteStartObject();
                    json.WriteString("id", build.Id);
                    json.WriteString("applicationId", ApplicationId);
                    json.WriteString("applicationSlug", "example");
                    json.WriteString("platform", build.Platform);
                    json.WriteString("version", build.Version);
                    if (build.Channel is null) json.WriteNull("channel"); else json.WriteString("channel", build.Channel);
                    json.WriteBoolean("published", build.Published);
                    json.WriteNumber("sizeBytes", build.Size);
                    json.WriteString("releaseNotes", build.Notes);
                    json.WriteString("artifactType", build.Format);
                    json.WriteString("architecture", build.Architecture);
                    json.WriteString("originalFileName", build.FileName);
                    json.WriteString("sha256", build.Sha256);
                    json.WriteEndObject();
                }
            });
            return;
        }
        if (request.HttpMethod == "GET" && route == "channels")
        {
            KeyValuePair<string, (string BuildId, string Version)>[] listed;
            lock (gate) listed = [.. channels];
            await JsonArray(context, json =>
            {
                foreach (var entry in listed)
                {
                    json.WriteStartObject();
                    json.WriteString("applicationId", ApplicationId);
                    json.WriteString("applicationSlug", "example");
                    json.WriteString("name", entry.Key);
                    json.WriteString("currentBuildId", entry.Value.BuildId);
                    json.WriteString("currentVersion", entry.Value.Version);
                    json.WriteEndObject();
                }
            });
            return;
        }
        await Error(context, 404, "not_found", "no such route");
    }

    private async Task OpenSessionAsync(HttpListenerContext context, byte[] body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement asked = document.RootElement;
        string Text(string name) => asked.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        string key = Text("idempotencyKey");
        string declaration = string.Join('|', Text("applicationId"), Text("originalFileName"), asked.GetProperty("expectedSize").GetInt64().ToString(CultureInfo.InvariantCulture),
            Text("expectedSha256"), Text("packageName"), Text("version"), Text("releaseNotes"), Text("format"), Text("platform"), Text("architecture"));
        if (Text("applicationId") != ApplicationId) { await Error(context, 404, "application_not_found", "no such application"); return; }
        long declared = asked.GetProperty("expectedSize").GetInt64();
        string? refusal =
            key.Length is 0 or > 200 ? "idempotencyKey" :
            Text("originalFileName").Length is 0 or > 200 || Text("originalFileName").Contains('/') || Text("originalFileName").Contains('\\') ? "originalFileName" :
            declared <= 0 || declared > MaximumArtifactBytes ? "expectedSize" :
            Text("expectedSha256").Length != 64 || !Text("expectedSha256").All(char.IsAsciiHexDigit) ? "expectedSha256" :
            Text("version").Length is 0 or > 100 ? "version" :
            Text("releaseNotes").Length > 8000 ? "releaseNotes" :
            !Formats.Contains(Text("format")) ? "format" :
            !Platforms.Contains(Text("platform")) ? "platform" :
            Text("architecture") is not { Length: > 0 and <= 32 } ? "architecture" :
            Text("format") == "apk" && (!Text("originalFileName").EndsWith(".apk", StringComparison.Ordinal) || Text("platform") != "Android" || Text("packageName").Length == 0) ? "apk" : null;
        if (refusal is not null) { await Error(context, 400, "invalid_declaration", "the " + refusal + " is not acceptable"); return; }

        Session session;
        lock (gate)
        {
            if (sessions.TryGetValue(key, out Session? existing))
            {
                if (existing.Declaration != declaration) session = null!;
                else
                {
                    session = existing;
                    if (ExpireOnResume && session.State == "Uploading") session.State = "Expired";
                    session.TransferToken = "transfer-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
                }
            }
            else
            {
                long expected = asked.GetProperty("expectedSize").GetInt64();
                session = new(Guid.NewGuid().ToString("D"), declaration, Text("originalFileName"), expected, Text("expectedSha256").ToLowerInvariant(), ChunkSize,
                    (int)((expected + ChunkSize - 1) / ChunkSize), Text("format"), Text("platform"), Text("architecture"), Text("version"), Text("releaseNotes"),
                    Text("packageName") is { Length: > 0 } package ? package : null)
                { TransferToken = "transfer-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)) };
                sessions[key] = session;
                Interlocked.Increment(ref UploadSessionsOpened);
            }
        }
        if (session is null) { await Error(context, 409, "idempotency_conflict", "the key was used with a different declaration"); return; }
        await WriteSession(context, session, withTransfer: true);
    }

    private async Task HandleChunkAsync(HttpListenerContext context, string rest, string bearer)
    {
        HttpListenerRequest request = context.Request;
        string[] parts = rest.Split('/');
        if (RedirectChunksTo is not null) { await ReadAsync(request); context.Response.Redirect(RedirectChunksTo); context.Response.StatusCode = 307; context.Response.Close(); return; }
        Session? session;
        lock (gate) session = sessions.Values.FirstOrDefault(candidate => candidate.Id == parts[0]);
        if (request.HttpMethod != "PUT" || parts is not [_, "chunks", _] || session is null || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        { await Error(context, 404, "session_not_found", "no such upload session"); return; }
        if (bearer == ApiKey) Violations.Enqueue("the API key was sent to a chunk address");
        if (bearer != session.TransferToken) { await Error(context, 401, "transfer_token", "the transfer token is not valid"); return; }
        if (request.ContentType != "application/octet-stream") Violations.Enqueue("a chunk was not sent as application/octet-stream");
        if (DropChunk.TryGetValue(index, out int drops) && drops > 0)
        {
            DropChunk[index] = drops - 1;
            context.Response.Abort();
            return;
        }
        if (BusyChunks > 0)
        {
            BusyChunks--;
            context.Response.Headers["Retry-After"] = "0";
            await Error(context, 429, "capacity", "upload capacity is exhausted");
            return;
        }
        byte[] bytes = await ReadAsync(request);
        string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(request.Headers["X-Chunk-SHA256"], digest, StringComparison.OrdinalIgnoreCase)) { await Error(context, 400, "chunk_digest", "the chunk does not match its digest"); return; }
        int status = 200;
        lock (gate)
        {
            long length = Math.Min(session.ChunkSize, session.ExpectedSize - (long)index * session.ChunkSize);
            if (session.State != "Uploading") status = 409;
            else if (index < 0 || index >= session.TotalChunks || bytes.Length != length) status = 400;
            else if (session.Chunks.TryGetValue(index, out byte[]? held) && !held.AsSpan().SequenceEqual(bytes)) status = 409;
            else session.Chunks[index] = bytes;
        }
        if (status == 200) ChunkArrivals.AddOrUpdate(session.FileName + "#" + index.ToString(CultureInfo.InvariantCulture), 1, (_, count) => count + 1);
        if (status == 200 && LoseAnswer.TryGetValue(index, out int losses) && losses > 0)
        {
            LoseAnswer[index] = losses - 1;
            context.Response.Abort();
            return;
        }
        if (status == 409) { await Error(context, 409, "chunk_conflict", "a different chunk is already held, or the session is closed"); return; }
        if (status == 400) { await Error(context, 400, "chunk_invalid", "the chunk is not one this session expects"); return; }
        await Json(context, 200, json => json.WriteNumber("index", index));
    }

    /// <summary>Moves a validating session towards its end, one poll at a time. Called under the gate.</summary>
    private void Settle(Session session)
    {
        if (session.State != "Validating") return;
        if (session.PollsLeft-- > 0) return;
        byte[] whole = [.. Enumerable.Range(0, session.TotalChunks).SelectMany(index => session.Chunks[index])];
        string actual = Convert.ToHexStringLower(SHA256.HashData(whole));
        if (actual != session.ExpectedSha256) { session.State = "Rejected"; session.ErrorCode = "sha256_mismatch"; session.ErrorDetail = "the file does not match its declared hash"; return; }
        if (Reject.Contains(session.FileName)) { session.State = "Rejected"; session.ErrorCode = "malware_scan"; session.ErrorDetail = "the file failed validation"; return; }
        string stored = session.Format == "apk" && SignAndroidPackages ? Convert.ToHexStringLower(SHA256.HashData([.. whole, 1])) : actual;
        Build build = new(Guid.NewGuid().ToString("D"), session.FileName, session.Platform, session.Architecture, session.Format, session.Version, session.ExpectedSize, stored, session.PackageName)
        { Notes = session.Notes };
        builds[build.Id] = build;
        session.BuildId = build.Id;
        session.State = "Accepted";
        // Accepting a build changes the application, which is why a publisher reads the revision afterwards.
        revision++;
    }

    private async Task WriteSession(HttpListenerContext context, Session session, bool withTransfer)
    {
        string template = ChunkPathTemplate ?? "/" + Opaque + "/upload/v1/upload-sessions/" + session.Id + "/chunks/{index}";
        await Json(context, 200, json =>
        {
            lock (gate)
            {
                json.WriteString("id", session.Id);
                json.WriteString("applicationId", ApplicationId);
                json.WriteString("state", session.State);
                json.WriteNumber("expectedSize", session.ExpectedSize);
                json.WriteNumber("receivedBytes", session.Chunks.Values.Sum(chunk => (long)chunk.Length));
                json.WriteNumber("chunkSize", session.ChunkSize);
                json.WriteNumber("totalChunks", session.TotalChunks);
                json.WriteString("expiresAt", DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture));
                if (session.BuildId is null) json.WriteNull("buildId"); else json.WriteString("buildId", session.BuildId);
                if (session.ErrorCode is null) json.WriteNull("errorCode"); else json.WriteString("errorCode", session.ErrorCode);
                if (session.ErrorDetail is null) json.WriteNull("errorDetail"); else json.WriteString("errorDetail", session.ErrorDetail);
                if (withTransfer)
                {
                    json.WriteString("chunkPathTemplate", template);
                    json.WriteString("transferToken", session.TransferToken);
                }
            }
        });
    }

    private static async Task<byte[]> ReadAsync(HttpListenerRequest request)
    {
        using MemoryStream buffer = new();
        await request.InputStream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static Task Error(HttpListenerContext context, int status, string code, string detail) =>
        Json(context, status, json => { json.WriteString("error", code); json.WriteString("detail", detail); });

    private static async Task Json(HttpListenerContext context, int status, Action<Utf8JsonWriter> write)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer))
        {
            json.WriteStartObject();
            write(json);
            json.WriteEndObject();
        }
        await Send(context, status, buffer.ToArray());
    }

    private static async Task JsonArray(HttpListenerContext context, Action<Utf8JsonWriter> write)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer))
        {
            json.WriteStartArray();
            write(json);
            json.WriteEndArray();
        }
        await Send(context, 200, buffer.ToArray());
    }

    private static async Task Send(HttpListenerContext context, int status, byte[] bytes)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public void Dispose()
    {
        try { listener.Stop(); listener.Close(); }
        catch (ObjectDisposedException) { }
    }

    private sealed class Session(string id, string declaration, string fileName, long expectedSize, string expectedSha256, int chunkSize, int totalChunks,
        string format, string platform, string architecture, string version, string notes, string? packageName)
    {
        public string Id { get; } = id;
        public string Declaration { get; } = declaration;
        public string FileName { get; } = fileName;
        public long ExpectedSize { get; } = expectedSize;
        public string ExpectedSha256 { get; } = expectedSha256;
        public int ChunkSize { get; } = chunkSize;
        public int TotalChunks { get; } = totalChunks;
        public string Format { get; } = format;
        public string Platform { get; } = platform;
        public string Architecture { get; } = architecture;
        public string Version { get; } = version;
        public string Notes { get; } = notes;
        public string? PackageName { get; } = packageName;
        public Dictionary<int, byte[]> Chunks { get; } = [];
        public string State { get; set; } = "Uploading";
        public string TransferToken { get; set; } = "";
        public int PollsLeft { get; set; }
        public string? BuildId { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorDetail { get; set; }
    }

    private sealed class Build(string id, string fileName, string platform, string architecture, string format, string version, long size, string sha256, string? packageName)
    {
        public string Id { get; } = id;
        public string FileName { get; } = fileName;
        public string Platform { get; } = platform;
        public string Architecture { get; } = architecture;
        public string Format { get; } = format;
        public string Version { get; } = version;
        public long Size { get; } = size;
        public string Sha256 { get; } = sha256;
        public string? PackageName { get; } = packageName;
        public string? Channel { get; set; }
        public bool Published { get; set; }
        public string Notes { get; set; } = "";
    }
}
