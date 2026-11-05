using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using Graft.Contracts;

namespace Graft.Remote;

/// <summary>
/// HTTP to one Graft repository, the way Git's own HTTP transport would do it: anonymous first, then the credential Git's
/// credential helpers hold, approved when it works and rejected when it does not; TLS checked unless Git is configured
/// not to. On top of that, the chunked transfer of ADR 0043, which is the reason this program exists: a request goes up
/// in parts no bigger than the forge allows, is acted on while this asks how it is going, and its answer comes back in
/// ranges, each step retried where a proxy in between dropped it.
/// </summary>
internal sealed class ForgeClient : IDisposable
{
    /// <summary>The first part of a transfer, sent before the forge has said how large a part may be.</summary>
    private const long FirstPartBytes = 1024 * 1024;
    /// <summary>How much of a result one ranged read asks for.</summary>
    private const long RangeBytes = 32 * 1024 * 1024;
    private const int Attempts = 6;

    private readonly HttpClient http;
    private readonly Uri repository;
    private readonly IReadOnlyDictionary<string, string>? fixedHeaders;
    private readonly Action<string> progress;
    private Credential? credential;
    private bool approved;

    /// <param name="repository">The repository's address, ending in <c>.git</c> or not, without a trailing slash.</param>
    /// <param name="fixedHeaders">Headers to send instead of asking for a credential: what a Git LFS batch answer handed over.</param>
    public ForgeClient(Uri repository, IReadOnlyDictionary<string, string>? fixedHeaders, Action<string> progress)
    {
        this.repository = repository;
        this.fixedHeaders = fixedHeaders;
        this.progress = progress;
        SocketsHttpHandler handler = new() { AutomaticDecompression = DecompressionMethods.None, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        GitTls tls = GitTls.For(repository);
        if (!tls.Verify) handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        else if (tls.Authorities is { Count: > 0 } authorities)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None) return true;
                if (certificate is null || errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch)) return false;
                using X509Chain chain = new();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(authorities);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(certificate));
            };
        }
        http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("git-remote-graft/" + HelperVersion.Text);
    }

    public Uri Repository => repository;

    /// <summary>An address under the repository: <c>info/refs?...</c>, <c>git-upload-pack</c>, <c>graft/transfers</c>.</summary>
    public Uri Under(string relative) => new(repository.AbsoluteUri.TrimEnd('/') + "/" + relative);

    /// <summary>
    /// Sends a request built afresh for each attempt, asking for a credential on the first 401 and retrying what a proxy
    /// or a busy forge turned away. <paramref name="retry"/> is false for a request that must not be sent twice.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, bool retry, CancellationToken cancellationToken)
    {
        bool askedForCredential = false;
        for (int attempt = 1; ; attempt++)
        {
            using HttpRequestMessage request = build();
            Authorize(request);
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
            catch (Exception error) when (retry && attempt < Attempts && error is HttpRequestException or IOException ||
                retry && attempt < Attempts && error is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                progress("graft: " + error.Message + "; trying again");
                await Task.Delay(Backoff(attempt), cancellationToken);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && fixedHeaders is null)
            {
                response.Dispose();
                if (credential is not null && approved is false && askedForCredential)
                {
                    await GitCredentials.RejectAsync(repository, credential, cancellationToken);
                    throw new HelperException("Authentication failed for '" + repository + "'. The password Graft wants is a personal access token.");
                }
                credential = await GitCredentials.FillAsync(repository, cancellationToken)
                    ?? throw new HelperException("Authentication is needed for '" + repository + "' and no credential was given.");
                askedForCredential = true;
                continue;
            }
            if (retry && attempt < Attempts && IsTransient(response.StatusCode))
            {
                progress("graft: the forge answered " + (int)response.StatusCode + "; trying again");
                response.Dispose();
                await Task.Delay(Backoff(attempt), cancellationToken);
                continue;
            }
            if (credential is not null && !approved && response.IsSuccessStatusCode)
            {
                approved = true;
                await GitCredentials.ApproveAsync(repository, credential, cancellationToken);
            }
            return response;
        }
    }

    private void Authorize(HttpRequestMessage request)
    {
        if (fixedHeaders is not null)
        {
            foreach ((string name, string value) in fixedHeaders) request.Headers.TryAddWithoutValidation(name, value);
            return;
        }
        if (credential is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(credential.Username + ":" + credential.Password)));
    }

    private static bool IsTransient(HttpStatusCode status) =>
        (int)status is 408 or 429 or 502 or 503 or 504 or (>= 520 and <= 530);

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(30, 1 << (attempt - 1)));

    /// <summary>Reads a whole small answer, refusing anything but success with the forge's own reason when it gave one.</summary>
    public static async Task<byte[]> ReadSuccessAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HelperException(what + " failed: HTTP " + (int)response.StatusCode + Reason(body));
        return body;
    }

    private static string Reason(byte[] body)
    {
        try
        {
            string? message = System.Text.Json.Nodes.JsonNode.Parse(body)?["message"]?.GetValue<string>();
            return message is null ? "" : " (" + message + ")";
        }
        catch (System.Text.Json.JsonException) { return ""; }
        catch (InvalidOperationException) { return ""; }
    }

    /// <summary>
    /// Sends <paramref name="request"/> as a chunked transfer to <paramref name="start"/> and, when <paramref name="result"/>
    /// is given, writes the answer there. Throws with the forge's reason when it refuses the transfer.
    /// </summary>
    /// <param name="start">The transfer's address with its kind and size in the query.</param>
    public async Task TransferAsync(Uri start, Stream request, Stream? result, string label, CancellationToken cancellationToken)
    {
        long size = request.Length;
        TransferStatusResponse status = await ExchangeAsync(() => new HttpRequestMessage(HttpMethod.Post, Append(start, "wait=20"))
        {
            Content = new SliceContent(request, 0, Math.Min(size, FirstPartBytes))
        }, "starting the " + label, cancellationToken);
        Uri session = new(start.GetLeftPart(UriPartial.Path) + "/" + status.Id);
        try
        {
            long reported = 0;
            while (status.State == "receiving")
            {
                long offset = status.Received;
                long count = Math.Min(Math.Max(1, status.PartBytes), size - offset);
                if (size > status.PartBytes && offset - reported >= status.PartBytes)
                {
                    progress("graft: sent " + Mebibytes(offset) + " of " + Mebibytes(size) + " MiB of the " + label);
                    reported = offset;
                }
                status = await ExchangeAsync(() => new HttpRequestMessage(HttpMethod.Put,
                    new Uri(session + "?offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture) + "&wait=20"))
                {
                    Content = new SliceContent(request, offset, count)
                }, "sending the " + label, cancellationToken, conflictIsStatus: true);
            }
            if (size > status.PartBytes) progress("graft: sent all " + Mebibytes(size) + " MiB of the " + label + "; the forge is acting on it");
            while (status.State == "running")
                status = await ExchangeAsync(() => new HttpRequestMessage(HttpMethod.Get, new Uri(session + "?wait=20")), "waiting on the " + label, cancellationToken);
            if (status.State != "done") throw new HelperException("the forge refused the " + label + ": " + (status.Error ?? status.State));
            if (result is not null) await ReadResultAsync(new Uri(session + "/result"), status.ResultBytes ?? 0, result, label, cancellationToken);
        }
        finally
        {
            try { (await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, session), retry: false, CancellationToken.None)).Dispose(); }
            catch (Exception) { }
        }
    }

    /// <summary>Reads a result of known length in ranges, starting each range again where a dropped one stopped.</summary>
    public async Task ReadResultAsync(Uri address, long length, Stream destination, string label, CancellationToken cancellationToken)
    {
        long offset = 0;
        long reported = 0;
        int failures = 0;
        byte[] buffer = new byte[64 * 1024];
        while (offset < length)
        {
            long end = Math.Min(length, offset + RangeBytes) - 1;
            try
            {
                using HttpResponseMessage response = await SendAsync(() =>
                {
                    HttpRequestMessage request = new(HttpMethod.Get, address);
                    request.Headers.Range = new RangeHeaderValue(offset, end);
                    return request;
                }, retry: true, cancellationToken);
                if (response.StatusCode != HttpStatusCode.PartialContent && !(response.StatusCode == HttpStatusCode.OK && offset == 0))
                    await ReadSuccessAsync(response, "reading the " + label, cancellationToken);
                await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    offset += read;
                }
                failures = 0;
            }
            catch (Exception error) when (error is HttpRequestException or IOException && ++failures < Attempts)
            {
                progress("graft: " + error.Message + "; resuming at " + Mebibytes(offset) + " MiB");
                await Task.Delay(Backoff(failures), cancellationToken);
            }
            if (length >= 4 * FirstPartBytes && offset - reported >= RangeBytes && offset < length)
            {
                progress("graft: received " + Mebibytes(offset) + " of " + Mebibytes(length) + " MiB of the " + label);
                reported = offset;
            }
        }
    }

    private async Task<TransferStatusResponse> ExchangeAsync(Func<HttpRequestMessage> build, string what, CancellationToken cancellationToken,
        bool conflictIsStatus = false)
    {
        using HttpResponseMessage response = await SendAsync(build, retry: true, cancellationToken);
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        // A part sent beyond what arrived, after an answer was lost: the status says where to resume.
        if (!response.IsSuccessStatusCode && !(conflictIsStatus && response.StatusCode == HttpStatusCode.Conflict))
            throw new HelperException(what + " failed: HTTP " + (int)response.StatusCode + Reason(body));
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(body, ApiJson.Default.TransferStatusResponse)
                ?? throw new HelperException(what + " failed: the forge's answer was empty");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new HelperException(what + " failed: HTTP " + (int)response.StatusCode + Reason(body));
        }
    }

    private static Uri Append(Uri address, string query) => new(address + (address.Query.Length > 0 ? "&" : "?") + query);

    public static string Mebibytes(long bytes) => (bytes / (1024.0 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    public void Dispose() => http.Dispose();

    /// <summary>A range of a seekable stream as a request body, read afresh each time it is sent.</summary>
    private sealed class SliceContent(Stream source, long offset, long count) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            source.Position = offset;
            byte[] buffer = new byte[64 * 1024];
            long remaining = count;
            while (remaining > 0)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
                if (read == 0) throw new IOException("the request ended early");
                await stream.WriteAsync(buffer.AsMemory(0, read));
                remaining -= read;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = count;
            return true;
        }
    }
}

