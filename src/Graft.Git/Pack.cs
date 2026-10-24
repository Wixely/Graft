using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Graft.Git;

public sealed record GitObject
{
    private byte[]? data;
    private string? path;
    internal string? StoredPath => path;
    public int Type { get; }
    public int Size { get; }
    public GitObject(int type, byte[] data) { Type = type; this.data = data; Size = data.Length; }
    internal GitObject(int type, int size, string path) { Type = type; Size = size; this.path = path; }
    public byte[] Data
    {
        get
        {
            if (data is not null) return data;
            using Stream source = OpenRead();
            byte[] content = new byte[Size];
            source.ReadExactly(content);
            return content;
        }
    }
    public Stream OpenRead()
    {
        if (data is not null) return new MemoryStream(data, false);
        FileStream source = new(path!, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        source.Position = 1;
        return new ObjectContentStream(source);
    }
    internal void StoredAt(string destination) { path = destination; data = null; }
    internal void Spill(string directory)
    {
        string destination = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        using (FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write))
        {
            output.WriteByte((byte)Type);
            using Stream source = OpenRead();
            source.CopyTo(output);
        }
        StoredAt(destination);
    }
    private sealed class ObjectContentStream(FileStream source) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => source.Length - 1;
        public override long Position { get => source.Position - 1; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => source.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin)
        {
            long position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (position < 0) throw new IOException("Negative object position.");
            return source.Seek(position + 1, SeekOrigin.Begin) - 1;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) source.Dispose();
            base.Dispose(disposing);
        }
    }
    public string TypeName => Type switch { 1 => "commit", 2 => "tree", 3 => "blob", 4 => "tag", _ => throw new GitInputException("object-type") };
    public string Id
    {
        get
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            hash.AppendData(Encoding.ASCII.GetBytes(TypeName + " " + Size.ToString(CultureInfo.InvariantCulture) + "\0"));
            using Stream source = OpenRead();
            byte[] buffer = new byte[64 * 1024];
            int count;
            while ((count = source.Read(buffer)) != 0) hash.AppendData(buffer.AsSpan(0, count));
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }
}

public sealed record PackResult(Dictionary<string, GitObject> Objects, int OffsetDeltas, int ReferenceDeltas);

public static class Pack
{
    private sealed class Entry(long offset, int type, GitObject payload, long? baseOffset, string? baseId)
    {
        public long Offset { get; } = offset;
        public int Type { get; } = type;
        public GitObject Payload { get; } = payload;
        public long? BaseOffset { get; } = baseOffset;
        public string? BaseId { get; } = baseId;
        public GitObject? Object { get; set; }
        public int Depth { get; set; }
    }

    public static PackResult Read(byte[] bytes, IReadOnlyDictionary<string, GitObject> existing, CancellationToken cancellationToken = default)
    {
        using MemoryStream source = new(bytes, false);
        return Read(source, existing, cancellationToken);
    }

    public static PackResult Read(Stream source, IReadOnlyDictionary<string, GitObject> existing,
        CancellationToken cancellationToken = default, string? stagingDirectory = null,
        long expandedBudget = Limits.PackExpandedBytes)
    {
        Limits.Require(expandedBudget is > 0 and <= Limits.PackExpandedBytes, "expanded-budget");
        long start = source.Position;
        Limits.Require(source.Length - start is >= 32 and <= Limits.RequestBytes, "pack-length");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] buffer = new byte[64 * 1024];
        long remaining = source.Length - start - 20;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            Limits.Require(read > 0, "truncated");
            hash.AppendData(buffer.AsSpan(0, read));
            remaining -= read;
        }
        byte[] checksum = new byte[20];
        source.ReadExactly(checksum);
        Limits.Require(hash.GetHashAndReset().AsSpan().SequenceEqual(checksum), "pack-checksum");
        var cursor = new Cursor(source, start, source.Length - 20);
        Limits.Require(cursor.Take(4).AsSpan().SequenceEqual("PACK"u8), "pack-magic");
        Limits.Require(BinaryPrimitives.ReadUInt32BigEndian(cursor.Take(4)) == 2, "pack-version");
        var count = BinaryPrimitives.ReadUInt32BigEndian(cursor.Take(4));
        Limits.Require(count <= Limits.ObjectCount, "object-count");
        var entries = new Dictionary<long, Entry>();
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
            Limits.Require(size <= Limits.ObjectBytes && (expanded += size) <= expandedBudget, "expanded-limit");
            long? baseOffset = null;
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
                Limits.Require(distance > 0 && distance < offset && entries.ContainsKey(offset - distance), "delta-base-offset");
                baseOffset = offset - distance;
            }
            if (type == 7) baseId = Convert.ToHexStringLower(cursor.Take(20));

            // A single-byte source prevents ZLibStream read-ahead from consuming the
            // next pack entry. Slow but exact and bounded; optimize only with evidence.
            using var exactSource = new ExactInput(cursor, cancellationToken);
            using var zlib = new ZLibStream(exactSource, CompressionMode.Decompress, leaveOpen: true);
            var data = new byte[(int)size];
            try
            {
                zlib.ReadExactly(data);
                Limits.Require(zlib.ReadByte() == -1, "inflated-size");
            }
            catch (InvalidDataException) { throw new GitInputException("zlib-data"); }
            catch (EndOfStreamException) { throw new GitInputException("zlib-truncated"); }
            GitObject payload = new(type, data);
            if (stagingDirectory is not null) payload.Spill(stagingDirectory);
            entries.Add(offset, new Entry(offset, type, payload, baseOffset, baseId));
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
                if (entry.Type <= 4) obj = entry.Payload;
                else
                {
                    GitObject? basis = null;
                    var depth = 0;
                    if (entry.BaseOffset is long baseAt)
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
                    var data = ApplyDelta(basis.Data, entry.Payload.Data);
                    Limits.Require((expanded += data.Length) <= expandedBudget, "expanded-limit");
                    obj = new GitObject(basis.Type, data);
                    if (stagingDirectory is not null) obj.Spill(stagingDirectory);
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
        using var output = new MemoryStream();
        Write(output, objects, cancellationToken);
        return output.ToArray();
    }

    public static void Write(Stream output, IEnumerable<GitObject> objects, CancellationToken cancellationToken = default)
    {
        var items = objects.ToArray();
        Limits.Require(items.Length <= Limits.ObjectCount, "object-count");
        long start = output.Position;
        output.Write("PACK"u8);
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, 2); output.Write(number);
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)items.Length); output.Write(number);
        foreach (var obj in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteHeader(output, obj.Type, obj.Size);
            if (obj.Size == 0) Compress(output, []);
            else
            {
                using Stream source = obj.OpenRead();
                using var compressed = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true);
                byte[] buffer = new byte[64 * 1024];
                int count;
                while ((count = source.Read(buffer)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    compressed.Write(buffer.AsSpan(0, count));
                }
            }
            Limits.Require(output.Position - start <= Limits.RequestBytes - 20, "output-limit");
        }
        long end = output.Position;
        output.Position = start;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] hashingBuffer = new byte[64 * 1024];
        while (output.Position < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = output.Read(hashingBuffer, 0, (int)Math.Min(hashingBuffer.Length, end - output.Position));
            Limits.Require(count > 0, "truncated");
            hash.AppendData(hashingBuffer.AsSpan(0, count));
        }
        output.Write(hash.GetHashAndReset());
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
