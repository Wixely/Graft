using System.Security.Cryptography;
using System.Text;

namespace Graft.Core;

/// <summary>
/// Somewhere outside the forge a project's releases can be published to (ADR 0052). The endpoint and the key are
/// the server's to keep: what is here is what may be shown, which of the endpoint is its host and nothing more.
/// </summary>
public sealed record ReleaseDestination(
    long Id,
    long ProjectId,
    string Name,
    string Kind,
    string EndpointHost,
    string ApplicationId,
    string Channel,
    bool AllowProductionPromotion,
    string? PackageName,
    IReadOnlyList<ArtifactRule> Rules,
    string UpdatedByUsername,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>A destination with what the server keeps protected, for the worker that is about to use it.</summary>
public sealed record ReleaseDestinationSecrets(ReleaseDestination Destination, string ProtectedEndpoint, string ProtectedKey);

/// <summary>
/// One line of a destination's file rules: which release files it names, and what they are declared as. A rule
/// either skips the file or gives its platform and architecture; <see cref="Main"/> marks the file whose build
/// the channel should report afterwards.
/// </summary>
public sealed record ArtifactRule(string Pattern, bool Skip, string Platform, string Architecture, bool Main);

/// <summary>What one release file is declared as to a destination.</summary>
public sealed record ArtifactDeclaration(string Format, string Platform, string Architecture, bool Main);

/// <summary>One publication of one release to one destination, as it stands.</summary>
public sealed record ReleasePublication(
    long Id,
    long ProjectId,
    string DestinationName,
    string TagName,
    string State,
    bool DryRun,
    string Channel,
    string Summary,
    IReadOnlyList<ArtifactOutcome> Artifacts,
    bool? ChannelVerified,
    string RequestedByUsername,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? FinishedAtUtc);

/// <summary>A publication the worker has claimed: what to publish, and to which destination.</summary>
public sealed record ReleasePublicationJob(long Id, long ProjectId, long DestinationId, string TagName, bool DryRun);

public static class ReleasePublications
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <summary>Which addresses the server may publish releases to. The operator's decision, as importing by address is.</summary>
public enum ReleasePublishingPolicy
{
    /// <summary>No publishing at all. The default.</summary>
    Off = 0,
    /// <summary>Only https endpoints whose every address is on the public internet.</summary>
    Public = 1,
    /// <summary>Any address the server can reach, over https or http: for a store on the server's own network.</summary>
    Any = 2
}

/// <summary>The rules a release destination's settings follow, and how a release file is declared to one.</summary>
public static class ReleaseDestinations
{
    /// <summary>The one kind there is: an application store that takes builds through upload sessions (ADR 0052).</summary>
    public const string AppStoreKind = "app-store-v1";

    public const int MaximumPerProject = 10;
    public const int MaximumRules = 50;
    public const int MaximumVersionLength = 100;
    public const int MaximumNotesLength = 8000;
    public const int MaximumFileNameLength = 200;

    public static readonly IReadOnlyList<string> Channels = ["none", "stable", "beta"];
    public static readonly IReadOnlyList<string> Platforms = ["Android", "Windows", "Linux", "MacOS", "iOS", "Unknown"];

    public static bool IsName(string? name) =>
        name is { Length: > 0 and <= 40 } && (char.IsAsciiLetterLower(name[0]) || char.IsAsciiDigit(name[0])) &&
        name.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '_');

    /// <summary>
    /// The endpoint as it must be: an absolute address ending in <c>/api/v1</c>, with no credentials, query or
    /// fragment in it. Https unless the operator allowed any address. Returns null and says why otherwise.
    /// </summary>
    public static Uri? ParseEndpoint(string? value, bool allowHttp, out string problem)
    {
        problem = "";
        string trimmed = (value ?? "").Trim().TrimEnd('/');
        if (trimmed.Length is 0 or > 2048 || !Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp)))
        { problem = "The endpoint is the complete https address of the store's API."; return null; }
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        { problem = "The endpoint carries no credentials, query or fragment; the key is given separately."; return null; }
        if (!uri.AbsolutePath.EndsWith("/api/v1", StringComparison.Ordinal))
        { problem = "The endpoint ends in /api/v1."; return null; }
        return uri;
    }

    public static bool IsApplicationId(string? value) =>
        value is { Length: 36 } && Guid.TryParseExact(value, "D", out _);

    public static bool IsArchitecture(string? value) =>
        value is { Length: > 0 and <= 32 } && value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '.' or '_' or '-');

    public static bool IsPackageName(string? value) =>
        value is { Length: > 0 and <= 255 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_');

    /// <summary>
    /// Reads file rules, one to a line: a pattern, then either <c>skip</c> or a platform, optionally an
    /// architecture, optionally <c>main</c>. Returns null and names the line that is wrong.
    /// </summary>
    public static IReadOnlyList<ArtifactRule>? ParseRules(string? text, out string problem)
    {
        problem = "";
        List<ArtifactRule> rules = [];
        int number = 0;
        foreach (string raw in (text ?? "").Split('\n'))
        {
            number++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string where = "File rule " + number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ": ";
            if (parts[0].Length > 200 || parts[0].Any(character => char.IsControl(character) || character is '/' or '\\'))
            { problem = where + "the pattern is a file name, with * and ? as wildcards."; return null; }
            if (parts.Length == 2 && parts[1] == "skip") { rules.Add(new(parts[0], true, "", "", false)); continue; }
            if (parts.Length is < 2 or > 4)
            { problem = where + "write a pattern, then 'skip' or a platform, optionally an architecture, optionally 'main'."; return null; }
            string? platform = Platforms.FirstOrDefault(known => string.Equals(known, parts[1], StringComparison.OrdinalIgnoreCase));
            if (platform is null) { problem = where + "the platform is one of " + string.Join(", ", Platforms) + "."; return null; }
            bool main = parts[^1] == "main";
            int named = parts.Length - (main ? 1 : 0);
            if (named > 3) { problem = where + "the last word may only be 'main'."; return null; }
            string architecture = named == 3 ? parts[2].ToLowerInvariant() : "any";
            if (!IsArchitecture(architecture))
            { problem = where + "an architecture is up to 32 lower-case letters, digits, '.', '_' and '-'."; return null; }
            rules.Add(new(parts[0], false, platform, architecture, main));
        }
        if (rules.Count > MaximumRules) { problem = "A destination has at most " + MaximumRules + " file rules."; return null; }
        if (rules.Count(rule => rule.Main) > 1) { problem = "Only one file rule is marked main."; return null; }
        return rules;
    }

    public static string FormatRules(IEnumerable<ArtifactRule> rules) =>
        string.Join('\n', rules.Select(rule => rule.Skip
            ? rule.Pattern + " skip"
            : rule.Pattern + " " + rule.Platform + " " + rule.Architecture + (rule.Main ? " main" : "")));

    /// <summary>
    /// What a release file is declared as, or null when a rule says to leave it out. The first rule whose
    /// pattern matches decides; a file no rule names is declared from its own name, which is a guess the
    /// rules exist to correct.
    /// </summary>
    public static ArtifactDeclaration? Declare(string fileName, IReadOnlyList<ArtifactRule> rules)
    {
        bool apk = fileName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);
        foreach (ArtifactRule rule in rules)
        {
            if (!Matches(rule.Pattern, fileName)) continue;
            if (rule.Skip) return null;
            return new(apk && rule.Platform == "Android" ? "apk" : "file", rule.Platform, rule.Architecture, rule.Main);
        }
        if (apk) return new("apk", "Android", "any", false);
        return new("file", GuessPlatform(fileName), GuessArchitecture(fileName), false);
    }

    /// <summary>The version a tag names: the tag, without the <c>v</c> that conventionally precedes a number.</summary>
    public static string VersionOf(string tagName)
    {
        string tag = tagName.Trim();
        return tag.Length > 1 && tag[0] is 'v' or 'V' && char.IsAsciiDigit(tag[1]) ? tag[1..] : tag;
    }

    /// <summary>
    /// The key that makes an upload the same upload when it is tried again: a digest of the project, the
    /// release, the application, the file's name and the file's bytes. Any of them changing is a different upload.
    /// </summary>
    public static string IdempotencyKey(string projectIdentity, string tagName, string applicationId, string fileName, string sha256) =>
        "graft-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', projectIdentity, tagName, applicationId.ToLowerInvariant(), fileName, sha256.ToLowerInvariant()))));

    public static bool Matches(string pattern, string name)
    {
        // Iterative wildcard match with one backtrack point, so a pattern of stars cannot be made slow.
        int p = 0, n = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(name[n]))) { p++; n++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = n; }
            else if (star >= 0) { p = star + 1; n = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static string GuessPlatform(string fileName)
    {
        string name = fileName.ToLowerInvariant();
        string[] words = name.Split(['-', '_', '.', ' ', '+'], StringSplitOptions.RemoveEmptyEntries);
        bool Word(params string[] any) => words.Any(word => any.Contains(word, StringComparer.Ordinal));
        bool Ends(params string[] any) => any.Any(extension => name.EndsWith(extension, StringComparison.Ordinal));
        if (Word("android") || Ends(".aab")) return "Android";
        if (Word("ios", "iphone", "ipados") || Ends(".ipa")) return "iOS";
        if (Word("windows", "win", "win32", "win64") || Ends(".exe", ".msi", ".msix")) return "Windows";
        if (Word("macos", "mac", "osx", "darwin") || Ends(".dmg", ".pkg")) return "MacOS";
        if (Word("linux") || Ends(".appimage", ".deb", ".rpm")) return "Linux";
        return "Unknown";
    }

    private static string GuessArchitecture(string fileName)
    {
        string name = fileName.ToLowerInvariant();
        string[] words = name.Split(['-', '_', '.', ' ', '+'], StringSplitOptions.RemoveEmptyEntries);
        bool Word(params string[] any) => words.Any(word => any.Contains(word, StringComparer.Ordinal));
        if (Word("amd64", "x64", "win64") || name.Contains("x86_64", StringComparison.Ordinal) || name.Contains("x86-64", StringComparison.Ordinal)) return "x64";
        if (Word("arm64", "aarch64")) return "arm64";
        if (Word("arm", "armv7", "armhf", "arm32")) return "arm";
        if (Word("x86", "i386", "i686", "win32")) return "x86";
        return "any";
    }
}
