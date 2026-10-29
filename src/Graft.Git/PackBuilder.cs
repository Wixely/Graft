using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Graft.Git;

/// <summary>An object to pack, with the path it was last seen at when the caller knows it.</summary>
public sealed record PackEntry(GitObject Object, string? Path);

/// <summary>Where an object landed in a written pack, which is what an index records.</summary>
public sealed record PackPlacement(string Id, long Offset, uint Crc);

/// <summary>
/// Writes pack files the way Git does: objects sorted so that versions of the same file sit together,
/// each tried as a delta against a window of its neighbours, and stored as the smaller of itself and the
/// best delta found. Deltas point at their base by offset when the reader allows it, otherwise by id.
/// The result is a standard pack any Git can index, and the index written beside it is a standard
/// version 2 index.
/// </summary>
public static class PackBuilder
{
    /// <summary>Objects larger than this are stored whole: the window would otherwise hold too much.</summary>
    public const int DeltaCandidateBytes = 16 * 1024 * 1024;
    public const int Window = 10;
    public const int MaximumDepth = 20;

    public static List<PackPlacement> Write(Stream output, IEnumerable<PackEntry> entries, bool offsetDeltas, bool deltify,
        CancellationToken cancellationToken = default)
    {
        PackEntry[] items = [.. entries];
        Limits.Require(items.Length <= Limits.ObjectCount, "object-count");
        // Git's order: by type, then by a hash of the name so versions of one file are neighbours, then
        // larger first so a delta shrinks towards the smaller version, and finally by id for determinism.
        Array.Sort(items, (left, right) =>
        {
            int compare = left.Object.Type.CompareTo(right.Object.Type);
            if (compare != 0) return compare;
            compare = NameHash(left.Path).CompareTo(NameHash(right.Path));
            if (compare != 0) return compare;
            compare = right.Object.Size.CompareTo(left.Object.Size);
            return compare != 0 ? compare : string.CompareOrdinal(left.Object.Id, right.Object.Id);
        });

        long start = output.Position;
        using HashingStream hashing = new(output);
        hashing.Write("PACK"u8);
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, 2); hashing.Write(number);
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)items.Length); hashing.Write(number);

        List<PackPlacement> placements = new(items.Length);
        LinkedList<Written> window = new();
        foreach (PackEntry entry in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GitObject obj = entry.Object;
            long offset = hashing.Position;
            string id = obj.Id;
            byte[]? data = null;
            Written? best = null;
            byte[]? bestDelta = null;
            if (deltify && obj.Size is > 32 and <= DeltaCandidateBytes && window.Count > 0)
            {
                data = obj.Data;
                int limit = data.Length / 2;
                foreach (Written candidate in window)
                {
                    if (candidate.Type != obj.Type || candidate.Depth >= MaximumDepth || candidate.Data is null) continue;
                    byte[]? delta = Delta.Create(candidate.Data, data, limit);
                    if (delta is null) continue;
                    best = candidate;
                    bestDelta = delta;
                    limit = delta.Length - 1;
                    if (limit <= 0) break;
                }
            }

            uint crc;
            int depth = 0;
            if (best is not null && bestDelta is not null)
            {
                depth = best.Depth + 1;
                using CrcStream crcStream = new(hashing);
                if (offsetDeltas)
                {
                    Pack.WriteHeader(crcStream, 6, bestDelta.Length);
                    WriteOffset(crcStream, offset - best.Offset);
                }
                else
                {
                    Pack.WriteHeader(crcStream, 7, bestDelta.Length);
                    crcStream.Write(Convert.FromHexString(best.Id));
                }
                Pack.Compress(crcStream, bestDelta);
                crc = crcStream.Crc;
            }
            else
            {
                using CrcStream crcStream = new(hashing);
                Pack.WriteHeader(crcStream, obj.Type, obj.Size);
                if (obj.Size == 0) Pack.Compress(crcStream, []);
                else if (data is not null) Pack.Compress(crcStream, data);
                else
                {
                    using Stream source = obj.OpenRead();
                    using ZLibStream compressed = new(crcStream, CompressionLevel.Fastest, leaveOpen: true);
                    byte[] buffer = new byte[64 * 1024];
                    int count;
                    while ((count = source.Read(buffer)) != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        compressed.Write(buffer.AsSpan(0, count));
                    }
                }
                crc = crcStream.Crc;
            }
            Limits.Require(hashing.Position - start <= Limits.RequestBytes - 20, "output-limit");
            placements.Add(new(id, offset, crc));

            if (deltify && obj.Size <= DeltaCandidateBytes)
            {
                window.AddFirst(new Written(id, obj.Type, offset, depth, data ?? (obj.Size <= DeltaCandidateBytes ? obj.Data : null)));
                while (window.Count > Window) window.RemoveLast();
            }
        }
        output.Write(hashing.Finish());
        return placements;
    }

    /// <summary>Writes a version 2 pack index for a pack whose objects landed as <paramref name="placements"/> say.</summary>
    public static void WriteIndex(Stream output, IReadOnlyList<PackPlacement> placements, byte[] packChecksum)
    {
        Limits.Require(packChecksum.Length == 20, "pack-checksum");
        PackPlacement[] sorted = [.. placements.OrderBy(placement => placement.Id, StringComparer.Ordinal)];
        using HashingStream hashing = new(output);
        hashing.Write(new byte[] { 0xff, 0x74, 0x4f, 0x63, 0, 0, 0, 2 });
        Span<byte> number = stackalloc byte[8];
        int position = 0;
        for (int bucket = 0; bucket < 256; bucket++)
        {
            while (position < sorted.Length && Convert.ToByte(sorted[position].Id[..2], 16) <= bucket) position++;
            BinaryPrimitives.WriteUInt32BigEndian(number, (uint)position);
            hashing.Write(number[..4]);
        }
        foreach (PackPlacement placement in sorted) hashing.Write(Convert.FromHexString(placement.Id));
        foreach (PackPlacement placement in sorted)
        {
            BinaryPrimitives.WriteUInt32BigEndian(number, placement.Crc);
            hashing.Write(number[..4]);
        }
        List<long> large = [];
        foreach (PackPlacement placement in sorted)
        {
            if (placement.Offset < 0x8000_0000)
                BinaryPrimitives.WriteUInt32BigEndian(number, (uint)placement.Offset);
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(number, 0x8000_0000u | (uint)large.Count);
                large.Add(placement.Offset);
            }
            hashing.Write(number[..4]);
        }
        foreach (long offset in large)
        {
            BinaryPrimitives.WriteUInt64BigEndian(number, (ulong)offset);
            hashing.Write(number);
        }
        hashing.Write(packChecksum);
        output.Write(hashing.Finish());
    }

    /// <summary>Git's name hash: the tail of the path weighs most, so renames within a directory still group.</summary>
    public static uint NameHash(string? path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        uint hash = 0;
        foreach (char c in path)
        {
            if (c == ' ') continue;
            hash = (hash >> 2) + ((uint)(c & 0xff) << 24);
        }
        return hash;
    }

    private static void WriteOffset(Stream output, long distance)
    {
        // Git's offset encoding: big-endian base-128 with the continuation bit, where every byte after the
        // first counts from one rather than zero.
        Span<byte> bytes = stackalloc byte[10];
        int position = bytes.Length - 1;
        bytes[position] = (byte)(distance & 127);
        while ((distance >>= 7) > 0)
        {
            distance -= 1;
            bytes[--position] = (byte)(128 | (distance & 127));
        }
        output.Write(bytes[position..]);
    }

    private sealed record Written(string Id, int Type, long Offset, int Depth, byte[]? Data);

    /// <summary>Passes bytes through while hashing them, so the trailing checksum costs no second read.</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        private long position;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => position;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hash.AppendData(buffer);
            inner.Write(buffer);
            position += buffer.Length;
        }
        public override void WriteByte(byte value) => Write(new ReadOnlySpan<byte>(in value));
        public byte[] Finish() => hash.GetHashAndReset();
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) hash.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class CrcStream(Stream inner) : Stream
    {
        public uint Crc { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Crc = Crc32.Update(Crc, buffer);
            inner.Write(buffer);
        }
        public override void WriteByte(byte value) => Write(new ReadOnlySpan<byte>(in value));
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>The CRC-32 a pack index stores for each entry, over the entry's bytes as they sit in the pack.</summary>
public static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>Continues a running CRC; start from zero and feed every byte in order.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> bytes)
    {
        uint c = crc ^ 0xFFFFFFFFu;
        foreach (byte b in bytes) c = Table[(c ^ b) & 0xff] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

/// <summary>
/// Git's delta format, produced the way Git produces it: the base is indexed in blocks, the target is
/// scanned with a rolling hash, and runs found in the base become copy instructions while the rest is
/// inserted literally.
/// </summary>
public static class Delta
{
    private const int BlockSize = 16;
    private const int MaximumCopy = 0xFFFF;
    private const int MaximumInsert = 127;
    private const ulong Multiplier = 0x100000001b3;

    /// <summary>
    /// A delta turning <paramref name="basis"/> into <paramref name="target"/>, or null when none shorter
    /// than <paramref name="limit"/> bytes can be made.
    /// </summary>
    public static byte[]? Create(byte[] basis, byte[] target, int limit)
    {
        if (basis.Length < BlockSize || target.Length < BlockSize || limit <= 0) return null;
        Dictionary<ulong, int> blocks = new(basis.Length / BlockSize);
        for (int at = basis.Length - BlockSize; at >= 0; at -= BlockSize)
            blocks[Hash(basis, at)] = at;

        using MemoryStream output = new();
        WriteSize(output, basis.Length);
        WriteSize(output, target.Length);
        int insertStart = 0;
        int position = 0;
        ulong rolling = 0;
        bool primed = false;
        ulong removeFactor = 1;
        for (int i = 0; i < BlockSize - 1; i++) removeFactor *= Multiplier;
        while (position + BlockSize <= target.Length)
        {
            if (!primed)
            {
                rolling = Hash(target, position);
                primed = true;
            }
            if (blocks.TryGetValue(rolling, out int baseAt) && basis.AsSpan(baseAt, BlockSize).SequenceEqual(target.AsSpan(position, BlockSize)))
            {
                int length = BlockSize;
                int most = Math.Min(Math.Min(basis.Length - baseAt, target.Length - position), MaximumCopy);
                while (length < most && basis[baseAt + length] == target[position + length]) length++;
                // Extend backwards into bytes that were going to be inserted, when the base agrees there too.
                while (position > insertStart && baseAt > 0 && length < MaximumCopy && basis[baseAt - 1] == target[position - 1])
                {
                    baseAt--;
                    position--;
                    length++;
                }
                if (!Insert(output, target, insertStart, position, limit)) return null;
                if (!Copy(output, baseAt, length, limit)) return null;
                position += length;
                insertStart = position;
                primed = false;
                continue;
            }
            if (position + BlockSize >= target.Length) break;
            rolling = (rolling - target[position] * removeFactor) * Multiplier + target[position + BlockSize];
            position++;
            if (position - insertStart >= MaximumInsert * 4 && output.Length + (position - insertStart) >= limit) return null;
        }
        if (!Insert(output, target, insertStart, target.Length, limit)) return null;
        return output.Length < limit ? output.ToArray() : null;
    }

    private static ulong Hash(byte[] data, int at)
    {
        ulong hash = 0;
        for (int i = 0; i < BlockSize; i++) hash = hash * Multiplier + data[at + i];
        return hash;
    }

    private static bool Insert(MemoryStream output, byte[] target, int from, int to, int limit)
    {
        while (from < to)
        {
            int count = Math.Min(MaximumInsert, to - from);
            if (output.Length + 1 + count >= limit) return false;
            output.WriteByte((byte)count);
            output.Write(target, from, count);
            from += count;
        }
        return true;
    }

    private static bool Copy(MemoryStream output, int offset, int length, int limit)
    {
        if (output.Length + 8 >= limit) return false;
        Span<byte> bytes = stackalloc byte[8];
        int at = 1;
        byte op = 0x80;
        for (int i = 0; i < 4; i++)
        {
            byte part = (byte)((offset >> (8 * i)) & 0xff);
            if (part == 0) continue;
            op |= (byte)(1 << i);
            bytes[at++] = part;
        }
        for (int i = 0; i < 3; i++)
        {
            byte part = (byte)((length >> (8 * i)) & 0xff);
            if (part == 0) continue;
            op |= (byte)(0x10 << i);
            bytes[at++] = part;
        }
        bytes[0] = op;
        output.Write(bytes[..at]);
        return true;
    }

    private static void WriteSize(Stream output, int size)
    {
        do
        {
            int part = size & 127;
            size >>= 7;
            output.WriteByte((byte)(part | (size > 0 ? 128 : 0)));
        } while (size > 0);
    }
}
