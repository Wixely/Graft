using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using Graft.Contracts;

namespace Graft.Client;

/// <summary>The outcome of a mutation, carrying the server's own message when it refused.</summary>
public readonly record struct ApiResult(bool Ok, string Message)
{
    public static ApiResult Accepted(string message) => new(true, message);
    public static ApiResult Refused(string message) => new(false, message);
}

/// <summary>A mutation outcome with a deserialized response body.</summary>
public readonly record struct ApiResult<T>(bool Ok, string Message, T? Value);

/// <summary>
/// Session-aware API access for the browser client. Every Graft mutation requires the session cookie plus the
/// matching <c>X-Graft-CSRF</c> header, so that pairing lives here instead of in each page. The session itself
/// is loaded once per page load and shared, because the shell and the routed page both need it.
/// </summary>
public sealed class GraftApi(HttpClient http)
{
    private const string Unreachable = "Graft could not be reached.";
    private Task<bool>? load;

    public SessionResponse? Session { get; private set; }
    public bool Authenticated => Session?.Authenticated == true;
    public bool IsAdministrator => Session?.IsAdministrator == true;
    public string Username => Session?.Username ?? "";

    /// <summary>
    /// False once a request has failed at the transport level. The shell reports that differently from a
    /// refusal, because an unreachable server is not an unauthenticated one.
    /// </summary>
    public bool Reachable { get; private set; } = true;

    /// <summary>Raised after the session changes so the shell can redraw its identity-dependent chrome.</summary>
    public event Action? SessionChanged;

    /// <summary>Loads the session at most once. Concurrent callers await the same request.</summary>
    public Task<bool> EnsureSessionAsync(CancellationToken cancellationToken = default) =>
        load ??= RefreshSessionAsync(cancellationToken);

    public async Task<bool> RefreshSessionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Session = await http.GetFromJsonAsync("api/session", ApiJson.Default.SessionResponse, cancellationToken);
            Reachable = true;
        }
        catch (HttpRequestException) { Session = null; Reachable = false; }
        load = Task.FromResult(Authenticated);
        SessionChanged?.Invoke();
        return Authenticated;
    }

    public async Task<T?> GetAsync<T>(string uri, JsonTypeInfo<T> type, CancellationToken cancellationToken = default)
    {
        try { return await http.GetFromJsonAsync(uri, type, cancellationToken); }
        catch (HttpRequestException) { Reachable = false; return default; }
    }

    public Task<ApiResult> PostAsync(string uri, string accepted, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, uri, null, accepted, cancellationToken);

    public Task<ApiResult> PostAsync<TRequest>(string uri, TRequest body, JsonTypeInfo<TRequest> type, string accepted,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, uri, JsonContent.Create(body, type), accepted, cancellationToken);

    public Task<ApiResult> DeleteAsync(string uri, string accepted, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, uri, null, accepted, cancellationToken);

    public async Task<ApiResult> SendAsync(HttpMethod method, string uri, HttpContent? content, string accepted,
        CancellationToken cancellationToken = default)
    {
        if (Session is null) return ApiResult.Refused("There is no active session.");
        try
        {
            using HttpRequestMessage request = Build(method, uri, content);
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? ApiResult.Accepted(accepted)
                : ApiResult.Refused(await ReadMessageAsync(response, cancellationToken));
        }
        catch (HttpRequestException) { Reachable = false; return ApiResult.Refused(Unreachable); }
    }

    /// <summary>Sends a mutation and deserializes its response body, which some creations return exactly once.</summary>
    public async Task<ApiResult<TResponse>> SendAsync<TRequest, TResponse>(HttpMethod method, string uri,
        TRequest body, JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, string accepted,
        CancellationToken cancellationToken = default)
    {
        if (Session is null) return new(false, "There is no active session.", default);
        try
        {
            using HttpRequestMessage request = Build(method, uri, JsonContent.Create(body, requestType));
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(false, await ReadMessageAsync(response, cancellationToken), default);
            return new(true, accepted, await response.Content.ReadFromJsonAsync(responseType, cancellationToken));
        }
        catch (HttpRequestException) { Reachable = false; return new(false, Unreachable, default); }
    }

    /// <summary>Signs in. Login precedes the CSRF secret, so it does not go through <see cref="SendAsync"/>.</summary>
    public async Task<ApiResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync("api/login",
                new LoginRequest(username, password), ApiJson.Default.LoginRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return ApiResult.Refused(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Username or password was not accepted.",
                    HttpStatusCode.TooManyRequests => "Too many sign-in attempts. Wait and try again.",
                    _ => "Sign-in failed."
                });
            await RefreshSessionAsync(cancellationToken);
            return ApiResult.Accepted("Signed in.");
        }
        catch (HttpRequestException) { Reachable = false; return ApiResult.Refused(Unreachable); }
    }

    public async Task<ApiResult> LogoutAsync(CancellationToken cancellationToken = default)
    {
        ApiResult result = await PostAsync("api/logout", "Signed out.", cancellationToken);
        await RefreshSessionAsync(cancellationToken);
        return result;
    }

    private HttpRequestMessage Build(HttpMethod method, string uri, HttpContent? content)
    {
        HttpRequestMessage request = new(method, uri) { Content = content };
        request.Headers.Add("X-Graft-CSRF", Session!.Csrf);
        return request;
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            ErrorResponse? error = await response.Content.ReadFromJsonAsync(ApiJson.Default.ErrorResponse, cancellationToken);
            if (error?.Message is { Length: > 0 } message) return message;
        }
        catch (HttpRequestException) { }
        catch (System.Text.Json.JsonException) { }
        catch (NotSupportedException) { }
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Sign in again to continue.",
            HttpStatusCode.Forbidden => "That action is not permitted.",
            HttpStatusCode.NotFound => "That resource no longer exists.",
            HttpStatusCode.TooManyRequests => "Too many attempts. Wait and try again.",
            _ => "The request was refused."
        };
    }
}
