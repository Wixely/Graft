using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Graft.Mcp;

/// <summary>
/// The shared connection to one Graft instance: where it is, how its certificate is trusted, and whether
/// tools reached through it may change anything by default. It holds no credential.
/// </summary>
public sealed class GraftConnection : IDisposable
{
    public GraftConnection(Uri server, bool allowWrites, X509Certificate2? trustedRoot = null)
        : this(server, allowWrites, Handler(trustedRoot), "An operator can restart it with --allow-writes true.")
    {
    }

    private GraftConnection(Uri server, bool allowWrites, SocketsHttpHandler handler, string writeRemedy)
    {
        ArgumentNullException.ThrowIfNull(server);
        AllowWrites = allowWrites;
        WriteRemedy = writeRemedy;
        Http = new HttpClient(handler) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(30) };
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Graft.Mcp", "1.0"));
    }

    /// <summary>
    /// A connection from the forge to its own listener, for the endpoint the forge hosts in-process.
    ///
    /// <para>
    /// The tools still go through the HTTP API rather than reaching into the store, so an agent inside the
    /// forge can do exactly what the same token could do with curl, and every authorization decision is the
    /// one the API route already makes. What this changes is only the wire: requests are addressed to the
    /// public origin, so SNI, the Host header and every URL the API builds are the ones a browser would see,
    /// but the socket is opened straight to the listener instead of resolving that name, and the certificate
    /// that answers must be byte-for-byte the one this process is serving. Nothing between here and there
    /// can be impersonated, because there is nothing between here and there.
    /// </para>
    /// </summary>
    /// <param name="origin">The public origin, with a trailing slash.</param>
    /// <param name="listener">Where this process is actually listening.</param>
    /// <param name="own">The certificate this process serves, which is the only one accepted.</param>
    /// <param name="writeRemedy">What a refused write tells the agent an administrator could change.</param>
    public static GraftConnection ToOwnListener(Uri origin, IPEndPoint listener, X509Certificate2 own, string writeRemedy)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(own);
        byte[] expected = own.GetCertHash(HashAlgorithmName.SHA256);
        SocketsHttpHandler handler = new()
        {
            UseProxy = false,
            ConnectCallback = async (_, cancellationToken) =>
            {
                Socket socket = new(listener.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(listener, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
            presented is not null &&
            CryptographicOperations.FixedTimeEquals(expected, presented.GetCertHash(HashAlgorithmName.SHA256));
        // Writes are decided per caller by the administrator's setting, so the connection's own default is
        // the closed one and the per-request client carries the live answer.
        return new(origin, allowWrites: false, handler, writeRemedy);
    }

    /// <summary>
    /// A self-hosted forge often presents a certificate from a private authority, or its own self-signed
    /// one. The answer to that is to name the certificate to trust, not to stop checking: there is no
    /// option here that accepts any certificate, because a tool that carries a credential to a host it
    /// did not verify is handing the credential to whoever answered.
    ///
    /// <para>
    /// Naming a root replaces the system store rather than adding to it, which pins this client to that
    /// one authority. For a tool pointed at a single known forge that is the stronger position, and the
    /// hostname is still checked against the certificate either way.
    /// </para>
    /// </summary>
    private static SocketsHttpHandler Handler(X509Certificate2? trustedRoot)
    {
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
        return handler;
    }

    internal HttpClient Http { get; }

    /// <summary>
    /// Whether the tools that change something are permitted for a caller that carries no answer of its
    /// own. Off unless an operator turned it on, so an agent handed this server by default can read a forge
    /// and cannot alter one.
    /// </summary>
    public bool AllowWrites { get; }

    /// <summary>What a refused write says would permit it. Names the switch an operator or administrator
    /// would actually change, which differs between a launched process and the forge's own endpoint.</summary>
    public string WriteRemedy { get; }

    public void Dispose() => Http.Dispose();
}

/// <summary>
/// How a single caller reaches Graft: the ordinary HTTP API, with that caller's personal access token,
/// exactly as any other API client would. It holds no database handle and no in-process authorization.
///
/// <para>
/// That is the point. The tools cannot reach anything the token's owner could not reach themselves, so the
/// blast radius of pointing an agent at a Graft instance is bounded by a credential an administrator can
/// see and revoke, rather than by how carefully these tools were written.
/// </para>
///
/// <para>
/// The token lives here rather than on the shared <see cref="GraftConnection"/> because over HTTP each
/// caller brings their own, and one connection serves them all.
/// </para>
/// </summary>
public sealed class GraftClient
{
    private readonly GraftConnection connection;
    private readonly string token;
    private readonly bool allowWrites;

    public GraftClient(GraftConnection connection, string token)
        : this(connection, token, connection.AllowWrites)
    {
    }

    /// <summary>A client whose write permission was decided for this caller, as the forge's own endpoint
    /// decides it from the administrator's live setting rather than from how the process was started.</summary>
    public GraftClient(GraftConnection connection, string token, bool allowWrites)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(token);
        this.connection = connection;
        this.token = token;
        this.allowWrites = allowWrites;
    }

    public bool AllowWrites => allowWrites;

    public string WriteRemedy => connection.WriteRemedy;

    public async Task<(bool Ok, string Body, int Status)> SendAsync(
        HttpMethod method,
        string path,
        JsonObject? body = null,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Build(method, path);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using HttpResponseMessage response = await connection.Http.SendAsync(request, cancellationToken);
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
    /// Sends a raw body rather than JSON, for the routes that take a file's bytes. The caller's token still
    /// travels on the request exactly as it does everywhere else.
    /// </summary>
    public async Task<(bool Ok, string Body, int Status)> SendBytesAsync(
        HttpMethod method,
        string path,
        byte[] content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        using HttpRequestMessage request = Build(method, path);
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        try
        {
            using HttpResponseMessage response = await connection.Http.SendAsync(request, cancellationToken);
            string text = await response.Content.ReadAsStringAsync(cancellationToken);
            return (response.IsSuccessStatusCode, text, (int)response.StatusCode);
        }
        catch (HttpRequestException error) { return (false, "Graft could not be reached: " + error.Message, 0); }
        catch (TaskCanceledException) { return (false, "Graft did not answer within the timeout.", 0); }
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
            using HttpRequestMessage request = Build(HttpMethod.Get, path);
            using HttpResponseMessage response = await connection.Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (default, Describe((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken)));
            return (await response.Content.ReadFromJsonAsync(type, cancellationToken), null);
        }
        catch (HttpRequestException error) { return (default, "Graft could not be reached: " + error.Message); }
        catch (TaskCanceledException) { return (default, "Graft did not answer within the timeout."); }
        catch (JsonException error) { return (default, "Graft returned something this client could not read: " + error.Message); }
    }

    /// <summary>Whether this caller presented a credential at all. False only where an operator has
    /// explicitly allowed anonymous access, in which case Graft answers as it would any stranger.</summary>
    public bool Anonymous => token.Length == 0;

    /// <summary>
    /// The token travels on the request rather than on the shared client, so one connection can serve many
    /// callers without any of them inheriting another's credential.
    ///
    /// <para>
    /// An empty token sends no header at all rather than an empty one. Graft treats a header it cannot
    /// resolve as a bad credential and answers 401, while no header is an anonymous request that may still
    /// read public projects, and those are different answers.
    /// </para>
    /// </summary>
    private HttpRequestMessage Build(HttpMethod method, string path)
    {
        HttpRequestMessage request = new(method, path);
        if (token.Length > 0) request.Headers.Add("PRIVATE-TOKEN", token);
        return request;
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
}
