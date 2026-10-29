using System.Globalization;
using System.Text;

namespace Graft.Git;

public sealed class GitInputException(string message) : Exception(message);

public static class Limits
{
    public const long RequestBytes = 5L * 1024 * 1024 * 1024;
    public const int ObjectBytes = 64 * 1024 * 1024;
    public const long ExpandedBytes = 4L * 1024 * 1024 * 1024;
    public const long PackExpandedBytes = 2 * ExpandedBytes;
    public const int ObjectCount = 1_000_000;
    public const int RefCount = 4096;
    public const int DeltaDepth = 32;
    public const string Zero = "0000000000000000000000000000000000000000";
    public static void Require(bool value, string code)
    {
        if (!value) throw new GitInputException(code);
    }
    public static bool IsId(string text) => text.Length == 40 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    /// <summary>
    /// A reference this engine will store. Branches and tags come from clients; <c>refs/merge-requests/</c>
    /// is written by the server alone, to hold the tip of a proposal from a fork where the target
    /// repository can read it. A client may fetch one and may not push one; <see cref="IsClientRef"/>
    /// is the narrower check the receive path applies.
    /// </summary>
    public static bool IsRef(string text) => text.Length <= 240 &&
        (text.StartsWith("refs/heads/", StringComparison.Ordinal) || text.StartsWith("refs/tags/", StringComparison.Ordinal) ||
         text.StartsWith("refs/merge-requests/", StringComparison.Ordinal)) &&
        IsRefShape(text);

    public static bool IsClientRef(string text) => IsRef(text) && !text.StartsWith("refs/merge-requests/", StringComparison.Ordinal);

    private static bool IsRefShape(string text) =>
        !text.Contains("..", StringComparison.Ordinal) && !text.Contains("@{", StringComparison.Ordinal) &&
        text.All(c => c is >= '!' and <= '~' && !" ~^:?*[\\".Contains(c)) &&
        text.Split('/').All(p => p.Length > 0 && p[0] != '.' && !p.EndsWith('.') && !p.EndsWith(".lock", StringComparison.Ordinal));
}

public sealed class Cursor
{
    private readonly Stream source;
    public Cursor(byte[] bytes, int start = 0, int? end = null)
        : this(new MemoryStream(bytes, false), start, end ?? bytes.Length) { }
    public Cursor(Stream source, long start, long end)
    {
        this.source = source;
        source.Position = start;
        End = end;
    }
    public long Position => source.Position;
    public long End { get; }
    public long Remaining => End - Position;
    public byte Byte()
    {
        Limits.Require(Remaining > 0, "truncated");
        int value = source.ReadByte();
        Limits.Require(value >= 0, "truncated");
        return (byte)value;
    }
    public byte[] Take(int count)
    {
        Limits.Require(count >= 0 && count <= Remaining, "truncated");
        var result = new byte[count];
        try { source.ReadExactly(result); }
        catch (EndOfStreamException) { throw new GitInputException("truncated"); }
        return result;
    }
    /// <summary>A packet line of any kind: data, the flush <c>0000</c>, the delimiter <c>0001</c> or the response end <c>0002</c>.</summary>
    public (PacketKind Kind, string? Text) Line()
    {
        var header = Encoding.ASCII.GetString(Take(4));
        Limits.Require(int.TryParse(header, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var length), "packet-header");
        if (length == 0) return (PacketKind.Flush, null);
        if (length == 1) return (PacketKind.Delimiter, null);
        if (length == 2) return (PacketKind.ResponseEnd, null);
        Limits.Require(length is >= 4 and <= 65520, "packet-length");
        var payload = Take(length - 4);
        Limits.Require(payload.All(b => b <= 127), "packet-encoding");
        return (PacketKind.Data, Encoding.ASCII.GetString(payload));
    }

    public string? Packet()
    {
        var header = Encoding.ASCII.GetString(Take(4));
        Limits.Require(int.TryParse(header, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var length), "packet-header");
        if (length == 0) return null;
        Limits.Require(length is >= 4 and <= 65520, "packet-length");
        var payload = Take(length - 4);
        Limits.Require(payload.All(b => b <= 127), "packet-encoding");
        return Encoding.ASCII.GetString(payload);
    }
}

public static class Packet
{
    public static void Write(Stream stream, string text)
    {
        var data = Encoding.ASCII.GetBytes(text);
        Limits.Require(data.Length <= 65516, "packet-length");
        stream.Write(Encoding.ASCII.GetBytes((data.Length + 4).ToString("x4", CultureInfo.InvariantCulture)));
        stream.Write(data);
    }
    public static void Flush(Stream stream) => stream.Write("0000"u8);
    public static void Delimiter(Stream stream) => stream.Write("0001"u8);

    /// <summary>A sideband packet: the band number followed by raw bytes, at most 65515 of them.</summary>
    public static void WriteBand(Stream stream, byte band, ReadOnlySpan<byte> data)
    {
        Limits.Require(data.Length <= 65515, "packet-length");
        stream.Write(Encoding.ASCII.GetBytes((data.Length + 5).ToString("x4", CultureInfo.InvariantCulture)));
        stream.WriteByte(band);
        stream.Write(data);
    }
}

public enum PacketKind
{
    Data,
    Flush,
    Delimiter,
    ResponseEnd
}
