namespace Graft.Core;

/// <summary>
/// The one matcher every project-page decision runs through. A page is a deny-by-default surface, so the
/// question "may this path be served" has to be answered the same way everywhere; two matchers would be two
/// answers, and the leak would live in whichever one somebody forgot to update.
///
/// <para>
/// Deliberately small. <c>*</c> matches within a path segment, <c>**</c> matches across segments, and
/// nothing else is a metacharacter. No regular expressions, so there is no backtracking to blow up on a
/// hostile pattern, and no negation, because "everything except" leaks the moment a path appears that
/// nobody thought about.
/// </para>
///
/// <para>
/// Paths are compared whole. A pattern never matches a prefix, so <c>docs</c> does not open
/// <c>docs-private/secret</c>, which a careless prefix test would.
/// </para>
/// </summary>
public static class PathMatcher
{
    /// <summary>Longest pattern accepted, so a repository cannot ship one that costs more to match than to read.</summary>
    public const int MaximumPatternLength = 200;

    /// <summary>
    /// Whether any pattern admits this path. An empty or null set admits nothing: no allowlist means no
    /// files, never all files.
    /// </summary>
    public static bool IsAllowed(IReadOnlyList<string>? patterns, string path)
    {
        if (patterns is null || patterns.Count == 0 || string.IsNullOrEmpty(path)) return false;
        string normalized = Normalize(path);
        if (normalized.Length == 0) return false;
        foreach (string pattern in patterns)
            if (Matches(pattern, normalized)) return true;
        return false;
    }

    /// <summary>Whether one pattern admits one already-normalised path.</summary>
    public static bool Matches(string? pattern, string path)
    {
        if (string.IsNullOrEmpty(pattern) || pattern.Length > MaximumPatternLength) return false;
        return Walk(Normalize(pattern), 0, path, 0);
    }

    /// <summary>
    /// A path as the object store produced it: forward slashes, no leading slash, no <c>.</c> or <c>..</c>
    /// segment. Anything with a traversal segment normalises to empty and therefore matches nothing, rather
    /// than being resolved into a path that might escape the allowlist.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string value = path.Replace('\\', '/').Trim();
        while (value.StartsWith('/')) value = value[1..];
        while (value.EndsWith('/')) value = value[..^1];
        if (value.Length == 0) return "";
        foreach (string segment in value.Split('/'))
            if (segment.Length == 0 || segment == "." || segment == "..") return "";
        return value;
    }

    /// <summary>
    /// Matches pattern against path, both normalised, one character at a time.
    ///
    /// <para>
    /// Recursion happens only where <c>**</c> appears, and each step consumes at least one path segment, so
    /// the depth is bounded by the number of segments in the path rather than by anything the pattern can
    /// say. That is what keeps a pathological pattern from costing more than the file it guards.
    /// </para>
    /// </summary>
    private static bool Walk(string pattern, int patternIndex, string path, int pathIndex)
    {
        while (patternIndex < pattern.Length)
        {
            char current = pattern[patternIndex];
            if (current == '*')
            {
                bool crossesSegments = patternIndex + 1 < pattern.Length && pattern[patternIndex + 1] == '*';
                if (crossesSegments)
                {
                    // `**` followed by `/` also matches zero directories, so `docs/**/*.md` admits
                    // `docs/thing.md` as well as `docs/deep/thing.md`. Without that a reader has to write
                    // two patterns for what is obviously one intent.
                    int afterStars = patternIndex + 2;
                    if (afterStars < pattern.Length && pattern[afterStars] == '/')
                    {
                        if (Walk(pattern, afterStars + 1, path, pathIndex)) return true;
                        afterStars++;
                    }
                    for (int candidate = pathIndex; candidate <= path.Length; candidate++)
                        if (Walk(pattern, afterStars, path, candidate)) return true;
                    return false;
                }

                // A single star stops at a separator, so `*.md` never reaches into a directory.
                int limit = path.IndexOf('/', pathIndex);
                if (limit < 0) limit = path.Length;
                for (int candidate = pathIndex; candidate <= limit; candidate++)
                    if (Walk(pattern, patternIndex + 1, path, candidate)) return true;
                return false;
            }

            if (pathIndex >= path.Length) return false;
            // Case-insensitive on purpose: a page that served README.md but not readme.md would be a
            // configuration trap on every case-insensitive working copy that produced the repository.
            if (char.ToLowerInvariant(current) != char.ToLowerInvariant(path[pathIndex])) return false;
            patternIndex++;
            pathIndex++;
        }
        return pathIndex == path.Length;
    }

    /// <summary>
    /// Whether a path sits inside a directory, used for the site root. Separate from the pattern matcher
    /// because a root is a containment question, not a glob: everything under it is in, everything else is
    /// out, and the comparison is segment-wise so <c>pages</c> never admits <c>pages-draft</c>.
    /// </summary>
    public static bool IsInside(string? root, string path)
    {
        string normalizedRoot = Normalize(root);
        string normalizedPath = Normalize(path);
        if (normalizedPath.Length == 0) return false;
        if (normalizedRoot.Length == 0) return true;
        return normalizedPath.Length > normalizedRoot.Length &&
            normalizedPath[normalizedRoot.Length] == '/' &&
            normalizedPath.AsSpan(0, normalizedRoot.Length).Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
