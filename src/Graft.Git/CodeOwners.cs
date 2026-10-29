using System.Text;

namespace Graft.Git;

/// <summary>One rule from a CODEOWNERS file: a pattern and the people it names.</summary>
public sealed record CodeOwnerRule(string Pattern, IReadOnlyList<string> Owners);

/// <summary>
/// Reads a repository's CODEOWNERS file and says who owns a path.
///
/// <para>
/// The file is looked for at <c>CODEOWNERS</c>, <c>.graft/CODEOWNERS</c>, <c>.gitlab/CODEOWNERS</c> and
/// <c>docs/CODEOWNERS</c> on a given commit, in that order, and the first found wins. Its syntax is the
/// common one: a pattern, then usernames each prefixed with <c>@</c>; <c>#</c> starts a comment. Patterns
/// follow gitignore: a leading slash anchors to the repository root, a trailing slash names a directory
/// and everything under it, <c>*</c> matches within one segment, <c>**</c> matches across segments, and a
/// pattern with no slash matches that name anywhere. The last matching rule wins, which is what both
/// GitLab and GitHub do, so a specific rule placed after a general one overrides it.
/// </para>
///
/// <para>
/// Names are usernames and nothing else: no groups, no emails, no sections. A name that is not a member of
/// the project still parses and is still shown; it simply cannot approve, because it cannot push.
/// </para>
/// </summary>
public static class CodeOwners
{
    public static readonly string[] Locations = ["CODEOWNERS", ".graft/CODEOWNERS", ".gitlab/CODEOWNERS", "docs/CODEOWNERS"];

    private const int MaximumBytes = 256 * 1024;
    private const int MaximumRules = 1000;

    /// <summary>The rules on a commit, or an empty list when the repository has no file.</summary>
    public static IReadOnlyList<CodeOwnerRule> Read(Repository repository, string commitId)
    {
        ArgumentNullException.ThrowIfNull(repository);
        foreach (string location in Locations)
        {
            GitFile? file = RepositoryView.File(repository, commitId, location);
            if (file is null) continue;
            if (file.Content.Length > MaximumBytes) return [];
            string text;
            try { text = new UTF8Encoding(false, true).GetString(file.Content); }
            catch (DecoderFallbackException) { return []; }
            return Parse(text);
        }
        return [];
    }

    public static IReadOnlyList<CodeOwnerRule> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<CodeOwnerRule> rules = [];
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int comment = line.IndexOf('#');
            if (comment >= 0) line = line[..comment].Trim();
            if (line.Length == 0) continue;
            string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            List<string> owners = [];
            foreach (string part in parts.Skip(1))
            {
                if (!part.StartsWith('@') || part.Length < 2) continue;
                string owner = part[1..];
                if (owner.Contains('/') || owner.Contains('@')) continue;
                if (!owners.Contains(owner, StringComparer.OrdinalIgnoreCase)) owners.Add(owner);
            }
            if (owners.Count == 0) continue;
            rules.Add(new(parts[0], owners));
            if (rules.Count >= MaximumRules) break;
        }
        return rules;
    }

    /// <summary>Who owns one path: the owners of the last rule that matches it, or none.</summary>
    public static IReadOnlyList<string> OwnersOf(IReadOnlyList<CodeOwnerRule> rules, string path)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(path);
        for (int index = rules.Count - 1; index >= 0; index--)
            if (Matches(rules[index].Pattern, path)) return rules[index].Owners;
        return [];
    }

    /// <summary>gitignore-style matching of one pattern against one repository path.</summary>
    public static bool Matches(string pattern, string path)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(path);
        if (pattern.Length == 0 || path.Length == 0) return false;
        bool directoryOnly = pattern.EndsWith('/');
        string body = directoryOnly ? pattern.TrimEnd('/') : pattern;
        bool anchored = body.StartsWith('/');
        if (anchored) body = body.TrimStart('/');
        if (body.Length == 0) return false;
        // A pattern with no slash in it names a file or directory at any depth, the way gitignore does.
        if (!anchored && !body.Contains('/')) body = "**/" + body;
        // A directory pattern, or any pattern, owns everything beneath what it names.
        string[] patternSegments = body.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] pathSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return MatchSegments(patternSegments, 0, pathSegments, 0, directoryOnly);
    }

    private static bool MatchSegments(string[] pattern, int p, string[] path, int s, bool directoryOnly)
    {
        while (p < pattern.Length)
        {
            if (pattern[p] == "**")
            {
                // Zero or more segments. Try every split.
                for (int skip = s; skip <= path.Length; skip++)
                    if (MatchSegments(pattern, p + 1, path, skip, directoryOnly)) return true;
                return false;
            }
            if (s >= path.Length) return false;
            if (!MatchSegment(pattern[p], path[s])) return false;
            p++;
            s++;
        }
        // The whole pattern matched a prefix of the path: the path is the thing named, or lies beneath it.
        // A directory-only pattern needs something beneath; any other pattern owns both.
        return directoryOnly ? s < path.Length : true;
    }

    private static bool MatchSegment(string pattern, string segment)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?')) return string.Equals(pattern, segment, StringComparison.Ordinal);
        return Glob(pattern, 0, segment, 0);
    }

    private static bool Glob(string pattern, int p, string text, int t)
    {
        while (p < pattern.Length)
        {
            char c = pattern[p];
            if (c == '*')
            {
                for (int skip = t; skip <= text.Length; skip++)
                    if (Glob(pattern, p + 1, text, skip)) return true;
                return false;
            }
            if (t >= text.Length) return false;
            if (c != '?' && c != text[t]) return false;
            p++;
            t++;
        }
        return t == text.Length;
    }
}
