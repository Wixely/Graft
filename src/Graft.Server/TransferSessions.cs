using System.Collections.Concurrent;
using System.Security.Cryptography;
using Graft.Git;

namespace Graft.Server;

/// <summary>What a chunked transfer carries: a push, a fetch request, or one Git LFS object.</summary>
public enum TransferKind { ReceivePack, UploadPack, LargeFile }

/// <summary>Where a chunked transfer is: still arriving, being acted on, or finished one way or the other.</summary>
public enum TransferState { Receiving, Running, Done, Failed }

/// <summary>
/// One request that arrives in parts (ADR 0043). A proxy in front of the forge may refuse any single request over a
/// size, Cloudflare's free plan at 100 MB, and may give up on a response that takes more than a minute or two to
/// start. So a large push, a fetch, or a large file is sent as numbered parts, each well under the limit, then acted
/// on in the background while the client asks how it is going, and its result is read back in ranges.
///
/// <para>
/// The parts are appended in order to one file. A part is placed by its offset, so a part whose answer was lost can be
/// sent again: an offset short of what has arrived cuts the file back to it, and one beyond it is refused with what
/// has arrived, which is where the client resumes.
/// </para>
/// </summary>
public sealed class TransferSession : IDisposable
{
    private readonly TemporaryStorage storage = new();
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TransferSession(string id, TransferKind kind, long projectId, long? userId, long size, bool protocolV2, string? oid, string origin)
    {
        Id = id;
        Kind = kind;
        ProjectId = projectId;
        UserId = userId;
        Size = size;
        ProtocolV2 = protocolV2;
        Oid = oid;
        Origin = origin;
        RequestPath = Path.Combine(storage.DirectoryPath, "request");
        ResultPath = Path.Combine(storage.DirectoryPath, "result");
        using (File.Create(RequestPath)) { }
        Touched = DateTimeOffset.UtcNow;
    }

    public string Id { get; }
    public TransferKind Kind { get; }
    public long ProjectId { get; }
    /// <summary>Who started it, or null for an anonymous fetch. Every later request must be the same person.</summary>
    public long? UserId { get; }
    public long Size { get; }
    public bool ProtocolV2 { get; }
    public string? Oid { get; }
    /// <summary>The forge's address as the client reached it, for the links a push's notifications carry.</summary>
    public string Origin { get; }
    public string RequestPath { get; }
    public string ResultPath { get; }
    public long Received { get; private set; }
    public TransferState State { get; private set; }
    public string? Error { get; private set; }
    public long? ResultBytes { get; private set; }
    public DateTimeOffset Touched { get; private set; }

    /// <summary>Serialises the parts of one transfer, so two copies of the same part cannot interleave.</summary>
    internal SemaphoreSlim Writing { get; } = new(1, 1);

    internal void Touch() => Touched = DateTimeOffset.UtcNow;

