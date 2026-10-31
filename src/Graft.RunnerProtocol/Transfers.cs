using System.Globalization;
using System.Security.Cryptography;

namespace Graft.RunnerProtocol;

/// <summary>
/// How files move between a runner and its forge, in the manner of DnaX's uploads: a file at or below the forge's
/// threshold travels whole, verified by its SHA-256, and starts again from nothing if it is interrupted; a larger
/// file travels in chunks, each verified by its own SHA-256 and kept durably by the receiver, so an interruption
/// costs at most the chunk in flight and the sender resumes where the receiver says it got to.
///
/// <para>
/// Underneath, everything is carried in pieces no larger than CupriNet's per-message ceiling, 192 KiB on a Shrine.
/// Pieces are the transport's business; chunks are the unit of verification and resumption.
/// </para>
/// </summary>
public static class Transfers
{
    /// <summary>The default threshold the user set: files above 80 MiB are chunked.</summary>
    public const long DefaultThresholdBytes = 80L * 1024 * 1024;

    /// <summary>The default chunk: big enough that its hash and its round trip are cheap beside it.</summary>
    public const long DefaultChunkBytes = 8L * 1024 * 1024;

    /// <summary>The largest chunk an operator may choose.</summary>
    public const long MaximumChunkBytes = 64L * 1024 * 1024;

    /// <summary>
    /// The largest piece of an upload carried in one request: under the 192 KiB a Shrine's request body may hold,
    /// with room for the frame around it.
    /// </summary>
    public const int PieceBytes = 160 * 1024;

    /// <summary>The largest file a runner and forge exchange, chunked or not: the forge's repository bound.</summary>
    public const long MaximumFileBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// The largest whole transfer: a whole download is one CupriNet relic and is held in memory, and a relic is
    /// bounded at 256 MiB. With chunking off, a larger file is refused by name.
    /// </summary>
    public const long MaximumWholeBytes = 256L * 1024 * 1024;

    /// <summary>The header carrying a piece's offset in the file.</summary>
    public const string OffsetHeader = "graft-offset";

    /// <summary>Whether a file of <paramref name="size"/> bytes is chunked under <paramref name="thresholdBytes"/> (-1 for off).</summary>
    public static bool IsChunked(long size, long thresholdBytes) => thresholdBytes >= 0 && size > thresholdBytes;

    public static int ChunkCount(long size, long chunkBytes) => size == 0 ? 0 : (int)((size + chunkBytes - 1) / chunkBytes);

    public static long ChunkLength(long size, long chunkBytes, int index) => Math.Min(chunkBytes, size - (long)index * chunkBytes);

    /// <summary>The relic name of chunk <paramref name="index"/> of a chunked download.</summary>
    public static string ChunkRelic(string name, int index) => name + "." + index.ToString("D5", CultureInfo.InvariantCulture);

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static async Task<string> Sha256HexAsync(Stream stream, CancellationToken cancellationToken) =>
        Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));

    /// <summary>The SHA-256 of each chunk of a file, read from <paramref name="path"/>.</summary>
    public static async Task<string[]> ChunkHashesAsync(string path, long chunkBytes, CancellationToken cancellationToken)
    {
        long size = new FileInfo(path).Length;
        string[] hashes = new string[ChunkCount(size, chunkBytes)];
        byte[] buffer = new byte[(int)Math.Min(chunkBytes, Math.Max(1, size))];
        await using FileStream file = File.OpenRead(path);
        for (int index = 0; index < hashes.Length; index++)
        {
            int length = (int)ChunkLength(size, chunkBytes, index);
            await file.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken);
            hashes[index] = Sha256Hex(buffer.AsSpan(0, length));
        }
        return hashes;
    }

    /// <summary>The routes of an artifacts upload, below a job's prefix.</summary>
    public static class Routes
    {
        public static string Start(long jobId) => RunnerProtocol.Routes.JobPrefix + jobId.ToString(CultureInfo.InvariantCulture) + "/artifacts";
        public static string Data(long jobId, string upload) => Start(jobId) + "/" + upload + "/data";
        public static string Commit(long jobId, string upload) => Start(jobId) + "/" + upload + "/commit";
        public static string Status(long jobId, string upload) => Start(jobId) + "/" + upload + "/status";
        public static string Complete(long jobId, string upload) => Start(jobId) + "/" + upload + "/complete";
    }
}
