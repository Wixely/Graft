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
using Graft.RunnerProtocol;
using Graft.Storage;
using AspNetProtector = Microsoft.AspNetCore.DataProtection.IDataProtector;
using Protocol = Graft.RunnerProtocol.RunnerProtocol;

namespace Graft.Server;

/// <summary>
/// Where runners reach the forge, what it tells them about itself, and how it moves files: the chunking threshold
/// (-1 for off) and chunk size the operator chose, a directory for transfers in progress, and where a job's
/// artifacts archive is kept.
/// </summary>
internal sealed record RunnerHostOptions(
    IPAddress ListenAddress,
    int Port,
    string AdvertisedHost,
    string SecretDirectory,
    AspNetProtector Protector,
    long ChunkThresholdBytes,
    long ChunkBytes,
    string TransferDirectory,
    Func<ProjectInfo, long, string> ArtifactPath);

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
///
/// <para>
/// Files move as <see cref="Transfers"/> describes: whole at or below the operator's threshold, in verified chunks
/// above it. A job's source is staged on disk and offered as one relic, or one relic per chunk read from disk when
/// asked for. An artifacts upload is a session with a journal beside its data, so what a runner has had committed
/// survives a dropped connection and a forge restart, and the runner resumes from it (ADR 0038).
/// </para>
/// </summary>
internal sealed class RunnerHost : IAsyncDisposable
{
    /// <summary>How long a runner's request for work waits for one to appear before answering that there is none.</summary>
    private static readonly TimeSpan LongPoll = TimeSpan.FromSeconds(20);

    private readonly GraftStore store;
    private readonly RunnerHostOptions options;
    private readonly Func<CiLease, Stream, CancellationToken, Task<bool>> archive;
    private readonly Action<string, Exception?> log;
    private readonly BouncyCastleSuite suite = new();
    private readonly JobArchives archives;
    private readonly ConcurrentDictionary<string, Upload> uploads = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();
    private VesselListener? listener;
    private CupriNode? node;
    private Signet? signet;
    private TaskCompletionSource pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object pulseGate = new();

    private RunnerHost(GraftStore store, RunnerHostOptions options, Func<CiLease, Stream, CancellationToken, Task<bool>> archive, Action<string, Exception?> log)
    {
        this.store = store;
        this.options = options;
        this.archive = archive;
        this.log = log;
        archives = new(suite, options.ChunkBytes);
    }

    public int Port => listener?.LocalEndPoint.Port ?? 0;
    public string ShrineAddress => signet?.Address ?? "";
    public TransferPolicy Policy => new(options.ChunkThresholdBytes, options.ChunkBytes);

    private string SourceDirectory => Path.Combine(options.TransferDirectory, "sources");
    private string UploadDirectory => Path.Combine(options.TransferDirectory, "uploads");

