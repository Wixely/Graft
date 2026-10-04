using System.Reflection;

namespace Graft.Server;

/// <summary>
/// The third-party attribution this build redistributes, embedded so it travels with the executable.
///
/// <para>
/// Several bundled components place their obligation on the <em>binary</em> redistribution rather than on the
/// source tree, so a notice that only exists as a file in the repository does not discharge it: a published
/// single-file server is routinely copied somewhere the repository is not. Embedding the text means the
/// binary can always produce it, through <c>--licences</c> or the <c>/-/licences</c> route.
/// </para>
/// </summary>
public static class ThirdPartyNotices
{
    private const string Resource = "graft/THIRD-PARTY-NOTICES.md";

    /// <summary>The notices, or an explanatory line if the build somehow omitted them.</summary>
    public static string Text { get; } = Load();

    private static string Load()
    {
        using Stream? stream = typeof(ThirdPartyNotices).Assembly.GetManifestResourceStream(Resource);
        if (stream is null) return "Third-party notices were not embedded in this build.";
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
