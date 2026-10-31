using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using CupriNet.Abstractions;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;
using CupriNet.Vessel;
using Graft.Contracts;
using Graft.RunnerProtocol;
using Protocol = Graft.RunnerProtocol.RunnerProtocol;

namespace Graft.Runner;

/// <summary>
/// The runner's connection to its forge, which outlives any one CupriNet visit: a request that finds the visit gone
/// opens another, so a job survives the network dropping under it. Requests are sent one at a time, as the Oracle
/// requires; a relic download runs beside them on the same visit.
///
/// <para>
/// For tests, <c>GRAFT_RUNNER_FAULT_DROP_EVERY=n</c> drops the visit before every n-th relic fetched or chunk
/// committed, so resumption is exercised against a real forge rather than assumed.
/// </para>
/// </summary>
internal sealed class ForgeLink(RunnerConfig config) : IAsyncDisposable
{
    private static readonly BouncyCastleSuite Suite = new();
    private readonly SemaphoreSlim connecting = new(1, 1);
    private readonly SemaphoreSlim consulting = new(1, 1);
    private readonly SemaphoreSlim fetching = new(1, 1);
    private readonly int faultEvery = int.TryParse(Environment.GetEnvironmentVariable("GRAFT_RUNNER_FAULT_DROP_EVERY"),
        NumberStyles.None, CultureInfo.InvariantCulture, out int every) && every > 1 ? every : 0;
    private int transferSteps;
    private ShrineSession? session;

    public static async Task<ShrineSession> ConnectAsync(RunnerConfig config, CancellationToken cancellationToken)
    {
        Sigil shrine = new(Convert.FromBase64String(config.ShrineSigil));
        Exception? last = null;
        foreach (RunnerEndpoint endpoint in config.Endpoints)
        {
            try
            {
                using CancellationTokenSource connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connect.CancelAfter(TimeSpan.FromSeconds(15));
                IVessel vessel = await TcpVessel.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken: connect.Token);
                return await Pilgrimage.OverVesselAsync(vessel, shrine, new Concordium(config.Network), Suite, enableToll: true, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                last = exception;
            }
        }
        throw new IOException("no address of the forge answered as " + config.ShrineAddress + (last is null ? "" : " (" + last.Message + ")"), last);
    }

    private async Task<ShrineSession> SessionAsync(CancellationToken cancellationToken)
    {
        ShrineSession? current = session;
        if (current is not null) return current;
        await connecting.WaitAsync(cancellationToken);
        try { return session ??= await ConnectAsync(config, cancellationToken); }
        finally { connecting.Release(); }
    }

    /// <summary>Ends the visit a failed request ran on, so the next request opens a fresh one.</summary>
    private async Task DropAsync(ShrineSession failed)
    {
        if (Interlocked.CompareExchange(ref session, null, failed) == failed)
            try { await failed.DisposeAsync(); } catch (Exception) { }
    }

    private async Task InjectFaultAsync(ShrineSession current)
    {
        if (faultEvery == 0 || Interlocked.Increment(ref transferSteps) % faultEvery != 0) return;
        await DropAsync(current);
        throw new IOException("the connection was dropped on purpose (GRAFT_RUNNER_FAULT_DROP_EVERY)");
    }

    /// <summary>One request on the current visit, opening one if there is none. Throws when the visit fails.</summary>
    public async Task<OracleResponse> ConsultAsync(OracleRequest request, CancellationToken cancellationToken, bool transferStep = false)
    {
        ShrineSession current = await SessionAsync(cancellationToken);
        await consulting.WaitAsync(cancellationToken);
        try
        {
            if (transferStep) await InjectFaultAsync(current);
            return await current.ConsultAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await DropAsync(current);
            throw;
        }
        finally { consulting.Release(); }
    }

