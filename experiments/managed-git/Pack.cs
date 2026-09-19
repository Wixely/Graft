using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Graft.ManagedGit;

public sealed record GitObject(int Type, byte[] Data)
{
    public string TypeName => Type switch { 1 => "commit", 2 => "tree", 3 => "blob", 4 => "tag", _ => throw new GitInputException("object-type") };
    public string Id
    {
        get
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            hash.AppendData(Encoding.ASCII.GetBytes(TypeName + " " + Data.Length.ToString(CultureInfo.InvariantCulture) + "\0"));
            hash.AppendData(Data);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }
}

public sealed record PackResult(Dictionary<string, GitObject> Objects, int OffsetDeltas, int ReferenceDeltas);

public static class Pack
{
    private sealed class Entry(int offset, int type, byte[] data, int? baseOffset, string? baseId)
    {
        public int Offset { get; } = offset;
        public int Type { get; } = type;
        public byte[] Data { get; } = data;
        public int? BaseOffset { get; } = baseOffset;
        public string? BaseId { get; } = baseId;
        public GitObject? Object { get; set; }
        public int Depth { get; set; }
    }

    public static PackResult Read(byte[] bytes, IReadOnlyDictionary<string, GitObject> existing, CancellationToken cancellationToken = default)
    {
        Limits.Require(bytes.Length is >= 32 and <= Limits.RequestBytes, "pack-length");
        Limits.Require(bytes.AsSpan(0, 4).SequenceEqual("PACK"u8), "pack-magic");
        Limits.Require(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4)) == 2, "pack-version");
        Limits.Require(SHA1.HashData(bytes.AsSpan(0, bytes.Length - 20)).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - 20)), "pack-checksum");
        var count = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4));
        Limits.Require(count <= Limits.ObjectCount, "object-count");
        var cursor = new Cursor(bytes, 12, bytes.Length - 20);
        var entries = new Dictionary<int, Entry>();
        long expanded = 0;
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = cursor.Position;
            var first = cursor.Byte();
            var type = (first >> 4) & 7;
            Limits.Require(type is >= 1 and <= 4 or 6 or 7, "object-type");
            long size = first & 15;
            var shift = 4;
            var next = first;
            while ((next & 128) != 0)
            {
                Limits.Require(shift <= 25, "object-size");
                next = cursor.Byte();
                size |= (long)(next & 127) << shift;
                shift += 7;
            }
            Limits.Require(size <= Limits.ObjectBytes && (expanded += size) <= Limits.ExpandedBytes, "expanded-limit");
            int? baseOffset = null;
            string? baseId = null;
            if (type == 6)
            {
                next = cursor.Byte();
                long distance = next & 127;
                var groups = 1;
                while ((next & 128) != 0)
                {
                    Limits.Require(++groups <= 5, "delta-offset");
                    next = cursor.Byte();
                    distance = ((distance + 1) << 7) | (uint)(next & 127);
                }
                Limits.Require(distance > 0 && distance < offset && entries.ContainsKey(offset - (int)distance), "delta-base-offset");
                baseOffset = offset - (int)distance;
            }
            if (type == 7) baseId = Convert.ToHexStringLower(cursor.Take(20));

            // A single-byte source prevents ZLibStream read-ahead from consuming the
            // next pack entry. Slow but exact and bounded; optimize only with evidence.
            using var source = new ExactInput(cursor, cancellationToken);
            using var zlib = new ZLibStream(source, CompressionMode.Decompress, leaveOpen: true);
            var data = new byte[(int)size];
            try
            {
                zlib.ReadExactly(data);
                Limits.Require(zlib.ReadByte() == -1, "inflated-size");
            }
            catch (InvalidDataException) { throw new GitInputException("zlib-data"); }
            catch (EndOfStreamException) { throw new GitInputException("zlib-truncated"); }
            entries.Add(offset, new Entry(offset, type, data, baseOffset, baseId));
        }
        Limits.Require(cursor.Remaining == 0, "pack-trailing-data");
        var result = new Dictionary<string, GitObject>(StringComparer.Ordinal);
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var pass = 0; pass <= Limits.DeltaDepth; pass++)
        {
            var progress = false;
            foreach (var entry in entries.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Object is not null) continue;
                GitObject obj;
                if (entry.Type <= 4) obj = new GitObject(entry.Type, entry.Data);
                else
                {
                    GitObject? basis = null;
                    var depth = 0;
                    if (entry.BaseOffset is int baseAt)
                    {
                        basis = entries[baseAt].Object;
                        depth = entries[baseAt].Depth;
                    }
                    else if (entry.BaseId is string id)
                    {
                        if (result.TryGetValue(id, out basis)) depth = depths[id];
                        else existing.TryGetValue(id, out basis);
                    }
                    if (basis is null) continue;
                    entry.Depth = depth + 1;
                    Limits.Require(entry.Depth <= Limits.DeltaDepth, "delta-depth");
                    var data = ApplyDelta(basis.Data, entry.Data);
                    Limits.Require((expanded += data.Length) <= Limits.ExpandedBytes, "expanded-limit");
                    obj = new GitObject(basis.Type, data);
                }
                var objectId = obj.Id;
                if (existing.TryGetValue(objectId, out var previous) || result.TryGetValue(objectId, out previous))
                    Limits.Require(previous.Type == obj.Type && previous.Data.AsSpan().SequenceEqual(obj.Data), "object-collision");
                result[objectId] = obj;
                depths[objectId] = entry.Depth;
                entry.Object = obj;
                progress = true;
            }
            if (entries.Values.All(e => e.Object is not null))
                return new PackResult(result, entries.Values.Count(e => e.Type == 6), entries.Values.Count(e => e.Type == 7));
            Limits.Require(progress, "missing-delta-base");
        }
        throw new GitInputException("delta-depth");
    }

    public static byte[] ApplyDelta(byte[] basis, byte[] delta)
    {
        var cursor = new Cursor(delta);
        int Size()
        {
            long value = 0;
            for (var shift = 0; shift <= 28; shift += 7)
            {
                var part = cursor.Byte();
                value |= (long)(part & 127) << shift;
                Limits.Require(value <= Limits.ObjectBytes, "delta-size");
                if ((part & 128) == 0) return (int)value;
            }
            throw new GitInputException("delta-size");
        }
        Limits.Require(Size() == basis.Length, "delta-source-size");
        var output = new byte[Size()];
        var written = 0;
        while (cursor.Remaining > 0)
        {
            var op = cursor.Byte();
            if ((op & 128) == 0)
            {
                Limits.Require(op > 0 && op <= output.Length - written, "delta-insert");
                cursor.Take(op).CopyTo(output, written);
                written += op;
                continue;
            }
            uint offset = 0;
            var length = 0;
            for (var i = 0; i < 4; i++) if ((op & (1 << i)) != 0) offset |= (uint)cursor.Byte() << (i * 8);
            for (var i = 0; i < 3; i++) if ((op & (1 << (i + 4))) != 0) length |= cursor.Byte() << (i * 8);
            if (length == 0) length = 65536;
            Limits.Require(offset <= basis.Length && length <= basis.Length - (long)offset && length <= output.Length - written, "delta-copy");
            basis.AsSpan((int)offset, length).CopyTo(output.AsSpan(written));
            written += length;
        }
        Limits.Require(written == output.Length, "delta-result-size");
        return output;
    }

    public static byte[] Write(IEnumerable<GitObject> objects, CancellationToken cancellationToken = default)
    {
        var items = objects.ToArray();
        Limits.Require(items.Length <= Limits.ObjectCount, "object-count");
        using var output = new MemoryStream();
        output.Write("PACK"u8);
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, 2); output.Write(number);
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)items.Length); output.Write(number);
        foreach (var obj in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteHeader(output, obj.Type, obj.Data.Length);
            Compress(output, obj.Data);
            Limits.Require(output.Length <= Limits.ExpandedBytes, "output-limit");
        }
        output.Write(SHA1.HashData(output.GetBuffer().AsSpan(0, (int)output.Length)));
        return output.ToArray();
    }

    public static void WriteHeader(Stream output, int type, int size)
    {
        var first = (type << 4) | (size & 15);
        size >>= 4;
        output.WriteByte((byte)(first | (size > 0 ? 128 : 0)));
        while (size > 0)
        {
            var part = size & 127; size >>= 7;
            output.WriteByte((byte)(part | (size > 0 ? 128 : 0)));
        }
    }

    public static void Compress(Stream output, byte[] data)
    {
        // A never-started ZLibStream can emit no bytes on disposal. Git still needs
        // a complete zlib member for empty trees/blobs: empty DEFLATE + Adler-32.
        if (data.Length == 0) { output.Write(new byte[] { 0x78, 0x9c, 3, 0, 0, 0, 0, 1 }); return; }
        using var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true);
        zlib.Write(data);
    }

    private sealed class ExactInput(Cursor cursor, CancellationToken cancellationToken) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length == 0) return 0;
            // ZLibStream may treat source EOF as successful completion even when
            // the zlib trailer is missing. A complete member finishes without
            // reading past its trailer; turn premature source EOF into rejection.
            Limits.Require(cursor.Remaining > 0, "zlib-truncated");
            buffer[0] = cursor.Byte(); return 1;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
