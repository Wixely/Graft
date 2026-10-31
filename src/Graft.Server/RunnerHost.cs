using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using CupriNet.Abstractions;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Persistence;
using CupriNet.Rites;
using CupriNet.Vessel;
using Graft.Contracts;
using Graft.Core;
using Protocol = Graft.RunnerProtocol.RunnerProtocol;
using Graft.Storage;
using AspNetProtector = Microsoft.AspNetCore.DataProtection.IDataProtector;

namespace Graft.Server;

/// <summary>Where runners reach the forge, and what it tells them about itself.</summary>
internal sealed record RunnerHostOptions(IPAddress ListenAddress, int Port, string AdvertisedHost, string SecretDirectory, AspNetProtector Protector);

/// <summary>CupriNet keeps its secrets through an interface of its own; this hands it the forge's key ring.</summary>
internal sealed class KeyRingProtector(AspNetProtector protector) : CupriNet.Abstractions.IDataProtector
{
    public byte[] Protect(ReadOnlySpan<byte> plaintext) => protector.Protect(plaintext.ToArray());
    public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => protector.Unprotect(protectedData.ToArray());
}

/// <summary>
/// The forge's side of the runner protocol: a CupriNet Shrine on a port of its own, which runners reach from the
/// Intonation in their install script.
///
/// <para>
/// The Shrine's <c>cupri1…</c> address is a key kept beside the database, so a runner that pinned it at
/// registration keeps reaching the same forge across restarts and moves, and nothing else can answer as it. The
/// CupriNet node behind it does nothing but answer those visits: its overlay listener is bound to loopback and
/// never advertised, and gossip, decoy traffic, peer exchange and address discovery are off. A visitor is
/// anonymous to CupriNet; who it is and what it may do is decided here, from the credential it presents, on every
/// request (ADR 0037).
/// </para>
/// </summary>
internal sealed class RunnerHost : IAsyncDisposable
{
    /// <summary>The largest source archive handed to a runner. A larger one fails the job, saying so.</summary>
    public const long MaximumArchiveBytes = 64L * 1024 * 1024;

    /// <summary>How long a runner's request for work waits for one to appear before answering that there is none.</summary>
    private static readonly TimeSpan LongPoll = TimeSpan.FromSeconds(20);

    private readonly GraftStore store;
    private readonly Func<CiLease, CancellationToken, Task<byte[]?>> archive;
    private readonly Action<string, Exception?> log;
    private readonly BouncyCastleSuite suite = new();
    private readonly JobArchives archives;
    private readonly CancellationTokenSource stopping = new();
    private VesselListener? listener;
    private CupriNode? node;
    private Signet? signet;
    private TaskCompletionSource pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object pulseGate = new();

    private RunnerHost(GraftStore store, Func<CiLease, CancellationToken, Task<byte[]?>> archive, Action<string, Exception?> log)
    {
        this.store = store;
        this.archive = archive;
        this.log = log;
        archives = new(suite);
    }

    public int Port => listener?.LocalEndPoint.Port ?? 0;
    public string ShrineAddress => signet?.Address ?? "";