    /// <summary>A relic, verified by CupriNet against its manifest, on the current visit.</summary>
    public async Task<byte[]> FetchRelicAsync(string name, long maxBytes, CancellationToken cancellationToken)
    {
        ShrineSession current = await SessionAsync(cancellationToken);
        await fetching.WaitAsync(cancellationToken);
        try
        {
            await InjectFaultAsync(current);
            return await current.FetchRelicAsync(name, maxBytes, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await DropAsync(current);
            throw;
        }
        finally { fetching.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        ShrineSession? current = Interlocked.Exchange(ref session, null);
        if (current is not null) await current.DisposeAsync();
    }
}

/// <summary>
/// The runner's half of <see cref="Transfers"/>: fetching a job's source, whole or chunk by chunk, and sending its
/// artifacts the same way. Every step that fails because the connection did is retried on a new one, from where the
/// forge says the transfer stands; a transfer gives up only after repeated failures without progress.
/// </summary>
internal sealed class RunnerTransfers(ForgeLink link, string home, Action<string> say)
{
    /// <summary>Failures in a row, without progress between them, before a transfer is abandoned.</summary>
    private const int Attempts = 8;

    private string TransferDirectory => Path.Combine(home, "transfers");

    // ------------------------------------------------------------------------------------------------
    // Downloads
    // ------------------------------------------------------------------------------------------------

    /// <summary>Downloads the job's source to a file and returns its path, after checking the whole file's SHA-256.</summary>
    public async Task<string> DownloadAsync(TransferDescriptor source, CancellationToken cancellationToken)
    {
        if (source.Size > Transfers.MaximumFileBytes) throw new InvalidOperationException("the forge offered " + source.Size + " bytes, past the 4 GiB a runner accepts");
        Directory.CreateDirectory(TransferDirectory);
        string path = Path.Combine(TransferDirectory, source.Name);
        if (!source.Chunked)
        {
            for (int failures = 0; ; failures++)
            {
                try
                {
                    byte[] bytes = await link.FetchRelicAsync(source.Name, Math.Max(1, source.Size), cancellationToken);
                    if (bytes.LongLength != source.Size || Transfers.Sha256Hex(bytes) != source.Sha256)
                        throw new InvalidDataException("the source did not match the size and SHA-256 the forge declared");
                    await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                    return path;
                }
                catch (Exception exception) when (Retryable(exception, cancellationToken) && failures + 1 < Attempts)
                {
                    say("Fetching the source failed (" + exception.Message + "); starting it again");
                    await Pause(failures, cancellationToken);
                }
            }
        }

        // Chunked: each chunk is checked against its own hash as it lands, and how many have landed is written down
        // after them, so a retry, and a runner started again, carries on from the first chunk it does not have.
        int count = Transfers.ChunkCount(source.Size, source.ChunkSize);
        if (source.ChunkSha256.Length != count) throw new InvalidDataException("the forge's chunk list does not match the size it declared");
        string progressPath = path + ".progress";
        int have = ReadProgress(progressPath, source);
        await using (FileStream file = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            file.SetLength((long)have * source.ChunkSize);
            if (have > 0) say("Resuming the source at chunk " + (have + 1) + " of " + count);
            int failures = 0;
            while (have < count)
            {
                try
                {
                    long length = Transfers.ChunkLength(source.Size, source.ChunkSize, have);
                    byte[] bytes = await link.FetchRelicAsync(Transfers.ChunkRelic(source.Name, have), length, cancellationToken);
                    if (bytes.LongLength != length || Transfers.Sha256Hex(bytes) != source.ChunkSha256[have])
                        throw new InvalidDataException("chunk " + (have + 1) + " did not match its SHA-256");
                    file.Position = (long)have * source.ChunkSize;
                    await file.WriteAsync(bytes, cancellationToken);
                    file.Flush(flushToDisk: true);
                    have++;
                    WriteProgress(progressPath, source, have);
                    failures = 0;
                }
                catch (Exception exception) when (Retryable(exception, cancellationToken) && ++failures < Attempts)
                {
                    say("Fetching chunk " + (have + 1) + " of " + count + " failed (" + exception.Message + "); resuming");
                    await Pause(failures, cancellationToken);
                }
            }
            file.Position = 0;
            if (await Transfers.Sha256HexAsync(file, cancellationToken) != source.Sha256)
            {
                File.Delete(progressPath);
                throw new InvalidDataException("the source's chunks all matched but the whole did not match its SHA-256");
            }
        }
        File.Delete(progressPath);
        say("Fetched the source in " + count + " chunks");
        return path;
    }

    private static int ReadProgress(string progressPath, TransferDescriptor source)
    {
        try
        {
            string[] parts = File.ReadAllText(progressPath).Split(' ');
            if (parts.Length == 3 && parts[0] == source.Sha256 && parts[1] == source.ChunkSize.ToString(CultureInfo.InvariantCulture) &&
                int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int have) &&
                have <= Transfers.ChunkCount(source.Size, source.ChunkSize))
                return have;
        }
        catch (IOException) { }
        return 0;
    }

    private static void WriteProgress(string progressPath, TransferDescriptor source, int have)
    {
        File.WriteAllText(progressPath + ".tmp", source.Sha256 + " " + source.ChunkSize.ToString(CultureInfo.InvariantCulture) + " " + have.ToString(CultureInfo.InvariantCulture));
        File.Move(progressPath + ".tmp", progressPath, overwrite: true);
    }

    // ------------------------------------------------------------------------------------------------
    // Artifacts
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Zips what the job's <c>artifacts: paths:</c> match, relative to the workspace. Null when nothing matched,
    /// which GitLab reports and does not fail on.
    /// </summary>
    public string? CollectArtifacts(long jobId, string workspace, IReadOnlyList<string> patterns)
    {
        string root = Path.GetFullPath(workspace);
        SortedSet<string> files = new(StringComparer.Ordinal);
        foreach (string pattern in patterns)
        {
            int before = files.Count;
            foreach (string match in ArtifactGlob.Match(root, pattern)) files.Add(match);
            if (files.Count == before) say("WARNING: " + pattern + ": no matching files");
        }
        if (files.Count == 0) return null;
        Directory.CreateDirectory(TransferDirectory);
        string zip = Path.Combine(TransferDirectory, "job-" + jobId.ToString(CultureInfo.InvariantCulture) + "-artifacts.zip");
        File.Delete(zip);
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (string relative in files)
                archive.CreateEntryFromFile(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), relative, CompressionLevel.Optimal);
        say("Collected " + files.Count + " file(s) as artifacts, " + new FileInfo(zip).Length + " bytes");
        return zip;
    }

