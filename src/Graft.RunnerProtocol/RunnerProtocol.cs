using System.Globalization;
using CupriMark;

namespace Graft.RunnerProtocol;

/// <summary>
/// What a Graft server and a Graft runner agree on before either trusts the other's requests: the CupriNet
/// network they meet on, the protocol versions each speaks, and the routes the runner calls on the server's
/// Shrine.
///
/// <para>
/// The version is negotiated with CupriMark rather than compared for equality, so a runner and a server on
/// different releases keep working through a migration window instead of breaking on the day one of them is
/// upgraded. A runner sends the range of protocol versions it speaks on every request; the server answers with
/// the one it chose, or refuses by name when the two ranges share nothing at or above its floor.
/// </para>
/// </summary>
public static class RunnerProtocol
{
    /// <summary>The CupriNet network a runner and its server meet on. An Intonation for any other is refused.</summary>
    public const string Network = "graft-runners";

    /// <summary>The request header carrying the runner's supported range, as <c>min-max</c>.</summary>
    public const string VersionHeader = "graft-runner-protocol";

    /// <summary>The request header carrying the runner's credential, or a job's token.</summary>
    public const string AuthorizationHeader = "authorization";

    /// <summary>The name of the component negotiated.</summary>
    public const string ComponentName = "runner-protocol";

    /// <summary>
    /// Every version this protocol has had. A published version is never changed; a new one is appended. A version
    /// that fixes a security problem is marked <see cref="BumpReason.Security"/>, which raises the floor.
    /// </summary>
    public static Component Component { get; } = new(ComponentName,
    [
        new ComponentVersion(1, BumpReason.Functionality, VersionStatus.Active),
    ]);

    public static Catalogue Catalogue { get; } = Catalogue.Create("graft-runner", [Component]);

    /// <summary>What this build speaks, as the header value.</summary>
    public static string Advertised =>
        Component.Supported.Min.ToString(CultureInfo.InvariantCulture) + "-" + Component.Supported.Max.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Chooses the version to speak with a peer that advertised <paramref name="header"/>. Returns the ordinal, or
    /// null with the reason when there is none, which the caller reports to the peer.
    /// </summary>
    public static ushort? Negotiate(string? header, out string reason)
    {
        reason = "";
        if (!TryParse(header, out OrdinalRange range))
        {
            reason = "the runner did not say which protocol versions it speaks";
            return null;
        }
        NegotiationResult result = Negotiator.Negotiate(Component, range);
        if (result.Accepted) return result.SelectedOrdinal;
        reason = "no protocol version in common (runner speaks " + range + ", server speaks " + Advertised + ": " + result.Reason + ")";
        return null;
    }

    private static bool TryParse(string? header, out OrdinalRange range)
    {
        range = OrdinalRange.Empty;
        if (string.IsNullOrWhiteSpace(header)) return false;
        string[] parts = header.Trim().Split('-');
        if (parts.Length != 2 ||
            !ushort.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out ushort min) ||
            !ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ushort max) ||
            min == 0 || max < min)
            return false;
        range = OrdinalRange.Create(min, max);
        return true;
    }

    /// <summary>The routes a runner calls. Everything else on the Shrine answers 404.</summary>
    public static class Routes
    {
        public const string Register = "/runner/v1/register";
        public const string RequestJob = "/runner/v1/jobs/request";
        /// <summary>Followed by the job id and <c>/trace</c> or <c>/finish</c>.</summary>
        public const string JobPrefix = "/runner/v1/jobs/";
    }

    /// <summary>The relic name a job's source archive is served under; chunks add <c>.00000</c> onward.</summary>
    public static string ArchiveRelic(long jobId) => "job-" + jobId.ToString(CultureInfo.InvariantCulture) + "-source.tar.gz";
}
