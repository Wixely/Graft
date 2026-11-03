namespace Graft.Remote;

/// <summary>
/// The one-time configuration that makes the helper transparent: Git's own <c>insteadOf</c> rewriting sends this forge's
/// addresses through <c>graft::</c>, so existing remotes, clones and tools are unchanged; and git-lfs learns of the
/// <c>graft</c> transfer, which a forge offers and any other server ignores. All of it in the user's global Git
/// configuration, and all of it undone by <c>remove</c>.
/// </summary>
internal static class Setup
{
    public static int Install(string forge, bool pushOnly)
    {
        string origin = Origin(forge);
        string path = Environment.ProcessPath ?? throw new HelperException("cannot tell where git-remote-graft is installed");
        Unset(origin);
        Set("url.graft::" + origin + "." + (pushOnly ? "pushInsteadOf" : "insteadOf"), origin);
        Set("lfs.customtransfer.graft.path", path);
        Set("lfs.customtransfer.graft.args", "lfs-agent");
        Set("lfs.customtransfer.graft.concurrent", "true");
        Set("lfs.customtransfer.graft.direction", "both");
        Console.WriteLine((pushOnly ? "Pushes" : "Fetches and pushes") + " to " + origin + " now go through git-remote-graft, in parts no larger than the forge allows.");
        Console.WriteLine("Large files tracked with Git LFS go the same way whenever the forge offers it.");
        if (!OnPath(path))
            Console.WriteLine("Git finds the helper on PATH: add " + Path.GetDirectoryName(path) + " to PATH, or Git will report that it cannot find remote-graft.");
        return 0;
    }

    public static int Remove(string forge)
    {
        string origin = Origin(forge);
        Unset(origin);
        Console.WriteLine("Git reaches " + origin + " directly again. The graft transfer stays known to git-lfs for any other forge set up; remove it with");
        Console.WriteLine("  git config --global --remove-section lfs.customtransfer.graft");
        return 0;
    }

    /// <summary>The forge's origin with a trailing slash, so a rewrite matches this forge's paths and no other host's.</summary>
    private static string Origin(string forge)
    {
        if (!Uri.TryCreate(forge, UriKind.Absolute, out Uri? address) || address.Scheme is not ("https" or "http") || address.Query.Length > 0)
            throw new HelperException("'" + forge + "' is not an https origin such as https://forge.example");
        return address.GetLeftPart(UriPartial.Authority) + "/";
    }

    private static void Unset(string origin)
    {
        foreach (string kind in new[] { "insteadOf", "pushInsteadOf" })
            Git.RunAsync(["config", "--global", "--unset-all", "url.graft::" + origin + "." + kind], null, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void Set(string key, string value)
    {
        (int exit, _) = Git.RunAsync(["config", "--global", "--replace-all", key, value], null, CancellationToken.None).GetAwaiter().GetResult();
        if (exit != 0) throw new HelperException("git config could not set " + key);
    }

    private static bool OnPath(string path)
    {
        string directory = Path.GetFullPath(Path.GetDirectoryName(path)!).TrimEnd(Path.DirectorySeparatorChar);
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(entry => string.Equals(Path.GetFullPath(entry).TrimEnd(Path.DirectorySeparatorChar), directory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }
}
