using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Graft.Mcp;

/// <summary>
/// How this server reaches Graft: the ordinary HTTP API, with a personal access token, exactly as any other
/// API client would. It holds no database handle and no in-process authorization.
///
/// <para>
/// That is the point. The tools cannot reach anything the token's owner could not reach themselves, so the
/// blast radius of pointing an agent at a Graft instance is bounded by a credential an administrator can
/// see and revoke, rather than by how carefully these tools were written.
/// </para>
/// </summary>
public sealed class GraftClient : IDisposable
{
    private readonly HttpClient http;

    public GraftClient(Uri server, string token, bool allowWrites, X509Certificate2? trustedRoot = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        AllowWrites = allowWrites;

        // A self-hosted forge often presents a certificate from a private authority, or its own self-signed
        // one. The answer to that is to name the certificate to trust, not to stop checking: there is no
        // flag here that accepts any certificate, because a tool that carries a credential to a host it did
        // not verify is handing the credential to whoever answered.
        //
        // Naming a root replaces the system store rather than adding to it, which pins this client to that
        // one authority. For a tool pointed at a single known forge that is the stronger position, and the
        // hostname is still checked against the certificate either way.
        SocketsHttpHandler handler = new();
        if (trustedRoot is not null)
        {
            X509ChainPolicy policy = new()
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck
            };
            policy.CustomTrustStore.Add(trustedRoot);
            handler.SslOptions.CertificateChainPolicy = policy;
        }

        http = new HttpClient(handler) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", token);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Graft.Mcp", "1.0"));
    }

    /// <summary>
    /// Whether the tools that change something are permitted. Off unless an operator turned it on, so an
    /// agent handed this server by default can read a forge and cannot alter one.
    /// </summary>
    public bool AllowWrites { get; }

    public async Task<(bool Ok, string Body, int Status)> SendAsync(
        HttpMethod method,
        string path,
        JsonObject? body = null,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(method, path);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            string text = await response.Content.ReadAsStringAsync(cancellationToken);
            return (response.IsSuccessStatusCode, text, (int)response.StatusCode);
        }
        catch (HttpRequestException error)
        {
            return (false, "Graft could not be reached: " + error.Message, 0);
        }
        catch (TaskCanceledException)
        {
            return (false, "Graft did not answer within the timeout.", 0);
        }
    }

    /// <summary>
    /// Reads a typed response through the source-generated contracts the server itself serializes with, so
    /// the shapes cannot drift apart and nothing here needs reflection at runtime.
    /// </summary>
    public async Task<(T? Value, string? Error)> GetAsync<T>(string path, JsonTypeInfo<T> type,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(path, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (default, Describe((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken)));
            return (await response.Content.ReadFromJsonAsync(type, cancellationToken), null);
        }
        catch (HttpRequestException error) { return (default, "Graft could not be reached: " + error.Message); }
        catch (TaskCanceledException) { return (default, "Graft did not answer within the timeout."); }
        catch (JsonException error) { return (default, "Graft returned something this client could not read: " + error.Message); }
    }

    /// <summary>
    /// Turns a refusal into something an agent can act on. A 404 is deliberately ambiguous in Graft: a
    /// project you may not see and one that does not exist answer identically, and saying so is more useful
    /// than reporting "not found" and letting the reader conclude it is gone.
    /// </summary>
    public static string Describe(int status, string body) => status switch
    {
        401 => "The token was not accepted. It may be revoked, expired, or belong to a disabled account.",
        403 => "The token's scopes do not allow this. A token needs the api scope for project changes.",
        404 => "No such project, or the token's owner may not see it. Graft answers both the same way.",
        429 => "Graft is rate limiting or busy. Try again shortly.",
        _ => "Graft refused with status " + status.ToString(System.Globalization.CultureInfo.InvariantCulture) +
             (body.Length == 0 ? "." : ": " + Trim(body))
    };

    private static string Trim(string body) => body.Length <= 400 ? body : body[..400] + "…";

    public void Dispose() => http.Dispose();
}
