using System.Net;
using System.Net.Sockets;

namespace Graft.Core;

/// <summary>
/// A project imported from another server's address, and whether it is kept up to date from there. One row per
/// imported project; the first import and every later update are the same operation, so they share a status.
/// </summary>
public sealed record ProjectMirror(
    long ProjectId,
    string Url,
    bool Enabled,
    MirrorStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    DateTimeOffset? LastChangeAtUtc,
    string? LastError,
    int ConsecutiveFailures,
    DateTimeOffset NextAttemptAtUtc)
{
    /// <summary>GitLab's <c>import_status</c>: the first import's state, which stays <c>finished</c> once one succeeded.</summary>
    public string ImportStatus => LastSuccessAtUtc is not null ? "finished" : Status switch
    {
        MirrorStatus.Started => "started",
        MirrorStatus.Failed => "failed",
        _ => "scheduled"
    };
}

/// <summary>Where a mirror's latest update stands. The names are GitLab's <c>update_status</c> values.</summary>
public enum MirrorStatus
{
    Scheduled = 0,
    Started = 1,
    Finished = 2,
    Failed = 3
}

/// <summary>A mirror the worker is about to update: enough to fetch it without reading the project again.</summary>
public sealed record MirrorJob(long ProjectId, string Url, bool FirstImport);

/// <summary>Which addresses the server may import from. The operator's decision, as outbound webhooks are.</summary>
public enum UrlImportPolicy
{
    /// <summary>No import by address at all. The default.</summary>
    Off = 0,
    /// <summary>Only hosts whose every address is on the public internet.</summary>
    Public = 1,
    /// <summary>Any address the server can reach, including its own network: for importing from a server beside it.</summary>
    Any = 2
}

/// <summary>The rules an import address must follow before anything is fetched from it.</summary>
public static class ImportAddresses
{
    public const int MaximumUrlLength = 2048;

    /// <summary>
    /// Accepts an http or https address of a repository with a host and a path, and nothing else: no user name or
    /// password, because a credential in an address would be stored and shown wherever the address is; no query or
    /// fragment, because Git's smart HTTP appends its own path to the address.
    /// </summary>
    public static bool TryParse(string? text, out Uri? address, out string? error)
    {
        address = null;
        error = null;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) { error = "import_url is empty"; return false; }
        if (text.Length > MaximumUrlLength) { error = "import_url must be at most 2048 characters"; return false; }
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed) || parsed.Scheme is not ("https" or "http"))
        { error = "import_url must be an http or https address"; return false; }
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        { error = "import_url must not carry a user name or password; only repositories anybody may clone can be imported"; return false; }
        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        { error = "import_url must not have a query or fragment"; return false; }
        if (parsed.HostNameType is UriHostNameType.Unknown or UriHostNameType.Basic || parsed.Host.Length == 0)
        { error = "import_url must name a host"; return false; }
        if (parsed.AbsolutePath.Trim('/').Length == 0) { error = "import_url must name a repository, not just a server"; return false; }
        address = parsed;
        return true;
    }

    /// <summary>A project path suggested by an import address: its last segment without <c>.git</c>.</summary>
    public static string SuggestedPath(Uri address)
    {
        string last = address.AbsolutePath.TrimEnd('/').Split('/')[^1];
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) last = last[..^4];
        return Uri.UnescapeDataString(last);
    }
}

/// <summary>
/// Whether an address is on the public internet. Everything else, loopback, private ranges, link-local (where cloud
/// metadata services answer), carrier-grade NAT, unique-local, multicast, documentation and unspecified addresses, is
/// refused when the policy is <see cref="UrlImportPolicy.Public"/>, because a request the server makes on somebody's
/// behalf must not reach what only the server can reach.
/// </summary>
public static class OutboundAddresses
{
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 ||
                (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||
                (b[0] == 169 && b[1] == 254) ||
                (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                (b[0] == 192 && b[1] == 0 && b[2] == 0) ||
                (b[0] == 192 && b[1] == 0 && b[2] == 2) ||
                (b[0] == 192 && b[1] == 88 && b[2] == 99) ||
                (b[0] == 192 && b[1] == 168) ||
                (b[0] == 198 && (b[1] == 18 || b[1] == 19)) ||
                (b[0] == 198 && b[1] == 51 && b[2] == 100) ||
                (b[0] == 203 && b[1] == 0 && b[2] == 113) ||
                b[0] >= 224);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any)) return false;
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal) return false;
            byte[] b = address.GetAddressBytes();
            // 64:ff9b::/96 and the other translation prefixes carry an IPv4 address that may be private; 2001:db8::/32
            // is documentation; 2002::/16 embeds an IPv4 address. None is somewhere a repository should be.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b) return false;
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false;
            if (b[0] == 0x20 && b[1] == 0x02) return false;
            // Only global unicast, 2000::/3, is public.
            return (b[0] & 0xe0) == 0x20;
        }
        return false;
    }
}