    /// <summary>Sends the artifacts archive, whole or chunked as the forge decides, and has the forge keep it.</summary>
    public async Task UploadAsync(long jobId, string jobToken, string path, CancellationToken cancellationToken)
    {
        long size = new FileInfo(path).Length;
        string sha256;
        await using (FileStream reader = File.OpenRead(path)) sha256 = await Transfers.Sha256HexAsync(reader, cancellationToken);
        byte[] startBody = JsonSerializer.SerializeToUtf8Bytes(new ArtifactUploadRequest(size, sha256), ApiJson.Default.ArtifactUploadRequest);
        await using FileStream file = File.OpenRead(path);
        byte[] piece = new byte[Transfers.PieceBytes];
        int failures = 0;
        long bestCommitted = -1;
        bool announced = false;
        while (true)
        {
            try
            {
                // Starting again is how a transfer resumes: the forge finds the same upload and says where it stands,
                // from the last committed chunk when chunked, from nothing when whole.
                ArtifactUploadResponse state = Read(await link.ConsultAsync(
                    Request(Transfers.Routes.Start(jobId), startBody, jobToken), cancellationToken), "start the artifacts upload");
                if (!announced && state.Chunked)
                    say("Sending artifacts in " + Transfers.ChunkCount(size, state.ChunkSize) + " chunks of " + state.ChunkSize / (1024 * 1024) + " MiB");
                if (announced && state.CommittedOffset > 0) say("Resuming the artifacts at byte " + state.CommittedOffset);
                announced = true;
                long offset = state.ReceivedOffset;
                long committed = state.CommittedOffset;
                while (true)
                {
                    if (state.Chunked && committed > bestCommitted) { bestCommitted = committed; failures = 0; }
                    if (state.Chunked && committed == size) break;
                    if (!state.Chunked && offset == size) break;
                    int index = state.Chunked ? (int)(committed / state.ChunkSize) : 0;
                    long chunkEnd = state.Chunked ? committed + Transfers.ChunkLength(size, state.ChunkSize, index) : size;
                    if (state.Chunked && offset == chunkEnd)
                    {
                        string hash = await HashRangeAsync(file, committed, chunkEnd - committed, cancellationToken);
                        byte[] commitBody = JsonSerializer.SerializeToUtf8Bytes(new ArtifactChunkCommit(index, hash), ApiJson.Default.ArtifactChunkCommit);
                        OracleResponse response = await link.ConsultAsync(Request(Transfers.Routes.Commit(jobId, state.UploadId), commitBody, jobToken),
                            cancellationToken, transferStep: true);
                        if (response.Status == 409) { (committed, offset) = Positions(response, committed, offset); continue; }
                        ArtifactUploadResponse after = Read(response, "commit chunk " + (index + 1));
                        committed = after.CommittedOffset;
                        offset = after.ReceivedOffset;
                        continue;
                    }
                    int length = (int)Math.Min(Transfers.PieceBytes, chunkEnd - offset);
                    file.Position = offset;
                    await file.ReadExactlyAsync(piece.AsMemory(0, length), cancellationToken);
                    OracleRequest data = Request(Transfers.Routes.Data(jobId, state.UploadId), piece[..length], jobToken,
                        new OracleHeader(Transfers.OffsetHeader, offset.ToString(CultureInfo.InvariantCulture)));
                    OracleResponse sent = await link.ConsultAsync(data, cancellationToken);
                    if (sent.Status == 409) { (committed, offset) = Positions(sent, committed, offset); continue; }
                    offset = Read(sent, "send a piece of the artifacts").ReceivedOffset;
                }
                OracleResponse completed = await link.ConsultAsync(Request(Transfers.Routes.Complete(jobId, state.UploadId), [], jobToken), cancellationToken);
                if (completed.Status == 200) return;
                if (completed.Status == 409) throw new InvalidDataException(Describe(completed));
                throw new RunnerException("The forge did not keep the artifacts: " + Describe(completed));
            }
            catch (Exception exception) when (Retryable(exception, cancellationToken) && ++failures < Attempts)
            {
                say("Sending the artifacts failed (" + exception.Message + "); resuming");
                await Pause(failures, cancellationToken);
            }
        }
    }

