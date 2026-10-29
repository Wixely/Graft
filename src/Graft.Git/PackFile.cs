using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Graft.Git;

/// <summary>
/// A pack file and its index, read the way Git reads them: the index names every object and where it
/// starts, the pack holds each one compressed, possibly as a delta against another. Objects are read on
/// demand with positional reads, so one open pack serves any number of concurrent readers. Both checksums
/// are verified when the pack is opened; after that the index is trusted the way Git trusts it.
/// </summary>
public sealed class PackFile : IDisposable
{
    private const int FanoutBytes = 256 * 4;
    private const long CacheBytes = 64L * 1024 * 1024;
    private readonly SafeFileHandle handle;
    private readonly byte[] index;
    private readonly int count;
    private readonly long packLength;
    private readonly object cacheLock = new();
    private readonly Dictionary<long, byte[]> cache = new();
    private readonly LinkedList<long> cacheOrder = new();
    private long cacheSize;

    public string PackPath { get; }
    public string IndexPath { get; }
    public int Count => count;
    public string Checksum { get; }

    private PackFile(string packPath, string indexPath, SafeFileHandle handle, byte[] index, long packLength, string checksum)
    {
        PackPath = packPath;
        IndexPath = indexPath;
        this.handle = handle;
        this.index = index;
        this.packLength = packLength;
        count = (int)BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(8 + 255 * 4));
        Checksum = checksum;
    }

    /// <summary>Opens <c>name.pack</c> beside <c>name.idx</c>, verifying both trailing checksums.</summary>
    public static PackFile Open(string packPath, CancellationToken cancellationToken = default)
    {
        string indexPath = Path.ChangeExtension(packPath, ".idx");
        byte[] index = File.ReadAllBytes(indexPath);
        Limits.Require(index.Length >= 8 + FanoutBytes + 40, "pack-index-size");
        Limits.Require(index[0] == 0xff && index[1] == 0x74 && index[2] == 0x4f && index[3] == 0x63, "pack-index-magic");
        Limits.Require(BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(4)) == 2, "pack-index-version");
        Limits.Require(SHA1.HashData(index.AsSpan(0, index.Length - 20)).AsSpan().SequenceEqual(index.AsSpan(index.Length - 20)), "pack-index-checksum");
        uint entries = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(8 + 255 * 4));
        Limits.Require(entries <= Limits.ObjectCount, "pack-index-count");
        uint previous = 0;
        for (int bucket = 0; bucket < 256; bucket++)
        {
            uint value = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(8 + bucket * 4));
            Limits.Require(value >= previous, "pack-index-fanout");
            previous = value;
        }
        long expected = 8 + FanoutBytes + (long)entries * (20 + 4 + 4) + 40;
        Limits.Require(index.Length >= expected && (index.Length - expected) % 8 == 0, "pack-index-size");

        SafeFileHandle handle = File.OpenHandle(packPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try
        {
            long length = RandomAccess.GetLength(handle);
            Limits.Require(length >= 12 + 20, "pack-length");
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            byte[] buffer = new byte[256 * 1024];
            long position = 0;
            while (position < length - 20)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - 20 - position)), position);
                Limits.Require(read > 0, "truncated");
                hash.AppendData(buffer.AsSpan(0, read));
                position += read;
            }
            byte[] trailer = new byte[20];
            Limits.Require(RandomAccess.Read(handle, trailer, length - 20) == 20, "truncated");
            Limits.Require(hash.GetHashAndReset().AsSpan().SequenceEqual(trailer), "pack-checksum");
            Limits.Require(index.AsSpan(index.Length - 40, 20).SequenceEqual(trailer), "pack-index-mismatch");
            byte[] header = new byte[12];
            Limits.Require(RandomAccess.Read(handle, header, 0) == 12 && header.AsSpan(0, 4).SequenceEqual("PACK"u8), "pack-magic");
            Limits.Require(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)) == 2, "pack-version");
            Limits.Require(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)) == entries, "pack-index-count");
            return new(packPath, indexPath, handle, index, length, Convert.ToHexStringLower(trailer));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Every object in the index with its offset, in index order (sorted by id).</summary>
    public IEnumerable<(string Id, long Offset)> Entries()
    {
        for (int position = 0; position < count; position++)
            yield return (Convert.ToHexStringLower(index.AsSpan(NameAt(position), 20)), OffsetAt(position));
    }

    public bool TryFindOffset(string id, out long offset)
    {
        offset = 0;
        if (!Limits.IsId(id)) return false;
        byte[] wanted = Convert.FromHexString(id);
        int low = wanted[0] == 0 ? 0 : (int)BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(8 + (wanted[0] - 1) * 4));
        int high = (int)BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(8 + wanted[0] * 4));
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            int comparison = index.AsSpan(NameAt(middle), 20).SequenceCompareTo(wanted);
            if (comparison == 0) { offset = OffsetAt(middle); return true; }
            if (comparison < 0) low = middle + 1; else high = middle;
        }
        return false;
    }

    private int NameAt(int position) => 8 + FanoutBytes + position * 20;

    private long OffsetAt(int position)
    {
        int offsets = 8 + FanoutBytes + count * 24;
        uint small = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(offsets + position * 4));
        if ((small & 0x8000_0000) == 0) return small;
        int large = (int)(small & 0x7fff_ffff);
        long value = (long)BinaryPrimitives.ReadUInt64BigEndian(index.AsSpan(offsets + count * 4 + large * 8));
        Limits.Require(value >= 12 && value < packLength - 20, "pack-index-offset");
        return value;
    }

    /// <summary>The type and content size of the object at an offset, resolving a delta's type through its base.</summary>
    public (int Type, int Size) Describe(long offset)
    {
        Header header = ReadHeader(offset);
        if (header.Type <= 4)
        {
            Limits.Require(header.Size <= Limits.ObjectBytes, "object-size");
            return (header.Type, (int)header.Size);
        }
        // A delta's own header says how long the delta is; the result size is the second number inside it,
        // so a little of the delta is inflated to learn it. Its type is its base's type, at the end of the chain.
        byte[] prefix = InflatePrefix(header.DataOffset, (int)Math.Min(header.Size, 32));
        Cursor cursor = new(prefix);
        ReadVarint(cursor);
        long resultSize = ReadVarint(cursor);
        Limits.Require(resultSize <= Limits.ObjectBytes, "delta-size");
        int type = header.Type;
        long at = offset;
        for (int depth = 0; type > 4; depth++)
        {
            Limits.Require(depth < Limits.DeltaDepth, "delta-depth");
            Header current = ReadHeader(at);
            if (current.Type <= 4) { type = current.Type; break; }
            at = BaseOffset(at, current);
        }
        return (type, (int)resultSize);
    }

    /// <summary>Reads the whole object at an offset, applying its delta chain.</summary>
    public byte[] Read(long offset) => Read(offset, 0);

    private byte[] Read(long offset, int depth)
    {
        Limits.Require(depth <= Limits.DeltaDepth, "delta-depth");
        Header header = ReadHeader(offset);
        Limits.Require(header.Size <= Limits.ObjectBytes, "object-size");
        if (header.Type <= 4) return Inflate(header.DataOffset, (int)header.Size);
        long baseAt = BaseOffset(offset, header);
        byte[]? basis = CacheGet(baseAt);
        if (basis is null)
        {
            basis = Read(baseAt, depth + 1);
            CachePut(baseAt, basis);
        }
        byte[] delta = Inflate(header.DataOffset, (int)header.Size);
        return Pack.ApplyDelta(basis, delta);
    }

    private long BaseOffset(long offset, Header header)
    {
        if (header.Type == 6) return header.BaseOffset;
        Limits.Require(TryFindOffset(header.BaseId!, out long found), "delta-base-missing");
        return found;
    }

    private readonly record struct Header(int Type, long Size, long DataOffset, long BaseOffset, string? BaseId);

    private Header ReadHeader(long offset)
    {
        Limits.Require(offset >= 12 && offset < packLength - 20, "pack-offset");
        byte[] bytes = new byte[32];
        int available = RandomAccess.Read(handle, bytes, offset);
        Limits.Require(available > 0, "truncated");
        int at = 0;
        byte Next() { Limits.Require(at < available, "truncated"); return bytes[at++]; }
        byte first = Next();
        int type = (first >> 4) & 7;
        Limits.Require(type is >= 1 and <= 4 or 6 or 7, "object-type");
        long size = first & 15;
        int shift = 4;
        byte next = first;
        while ((next & 128) != 0)
        {
            Limits.Require(shift <= 60, "object-size");
            next = Next();
            size |= (long)(next & 127) << shift;
            shift += 7;
        }
        long baseOffset = 0;
        string? baseId = null;
        if (type == 6)
        {
            next = Next();
            long distance = next & 127;
            int groups = 1;
            while ((next & 128) != 0)
            {
                Limits.Require(++groups <= 9, "delta-offset");
                next = Next();
                distance = ((distance + 1) << 7) | (uint)(next & 127);
            }
            Limits.Require(distance > 0 && distance <= offset - 12, "delta-base-offset");
            baseOffset = offset - distance;
        }
        else if (type == 7)
        {
            byte[] id = new byte[20];
            Limits.Require(RandomAccess.Read(handle, id, offset + at) == 20, "truncated");
            at += 20;
            baseId = Convert.ToHexStringLower(id);
        }
        return new(type, size, offset + at, baseOffset, baseId);
    }

    private byte[] Inflate(long dataOffset, int size)
    {
        byte[] data = new byte[size];
        using PositionedStream source = new(handle, dataOffset, packLength - 20);
        using ZLibStream zlib = new(source, CompressionMode.Decompress);
        try
        {
            zlib.ReadExactly(data);
            Limits.Require(zlib.ReadByte() == -1, "inflated-size");
        }
        catch (InvalidDataException) { throw new GitInputException("zlib-data"); }
        catch (EndOfStreamException) { throw new GitInputException("zlib-truncated"); }
        return data;
    }

    private byte[] InflatePrefix(long dataOffset, int count)
    {
        byte[] data = new byte[count];
        using PositionedStream source = new(handle, dataOffset, packLength - 20);
        using ZLibStream zlib = new(source, CompressionMode.Decompress);
        int read = 0;
        try
        {
            while (read < count)
            {
                int got = zlib.Read(data, read, count - read);
                if (got == 0) break;
                read += got;
            }
        }
        catch (InvalidDataException) { throw new GitInputException("zlib-data"); }
        return read == count ? data : data[..read];
    }

    private static long ReadVarint(Cursor cursor)
    {
        long value = 0;
        for (int shift = 0; shift <= 28; shift += 7)
        {
            byte part = cursor.Byte();
            value |= (long)(part & 127) << shift;
            if ((part & 128) == 0) return value;
        }
        throw new GitInputException("delta-size");
    }

    private byte[]? CacheGet(long offset)
    {
        lock (cacheLock)
        {
            if (!cache.TryGetValue(offset, out byte[]? value)) return null;
            cacheOrder.Remove(offset);
            cacheOrder.AddLast(offset);
            return value;
        }
    }

    private void CachePut(long offset, byte[] value)
    {
        if (value.Length > CacheBytes / 4) return;
        lock (cacheLock)
        {
            if (cache.TryAdd(offset, value))
            {
                cacheOrder.AddLast(offset);
                cacheSize += value.Length;
            }
            while (cacheSize > CacheBytes && cacheOrder.First is { } oldest)
            {
                cacheSize -= cache[oldest.Value].Length;
                cache.Remove(oldest.Value);
                cacheOrder.RemoveFirst();
            }
        }
    }

    public void Dispose() => handle.Dispose();

    /// <summary>A forward-only view of the pack from one offset to the start of the trailer.</summary>
    private sealed class PositionedStream(SafeFileHandle handle, long start, long end) : Stream
    {
        private readonly long origin = start;
        private long position = start;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => end - origin;
        public override long Position { get => position - origin; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            long remaining = end - position;
            if (remaining <= 0 || buffer.Length == 0) return 0;
            int read = RandomAccess.Read(handle, buffer[..(int)Math.Min(buffer.Length, remaining)], position);
            position += read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