    public static async Task<RunnerHost> StartAsync(RunnerHostOptions options, GraftStore store,
        Func<CiLease, Stream, CancellationToken, Task<bool>> archive, Action<string, Exception?> log)
    {
        RunnerHost host = new(store, options, archive, log);
        // Sources are rebuilt on demand, so whatever a previous run left is garbage; uploads are kept, because a
        // runner may come back to finish one.
        if (Directory.Exists(host.SourceDirectory)) Directory.Delete(host.SourceDirectory, recursive: true);
        Directory.CreateDirectory(host.SourceDirectory);
        Directory.CreateDirectory(host.UploadDirectory);
        // A job runs for an hour at most, so an upload untouched for a day belongs to a job that is over.
        foreach (string stale in Directory.EnumerateFiles(host.UploadDirectory))
            if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddDays(-1)) try { File.Delete(stale); } catch (IOException) { }
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
                if (parts.Length >= 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long jobId))
                {
                    string token = request.Header(Protocol.AuthorizationHeader) ?? "";
                    if (parts.Length == 2 && parts[1] == "trace") return await TraceAsync(request, jobId, token, cancellationToken);
                    if (parts.Length == 2 && parts[1] == "finish") return await FinishAsync(request, jobId, token, cancellationToken);
                    if (parts[1] == "artifacts")
                    {
                        if (parts.Length == 2) return await StartUploadAsync(request, jobId, token, cancellationToken);
                        if (parts.Length == 4) return await UploadStepAsync(request, jobId, token, parts[2], parts[3], cancellationToken);
                    }
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

    /// <summary>
    /// Stages the job's source on disk and offers it whole or in chunks, as the threshold says; null when the job
    /// had to be failed instead.
    /// </summary>
    private async Task<OracleResponse?> HandOverAsync(CiLease lease, CancellationToken cancellationToken)
    {
        string relic = Protocol.ArchiveRelic(lease.Job.Id);
        string path = Path.Combine(SourceDirectory, relic);
        bool written;
        try
        {
            await using (FileStream file = new(path, FileMode.Create, FileAccess.Write, FileShare.None))
                written = await archive(lease, file, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log("archive for job " + lease.Job.Id.ToString(CultureInfo.InvariantCulture) + " failed", exception);
            written = false;
        }
        string? failure = null;
        long size = written ? new FileInfo(path).Length : 0;
        bool chunked = Transfers.IsChunked(size, options.ChunkThresholdBytes);
        if (!written) failure = "The forge could not read commit " + lease.Pipeline.Sha + " to send it to the runner.";
        else if (size > Transfers.MaximumFileBytes) failure = "The source at " + lease.Pipeline.Sha[..8] + " is larger than the 4 GiB a runner is sent.";
        else if (!chunked && size > Transfers.MaximumWholeBytes)
            failure = "The source at " + lease.Pipeline.Sha[..8] + " is " + Mebibytes(size) + " MiB, past the 256 MiB a whole transfer carries, " +
                "and this forge has chunking turned off (--runner-chunk-threshold off).";
        if (failure is not null)
        {
            File.Delete(path);
            await store.FailLeasedJobAsync(lease.Job.Id, failure, cancellationToken);
            return null;
        }

        string sha256;
        await using (FileStream file = File.OpenRead(path)) sha256 = await Transfers.Sha256HexAsync(file, cancellationToken);
        TransferDescriptor descriptor;
        if (chunked)
        {
            string[] hashes = await Transfers.ChunkHashesAsync(path, options.ChunkBytes, cancellationToken);
            archives.AddChunked(relic, path, size);
            descriptor = new(relic, size, sha256, true, options.ChunkBytes, hashes);
        }
        else
        {
            archives.AddWhole(relic, await File.ReadAllBytesAsync(path, cancellationToken));
            File.Delete(path);
            descriptor = new(relic, size, sha256, false, options.ChunkBytes, []);
        }
        RunnerJobResponse response = new(lease.Job.Id, lease.JobToken, lease.Job.Name, lease.Job.Stage,
            lease.Project.Namespace + "/" + lease.Project.Slug, lease.Pipeline.Ref, lease.Pipeline.Sha, [.. lease.Script],
            new Dictionary<string, string>(lease.Variables, StringComparer.Ordinal), descriptor, [.. lease.ArtifactPaths], Policy);
        return Json(200, JsonSerializer.SerializeToUtf8Bytes(response, ApiJson.Default.RunnerJobResponse));
    }

    private async Task<OracleResponse> TraceAsync(OracleRequest request, long jobId, string token, CancellationToken cancellationToken)
    {
        RunnerTraceRequest? body = JsonSerializer.Deserialize(request.Body, ApiJson.Default.RunnerTraceRequest);
        if (body is null || body.Offset < 0 || body.Text is null) return Error(400, "offset and text are required");
        GraftStore.TraceAppend result = await store.AppendTraceAsync(jobId, token, body.Offset, body.Text, cancellationToken);
        return result switch
        {
            GraftStore.TraceAppend.Accepted or GraftStore.TraceAppend.Duplicate => new OracleResponse { Status = 202 },
            GraftStore.TraceAppend.Conflict => Error(409, "the log does not end at that offset"),
            _ => Error(401, "the job token is not valid for a running job")
        };
    }

    private async Task<OracleResponse> FinishAsync(OracleRequest request, long jobId, string token, CancellationToken cancellationToken)
    {
        RunnerFinishRequest? body = JsonSerializer.Deserialize(request.Body, ApiJson.Default.RunnerFinishRequest);
        if (body?.Status is not ("success" or "failed")) return Error(400, "status must be success or failed");
        bool done = await store.FinishJobAsync(jobId, token, body.Status == "success", body.ExitCode, body.Failure, cancellationToken);
        archives.Remove(Protocol.ArchiveRelic(jobId));
        if (!done) return Error(401, "the job token is not valid for a running job");
        foreach (Upload abandoned in uploads.Values.Where(upload => upload.JobId == jobId).ToList()) Discard(abandoned);
        Notify();   // the next stage may now be ready
        return new OracleResponse { Status = 200 };
    }

    // ------------------------------------------------------------------------------------------------
    // Artifacts uploads
    // ------------------------------------------------------------------------------------------------

    private async Task<OracleResponse> StartUploadAsync(OracleRequest request, long jobId, string token, CancellationToken cancellationToken)
    {
        if (!await store.IsRunningJobTokenAsync(jobId, token, cancellationToken)) return Error(401, "the job token is not valid for a running job");
        ArtifactUploadRequest? body = JsonSerializer.Deserialize(request.Body, ApiJson.Default.ArtifactUploadRequest);
        if (body is null || body.Size < 0 || body.Sha256 is not { Length: 64 } || !body.Sha256.All(Uri.IsHexDigit))
            return Error(400, "size and a SHA-256 are required");
        if (body.Size > Transfers.MaximumFileBytes) return Error(413, "the artifacts are larger than the 4 GiB a forge keeps for a job");
        bool chunked = Transfers.IsChunked(body.Size, options.ChunkThresholdBytes);
        if (!chunked && body.Size > Transfers.MaximumWholeBytes)
            return Error(413, "the artifacts are " + Mebibytes(body.Size) + " MiB, past the 256 MiB a whole transfer carries, and this forge has chunking turned off");
        string sha = body.Sha256.ToLowerInvariant();

        // The same job sending the same file again is the same upload: a chunked one resumes from what was
        // committed; a whole one starts again from nothing, as a whole transfer does after any interruption.
        Upload? existing = uploads.Values.FirstOrDefault(upload => upload.JobId == jobId && upload.Size == body.Size && upload.Sha256 == sha)
            ?? LoadJournals().FirstOrDefault(upload => upload.JobId == jobId && upload.Size == body.Size && upload.Sha256 == sha);
        Upload upload;
        if (existing is not null && existing.Chunked == chunked && existing.ChunkBytes == options.ChunkBytes)
        {
            upload = existing;
            uploads[upload.Id] = upload;
            lock (upload) upload.Rewind(chunked ? upload.Committed : 0);
        }
        else
        {
            foreach (Upload other in uploads.Values.Where(other => other.JobId == jobId).ToList()) Discard(other);
            upload = new(Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)), jobId, body.Size, sha,
                chunked, options.ChunkBytes, UploadDirectory);
            uploads[upload.Id] = upload;
            lock (upload) upload.Rewind(0);
        }
        return Status(upload);
    }

    private async Task<OracleResponse> UploadStepAsync(OracleRequest request, long jobId, string token, string uploadId, string step, CancellationToken cancellationToken)
    {
        if (!await store.IsRunningJobTokenAsync(jobId, token, cancellationToken)) return Error(401, "the job token is not valid for a running job");
        if (!uploads.TryGetValue(uploadId, out Upload? upload) || upload.JobId != jobId)
        {
            upload = LoadJournals().FirstOrDefault(found => found.Id == uploadId && found.JobId == jobId);
            if (upload is null) return Error(404, "no such upload; start it again");
            uploads[upload.Id] = upload;
        }
        switch (step)
        {
            case "data":
            {
                if (!long.TryParse(request.Header(Transfers.OffsetHeader), NumberStyles.None, CultureInfo.InvariantCulture, out long offset))
                    return Error(400, "the piece's offset is required");
                if (request.Body.Length is 0 or > Transfers.PieceBytes) return Error(413, "a piece carries between 1 byte and 160 KiB");
                lock (upload)
                {
                    if (offset + request.Body.Length <= upload.Received) return Status(upload);   // a retry of a piece already written
                    if (offset != upload.Received) return Error(409, "the upload does not continue at that offset", upload);
                    if (offset + request.Body.Length > upload.Size) return Error(400, "the piece runs past the size declared");
                    if (upload.Chunked)
                    {
                        int index = (int)(upload.Committed / upload.ChunkBytes);
                        long chunkEnd = upload.Committed + Transfers.ChunkLength(upload.Size, upload.ChunkBytes, index);
                        if (offset + request.Body.Length > chunkEnd) return Error(400, "a piece may not cross into a chunk before the previous one is committed");
                    }
                    upload.Write(offset, request.Body);
                }
                return Status(upload);
            }
            case "commit":
            {
                ArtifactChunkCommit? body = JsonSerializer.Deserialize(request.Body, ApiJson.Default.ArtifactChunkCommit);
                if (!upload.Chunked) return Error(400, "a whole upload has no chunks to commit");
                if (body?.Sha256 is not { Length: 64 }) return Error(400, "the chunk's index and SHA-256 are required");
                lock (upload)
                {
                    int index = (int)(upload.Committed / upload.ChunkBytes);
                    if (upload.Committed == upload.Size || body.Index < index) return Status(upload);   // already committed
                    if (body.Index != index) return Error(409, "the next chunk to commit is " + index, upload);
                    long length = Transfers.ChunkLength(upload.Size, upload.ChunkBytes, index);
                    if (upload.Received != upload.Committed + length) return Error(409, "chunk " + index + " is not all here yet", upload);
                    if (upload.HashRange(upload.Committed, length) != body.Sha256.ToLowerInvariant())
                    {
                        upload.Rewind(upload.Committed);
                        return Error(409, "chunk " + index + " does not match its SHA-256; send it again", upload);
                    }
                    upload.Commit(upload.Received);
                }
                return Status(upload);
            }
            case "status":
            {
                // A chunked upload resumes from its last committed chunk; anything after it is discarded.
                lock (upload) if (upload.Chunked) upload.Rewind(upload.Committed);
                return Status(upload);
            }
            case "complete":
            {
                string path;
                lock (upload)
                {
                    if (upload.Received != upload.Size || (upload.Chunked && upload.Committed != upload.Size))
                        return Error(409, "the upload is not all here yet", upload);
                    if (upload.HashRange(0, upload.Size) != upload.Sha256)
                    {
                        upload.Rewind(0);
                        return Error(409, "the artifacts do not match their SHA-256; send them again", upload);
                    }
                    upload.Close();
                    path = upload.DataPath;
                }
                long? projectId = await store.FindJobProjectIdAsync(jobId, cancellationToken);
                ProjectDetails? project = projectId is null ? null : await store.GetReadyProjectAsync(projectId.Value, cancellationToken);
                if (project is null) { Discard(upload); return Error(404, "the job's project is gone"); }
                string destination = options.ArtifactPath(project.Project, jobId);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(path, destination, overwrite: true);
                if (!await store.RecordArtifactsAsync(jobId, token, upload.Size, upload.Sha256, cancellationToken))
                {
                    File.Delete(destination);
                    Discard(upload);
                    return Error(401, "the job token is not valid for a running job");
                }
                Discard(upload);
                log("job " + jobId.ToString(CultureInfo.InvariantCulture) + " kept " + upload.Size.ToString(CultureInfo.InvariantCulture) +
                    " bytes of artifacts" + (upload.Chunked ? " in " + Transfers.ChunkCount(upload.Size, upload.ChunkBytes).ToString(CultureInfo.InvariantCulture) + " chunks" : ""), null);
                return new OracleResponse { Status = 200 };
            }
            default:
                return Error(404, "no such upload step");
        }
    }

    private void Discard(Upload upload)
    {
        uploads.TryRemove(upload.Id, out _);
        lock (upload) upload.Delete();
    }

    private IEnumerable<Upload> LoadJournals()
    {
        foreach (string journal in Directory.EnumerateFiles(UploadDirectory, "*.json"))
        {
            string id = Path.GetFileNameWithoutExtension(journal);
            if (uploads.ContainsKey(id)) continue;
            Upload? upload = Upload.Load(journal);
            if (upload is not null) yield return upload;
        }
    }

    private static OracleResponse Status(Upload upload) =>
        Json(200, JsonSerializer.SerializeToUtf8Bytes(new ArtifactUploadResponse(upload.Id, upload.Chunked, upload.ChunkBytes, upload.Committed, upload.Received),
            ApiJson.Default.ArtifactUploadResponse));

    private static string Mebibytes(long bytes) => (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);

    private static OracleResponse Json(uint status, byte[] body) => new()
    {
        Status = status,
        Headers = [new OracleHeader("content-type", "application/json")],
        Body = body,
    };

    private static OracleResponse Error(uint status, string message) =>
        Json(status, JsonSerializer.SerializeToUtf8Bytes(new ErrorResponse(message), ApiJson.Default.ErrorResponse));

    /// <summary>A refusal that also says where the upload stands, so the runner can carry on from there.</summary>
    private static OracleResponse Error(uint status, string message, Upload upload) => new()
    {
        Status = status,
        Headers =
        [
            new OracleHeader("content-type", "application/json"),
            new OracleHeader("graft-committed-offset", upload.Committed.ToString(CultureInfo.InvariantCulture)),
            new OracleHeader("graft-received-offset", upload.Received.ToString(CultureInfo.InvariantCulture)),
        ],
        Body = JsonSerializer.SerializeToUtf8Bytes(new ErrorResponse(message), ApiJson.Default.ErrorResponse),
    };

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        if (listener is not null) await listener.DisposeAsync();
        if (node is not null) await node.DisposeAsync();
        foreach (Upload upload in uploads.Values) lock (upload) upload.Close();
        stopping.Dispose();
    }

    /// <summary>
    /// One artifacts upload: a data file and a journal. The journal records the committed offset and is rewritten
    /// atomically only after the data below it has been flushed to disk, so a crash never leaves a committed offset
    /// claiming bytes that were not kept.
    /// </summary>
    private sealed class Upload
    {
        private FileStream? data;

        public Upload(string id, long jobId, long size, string sha256, bool chunked, long chunkBytes, string directory)
        {
            Id = id;
            JobId = jobId;
            Size = size;
            Sha256 = sha256;
            Chunked = chunked;
            ChunkBytes = chunkBytes;
            DataPath = Path.Combine(directory, id + ".part");
            JournalPath = Path.Combine(directory, id + ".json");
        }

        public string Id { get; }
        public long JobId { get; }
        public long Size { get; }
        public string Sha256 { get; }
        public bool Chunked { get; }
        public long ChunkBytes { get; }
        public string DataPath { get; }
        public string JournalPath { get; }
        public long Committed { get; private set; }
        public long Received { get; private set; }

        private FileStream Data => data ??= new FileStream(DataPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        public void Write(long offset, byte[] bytes)
        {
            Data.Position = offset;
            Data.Write(bytes);
            Received = offset + bytes.Length;
        }

        /// <summary>Discards everything at and after <paramref name="offset"/>.</summary>
        public void Rewind(long offset)
        {
            Data.SetLength(offset);
            Received = offset;
            if (Committed > offset || !File.Exists(JournalPath)) Commit(offset);
        }

        public void Commit(long offset)
        {
            Data.Flush(flushToDisk: true);
            Committed = offset;
            SaveJournal();
        }

        public string HashRange(long offset, long length)
        {
            Data.Flush();
            using FileStream reader = new(DataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            reader.Position = offset;
            using System.Security.Cryptography.IncrementalHash hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            byte[] buffer = new byte[1024 * 1024];
            long left = length;
            while (left > 0)
            {
                int read = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                left -= read;
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        public void Close()
        {
            data?.Flush(flushToDisk: true);
            data?.Dispose();
            data = null;
        }

        public void Delete()
        {
            Close();
            try { File.Delete(DataPath); } catch (IOException) { }
            try { File.Delete(JournalPath); } catch (IOException) { }
        }

        private void SaveJournal()
        {
            string temporary = JournalPath + ".tmp";
            using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write))
            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("id", Id);
                writer.WriteNumber("job_id", JobId);
                writer.WriteNumber("size", Size);
                writer.WriteString("sha256", Sha256);
                writer.WriteBoolean("chunked", Chunked);
                writer.WriteNumber("chunk_size", ChunkBytes);
                writer.WriteNumber("committed_offset", Committed);
                writer.WriteEndObject();
            }
            File.Move(temporary, JournalPath, overwrite: true);
        }

        /// <summary>Reads a journal back after a restart; the data past the committed offset is discarded.</summary>
        public static Upload? Load(string journalPath)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(journalPath));
                JsonElement root = document.RootElement;
                Upload upload = new(root.GetProperty("id").GetString()!, root.GetProperty("job_id").GetInt64(), root.GetProperty("size").GetInt64(),
                    root.GetProperty("sha256").GetString()!, root.GetProperty("chunked").GetBoolean(), root.GetProperty("chunk_size").GetInt64(),
                    Path.GetDirectoryName(journalPath)!);
                long committed = root.GetProperty("committed_offset").GetInt64();
                // A journal never claims more than the data file holds; if the file is short or gone, start again.
                long kept = File.Exists(upload.DataPath) ? new FileInfo(upload.DataPath).Length : 0;
                upload.Committed = committed <= kept && committed <= upload.Size ? committed : 0;
                upload.Data.SetLength(upload.Committed);
                upload.Received = upload.Committed;
                if (upload.Committed != committed) upload.Commit(0);
                return upload;
            }
            catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The source archives of jobs being run. A whole archive is one relic held in memory; a chunked one is a file on
    /// disk, and each chunk becomes a relic of its own only when a runner asks for it, so memory holds a few chunks,
    /// never the file. CupriNet's static source never forgets a relic, so each lives in a source of its own here.
    /// </summary>
    private sealed class JobArchives(BouncyCastleSuite suite, long chunkBytes) : IRelicSource
    {
        private readonly ConcurrentDictionary<string, StaticRelicSource> whole = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (string Path, long Size)> chunked = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, StaticRelicSource> chunkCache = new(StringComparer.Ordinal);

        public void AddWhole(string name, byte[] content) => whole[name] = new StaticRelicSource(suite).Add(name, content);

        public void AddChunked(string name, string path, long size) => chunked[name] = (path, size);

        public void Remove(string name)
        {
            whole.TryRemove(name, out _);
            if (chunked.TryRemove(name, out (string Path, long Size) file))
                try { File.Delete(file.Path); } catch (IOException) { }
            foreach (string key in chunkCache.Keys.Where(key => key.StartsWith(name + ".", StringComparison.Ordinal)).ToList())
                chunkCache.TryRemove(key, out _);
        }

        private StaticRelicSource? Find(string name)
        {
            if (whole.TryGetValue(name, out StaticRelicSource? source)) return source;
            if (chunkCache.TryGetValue(name, out source)) return source;
            int dot = name.LastIndexOf('.');
            if (dot < 0 || !int.TryParse(name.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int index)) return null;
            if (!chunked.TryGetValue(name[..dot], out (string Path, long Size) file)) return null;
            if (index < 0 || index >= Transfers.ChunkCount(file.Size, chunkBytes)) return null;
            byte[] bytes = new byte[Transfers.ChunkLength(file.Size, chunkBytes, index)];
            using (FileStream stream = new(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Position = index * chunkBytes;
                stream.ReadExactly(bytes);
            }
            // A runner reads chunks in order, so the last few are all that is worth keeping.
            while (chunkCache.Count >= 4) chunkCache.TryRemove(chunkCache.Keys.First(), out _);
            source = new StaticRelicSource(suite).Add(name, bytes);
            chunkCache[name] = source;
            return source;
        }

        public ValueTask<ReliquaryManifest?> ResolveAsync(string name, CancellationToken cancellationToken = default) =>
            Find(name) is { } source ? source.ResolveAsync(name, cancellationToken) : ValueTask.FromResult<ReliquaryManifest?>(null);

        public ValueTask<byte[]?> ReadChunkAsync(string name, int fileIndex, int chunkIndex, CancellationToken cancellationToken = default) =>
            Find(name) is { } source ? source.ReadChunkAsync(name, fileIndex, chunkIndex, cancellationToken) : ValueTask.FromResult<byte[]?>(null);
    }
}
