using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Graft.Core;

/// <summary>
/// Time-based one-time passwords, RFC 6238 over RFC 4226: HMAC-SHA1 of the thirty-second step, six digits.
/// The parameters every authenticator app assumes when a URI names none, so an enrolment needs nothing
/// but the secret. Verification accepts the step either side of now, which is the usual allowance for a
/// phone's clock.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    public const int SecretBytes = 20;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>The code for one counter value; exposed so the test vectors of RFC 6238 can be checked.</summary>
    public static string Compute(ReadOnlySpan<byte> secret, long counter)
    {
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, message, hash);
        int offset = hash[19] & 0x0f;
        int binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Compute(ReadOnlySpan<byte> secret, DateTimeOffset at) => Compute(secret, at.ToUnixTimeSeconds() / StepSeconds);

    /// <summary>
    /// True when <paramref name="code"/> is the code for the current step or one step either side. Returns the
    /// step that matched through <paramref name="step"/>, so a caller can refuse the same step twice.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> secret, string code, DateTimeOffset at, out long step)
    {
        step = 0;
        if (code is null || code.Length != Digits || !code.All(char.IsAsciiDigit)) return false;
        long now = at.ToUnixTimeSeconds() / StepSeconds;
        for (long candidate = now - 1; candidate <= now + 1; candidate++)
        {
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(secret, candidate)), Encoding.ASCII.GetBytes(code)))
            {
                step = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>The <c>otpauth://</c> address an authenticator app enrols from, as text; a QR code is a picture of it.</summary>
    public static string Uri(string issuer, string account, ReadOnlySpan<byte> secret) =>
        "otpauth://totp/" + System.Uri.EscapeDataString(issuer) + ":" + System.Uri.EscapeDataString(account) +
        "?secret=" + Base32.Encode(secret) + "&issuer=" + System.Uri.EscapeDataString(issuer) +
        "&algorithm=SHA1&digits=6&period=30";
}

/// <summary>RFC 4648 Base32 without padding, which is how authenticator apps take a secret.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        StringBuilder text = new((bytes.Length * 8 + 4) / 5);
        int buffer = 0;
        int bits = 0;
        foreach (byte b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<byte> bytes = new(text.Length * 5 / 8);
        int buffer = 0;
        int bits = 0;
        foreach (char raw in text.TrimEnd('='))
        {
            char c = char.ToUpperInvariant(raw);
            if (c == ' ' || c == '-') continue;
            int value = Alphabet.IndexOf(c);
            if (value < 0) throw new FormatException("Not Base32.");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }
        return [.. bytes];
    }
}

/// <summary>An enrolment in progress: the secret to put in the app, and the address that carries it.</summary>
public sealed record SecondFactorSetup(string SecretBase32, string OtpauthUri);

/// <summary>What a password alone earned: a session, or the fact that a second factor is still owed.</summary>
public sealed record FirstFactorResult(SessionGrant? Session, long? SecondFactorOwedByUserId);
