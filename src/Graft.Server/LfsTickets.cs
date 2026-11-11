using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Graft.Server;

/// <summary>
/// The credential <c>git-lfs-authenticate</c> hands out over SSH (ADR 0049), which git-lfs then sends to the HTTPS
/// large-file routes. It names the SSH key that asked, one project and one operation, and expires; it is signed with
/// a key that lives only in this process, so a restart retires every outstanding one and git-lfs simply asks again.
///
/// <para>
/// Nothing about the person is trusted from it beyond the key's fingerprint. The routes that accept it resolve the key
/// again and decide again, so a key removed, an account disabled or a role taken away a second after the ticket was
/// issued stops it working at once.
/// </para>
/// </summary>
internal sealed class LfsTickets
{
    public const string Scheme = "GraftLfs";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);

    public string Issue(string fingerprint, long projectId, bool upload, DateTimeOffset now)
    {
        string payload = string.Join('|', "v1", fingerprint, projectId.ToString(CultureInfo.InvariantCulture), upload ? "upload" : "download",
            (now + Lifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        byte[] body = Encoding.UTF8.GetBytes(payload);
        return Base64Url(body) + "." + Base64Url(HMACSHA256.HashData(key, body));
    }

    /// <summary>The ticket's contents when its signature holds and it has not expired, otherwise null.</summary>
    public (string Fingerprint, long ProjectId, bool Upload)? Read(string ticket, DateTimeOffset now)
    {
        int dot = ticket.IndexOf('.');
        if (dot <= 0 || ticket.Length > 1024) return null;
        byte[]? body = FromBase64Url(ticket[..dot]);
        byte[]? mac = FromBase64Url(ticket[(dot + 1)..]);
        if (body is null || mac is null || !CryptographicOperations.FixedTimeEquals(mac, HMACSHA256.HashData(key, body))) return null;
        string[] parts = Encoding.UTF8.GetString(body).Split('|');
        if (parts.Length != 5 || parts[0] != "v1" || parts[3] is not ("upload" or "download") ||
            !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long projectId) ||
            !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out long expires) ||
            now.ToUnixTimeSeconds() >= expires)
            return null;
        return (parts[1], projectId, parts[3] == "upload");
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? FromBase64Url(string text)
    {
        string padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        try { return Convert.FromBase64String(padded); }
        catch (FormatException) { return null; }
    }
}
