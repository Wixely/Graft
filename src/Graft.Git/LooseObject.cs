using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Graft.Git;

/// <summary>
/// A loose object the way Git keeps one: the bytes <c>type size\0</c> followed by the content, the whole
/// thing a zlib stream, at <c>objects/xx/yyyy…</c>. Git opens these directly, which is the point: a
/// repository directory Graft writes is a bare repository any Git can read.
/// </summary>
public static class LooseObject
{
    /// <summary>Writes one object as a complete zlib member. Returns the number of bytes written.</summary>
    public static long Write(Stream output, int type, int size, Stream content, CancellationToken cancellationToken = default)
    {
        long start = output.CanSeek ? output.Position : 0;
        using (ZLibStream zlib = new(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(Encoding.ASCII.GetBytes(TypeName(type) + " " + size.ToString(CultureInfo.InvariantCulture) + "\0"));
            byte[] buffer = new byte[64 * 1024];
            int count;
            while ((count = content.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                zlib.Write(buffer.AsSpan(0, count));
            }
        }
        return output.CanSeek ? output.Position - start : -1;
    }

    /// <summary>Writes a new loose file at <paramref name="path"/> and returns it.</summary>
    public static void WriteFile(string path, int type, int size, Stream content, CancellationToken cancellationToken = default)
    {
        using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
        Write(output, type, size, content, cancellationToken);
    }

    /// <summary>Reads the header of a loose file: its type and size, without reading the content.</summary>
    public static (int Type, int Size) ReadHeader(string path)
    {
        using Stream content = OpenContent(path, out int type, out int size);
        return (type, size);
    }

    /// <summary>
    /// Opens the content of a loose file positioned after its header. The stream is forward-only; the
    /// header tells the caller what and how much is coming.
    /// </summary>
    public static Stream OpenContent(string path, out int type, out int size)
    {
        FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        ZLibStream zlib;
        try
        {
            Limits.Require(file.Length >= 2, "loose-object-size");
            zlib = new(file, CompressionMode.Decompress, leaveOpen: false);
            StringBuilder header = new();
            while (true)
            {
                int value;
                try { value = zlib.ReadByte(); }
                catch (InvalidDataException) { throw new GitInputException("loose-object-zlib"); }
                Limits.Require(value >= 0 && header.Length < 64, "loose-object-header");
                if (value == 0) break;
                header.Append((char)value);
            }
            string[] parts = header.ToString().Split(' ');
            Limits.Require(parts.Length == 2, "loose-object-header");
            type = parts[0] switch { "commit" => 1, "tree" => 2, "blob" => 3, "tag" => 4, _ => throw new GitInputException("loose-object-type") };
            Limits.Require(int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out size) && size <= Limits.ObjectBytes, "loose-object-header");
        }
        catch
        {
            file.Dispose();
            throw;
        }
        return new BoundedStream(zlib, size);
    }

    /// <summary>Reads a loose file's content, checking that it is exactly as long as its header claims.</summary>
    public static byte[] ReadData(string path, int expectedType, int expectedSize)
    {
        using Stream content = OpenContent(path, out int type, out int size);
        Limits.Require(type == expectedType && size == expectedSize, "loose-object-header");
        byte[] data = new byte[size];
        try
        {
            content.ReadExactly(data);
            Limits.Require(content.ReadByte() == -1, "loose-object-length");
        }
        catch (EndOfStreamException) { throw new GitInputException("loose-object-length"); }
        catch (InvalidDataException) { throw new GitInputException("loose-object-zlib"); }
        return data;
    }

    /// <summary>True when the file begins like a zlib stream, which the format Graft used before never did.</summary>
    public static bool LooksLikeZlib(string path)
    {
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        int first = file.ReadByte();
        int second = file.ReadByte();
        // CMF 0x78 is deflate with a 32K window, which every zlib writer of ordinary data produces, and
        // the two bytes together are a multiple of 31. The old format began with a type byte of 1 to 4.
        return first == 0x78 && second >= 0 && ((first << 8) | second) % 31 == 0;
    }

    public static string TypeName(int type) => type switch
    {
        1 => "commit", 2 => "tree", 3 => "blob", 4 => "tag",
        _ => throw new GitInputException("object-type")
    };

    /// <summary>A read-only view of the first <c>length</c> bytes of a stream, which closes the stream with it.</summary>
    private sealed class BoundedStream(Stream inner, long length) : Stream
    {
        private long position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            long remaining = length - position;
            if (remaining <= 0 || buffer.Length == 0) return 0;
            int read;
            try { read = inner.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]); }
            catch (InvalidDataException) { throw new GitInputException("loose-object-zlib"); }
            position += read;
            return read;
        }
        public override int ReadByte()
        {
            Span<byte> one = stackalloc byte[1];
            return Read(one) == 1 ? one[0] : -1;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
