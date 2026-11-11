using System.Text.RegularExpressions;

namespace Graft.Core;

/// <summary>
/// The <c>@name</c> mentions in a Markdown body (ADR 0050), lowercased as usernames are stored. Code is not prose: a
/// mention inside a fenced block or an inline code span is an example, not a summons, so neither counts. Nor does an
/// address like <c>someone@example.com</c>, because the character before the <c>@</c> is part of a word.
/// </summary>
public static partial class Mentions
{
    /// <summary>Most distinct names one body may mention. Enough for a team; too few to page a whole server.</summary>
    public const int MaximumPerBody = 50;

    [GeneratedRegex(@"(?<![A-Za-z0-9_.@/\-])@([A-Za-z0-9](?:[A-Za-z0-9_.\-]{0,37}[A-Za-z0-9_])?)(?![A-Za-z0-9_@])")]
    private static partial Regex Mention();

    [GeneratedRegex(@"`+[^`]*`+")]
    private static partial Regex InlineCode();

    public static IReadOnlyList<string> Find(string? body)
    {
        if (string.IsNullOrEmpty(body)) return [];
        List<string> found = [];
        string? fence = null;
        foreach (string raw in body.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.TrimStart();
            if (fence is null && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
            {
                fence = trimmed[..3];
                continue;
            }
            if (fence is not null)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal)) fence = null;
                continue;
            }
            foreach (Match match in Mention().Matches(InlineCode().Replace(line, " ")))
            {
                string name = match.Groups[1].Value.ToLowerInvariant();
                if (name.Length < 2 || found.Contains(name, StringComparer.Ordinal)) continue;
                found.Add(name);
                if (found.Count == MaximumPerBody) return found;
            }
        }
        return found;
    }

    /// <summary>The names <paramref name="after"/> mentions that <paramref name="before"/> did not, so an edit
    /// summons only the people it added.</summary>
    public static IReadOnlyList<string> Added(string? before, string? after)
    {
        IReadOnlyList<string> old = Find(before);
        return [.. Find(after).Where(name => !old.Contains(name, StringComparer.Ordinal))];
    }
}
