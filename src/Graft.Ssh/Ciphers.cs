using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Graft.Ssh;

/// <summary>
/// One direction of the binary packet protocol (RFC 4253 section 6): frames a payload into a packet on the
/// way out and recovers a payload from the wire on the way in, encrypting and authenticating as negotiated.
/// Each direction has its own instance, sequence number and keys.
/// </summary>
internal abstract class PacketCipher
{
    /// <summary>The largest packet accepted from the peer, OpenSSH's own limit.</summary>
    public const int MaximumPacketBytes = 256 * 1024;

    protected const int MinimumPadding = 4;

    public abstract byte[] Seal(ReadOnlySpan<byte> payload, uint sequence);
    public abstract Task<byte[]> OpenAsync(Stream stream, uint sequence, CancellationToken cancellationToken);

    /// <summary>Lays out padding_length, payload and random padding so the total is a multiple of the block
    /// size, counting or not counting the length field as the cipher's framing requires.</summary>
    protected static byte[] Frame(ReadOnlySpan<byte> payload, int blockSize, bool lengthCounts)
    {
        int covered = (lengthCounts ? 4 : 0) + 1 + payload.Length;
        int padding = blockSize - covered % blockSize;
        if (padding < MinimumPadding) padding += blockSize;
        byte[] packet = new byte[4 + 1 + payload.Length + padding];
        BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)(1 + payload.Length + padding));
        packet[4] = (byte)padding;
        payload.CopyTo(packet.AsSpan(5));
        RandomNumberGenerator.Fill(packet.AsSpan(5 + payload.Length, padding));
        return packet;
    }

    protected static byte[] Payload(ReadOnlySpan<byte> packetBody)
    {
        // packetBody is padding_length || payload || padding.
        if (packetBody.Length < 1) throw new SshProtocolException("empty packet");
        int padding = packetBody[0];
        if (padding < MinimumPadding || padding >= packetBody.Length) throw new SshProtocolException("bad padding");
        return packetBody[1..^padding].ToArray();
    }

    protected static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        try { await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false); }
        catch (EndOfStreamException) { throw new SshProtocolException("connection closed"); }
        return buffer;
    }

    protected static void CheckLength(uint length, int blockSize, bool lengthCounts)
    {
        if (length < 5 || length > MaximumPacketBytes) throw new SshProtocolException("bad packet length");
        if (((lengthCounts ? 4 : 0) + length) % blockSize != 0) throw new SshProtocolException("packet not block aligned");
    }
}

/// <summary>Before the first key exchange completes: no encryption, block size 8.</summary>
internal sealed class PlainCipher : PacketCipher
{
    public override byte[] Seal(ReadOnlySpan<byte> payload, uint sequence) => Frame(payload, 8, lengthCounts: true);