/// <summary>Whether Git would check this address's certificate, and against which authorities.</summary>
internal sealed record GitTls(bool Verify, X509Certificate2Collection? Authorities)
{
    public static GitTls For(Uri address)
    {
        if (Environment.GetEnvironmentVariable("GIT_SSL_NO_VERIFY") is { Length: > 0 }) return new(false, null);
        string? verify = Git.ConfigUrlMatch("http.sslverify", address, "bool");
        if (verify == "false") return new(false, null);
        string? authorities = Environment.GetEnvironmentVariable("GIT_SSL_CAINFO") ?? Git.ConfigUrlMatch("http.sslcainfo", address, "path");
        if (authorities is null || !File.Exists(authorities)) return new(true, null);
        X509Certificate2Collection collection = [];
        collection.ImportFromPemFile(authorities);
        return new(true, collection);
    }
}

internal sealed record Credential(string Username, string Password, IReadOnlyList<string> Fields);

/// <summary>Git's credential helpers, through <c>git credential</c>, so whatever stores the user's token is the one asked.</summary>
internal static class GitCredentials
{
    public static async Task<Credential?> FillAsync(Uri address, CancellationToken cancellationToken)
    {
        (int exit, string output) = await Git.RunAsync(["credential", "fill"], Describe(address), cancellationToken);
        if (exit != 0) return null;
        List<string> fields = [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r'))];
        string? username = fields.FirstOrDefault(line => line.StartsWith("username=", StringComparison.Ordinal))?[9..];
        string? password = fields.FirstOrDefault(line => line.StartsWith("password=", StringComparison.Ordinal))?[9..];
        return username is null || password is null ? null : new(username, password, fields);
    }

    public static Task ApproveAsync(Uri address, Credential credential, CancellationToken cancellationToken) =>
        Git.RunAsync(["credential", "approve"], string.Join("\n", credential.Fields) + "\n\n", cancellationToken);

    public static Task RejectAsync(Uri address, Credential credential, CancellationToken cancellationToken) =>
        Git.RunAsync(["credential", "reject"], string.Join("\n", credential.Fields) + "\n\n", cancellationToken);

    private static string Describe(Uri address) =>
        "protocol=" + address.Scheme + "\nhost=" + address.Authority + "\npath=" + address.AbsolutePath.TrimStart('/') + "\n\n";
}

/// <summary>Running <c>git</c> itself: for configuration and credentials, which belong to Git and not to this program.</summary>
internal static class Git
{
    public static async Task<(int Exit, string Output)> RunAsync(IReadOnlyList<string> arguments, string? input, CancellationToken cancellationToken)
    {
        System.Diagnostics.ProcessStartInfo start = new("git")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start) ?? throw new HelperException("git could not be started");
        if (input is not null) await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, output);
    }

    public static string? Config(params string[] arguments)
    {
        (int exit, string output) = RunAsync(["config", .. arguments], null, CancellationToken.None).GetAwaiter().GetResult();
        string value = output.TrimEnd('\r', '\n');
        return exit == 0 && value.Length > 0 ? value : null;
    }

    /// <summary>Sets a value in the repository's configuration, and says so when Git refuses.</summary>
    public static void SetConfig(params string[] arguments)
    {
        (int exit, _) = RunAsync(["config", .. arguments], null, CancellationToken.None).GetAwaiter().GetResult();
        if (exit != 0) throw new HelperException("git config " + string.Join(' ', arguments.Where(argument => !argument.Contains("://", StringComparison.Ordinal))) + " exited " + exit);
    }

    public static string? ConfigUrlMatch(string key, Uri address, string type) =>
        Config("--type=" + type, "--get-urlmatch", key, address.AbsoluteUri);
}

internal static class HelperVersion
{
    public static readonly string Text = typeof(HelperVersion).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        is [System.Reflection.AssemblyInformationalVersionAttribute { InformationalVersion: { Length: > 0 } version }, ..] ? version : "unknown";
}

/// <summary>A failure to show the person as Git shows its own: one line, after <c>fatal:</c>.</summary>
internal sealed class HelperException(string message) : Exception(message);
