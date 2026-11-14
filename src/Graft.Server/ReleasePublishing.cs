using System.Security.Cryptography;
using Graft.Core;
using Graft.Storage;

namespace Graft.Server;

/// <summary>
/// Publishes releases to their destinations (ADR 0052), out of the way of the request that asked.
///
/// <para>
/// A publication sends files that may be large to a server that then validates them at its own pace, so the
/// request that starts one returns at once and the work happens here, one publication at a time. The queue is
/// the database: a publication survives a restart as a row, a queued one is taken up again, and one that was
/// running is marked as interrupted, because the store keeps what it was sent and the next attempt carries on.
/// </para>
///
/// <para>
/// The endpoint and the key are unprotected here and nowhere else, held for the length of one publication and
/// never written to the log. What the log says about a publication is its number and how it ended.
/// </para>
/// </summary>
public sealed class ReleasePublishingWorker : IAsyncDisposable
{
    private readonly GraftStore store;
    private readonly IReleasePublisher publisher;
    private readonly Func<string, string> unprotect;
    private readonly Func<long, CancellationToken, Task<string?>> repositoryRootOf;
    private readonly bool allowHttp;
    private readonly Action<string> log;
    private readonly SemaphoreSlim pending = new(0);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task worker;

    /// <param name="unprotect">Turns a stored endpoint or key back into what was entered.</param>
    /// <param name="repositoryRootOf">The directory a project's repository and its files live in, or null when the project is gone.</param>
    /// <param name="allowHttp">Whether the operator allowed any address, which is what permits a plain http endpoint.</param>
    public ReleasePublishingWorker(GraftStore store, IReleasePublisher publisher, Func<string, string> unprotect,
        Func<long, CancellationToken, Task<string?>> repositoryRootOf, bool allowHttp, Action<string> log)
    {
        this.store = store;
        this.publisher = publisher;
        this.unprotect = unprotect;
        this.repositoryRootOf = repositoryRootOf;
        this.allowHttp = allowHttp;
        this.log = log;
        worker = Task.Run(RunAsync);
    }

    /// <summary>Says a publication was queued. Returns without waiting.</summary>
    public void Wake() => pending.Release();

    private async Task RunAsync()
    {
        try
        {
            int interrupted = await store.FailInterruptedReleasePublicationsAsync(stopping.Token);
            if (interrupted > 0) log(interrupted + " publication(s) were under way when the server last stopped, and are marked as interrupted.");
        }
        catch (Exception error) when (error is not OperationCanceledException) { log("could not read the queue at start-up: " + error.GetType().Name); }

        while (!stopping.IsCancellationRequested)
        {
            ReleasePublicationJob? job = null;
            try { job = await store.ClaimReleasePublicationAsync(stopping.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { log("could not read the queue: " + error.GetType().Name); }
            if (job is null)
            {
                // Woken when something is queued, and once a minute regardless, so a row queued by a request that
                // failed before it could wake this is still found.
                try { await pending.WaitAsync(TimeSpan.FromMinutes(1), stopping.Token); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            PublicationReport report;
            try { report = await PublishAsync(job, stopping.Token); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                // The type and not the message: this is the one failure nothing has redacted.
                log("publication " + job.Id + " failed inside Graft: " + error.GetType().Name);
                report = new(false, "Graft failed while publishing. Nothing more is known here; the server log names the kind of failure.", [], null);
            }
            try
            {
                await store.CompleteReleasePublicationAsync(job.Id, report, CancellationToken.None);
                log("publication " + job.Id + " of project " + job.ProjectId + (job.DryRun ? " (dry run) " : " ") + (report.Succeeded ? "succeeded" : "failed"));
            }
            catch (Exception error) { log("could not record how publication " + job.Id + " ended: " + error.GetType().Name); }
        }
    }

    private async Task<PublicationReport> PublishAsync(ReleasePublicationJob job, CancellationToken cancellationToken)
    {
        var inputs = await store.ReadReleasePublicationInputsAsync(job, cancellationToken);
        string? root = await repositoryRootOf(job.ProjectId, cancellationToken);
        if (inputs is null || root is null) return new(false, "The release, the destination or the project no longer exists.", [], null);
        ReleaseDestination destination = inputs.Value.Destination.Destination;
        Release release = inputs.Value.Release;

        string endpointText, key;
        try
        {
            endpointText = unprotect(inputs.Value.Destination.ProtectedEndpoint);
            key = unprotect(inputs.Value.Destination.ProtectedKey);
        }
        catch (CryptographicException)
        {
            return new(false, "The stored endpoint and key can no longer be read, which happens when the server's key ring is replaced. Enter them again in the destination's settings.", [], null);
        }
        Uri? endpoint = ReleaseDestinations.ParseEndpoint(endpointText, allowHttp, out string problem);
        if (endpoint is null) return new(false, "The stored endpoint is not one this server publishes to. " + problem, [], null);

        ProjectFiles files = new(root);
        List<PublishArtifact> artifacts = [];
        foreach (ReleaseAsset asset in release.Assets)
        {
            ArtifactDeclaration? declaration = ReleaseDestinations.Declare(asset.FileName, destination.Rules);
            if (declaration is null) continue;
            StoredReleaseAsset? stored = await store.ReadReleaseAssetForPublicationAsync(job.ProjectId, release.TagName, asset.FileName, cancellationToken);
            if (stored is null) continue;
            Func<Stream> open;
            if (stored.Stored is { } name)
            {
                string path = files.ReleaseFilePath(name);
                if (!File.Exists(path)) return new(false, "The file '" + asset.FileName + "' is missing from this server's storage.", [], null);
                open = () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            }
            else
            {
                byte[] bytes = stored.Bytes ?? [];
                open = () => new MemoryStream(bytes, writable: false);
            }
            artifacts.Add(new(asset.FileName, asset.Size, asset.Sha256, declaration, open));
        }

        PublishTarget target = new(endpoint, destination.ApplicationId, key, destination.Channel, destination.AllowProductionPromotion, destination.PackageName);
        PublishRelease what = new(Path.GetFileName(root), release.TagName, ReleaseDestinations.VersionOf(release.TagName), release.Description);
        return await publisher.PublishAsync(target, what, artifacts, job.DryRun,
            (stage, outcomes) => store.UpdateReleasePublicationAsync(job.Id, stage, outcomes, CancellationToken.None), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        try { await worker; }
        catch (OperationCanceledException) { }
        stopping.Dispose();
        pending.Dispose();
    }
}