    public override async Task<byte[]> OpenAsync(Stream stream, uint sequence, CancellationToken cancellationToken)
    {
        byte[] header = await ReadExactlyAsync(stream, 4, cancellationToken).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        CheckLength(length, 8, lengthCounts: true);
        return Payload(await ReadExactlyAsync(stream, (int)length, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>
/// <c>aes128-gcm@openssh.com</c> and <c>aes256-gcm@openssh.com</c>: the length field travels in clear as
/// associated data, the rest is encrypted and the 16-byte tag authenticates both. The nonce is the
/// negotiated IV whose last eight bytes count packets.
/// </summary>
internal sealed class GcmCipher(byte[] key, byte[] iv) : PacketCipher, IDisposable
{
    private readonly AesGcm aes = new(key, 16);
    private readonly byte[] nonce = (byte[])iv.Clone();

    public override byte[] Seal(ReadOnlySpan<byte> payload, uint sequence)
    {
        byte[] packet = Frame(payload, 16, lengthCounts: false);
        byte[] result = new byte[packet.Length + 16];
        packet.AsSpan(0, 4).CopyTo(result);
        aes.Encrypt(nonce, packet.AsSpan(4), result.AsSpan(4, packet.Length - 4), result.AsSpan(packet.Length, 16), packet.AsSpan(0, 4));
        Advance();
        return result;
    }

    public override async Task<byte[]> OpenAsync(Stream stream, uint sequence, CancellationToken cancellationToken)
    {
        byte[] header = await ReadExactlyAsync(stream, 4, cancellationToken).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        CheckLength(length, 16, lengthCounts: false);
        byte[] body = await ReadExactlyAsync(stream, (int)length + 16, cancellationToken).ConfigureAwait(false);
        byte[] plain = new byte[length];
        try { aes.Decrypt(nonce, body.AsSpan(0, (int)length), body.AsSpan((int)length, 16), plain, header); }
        catch (AuthenticationTagMismatchException) { throw new SshProtocolException("packet authentication failed"); }
        Advance();
        return Payload(plain);
    }

    private void Advance()
    {
        ulong counter = BinaryPrimitives.ReadUInt64BigEndian(nonce.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter + 1);
    }

    public void Dispose() => aes.Dispose();
}

/// <summary>
/// <c>aes128-ctr</c> and <c>aes256-ctr</c> with <c>hmac-sha2-256</c>, the fallback for a client without
/// GCM. The counter runs across packets; the MAC covers the sequence number and the whole plaintext packet.
/// </summary>
internal sealed class CtrCipher(byte[] key, byte[] iv, byte[] macKey) : PacketCipher, IDisposable
{
    private readonly Aes aes = CreateAes(key);
    private readonly byte[] counter = (byte[])iv.Clone();
    private readonly IncrementalHash mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKey);

    private static Aes CreateAes(byte[] key)
    {
        Aes aes = Aes.Create();
        aes.Key = key;
        return aes;
    }

    public override byte[] Seal(ReadOnlySpan<byte> payload, uint sequence)
    {
        byte[] packet = Frame(payload, 16, lengthCounts: true);
        byte[] result = new byte[packet.Length + 32];
        Span<byte> sequenceBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(sequenceBytes, sequence);
        mac.AppendData(sequenceBytes);
        mac.AppendData(packet);
        mac.GetHashAndReset(result.AsSpan(packet.Length, 32));
        Transform(packet, result.AsSpan(0, packet.Length));
        return result;
    }

    public override async Task<byte[]> OpenAsync(Stream stream, uint sequence, CancellationToken cancellationToken)
    {
        byte[] first = await ReadExactlyAsync(stream, 16, cancellationToken).ConfigureAwait(false);
        byte[] firstPlain = new byte[16];
        Transform(first, firstPlain);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(firstPlain);
        CheckLength(length, 16, lengthCounts: true);
        int rest = (int)length + 4 - 16;
        byte[] remainder = await ReadExactlyAsync(stream, rest + 32, cancellationToken).ConfigureAwait(false);
        byte[] plain = new byte[4 + length];
        firstPlain.CopyTo(plain, 0);
        Transform(remainder.AsSpan(0, rest), plain.AsSpan(16));
        Span<byte> sequenceBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(sequenceBytes, sequence);
        mac.AppendData(sequenceBytes);
        mac.AppendData(plain);
        Span<byte> expected = stackalloc byte[32];
        mac.GetHashAndReset(expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, remainder.AsSpan(rest, 32)))
            throw new SshProtocolException("packet authentication failed");
        return Payload(plain.AsSpan(4));
    }

    private void Transform(ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> block = stackalloc byte[16];
        for (int offset = 0; offset < input.Length; offset += 16)
        {
            aes.EncryptEcb(counter, block, PaddingMode.None);
            int count = Math.Min(16, input.Length - offset);
            for (int i = 0; i < count; i++) output[offset + i] = (byte)(input[offset + i] ^ block[i]);
            for (int i = 15; i >= 0; i--) if (++counter[i] != 0) break;
        }
    }

    public void Dispose()
    {
        aes.Dispose();
        mac.Dispose();
    }
}
