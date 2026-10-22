using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Graft.Core;
using Graft.Storage;

namespace Graft.Server;

/// <summary>
/// Delivers webhook payloads, out of the way of the request that caused them.
///
/// <para>
/// Nothing a person does in Graft waits for somebody else's server. A push that blocked until a continuous
/// integration box answered would make the forge as slow as the slowest thing listening to it, and as
/// unavailable as the least reliable. So an event is queued and the request returns; delivery happens on a
/// worker of its own.
/// </para>
///
/// <para>
/// The queue is bounded and in memory. A restart loses whatever had not been sent, and a queue that fills
/// drops the oldest rather than growing: both are recorded against the hook, and both are better than a
/// forge that runs out of memory because a receiver went away. This is a notification mechanism, not a
/// message broker, and it does not pretend to be one.
/// </para>
///
/// <para>
/// Every delivery carries an HMAC-SHA256 of the exact body under the hook's own secret, so a receiver can
/// tell a real delivery from anything else that found its address. The secret is never sent.
/// </para>
/// </summary>
public sealed class WebhookSender : IAsyncDisposable
{
    /// <summary>Enough to absorb a burst; small enough that a dead receiver cannot consume the process.</summary>
    private const int QueueDepth = 512;

    /// <summary>Three attempts, briefly spaced. A receiver that is down for longer than this is down, and
    /// retrying into it for minutes would only delay everything queued behind it.</summary>
    private static readonly TimeSpan[] Backoff = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)];

    private readonly GraftStore store;
    private readonly HttpClient client;
    private readonly ConcurrentQueue<Delivery> queue = new();
    private readonly SemaphoreSlim pending = new(0);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task worker;
    private int depth;

    public WebhookSender(GraftStore store, HttpMessageHandler? handler = null)
    {
        this.store = store;
        client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Graft", "1.0"));
        worker = Task.Run(RunAsync);
    }

    /// <summary>
    /// Queues one event for every hook on a project that asked for it. Returns without waiting, and
    /// without throwing: a failure to notify is never a failure of the thing that happened.
    /// </summary>
    public async Task PublishAsync(long projectId, WebhookEvent single, string payload, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<WebhookTarget> targets = await store.FindWebhookTargetsAsync(projectId, single, cancellationToken);
            foreach (WebhookTarget target in targets) Enqueue(new(target, WebhookEvents.Name(single), payload));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine("WEBHOOK: could not read hooks for project " +
                projectId.ToString(CultureInfo.InvariantCulture) + ": " + error.Message);
        }
    }

    private void Enqueue(Delivery delivery)
    {
        // Dropping the oldest keeps the newest, which is the one a receiver most needs: a stale push
        // notification is worth less than the current one.
        while (Volatile.Read(ref depth) >= QueueDepth && queue.TryDequeue(out Delivery? dropped))
        {
            Interlocked.Decrement(ref depth);
            Console.Error.WriteLine("WEBHOOK: queue full, dropped a " + dropped.Event + " delivery.");
        }
        queue.Enqueue(delivery);
        Interlocked.Increment(ref depth);
        pending.Release();
    }

    private async Task RunAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            try { await pending.WaitAsync(stopping.Token); }
            catch (OperationCanceledException) { return; }
            if (!queue.TryDequeue(out Delivery? delivery)) continue;
            Interlocked.Decrement(ref depth);
            await DeliverAsync(delivery);
        }
    }

    private async Task DeliverAsync(Delivery delivery)
    {
        int? status = null;
        string? error = null;
        for (int attempt = 0; attempt < Backoff.Length && !stopping.IsCancellationRequested; attempt++)
        {
            if (Backoff[attempt] > TimeSpan.Zero)
            {
                try { await Task.Delay(Backoff[attempt], stopping.Token); }
                catch (OperationCanceledException) { return; }
            }
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Post, delivery.Target.Url)
                {
                    Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json")
                };
                request.Headers.Add("X-Graft-Event", delivery.Event);
                request.Headers.Add("X-Graft-Signature", Sign(delivery.Target.Secret, delivery.Payload));
                using HttpResponseMessage response = await client.SendAsync(request, stopping.Token);
                status = (int)response.StatusCode;
                error = null;
                if (response.IsSuccessStatusCode) break;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or UriFormatException
                or InvalidOperationException or NotSupportedException)
            {
                status = null;
                error = failure.Message;
            }
        }

        try { await store.RecordWebhookResultAsync(delivery.Target.Id, status, error, CancellationToken.None); }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            Console.Error.WriteLine("WEBHOOK: could not record a delivery result: " + failure.Message);
        }
    }

    /// <summary>
    /// The signature a receiver checks: <c>sha256=</c> and the lowercase hex HMAC of the exact bytes sent.
    /// Over the body rather than over fields, so a receiver verifies what it actually read.
    /// </summary>
    public static string Sign(string secret, string payload) =>
        "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        pending.Release();
        try { await worker; }
        catch (OperationCanceledException) { }
        client.Dispose();
        stopping.Dispose();
        pending.Dispose();
    }

    private sealed record Delivery(WebhookTarget Target, string Event, string Payload);
}
