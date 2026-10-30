using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Graft.Ssh;

/// <summary>A protocol violation by the peer, or a limit this server imposes. Ends the connection.</summary>
public sealed class SshProtocolException(string message) : Exception(message);

/// <summary>
/// Builds the data types of RFC 4251 section 5 in order: bytes, booleans, 32-bit integers, strings with a
/// length prefix, multiple-precision integers and comma-separated name lists.
/// </summary>
public sealed class SshWriter
{
    private readonly MemoryStream buffer = new();

    public SshWriter Byte(byte value) { buffer.WriteByte(value); return this; }
    public SshWriter Boolean(bool value) => Byte(value ? (byte)1 : (byte)0);

    public SshWriter UInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        buffer.Write(bytes);
        return this;
    }

    public SwWriterRaw Raw => new(buffer);

    public SshWriter Bytes(ReadOnlySpan<byte> value) { buffer.Write(value); return this; }

    public SshWriter String(ReadOnlySpan<byte> value)
    {
        UInt32((uint)value.Length);
        buffer.Write(value);
        return this;
    }

    public SshWriter String(string value) => String(Encoding.UTF8.GetBytes(value));

    public SshWriter NameList(IEnumerable<string> names) => String(string.Join(',', names));

    /// <summary>A non-negative integer in two's complement big-endian form with no leading zero bytes, except
    /// the one that keeps the top bit clear, exactly as the format requires.</summary>
    public SshWriter MPInt(ReadOnlySpan<byte> unsignedBigEndian)
    {
        int start = 0;
        while (start < unsignedBigEndian.Length && unsignedBigEndian[start] == 0) start++;
        ReadOnlySpan<byte> trimmed = unsignedBigEndian[start..];
        if (trimmed.Length == 0) return UInt32(0);
        bool pad = (trimmed[0] & 0x80) != 0;
        UInt32((uint)(trimmed.Length + (pad ? 1 : 0)));
        if (pad) buffer.WriteByte(0);
        buffer.Write(trimmed);
        return this;
    }

    public SshWriter MPInt(BigInteger value) => MPInt(value.ToByteArray(isUnsigned: true, isBigEndian: true));

    public byte[] ToArray() => buffer.ToArray();
    public int Length => (int)buffer.Length;

    /// <summary>Appends bytes without a length prefix; used while building the exchange hash input.</summary>
    public readonly struct SwWriterRaw(MemoryStream buffer)
    {
        public void Write(ReadOnlySpan<byte> value) => buffer.Write(value);
    }
}

/// <summary>Reads the same types back, refusing anything that runs past the end.</summary>
public sealed class SshReader(ReadOnlyMemory<byte> data)
{
    private int position;

    public int Remaining => data.Length - position;
    public bool AtEnd => position >= data.Length;

    public byte Byte()
    {
        if (position >= data.Length) throw new SshProtocolException("truncated message");
        return data.Span[position++];
    }

    public bool Boolean() => Byte() != 0;

    public uint UInt32()
    {
        if (data.Length - position < 4) throw new SshProtocolException("truncated message");
        uint value = BinaryPrimitives.ReadUInt32BigEndian(data.Span.Slice(position, 4));
        position += 4;
        return value;
    }

    public ReadOnlyMemory<byte> Bytes(int count)
    {
        if (count < 0 || data.Length - position < count) throw new SshProtocolException("truncated message");
        ReadOnlyMemory<byte> value = data.Slice(position, count);
        position += count;
        return value;
    }

    public ReadOnlyMemory<byte> String()
    {
        uint length = UInt32();
        if (length > int.MaxValue) throw new SshProtocolException("string too long");
        return Bytes((int)length);
    }

    public string Text()
    {
        ReadOnlyMemory<byte> bytes = String();
        try { return new UTF8Encoding(false, true).GetString(bytes.Span); }
        catch (DecoderFallbackException) { throw new SshProtocolException("string is not UTF-8"); }
    }

    public string[] NameList()
    {
        string text = Text();
        return text.Length == 0 ? [] : text.Split(',');
    }

    /// <summary>The integer's unsigned big-endian bytes, with the sign-padding zero removed.</summary>
    public byte[] MPInt()
    {
        ReadOnlySpan<byte> raw = String().Span;
        if (raw.Length > 0 && (raw[0] & 0x80) != 0) throw new SshProtocolException("negative integer");
        int start = 0;
        while (start < raw.Length - 1 && raw[start] == 0) start++;
        return raw[start..].ToArray();
    }

    public void ExpectEnd()
    {
        if (!AtEnd) throw new SshProtocolException("trailing bytes in message");
    }
}
