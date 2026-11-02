using System.Globalization;
using Graft.Core;
using Graft.Storage;

namespace Graft.Server;

/// <summary>What one update of a mirror did, in a sentence for the log.</summary>
public sealed record MirrorOutcome(bool Changed, string Summary);

/// <summary>
/// Imports projects from their addresses and keeps the ones that asked for it up to date (ADR 0042).
///
/// <para>
/// One update at a time, on a worker of its own, so a slow or enormous upstream delays other mirrors and nothing else.
/// The schedule is in the database: the worker claims whatever is due, records the result with the time of the next
/// attempt, and sleeps until the earliest one or until somebody asks for an update. A restart loses nothing; an update
/// it interrupted runs again.
/// </para>
/// </summary>
public sealed class MirrorWorker : IAsyncDisposable
{
    /// <summary>The longest one update may take, transfer included, before it is abandoned and recorded as failed.</summary>
    public static readonly TimeSpan UpdateTimeout = TimeSpan.FromHours(2);

    /// <summary>How long the worker sleeps when nothing is due, at most: a backstop for a clock change or a missed wake.</summary>
    private static readonly TimeSpan IdleCheck = TimeSpan.FromMinutes(5);

    private readonly GraftStore store;
    private readonly Func<MirrorJob, CancellationToken, Task<MirrorOutcome>> update;
    private readonly TimeSpan interval;
    private readonly Action<string> log;
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly CancellationTokenSource stopping = new();
    private Task worker = Task.CompletedTask;

    private MirrorWorker(GraftStore store, TimeSpan interval, Func<MirrorJob, CancellationToken, Task<MirrorOutcome>> update, Action<string> log)
    {
        this.store = store;
        this.interval = interval;
        this.update = update;
        this.log = log;
    }

    /// <summary>The time between updates of a mirror that is kept up to date.</summary>
    public TimeSpan Interval => interval;

    public static async Task<MirrorWorker> StartAsync(GraftStore store, TimeSpan interval,
        Func<MirrorJob, CancellationToken, Task<MirrorOutcome>> update, Action<string> log)
    {
        MirrorWorker mirrors = new(store, interval, update, log);
        await store.ResetStartedMirrorsAsync();
        mirrors.worker = Task.Run(mirrors.RunAsync);
        return mirrors;
    }

    /// <summary>Wakes the worker to look for due work now, after an import was asked for or an update requested.</summary>
    public void Poke()
    {
        try { wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task RunAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                while (!stopping.IsCancellationRequested && await store.ClaimDueMirrorAsync(DateTimeOffset.UtcNow, stopping.Token) is { } job)
                    await UpdateAsync(job);
                DateTimeOffset? next = await store.NextMirrorDueAsync(stopping.Token);
                TimeSpan delay = next is null ? IdleCheck : next.Value - DateTimeOffset.UtcNow;
                if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
                if (delay > IdleCheck) delay = IdleCheck;
                await wake.WaitAsync(delay, stopping.Token);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                log("could not read the mirror schedule: " + error.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(30), stopping.Token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task UpdateAsync(MirrorJob job)
    {
        string project = "project " + job.ProjectId.ToString(CultureInfo.InvariantCulture);
        bool success = false;
        bool changed = false;
        string? error = null;
        using (CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
        {
            limit.CancelAfter(UpdateTimeout);
            try
            {
                MirrorOutcome outcome = await update(job, limit.Token);
                success = true;
                changed = outcome.Changed;
                log(project + (job.FirstImport ? " imported from " : " updated from ") + job.Url + ": " + outcome.Summary);
            }
            // A server that is stopping leaves the update started; the next start puts it back in the queue.
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { error = "the update took longer than " + UpdateTimeout.TotalHours.ToString(CultureInfo.InvariantCulture) + " hours and was abandoned"; }
            catch (Exception failure) { error = failure.Message; }
        }
        if (!success) log(project + (job.FirstImport ? " could not be imported from " : " could not be updated from ") + job.Url + ": " + error);
        try { await store.RecordMirrorResultAsync(job.ProjectId, success, changed, error, interval, DateTimeOffset.UtcNow, CancellationToken.None); }
        catch (Exception failure) { log(project + ": could not record the result: " + failure.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        try { await worker; }
        catch (OperationCanceledException) { }
        stopping.Dispose();
        wake.Dispose();
    }
}
