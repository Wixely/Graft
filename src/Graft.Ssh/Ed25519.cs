using System.Numerics;
using System.Security.Cryptography;

namespace Graft.Ssh;

/// <summary>
/// Ed25519 signature verification, RFC 8032 section 5.1.7, with nothing else: no key generation and no
/// signing, because this server only ever checks what a client signed.
///
/// <para>
/// Written here because .NET 10 has no public Ed25519 and most SSH keys made in the last decade are Ed25519.
/// Verification holds no secret, so a slow or non-constant-time implementation leaks nothing; what matters is
/// that it accepts exactly the signatures the reference accepts, and the acceptance suite checks it against
/// signatures OpenSSH itself produced, including one with a bit flipped.
/// </para>
///
/// <para>
/// Arithmetic is <see cref="BigInteger"/> on extended twisted Edwards coordinates. The check is the
/// unbatched one, <c>[S]B = R + [k]A</c>, which is what OpenSSH's own verifier does.
/// </para>
/// </summary>
public static class Ed25519
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    private static readonly BigInteger I = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly Point B = Decode(Convert.FromHexString("5866666666666666666666666666666666666666666666666666666666666666"))!.Value;

    /// <summary>Extended coordinates: x = X/Z, y = Y/Z, T = XY/Z.</summary>
    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    /// <summary>True when <paramref name="signature"/> is a valid signature of <paramref name="message"/> by
    /// the 32-byte public key <paramref name="publicKey"/>. Malformed inputs are simply invalid.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;
        Point? a = Decode(publicKey);
        Point? r = Decode(signature[..32]);
        if (a is null || r is null) return false;
        BigInteger s = new(signature[32..], isUnsigned: true, isBigEndian: false);
        if (s >= L) return false;

        byte[] toHash = new byte[64 + message.Length];
        signature[..32].CopyTo(toHash);
        publicKey.CopyTo(toHash.AsSpan(32));
        message.CopyTo(toHash.AsSpan(64));
        BigInteger k = new BigInteger(SHA512.HashData(toHash), isUnsigned: true, isBigEndian: false) % L;

        Point left = Multiply(B, s);
        Point right = Add(r.Value, Multiply(a.Value, k));
        return Equal(left, right);
    }

    private static BigInteger Mod(BigInteger value)
    {
        BigInteger result = value % P;
        return result.Sign < 0 ? result + P : result;
    }

    private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);

    /// <summary>Decompresses an encoded point, or returns null when the encoding is not a point on the curve.</summary>
    private static Point? Decode(ReadOnlySpan<byte> encoded)
    {
        byte[] bytes = encoded.ToArray();
        int sign = bytes[31] >> 7;
        bytes[31] &= 0x7f;
        BigInteger y = new(bytes, isUnsigned: true, isBigEndian: false);
        if (y >= P) return null;

        // x^2 = (y^2 - 1) / (d y^2 + 1)
        BigInteger y2 = Mod(y * y);
        BigInteger u = Mod(y2 - 1);
        BigInteger v = Mod(D * y2 + 1);
        BigInteger x = Mod(u * Inverse(v));
        BigInteger candidate = BigInteger.ModPow(x, (P + 3) / 8, P);
        if (Mod(candidate * candidate) != x)
        {
            candidate = Mod(candidate * I);
            if (Mod(candidate * candidate) != x) return null;
        }
        if (candidate.IsZero && sign == 1) return null;
        if ((int)(candidate & 1) != sign) candidate = P - candidate;
        return new(candidate, y, BigInteger.One, Mod(candidate * y));
    }

    /// <summary>Unified addition on the extended coordinates (RFC 8032 section 5.1.4).</summary>
    private static Point Add(Point p, Point q)
    {
        BigInteger a = Mod((p.Y - p.X) * (q.Y - q.X));
        BigInteger b = Mod((p.Y + p.X) * (q.Y + q.X));
        BigInteger c = Mod(2 * p.T * q.T * D);
        BigInteger d = Mod(2 * p.Z * q.Z);
        BigInteger e = b - a;
        BigInteger f = d - c;
        BigInteger g = d + c;
        BigInteger h = b + a;
        return new(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point Multiply(Point point, BigInteger scalar)
    {
        Point result = new(BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero);
        Point addend = point;
        while (!scalar.IsZero)
        {
            if (!scalar.IsEven) result = Add(result, addend);
            addend = Add(addend, addend);
            scalar >>= 1;
        }
        return result;
    }

    private static bool Equal(Point p, Point q) =>
        Mod(p.X * q.Z) == Mod(q.X * p.Z) && Mod(p.Y * q.Z) == Mod(q.Y * p.Z);
}
