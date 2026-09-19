using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Graft.Ui;

if (args.Length != 1 || !File.Exists(args[0])) { Console.Error.WriteLine("Usage: Graft.Ui.Checks <server DLL or EXE>"); return 2; }
var serverFile = Path.GetFullPath(args[0]);
var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
var logs = new ConcurrentQueue<string>();
var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
string? pin = null;
using var server = Process.Start(StartInfo("--fixture"))!;
var stderr = server.StandardError.ReadToEndAsync();
var reader = Task.Run(async () =>
{
    while (await server.StandardOutput.ReadLineAsync() is string line)
    {
        logs.Enqueue(line);
        if (line.StartsWith("CERT-SHA256: ", StringComparison.Ordinal)) pin = line[13..];
        if (line.StartsWith("READY: ", StringComparison.Ordinal)) ready.TrySetResult(line[7..]);
    }
});
var checks = 0;
try
{
    var origin = await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
    using var handler = new HttpClientHandler { UseProxy = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None };
    handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString(HashAlgorithmName.SHA256) == pin;
    using var client = new HttpClient(handler) { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(20) };
    Console.WriteLine(logs.Single(l => l.StartsWith("MODE:", StringComparison.Ordinal)));
    Console.WriteLine(logs.Single(l => l.StartsWith("ASSETS:", StringComparison.Ordinal)));
    using (var self = Process.Start(StartInfo("--self-test"))!)
    {
        var output = self.StandardOutput.ReadToEndAsync();
        var error = self.StandardError.ReadToEndAsync();
        await self.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert(self.ExitCode == 0, "server session self-checks");
        Console.WriteLine((await output).Trim()); await error;
    }
    using var home = await client.GetAsync("/");
    var html = await home.Content.ReadAsStringAsync();
    Assert(home.IsSuccessStatusCode && home.Content.Headers.ContentType?.MediaType == "text/html" && html.Contains("blazor.webassembly.js", StringComparison.Ordinal), "embedded HTML bootstrap");
    using var deep = await client.GetAsync("/projects");
    Assert(deep.IsSuccessStatusCode && await deep.Content.ReadAsStringAsync() == html, "deep-link shell");
    Assert(home.Headers.GetValues("Content-Security-Policy").Single().Contains("frame-ancestors 'none'", StringComparison.Ordinal), "CSP");
    foreach (var path in new[] { "/api/missing", "/API/missing", "/team/project.git/info/refs", "/_framework/missing.wasm", "/missing.css", "/not-a-page" })
    {
        using var result = await client.GetAsync(path);
        Assert(result.StatusCode == HttpStatusCode.NotFound && !(await result.Content.ReadAsStringAsync()).Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase), "no HTML fallback: " + path);
    }
    using var denied = await client.GetAsync("/api/projects");
    Assert(denied.StatusCode == HttpStatusCode.Unauthorized, "anonymous project denial");
    using var css = await client.GetAsync("/app.css");
    var cssBytes = await css.Content.ReadAsByteArrayAsync();
    Assert(css.IsSuccessStatusCode && css.Content.Headers.ContentType?.MediaType == "text/css", "CSS MIME");
    using var script = await client.GetAsync("/_framework/blazor.webassembly.js");
    Assert(script.IsSuccessStatusCode && script.Content.Headers.ContentType?.MediaType == "text/javascript", "bootstrap JavaScript MIME");
    var runtimeScript = await client.GetStringAsync("/_framework/dotnet.js");
    var wasmName = Regex.Match(runtimeScript, @"dotnet\.native\.[a-z0-9]+\.wasm").Value;
    Assert(wasmName.Length > 0, "published runtime manifest reference");
    using (var wasm = await client.GetAsync("/_framework/" + wasmName))
    {
        var bytes = await wasm.Content.ReadAsByteArrayAsync();
        Assert(wasm.IsSuccessStatusCode && wasm.Content.Headers.ContentType?.MediaType == "application/wasm" && bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 97, 115, 109 }), "WebAssembly MIME and payload");
    }
    using (var headRequest = new HttpRequestMessage(HttpMethod.Head, "/app.css"))
    using (var head = await client.SendAsync(headRequest))
        Assert(head.IsSuccessStatusCode && head.Content.Headers.ContentLength == cssBytes.Length && (await head.Content.ReadAsByteArrayAsync()).Length == 0, "HEAD asset");
    using (var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "/app.css"))
    {
        conditionalRequest.Headers.IfNoneMatch.Add(css.Headers.ETag!);
        using var notModified = await client.SendAsync(conditionalRequest);
        Assert(notModified.StatusCode == HttpStatusCode.NotModified && notModified.Headers.CacheControl?.NoCache == true, "ETag revalidation");
    }
    foreach (var encoding in new[] { "br", "gzip" })
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/app.css"); request.Headers.AcceptEncoding.ParseAdd(encoding);
        using var response = await client.SendAsync(request);
        using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using Stream decoder = encoding == "br" ? new BrotliStream(body, CompressionMode.Decompress) : new GZipStream(body, CompressionMode.Decompress);
        using var decoded = new MemoryStream(); await decoder.CopyToAsync(decoded);
        Assert(response.Content.Headers.ContentEncoding.Single() == encoding && decoded.ToArray().SequenceEqual(cssBytes) && response.Headers.Vary.Contains("Accept-Encoding"), encoding + " negotiation/content");
    }
    using (var request = new HttpRequestMessage(HttpMethod.Get, "/app.css"))
    {
        request.Headers.AcceptEncoding.ParseAdd("br;q=0, gzip;q=0");
        using var response = await client.SendAsync(request);
        Assert(!response.Content.Headers.ContentEncoding.Any(), "q=0 compression rejection");
    }
    using (var request = new HttpRequestMessage(HttpMethod.Get, "/app.css"))
    {
        request.Headers.AcceptEncoding.ParseAdd("*;q=0");
        using var response = await client.SendAsync(request);
        Assert(response.StatusCode == HttpStatusCode.NotAcceptable, "no acceptable representation");
    }
    using var anonymousResponse = await client.GetAsync("/api/session");
    var anonymous = (await anonymousResponse.Content.ReadFromJsonAsync(UiJson.Default.SessionView))!;
    var originalCookie = Cookie(anonymousResponse);
    var setCookie = anonymousResponse.Headers.GetValues("Set-Cookie").Single();
    Assert(setCookie.Contains("secure", StringComparison.OrdinalIgnoreCase) && setCookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && setCookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase) && setCookie.Contains("path=/", StringComparison.OrdinalIgnoreCase) && !setCookie.Contains("domain=", StringComparison.OrdinalIgnoreCase), "Secure HttpOnly Strict host-only cookie");
    Assert(!anonymous.Authenticated && anonymousResponse.Headers.CacheControl?.NoStore == true, "anonymous session/no-store");
    using var secondResponse = await client.GetAsync("/api/session");
    var second = (await secondResponse.Content.ReadFromJsonAsync(UiJson.Default.SessionView))!;
    await Status("/api/login", originalCookie, null, origin, HttpStatusCode.Forbidden, "missing CSRF");
    await Status("/api/login", originalCookie, second.Csrf, origin, HttpStatusCode.Forbidden, "CSRF bound to session");
    await Status("/api/login", originalCookie, anonymous.Csrf, "https://example.invalid", HttpStatusCode.Forbidden, "foreign Origin");
    await Status("/api/login", originalCookie, anonymous.Csrf, null, HttpStatusCode.Forbidden, "missing Origin");
    foreach (var pair in new[] { ("text/plain", "{}", HttpStatusCode.UnsupportedMediaType), ("application/json", "{", HttpStatusCode.BadRequest), ("application/json", "{\"username\":null,\"password\":null}", HttpStatusCode.BadRequest) })
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/login");
        request.Headers.Add("Cookie", originalCookie); request.Headers.Add("Origin", origin); request.Headers.Add("X-Graft-CSRF", anonymous.Csrf);
        request.Content = new StringContent(pair.Item2, Encoding.UTF8, pair.Item1);
        using var response = await client.SendAsync(request);
        Assert(response.StatusCode == pair.Item3, "invalid login content rejected");
    }
    using var badLogin = await Post("/api/login", originalCookie, anonymous.Csrf, origin, "incorrect");
    Assert(badLogin.StatusCode == HttpStatusCode.Unauthorized, "wrong password");
    using var login = await Post("/api/login", originalCookie, anonymous.Csrf, origin, password);
    var session = (await login.Content.ReadFromJsonAsync(UiJson.Default.SessionView))!;
    var signedCookie = Cookie(login);
    Assert(login.IsSuccessStatusCode && session.Authenticated && session.User == "fixture" && signedCookie != originalCookie && session.Csrf != anonymous.Csrf, "login rotates session and CSRF");
    using (var old = await GetWithCookie("/api/projects", originalCookie)) Assert(old.StatusCode == HttpStatusCode.Unauthorized, "pre-login cookie revoked");
    using (var projects = await GetWithCookie("/api/projects", signedCookie))
        Assert(projects.IsSuccessStatusCode && (await projects.Content.ReadFromJsonAsync(UiJson.Default.ProjectViewArray))?.Single().Name == "Graft sandbox", "authenticated project JSON");
    await Status("/api/logout", signedCookie, anonymous.Csrf, origin, HttpStatusCode.Forbidden, "old CSRF rejected");
    await Status("/api/logout", signedCookie, null, origin, HttpStatusCode.Forbidden, "logout CSRF enforced");
    using (var still = await GetWithCookie("/api/projects", signedCookie)) Assert(still.IsSuccessStatusCode, "rejected logout preserves session");
    await Status("/api/logout", signedCookie, session.Csrf, origin, HttpStatusCode.NoContent, "logout");
    using (var replay = await GetWithCookie("/api/projects", signedCookie)) Assert(replay.StatusCode == HttpStatusCode.Unauthorized, "revoked cookie replay rejected");
    await Status("/api/login", signedCookie, session.Csrf, origin, HttpStatusCode.Unauthorized, "revoked session cannot login");
    using (var hostileHost = new HttpRequestMessage(HttpMethod.Get, "/"))
    {
        hostileHost.Headers.Host = "example.invalid";
        using var response = await client.SendAsync(hostileHost);
        Assert(response.StatusCode == HttpStatusCode.BadRequest, "host validation");
    }
    // Two password checks above; eight further failures reach the fixed budget.
    for (var i = 0; i < 8; i++) { using var response = await Post("/api/login", Cookie(secondResponse), second.Csrf, origin, "incorrect"); Assert(response.StatusCode == HttpStatusCode.Unauthorized, "bounded login attempt"); }
    using (var limited = await Post("/api/login", Cookie(secondResponse), second.Csrf, origin, "incorrect")) Assert(limited.StatusCode == HttpStatusCode.TooManyRequests, "login attempt rate limit");
    Console.WriteLine($"HTTP ACCEPTANCE: PASS ({checks} assertions)");
    return 0;

    async Task<HttpResponseMessage> GetWithCookie(string path, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path); request.Headers.Add("Cookie", cookie); return await client.SendAsync(request);
    }
    async Task<HttpResponseMessage> Post(string path, string cookie, string? csrf, string? requestOrigin, string value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Cookie", cookie);
        if (csrf is not null) request.Headers.Add("X-Graft-CSRF", csrf);
        if (requestOrigin is not null) request.Headers.Add("Origin", requestOrigin);
        if (path == "/api/login") request.Content = JsonContent.Create(new LoginRequest("fixture", value), UiJson.Default.LoginRequest);
        return await client.SendAsync(request);
    }
    async Task Status(string path, string cookie, string? csrf, string? requestOrigin, HttpStatusCode expected, string name)
    {
        using var response = await Post(path, cookie, csrf, requestOrigin, password);
        Assert(response.StatusCode == expected, name);
    }
}
catch (Exception ex) { Console.Error.WriteLine("HTTP ACCEPTANCE: FAIL: " + ex.GetType().Name + ": " + ex.Message); return 1; }
finally
{
    if (!server.HasExited) server.Kill(entireProcessTree: true);
    await server.WaitForExitAsync(); await reader; await stderr;
}

ProcessStartInfo StartInfo(string argument)
{
    var dll = serverFile.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    var info = new ProcessStartInfo(dll ? "dotnet" : serverFile) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetTempPath() };
    if (dll) info.ArgumentList.Add(serverFile);
    info.ArgumentList.Add(argument); info.Environment["GRAFT_UI_PROBE_PASSWORD"] = password;
    return info;
}
void Assert(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks++; Console.WriteLine("PASS: " + name); }
static string Cookie(HttpResponseMessage response) => response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