    /// <summary>
    /// Writes one part at <paramref name="offset"/>, at most <paramref name="partBytes"/> long and never past the
    /// declared size. Returns false, writing nothing, when the offset is beyond what has arrived.
    /// </summary>
    internal async Task<bool> WritePartAsync(long offset, Stream body, long partBytes, CancellationToken cancellationToken)
    {
        if (offset < 0 || offset > Received) return false;
        await using FileStream file = new(RequestPath, FileMode.Open, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        file.SetLength(offset);
        file.Position = offset;
        Received = offset;
        byte[] buffer = new byte[64 * 1024];
        long written = 0;
        int count;
        while ((count = await ReadBodyAsync(body, buffer, partBytes, cancellationToken)) > 0)
        {
            written += count;
            if (written > partBytes) throw new TransferRefusedException(413, "a part is at most " + partBytes + " bytes");
            if (offset + written > Size) throw new TransferRefusedException(413, "the parts are larger than the size that was declared");
            await file.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        await file.FlushAsync(cancellationToken);
        Received = offset + written;
        Touch();
        return true;
    }

    /// <summary>A read of the request body, where the server's own limit on a body's size reads as the part limit it is.</summary>
    private static async Task<int> ReadBodyAsync(Stream body, byte[] buffer, long partBytes, CancellationToken cancellationToken)
    {
        try { return await body.ReadAsync(buffer, cancellationToken); }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException refused) when (refused.StatusCode == 413)
        {
            throw new TransferRefusedException(413, "a part is at most " + partBytes + " bytes");
        }
    }

    /// <summary>Runs the work once every byte has arrived. The work writes <see cref="ResultPath"/> if it answers with anything.</summary>
    internal void Start(Func<TransferSession, Task> work)
    {
        State = TransferState.Running;
        _ = Task.Run(async () =>
        {
            try
            {
                await work(this);
                ResultBytes = File.Exists(ResultPath) ? new FileInfo(ResultPath).Length : 0;
                State = TransferState.Done;
            }
            catch (TransferRefusedException refused) { Fail(refused.Message); }
            catch (GitInputException refused) { Fail(refused.Message); }
            catch (OperationCanceledException) { Fail("the server stopped before the transfer was acted on"); }
            catch (Exception) { Fail("the server could not act on the transfer"); }
            finally
            {
                Touch();
                finished.TrySetResult();
            }
        });
    }

    /// <summary>Marks the transfer finished without running anything: a large file the forge already has.</summary>
    internal void Complete()
    {
        ResultBytes = 0;
        State = TransferState.Done;
        finished.TrySetResult();
    }

    private void Fail(string message)
    {
        Error = message;
        State = TransferState.Failed;
    }

    /// <summary>Waits until the transfer is finished or <paramref name="limit"/> passes, whichever is first.</summary>
    public async Task WaitAsync(TimeSpan limit, CancellationToken cancellationToken)
    {
        if (limit <= TimeSpan.Zero || finished.Task.IsCompleted) return;
        try { await finished.Task.WaitAsync(limit, cancellationToken); }
        catch (TimeoutException) { }
    }

    public void Dispose()
    {
        try { storage.Dispose(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A transfer request refused with an HTTP status and a reason the client can show.</summary>
public sealed class TransferRefusedException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The transfers in flight. Bounded per person and in total, because each one holds a file of up to a push's size on
/// disk; forgotten after an hour without a request, finished or not.
/// </summary>
public sealed class TransferSessions : IDisposable
{
    public const int PerOwner = 8;
    public const int Total = 64;
    public static readonly TimeSpan Idle = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, TransferSession> sessions = new(StringComparer.Ordinal);
    private readonly Lock creating = new();
    private readonly Timer sweeper;

    public TransferSessions(long partBytes)
    {
        PartBytes = partBytes;
        sweeper = new Timer(_ => Sweep(DateTimeOffset.UtcNow), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>The most one part may carry.</summary>
    public long PartBytes { get; }

    /// <summary>Starts a transfer, or throws when the person or the forge already has as many as it allows.</summary>
    public TransferSession Create(TransferKind kind, long projectId, long? userId, long size, bool protocolV2, string? oid, string origin)
    {
        lock (creating)
        {
            Sweep(DateTimeOffset.UtcNow);
            if (sessions.Count >= Total) throw new TransferRefusedException(429, "the forge has as many transfers in flight as it allows; try again shortly");
            if (sessions.Values.Count(session => session.UserId == userId) >= PerOwner)
                throw new TransferRefusedException(429, "you have as many transfers in flight as one person may; finish or abandon one first");
            TransferSession session = new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), kind, projectId, userId, size, protocolV2, oid, origin);
            sessions[session.Id] = session;
            return session;
        }
    }

    /// <summary>The transfer by id, when it belongs to this project and this person; null otherwise, as for none.</summary>
    public TransferSession? Find(string id, long projectId, long? userId)
    {
        if (!sessions.TryGetValue(id, out TransferSession? session) || session.ProjectId != projectId || session.UserId != userId) return null;
        session.Touch();
        return session;
    }

    public void Remove(TransferSession session)
    {
        if (sessions.TryRemove(session.Id, out _)) session.Dispose();
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (TransferSession session in sessions.Values)
            if (session.State != TransferState.Running && now - session.Touched > Idle) Remove(session);
    }

    public void Dispose()
    {
        sweeper.Dispose();
        foreach (TransferSession session in sessions.Values) Remove(session);
    }
}