    private static (long Committed, long Received) Positions(OracleResponse response, long committed, long received)
    {
        long Header(string name, long fallback) =>
            response.Headers.FirstOrDefault(header => header.Name == name) is { } found &&
            long.TryParse(found.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : fallback;
        return (Header("graft-committed-offset", committed), Header("graft-received-offset", received));
    }

    private static async Task<string> HashRangeAsync(FileStream file, long offset, long length, CancellationToken cancellationToken)
    {
        using System.Security.Cryptography.IncrementalHash hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        file.Position = offset;
        long left = length;
        while (left > 0)
        {
            int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            hash.AppendData(buffer, 0, read);
            left -= read;
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static ArtifactUploadResponse Read(OracleResponse response, string what)
    {
        if (response.Status == 401 || response.Status == 413 || response.Status == 404)
            throw new RunnerException("The forge refused to " + what + ": " + Describe(response));
        if (response.Status != 200) throw new InvalidDataException("the forge answered " + response.Status + " to " + what + ": " + Describe(response));
        return JsonSerializer.Deserialize(response.Body, ApiJson.Default.ArtifactUploadResponse)
            ?? throw new InvalidDataException("the forge's answer to " + what + " was empty");
    }

    private static string Describe(OracleResponse response)
    {
        try
        {
            if (JsonSerializer.Deserialize(response.Body, ApiJson.Default.ErrorResponse)?.Message is { Length: > 0 } message) return message;
        }
        catch (JsonException) { }
        return "status " + response.Status;
    }

    /// <summary>A failure of the connection or of a check, worth another try; not one the forge stated as final.</summary>
    private static bool Retryable(Exception exception, CancellationToken cancellationToken) =>
        exception is not RunnerException && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);

    private static Task Pause(int failures, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10_000, 250 * (1 << Math.Min(failures, 6)))), cancellationToken);

    internal static OracleRequest Request(string path, byte[] body, string? authorization, OracleHeader? extra = null)
    {
        List<OracleHeader> headers = [new(Protocol.VersionHeader, Protocol.Advertised), new("content-type", "application/json")];
        if (authorization is not null) headers.Add(new(Protocol.AuthorizationHeader, authorization));
        if (extra is not null) headers.Add(extra);
        return new OracleRequest { Method = "POST", Path = path, Headers = headers, Body = body };
    }
}

/// <summary>
/// The <c>artifacts: paths:</c> patterns GitLab accepts, matched against a workspace: <c>*</c> and <c>?</c> within one
/// name, <c>**</c> across directories, and a directory standing for everything below it. Nothing outside the
/// workspace is ever matched, and links are not followed.
/// </summary>
internal static class ArtifactGlob
{
    public static IEnumerable<string> Match(string root, string pattern)
    {
        string[] segments = pattern.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".").ToArray();
        if (segments.Length == 0 || segments.Contains("..")) yield break;
        foreach (string match in Walk(root, "", segments, 0))
        {
            string full = Path.Combine(root, match.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(full) && !IsLink(full))
            {
                foreach (string file in Everything(root, match)) yield return file;
            }
            else if (File.Exists(full) && !IsLink(full)) yield return match;
        }
    }

    private static IEnumerable<string> Walk(string root, string relative, string[] segments, int index)
    {
        if (index == segments.Length) { if (relative.Length > 0) yield return relative; yield break; }
        string directory = relative.Length == 0 ? root : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(directory) || (relative.Length > 0 && IsLink(directory))) yield break;
        string segment = segments[index];
        if (segment == "**")
        {
            foreach (string match in Walk(root, relative, segments, index + 1)) yield return match;   // zero directories
            foreach (string child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
            {
                if (IsLink(child)) continue;
                foreach (string match in Walk(root, Join(relative, Path.GetFileName(child)), segments, index)) yield return match;
            }
            yield break;
        }
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(entry);
            if (Matches(segment, name)) foreach (string match in Walk(root, Join(relative, name), segments, index + 1)) yield return match;
        }
    }

    private static IEnumerable<string> Everything(string root, string relative)
    {
        string directory = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            if (IsLink(entry)) continue;
            string child = Join(relative, Path.GetFileName(entry));
            if (Directory.Exists(entry)) foreach (string file in Everything(root, child)) yield return file;
            else yield return child;
        }
    }

    private static string Join(string relative, string name) => relative.Length == 0 ? name : relative + "/" + name;

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    /// <summary>Whether <paramref name="name"/> matches a pattern of literals, <c>*</c> and <c>?</c>.</summary>
    internal static bool Matches(string pattern, string name)
    {
        int p = 0, n = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == name[n])) { p++; n++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = n; }
            else if (star >= 0) { p = star + 1; n = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