    public static async Task<RunnerHost> StartAsync(RunnerHostOptions options, GraftStore store,
        Func<CiLease, CancellationToken, Task<byte[]?>> archive, Action<string, Exception?> log)
    {
        RunnerHost host = new(store, archive, log);
        FileSecretStore secrets = new(options.SecretDirectory, new KeyRingProtector(options.Protector));
        host.signet = await new SignetStore(secrets).LoadOrCreateAsync(host.suite, "graft-runner-shrine");
        host.listener = new VesselListener(new IPEndPoint(options.ListenAddress, options.Port));
        host.listener.Start();
        host.node = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = Protocol.Network,
            ListenAddress = IPAddress.Loopback,
            ListenPort = 0,
            Suite = host.suite,
            SecretStore = secrets,
            AdvertisedBeacons = [new Beacon(EndpointKind.Manual, options.AdvertisedHost, host.listener.LocalEndPoint.Port)],
            // The operator named this host; it is advertised as given, even when it is a private address.
            AdvertiseLocalAddresses = true,
            PersistOverlay = false,
            EnableOverlayGossip = false,
            EnableHotFuzz = false,
            EnablePeerExchange = false,
            EnableReflexiveDiscovery = false,
            Moniker = "Graft",
        });
        host.node.HostShrine(host.signet, new DelegateOracleHandler(host.HandleAsync), feeds: null, relics: host.archives);
        _ = host.AcceptAsync(host.listener, host.stopping.Token);
        return host;
    }

    /// <summary>A link to this Shrine that stays valid for <paramref name="lifetime"/>, for one install script.</summary>
    public string Intone(TimeSpan lifetime) =>
        IntonationUri.ToUri(node!.Intone(lifetime, DateTimeOffset.UtcNow, signet!, LinkTransports.ClearnetOnly));

    /// <summary>Wakes runners waiting for work, because a pipeline was just created.</summary>
    public void Notify()
    {
        lock (pulseGate)
        {
            pulse.TrySetResult();
            pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private async Task AcceptAsync(VesselListener vessels, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IVessel vessel;
            try { vessel = await vessels.AcceptAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception exception) { log("runner listener accept failed", exception); continue; }
            _ = VisitAsync(vessel, cancellationToken);
        }
    }

    private async Task VisitAsync(IVessel vessel, CancellationToken cancellationToken)
    {
        try { await node!.AcceptPilgrimageOverVesselAsync(vessel, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { log("runner visit ended: " + exception.Message, null); }
    }

    // ------------------------------------------------------------------------------------------------
    // The protocol
    // ------------------------------------------------------------------------------------------------

    private async Task<OracleResponse> HandleAsync(OracleRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.Method != "POST") return Error(405, "runner requests are POST");
            ushort? version = Protocol.Negotiate(request.Header(Protocol.VersionHeader), out string reason);
            if (version is null) return Error(426, reason);
            if (request.Path == Protocol.Routes.Register) return await RegisterAsync(request, version.Value, cancellationToken);
            if (request.Path == Protocol.Routes.RequestJob) return await RequestJobAsync(request, cancellationToken);
            if (request.Path.StartsWith(Protocol.Routes.JobPrefix, StringComparison.Ordinal))
            {
                string[] parts = request.Path[Protocol.Routes.JobPrefix.Length..].Split('/');
                if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long jobId))
                {
                    if (parts[1] == "trace") return await TraceAsync(request, jobId, cancellationToken);
                    if (parts[1] == "finish") return await FinishAsync(request, jobId, cancellationToken);
                }
            }
            return Error(404, "no such runner route");
        }
        catch (JsonException) { return Error(400, "the request body is not valid JSON"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            log("runner request " + request.Path + " failed", exception);
            return Error(500, "the forge could not complete the request");
        }
    }

    private async Task<OracleResponse> RegisterAsync(OracleRequest request, ushort version, CancellationToken cancellationToken)
    {
        RunnerRegisterRequest? body = JsonSerializer.Deserialize(request.Body, ApiJson.Default.RunnerRegisterRequest);
        if (body?.Token is not { Length: > 0 }) return Error(400, "token is required");
        (RunnerInfo Runner, string Credential)? claimed = await store.ClaimRunnerAsync(body.Token, body.Name, body.Platform, body.Version, cancellationToken);
        if (claimed is null) return Error(403, "the registration token is not valid; it may have been used or have expired");
        log("runner " + claimed.Value.Runner.Id.ToString(CultureInfo.InvariantCulture) + " registered as " + (claimed.Value.Runner.Name ?? "?"), null);
        return Json(201, JsonSerializer.SerializeToUtf8Bytes(new RunnerRegisterResponse(claimed.Value.Runner.Id, claimed.Value.Credential, version),
            ApiJson.Default.RunnerRegisterResponse));
    }

    private async Task<OracleResponse> RequestJobAsync(OracleRequest request, CancellationToken cancellationToken)
    {
        RunnerInfo? runner = await store.ResolveRunnerAsync(request.Header(Protocol.AuthorizationHeader) ?? "", cancellationToken);
        if (runner is null) return Error(401, "the runner credential is not valid; the runner may have been removed");
        DateTimeOffset until = DateTimeOffset.UtcNow + LongPoll;
        while (true)
        {
            Task wake;
            lock (pulseGate) wake = pulse.Task;
            CiLease? lease = await store.LeaseJobAsync(runner.Id, cancellationToken);
            if (lease is not null)
            {
                OracleResponse? handed = await HandOverAsync(lease, cancellationToken);
                if (handed is not null) return handed;
                continue;   // the job failed on the server's side; look for another
            }
            TimeSpan left = until - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) return new OracleResponse { Status = 204 };
            try { await wake.WaitAsync(left, cancellationToken); }
            catch (TimeoutException) { return new OracleResponse { Status = 204 }; }
        }
    }

    /// <summary>Builds the job's source archive and offers it as a relic; null when the job had to be failed instead.</summary>
    private async Task<OracleResponse?> HandOverAsync(CiLease lease, CancellationToken cancellationToken)
    {
        byte[]? bytes;
        try { bytes = await archive(lease, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log("archive for job " + lease.Job.Id.ToString(CultureInfo.InvariantCulture) + " failed", exception);
            bytes = null;
        }
        if (bytes is null)
        {
            await store.FailLeasedJobAsync(lease.Job.Id, "The forge could not read commit " + lease.Pipeline.Sha + " to send it to the runner.", cancellationToken);
            return null;
        }
        if (bytes.Length > MaximumArchiveBytes)
        {
            await store.FailLeasedJobAsync(lease.Job.Id, "The source at " + lease.Pipeline.Sha[..8] + " is " + (bytes.Length / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) +
                " MiB compressed, past the 64 MiB a runner is sent.", cancellationToken);
            return null;
        }
        string relic = Protocol.ArchiveRelic(lease.Job.Id);
        archives.Add(relic, bytes);
        RunnerJobResponse response = new(lease.Job.Id, lease.JobToken, lease.Job.Name, lease.Job.Stage,
            lease.Project.Namespace + "/" + lease.Project.Slug, lease.Pipeline.Ref, lease.Pipeline.Sha, [.. lease.Script],
            new Dictionary<string, string>(lease.Variables, StringComparer.Ordinal), relic, bytes.Length);
        return Json(200, JsonSerializer.SerializeToUtf8Bytes(response, ApiJson.Default.RunnerJobResponse));
    }

    private async Task<OracleResponse> TraceAsync(OracleRequest request, long jobId, CancellationToken cancellationToken)
    {
        RunnerTraceRequest? body = JsonSerializer.Deserialize(request.Body, ApiJson.Default.RunnerTraceRequest);
        if (body is null || body.Offset < 0 || body.Text is null) return Error(400, "offset and text are required");
        GraftStore.TraceAppend result = await store.AppendTraceAsync(jobId, request.Header(Protocol.AuthorizationHeader) ?? "",
            body.Offset, body.Text, cancellationToken);
        return result switch
        {
            GraftStore.TraceAppend.Accepted or GraftStore.TraceAppend.Duplicate => new OracleResponse { Status = 202 },
            GraftStore.TraceAppend.Conflict => Error(409, "the log does not end at that offset"),
            _ => Error(401, "the job token is not valid for a running job")
        };
    }

    private async Task<OracleResponse> FinishAsync(OracleRequest request, long jobId, CancellationToken cancellationToken)
    {
        RunnerFinishRequest? body = JsonSerializer.Deserialize(request.Body, ApiJson.Default.RunnerFinishRequest);
        if (body?.Status is not ("success" or "failed")) return Error(400, "status must be success or failed");
        bool done = await store.FinishJobAsync(jobId, request.Header(Protocol.AuthorizationHeader) ?? "",
            body.Status == "success", body.ExitCode, body.Failure, cancellationToken);
        archives.Remove(Protocol.ArchiveRelic(jobId));
        if (!done) return Error(401, "the job token is not valid for a running job");
        Notify();   // the next stage may now be ready
        return new OracleResponse { Status = 200 };
    }

    private static OracleResponse Json(uint status, byte[] body) => new()
    {
        Status = status,
        Headers = [new OracleHeader("content-type", "application/json")],
        Body = body,
    };

    private static OracleResponse Error(uint status, string message) =>
        Json(status, JsonSerializer.SerializeToUtf8Bytes(new ErrorResponse(message), ApiJson.Default.ErrorResponse));

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        if (listener is not null) await listener.DisposeAsync();
        if (node is not null) await node.DisposeAsync();
        stopping.Dispose();
    }

    /// <summary>
    /// The source archives of jobs being run, one relic each, dropped when the job finishes. CupriNet's own
    /// static source never forgets a relic, so each archive lives in a source of its own behind this one.
    /// </summary>
    private sealed class JobArchives(BouncyCastleSuite suite) : IRelicSource
    {
        private readonly ConcurrentDictionary<string, StaticRelicSource> relics = new(StringComparer.Ordinal);

        public void Add(string name, byte[] content) => relics[name] = new StaticRelicSource(suite).Add(name, content);

        public void Remove(string name) => relics.TryRemove(name, out _);

        public ValueTask<ReliquaryManifest?> ResolveAsync(string name, CancellationToken cancellationToken = default) =>
            relics.TryGetValue(name, out StaticRelicSource? source) ? source.ResolveAsync(name, cancellationToken) : ValueTask.FromResult<ReliquaryManifest?>(null);

        public ValueTask<byte[]?> ReadChunkAsync(string name, int fileIndex, int chunkIndex, CancellationToken cancellationToken = default) =>
            relics.TryGetValue(name, out StaticRelicSource? source) ? source.ReadChunkAsync(name, fileIndex, chunkIndex, cancellationToken) : ValueTask.FromResult<byte[]?>(null);
    }
}
