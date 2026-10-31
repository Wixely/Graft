using System.Globalization;
using System.Text;

namespace Graft.Core;

/// <summary>One job as a pipeline file describes it: where it runs in the order, and the lines it runs.</summary>
public sealed record CiJobDefinition(
    string Name,
    string Stage,
    int StageIndex,
    IReadOnlyList<string> Script,
    IReadOnlyDictionary<string, string> Variables,
    bool AllowFailure,
    /// <summary>What the job keeps after a successful run, as paths relative to the source; empty for nothing.</summary>
    IReadOnlyList<string>? ArtifactPaths = null);

/// <summary>A pipeline file read into the jobs it asks for, in stage order.</summary>
public sealed record CiPlan(IReadOnlyList<string> Stages, IReadOnlyList<CiJobDefinition> Jobs);

/// <summary>Either a plan, or the reason the file cannot be run, said in terms of the file.</summary>
public sealed record CiParseResult(CiPlan? Plan, string? Error)
{
    public bool Ok => Plan is not null;
}

/// <summary>
/// Reads the subset of GitLab's <c>.gitlab-ci.yml</c> that Graft runs, and refuses the rest by name.
///
/// <para>
/// Supported: <c>stages</c>; top-level <c>variables</c>; jobs with <c>stage</c>, <c>script</c>,
/// <c>before_script</c>, <c>variables</c> and <c>allow_failure</c>; hidden jobs (a name starting with a dot),
/// which GitLab never runs and neither does Graft. Anything else, a key Graft does not run or a YAML feature
/// this reader does not parse, is an error naming the key or the line, so a file that asks for something is
/// never run as though it had asked for less.
/// </para>
///
/// <para>
/// The YAML reader is hand-written because Graft takes no YAML dependency and the common .NET ones reflect at
/// run time, which the NativeAOT build refuses. It reads block mappings and sequences, flow sequences of
/// scalars, plain and quoted scalars, and literal and folded block scalars. Anchors, aliases, merge keys, tags,
/// flow mappings and multiple documents are refused by name.
/// </para>
/// </summary>
public static class CiConfiguration
{
    /// <summary>Where a project's pipeline file lives, as on GitLab.</summary>
    public const string Path = ".gitlab-ci.yml";

    public const int MaximumBytes = 64 * 1024;
    public const int MaximumJobs = 50;
    public const int MaximumScriptLines = 200;

    private static readonly string[] DefaultStages = ["build", "test", "deploy"];
    private static readonly HashSet<string> TopLevelReserved = new(StringComparer.Ordinal) { "stages", "variables" };
    private static readonly HashSet<string> TopLevelRefused = new(StringComparer.Ordinal)
        { "default", "include", "workflow", "image", "services", "cache", "before_script", "after_script", "pages", "spec" };
    private static readonly HashSet<string> JobKeys = new(StringComparer.Ordinal)
        { "stage", "script", "before_script", "variables", "allow_failure", "artifacts" };

    public static CiParseResult Parse(string text)
    {
        if (text is null) return new(null, "The pipeline file is empty.");
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) return new(null, "The pipeline file is larger than 64 KB.");
        YamlNode root;
        try { root = new YamlReader(text).ReadDocument(); }
        catch (CiYamlException error) { return new(null, error.Message); }
        if (root is not YamlMap map) return new(null, "The pipeline file must be a mapping of job names and settings.");

        try { return new(Interpret(map), null); }
        catch (CiYamlException error) { return new(null, error.Message); }
    }

    private static CiPlan Interpret(YamlMap map)
    {
        List<string> stages = [.. DefaultStages];
        if (map.TryGet("stages", out YamlNode? stagesNode))
        {
            stages = Strings(stagesNode, "stages");
            if (stages.Count == 0) throw new CiYamlException("stages must list at least one stage.");
            if (stages.Distinct(StringComparer.Ordinal).Count() != stages.Count) throw new CiYamlException("stages names a stage twice.");
        }
        Dictionary<string, string> globalVariables = map.TryGet("variables", out YamlNode? globals) ? Variables(globals, "variables") : [];

        List<CiJobDefinition> jobs = [];
        foreach ((string name, YamlNode value) in map.Entries)
        {
            if (TopLevelReserved.Contains(name)) continue;
            if (TopLevelRefused.Contains(name))
                throw new CiYamlException("'" + name + "' is a GitLab setting Graft does not run yet; remove it or move what it does into the jobs' scripts.");
            if (name.StartsWith('.')) continue;
            if (value is not YamlMap job) throw new CiYamlException("Job '" + name + "' must be a mapping with at least a script.");
            foreach ((string key, _) in job.Entries)
                if (!JobKeys.Contains(key))
                    throw new CiYamlException("Job '" + name + "' uses '" + key + "', which Graft does not run yet. Supported: stage, script, before_script, variables, allow_failure, artifacts.");
            if (!job.TryGet("script", out YamlNode? scriptNode)) throw new CiYamlException("Job '" + name + "' has no script.");
            List<string> script = [];
            if (job.TryGet("before_script", out YamlNode? before)) script.AddRange(Lines(before, name, "before_script"));
            script.AddRange(Lines(scriptNode, name, "script"));
            if (script.Count == 0) throw new CiYamlException("Job '" + name + "' has an empty script.");
            if (script.Count > MaximumScriptLines) throw new CiYamlException("Job '" + name + "' has more than 200 script lines.");
            string stage = job.TryGet("stage", out YamlNode? stageNode) ? Scalar(stageNode, "Job '" + name + "' stage") : "test";
            int stageIndex = stages.IndexOf(stage);
            if (stageIndex < 0) throw new CiYamlException("Job '" + name + "' is in stage '" + stage + "', which stages does not list.");
            bool allowFailure = false;
            if (job.TryGet("allow_failure", out YamlNode? allowNode))
            {
                string allow = Scalar(allowNode, "Job '" + name + "' allow_failure");
                allowFailure = allow switch
                {
                    "true" => true,
                    "false" => false,
                    _ => throw new CiYamlException("Job '" + name + "' allow_failure must be true or false.")
                };
            }
            Dictionary<string, string> variables = new(globalVariables, StringComparer.Ordinal);
            if (job.TryGet("variables", out YamlNode? jobVariables))
                foreach ((string key, string val) in Variables(jobVariables, "Job '" + name + "' variables"))
                    variables[key] = val;
            List<string> artifactPaths = job.TryGet("artifacts", out YamlNode? artifactsNode) ? ArtifactPaths(artifactsNode, name) : [];
            jobs.Add(new(name, stage, stageIndex, script, variables, allowFailure, artifactPaths));
            if (jobs.Count > MaximumJobs) throw new CiYamlException("The pipeline has more than 50 jobs.");
        }
        if (jobs.Count == 0) throw new CiYamlException("The pipeline file defines no jobs.");
        // Stable: jobs keep the order the file gives them within a stage.
        return new(stages, [.. jobs.OrderBy(job => job.StageIndex)]);
    }

    /// <summary>
    /// <c>artifacts: paths:</c>, and nothing else of GitLab's artifact settings yet. A path is relative to the source,
    /// may use <c>*</c> and <c>**</c>, and may not climb out of it.
    /// </summary>
    private static List<string> ArtifactPaths(YamlNode node, string job)
    {
        if (node is not YamlMap map) throw new CiYamlException("Job '" + job + "' artifacts must be a mapping with paths.");
        foreach ((string key, _) in map.Entries)
            if (key != "paths")
                throw new CiYamlException("Job '" + job + "' artifacts uses '" + key + "', which Graft does not run yet. Supported: paths.");
        if (!map.TryGet("paths", out YamlNode? paths)) throw new CiYamlException("Job '" + job + "' artifacts has no paths.");
        List<string> result = Strings(paths, "Job '" + job + "' artifacts paths");
        if (result.Count is 0 or > 50) throw new CiYamlException("Job '" + job + "' artifacts lists between 1 and 50 paths.");
        foreach (string path in result)
        {
            string normal = path.Replace('\\', '/').Trim();
            if (normal.Length == 0 || normal.StartsWith('/') || normal.Contains(':') || normal.Split('/').Any(part => part == ".."))
                throw new CiYamlException("Job '" + job + "' artifacts path '" + path + "' must stay inside the source directory.");
        }
        return result;
    }

    private static string Scalar(YamlNode node, string what) =>
        node is YamlScalar scalar ? scalar.Value : throw new CiYamlException(what + " must be a single value.");

    private static List<string> Strings(YamlNode node, string what) => node switch
    {
        YamlSeq seq => seq.Items.Select(item => Scalar(item, what + " entries")).ToList(),
        _ => throw new CiYamlException(what + " must be a list.")
    };

    /// <summary>A script is one line or a list of lines; a nested list is flattened, as GitLab does.</summary>
    private static List<string> Lines(YamlNode node, string job, string key)
    {
        List<string> lines = [];
        void Add(YamlNode item)
        {
            switch (item)
            {
                case YamlScalar scalar: lines.Add(scalar.Value); break;
                case YamlSeq nested: foreach (YamlNode inner in nested.Items) Add(inner); break;
                case YamlMap:
                    throw new CiYamlException("Job '" + job + "' " + key + " has a line YAML reads as 'key: value'. A line containing ': ' must be quoted, as on GitLab.");
                default: throw new CiYamlException("Job '" + job + "' " + key + " must be a line or a list of lines.");
            }
        }
        Add(node);
        return lines;
    }

    private static Dictionary<string, string> Variables(YamlNode node, string what)
    {
        if (node is not YamlMap map) throw new CiYamlException(what + " must be a mapping of names to values.");
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach ((string key, YamlNode value) in map.Entries)
        {
            if (key.Length is 0 or > 128 || !(char.IsAsciiLetter(key[0]) || key[0] == '_') || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                throw new CiYamlException(what + " names '" + key + "', which is not a variable name.");
            result[key] = Scalar(value, what + " " + key);
        }
        return result;
    }

    // ------------------------------------------------------------------------------------------------
    // The YAML subset
    // ------------------------------------------------------------------------------------------------

    private abstract record YamlNode;
    private sealed record YamlScalar(string Value) : YamlNode;
    private sealed record YamlSeq(List<YamlNode> Items) : YamlNode;
    private sealed record YamlMap(List<(string Key, YamlNode Value)> Entries) : YamlNode
    {
        public bool TryGet(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out YamlNode? value)
        {
            foreach ((string k, YamlNode v) in Entries)
                if (k == key) { value = v; return true; }
            value = null;
            return false;
        }
    }

    private sealed class CiYamlException(string message) : Exception(message);

    /// <summary>An indentation-driven reader over the lines of one document.</summary>
    private sealed class YamlReader
    {
        private readonly List<(int Number, int Indent, string Text)> lines = [];
        private int position;

        public YamlReader(string text)
        {
            string[] raw = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            bool started = false;
            for (int index = 0; index < raw.Length; index++)
            {
                string line = raw[index];
                if (line.Contains('\t') && line.TrimStart(' ').StartsWith('\t'))
                    throw new CiYamlException("Line " + (index + 1) + " is indented with a tab; YAML indents with spaces.");
                string trimmed = line.Trim();
                if (trimmed == "---")
                {
                    if (started) throw new CiYamlException("Line " + (index + 1) + " starts a second document; a pipeline file is one.");
                    continue;
                }
                if (trimmed == "..." ) break;
                // Keep comment-only and blank lines as such so block scalars can carry them; drop them later.
                lines.Add((index + 1, line.Length - line.TrimStart(' ').Length, line));
                if (trimmed.Length > 0 && !trimmed.StartsWith('#')) started = true;
            }
        }

        public YamlNode ReadDocument()
        {
            SkipEmpty();
            if (position >= lines.Count) throw new CiYamlException("The pipeline file is empty.");
            YamlNode node = ReadBlock(lines[position].Indent);
            SkipEmpty();
            if (position < lines.Count) throw new CiYamlException("Line " + lines[position].Number + " is indented in a way that does not fit the structure above it.");
            return node;
        }

        private void SkipEmpty()
        {
            while (position < lines.Count && IsEmpty(lines[position].Text)) position++;
        }

        private static bool IsEmpty(string text)
        {
            string trimmed = text.Trim();
            return trimmed.Length == 0 || trimmed.StartsWith('#');
        }

        private YamlNode ReadBlock(int indent)
        {
            SkipEmpty();
            (int number, int lineIndent, string text) = lines[position];
            if (lineIndent != indent) throw new CiYamlException("Line " + number + " is indented unexpectedly.");
            string content = text.TrimStart(' ');
            return content.StartsWith("- ", StringComparison.Ordinal) || content == "-" ? ReadSequence(indent) : ReadMapping(indent);
        }

        private YamlSeq ReadSequence(int indent)
        {
            List<YamlNode> items = [];
            while (true)
            {
                SkipEmpty();
                if (position >= lines.Count || lines[position].Indent < indent) break;
                (int number, int lineIndent, string text) = lines[position];
                if (lineIndent > indent) throw new CiYamlException("Line " + number + " is indented unexpectedly.");
                string content = text.TrimStart(' ');
                if (!(content.StartsWith("- ", StringComparison.Ordinal) || content == "-")) break;
                string rest = content == "-" ? "" : StripComment(content[2..], number).Trim();
                if (rest.Length == 0)
                {
                    position++;
                    SkipEmpty();
                    if (position >= lines.Count || lines[position].Indent <= indent) items.Add(new YamlScalar(""));
                    else items.Add(ReadBlock(lines[position].Indent));
                    continue;
                }
                if (IsMappingEntry(rest))
                {
                    // "- key: value" opens a mapping whose further keys sit at the column after the dash.
                    int childIndent = indent + 2 + (content[2..].Length - content[2..].TrimStart(' ').Length);
                    lines[position] = (number, childIndent, new string(' ', childIndent) + content[2..].TrimStart(' '));
                    items.Add(ReadMapping(childIndent));
                    continue;
                }
                if (rest is "|" or "|-" or "|+" or ">" or ">-" or ">+")
                {
                    position++;
                    items.Add(new YamlScalar(ReadBlockScalar(indent, rest)));
                    continue;
                }
                items.Add(ParseInline(rest, number));
                position++;
            }
            return new(items);
        }

        private YamlMap ReadMapping(int indent)
        {
            List<(string, YamlNode)> entries = [];
            HashSet<string> seen = new(StringComparer.Ordinal);
            while (true)
            {
                SkipEmpty();
                if (position >= lines.Count || lines[position].Indent < indent) break;
                (int number, int lineIndent, string text) = lines[position];
                if (lineIndent > indent) throw new CiYamlException("Line " + number + " is indented unexpectedly.");
                string content = StripComment(text.TrimStart(' '), number);
                if (content.StartsWith("- ", StringComparison.Ordinal)) break;
                int colon = FindColon(content);
                if (colon < 0) throw new CiYamlException("Line " + number + " is not a 'key: value' line.");
                string key = Unquote(content[..colon].Trim(), number);
                if (key == "<<") throw new CiYamlException("Line " + number + " uses a merge key (<<), which Graft does not read.");
                if (key.Length == 0) throw new CiYamlException("Line " + number + " has an empty key.");
                if (!seen.Add(key)) throw new CiYamlException("Line " + number + " repeats the key '" + key + "'.");
                string rest = content[(colon + 1)..].Trim();
                position++;
                if (rest.Length == 0)
                {
                    SkipEmpty();
                    if (position < lines.Count && lines[position].Indent > indent)
                        entries.Add((key, ReadBlock(lines[position].Indent)));
                    else if (position < lines.Count && lines[position].Indent == indent && lines[position].Text.TrimStart(' ').StartsWith("- ", StringComparison.Ordinal))
                        entries.Add((key, ReadSequence(indent)));    // a sequence may sit at its key's own indent
                    else entries.Add((key, new YamlScalar("")));
                    continue;
                }
                if (rest is "|" or "|-" or "|+" or ">" or ">-" or ">+")
                {
                    entries.Add((key, new YamlScalar(ReadBlockScalar(indent, rest))));
                    continue;
                }
                entries.Add((key, ParseInline(rest, number)));
            }
            return new(entries);
        }

        /// <summary>Lines more indented than the owner, kept as written (literal) or joined (folded).</summary>
        private string ReadBlockScalar(int ownerIndent, string indicator)
        {
            List<string> collected = [];
            int blockIndent = -1;
            while (position < lines.Count)
            {
                (int _, int lineIndent, string text) = lines[position];
                if (text.Trim().Length == 0) { collected.Add(""); position++; continue; }
                if (lineIndent <= ownerIndent) break;
                if (blockIndent < 0) blockIndent = lineIndent;
                if (lineIndent < blockIndent) break;
                collected.Add(text[blockIndent..]);
                position++;
            }
            while (collected.Count > 0 && collected[^1].Length == 0) collected.RemoveAt(collected.Count - 1);
            string joined = indicator.StartsWith('>')
                ? string.Join(" ", collected).Replace("  ", " ", StringComparison.Ordinal)
                : string.Join("\n", collected);
            return indicator.EndsWith('-') ? joined : joined + "\n";
        }

        /// <summary>
        /// Whether a sequence item opens a mapping. As in YAML itself, a colon followed by a space outside a leading
        /// quote makes a key, so a script line containing <c>: </c> has to be quoted, exactly as GitLab requires.
        /// </summary>
        private static bool IsMappingEntry(string text) => FindColon(text) > 0;

        /// <summary>The first <c>: </c> (or a trailing colon) outside quotes, or -1.</summary>
        private static int FindColon(string text)
        {
            char quote = '\0';
            for (int index = 0; index < text.Length; index++)
            {
                char c = text[index];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if ((c == '"' || c == '\'') && index == 0) { quote = c; continue; }
                if (c == ':' && (index == text.Length - 1 || text[index + 1] == ' ')) return index;
            }
            return -1;
        }

        private static string StripComment(string text, int number)
        {
            char quote = '\0';
            for (int index = 0; index < text.Length; index++)
            {
                char c = text[index];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c is '"' or '\'' && (index == 0 || text[index - 1] is ' ' or '[' or ',' or ':' or '-')) { quote = c; continue; }
                if (c == '#' && (index == 0 || text[index - 1] == ' ')) return text[..index].TrimEnd();
            }
            return text;
        }

        private static YamlNode ParseInline(string text, int number)
        {
            if (text.StartsWith('&') || text.StartsWith('*')) throw new CiYamlException("Line " + number + " uses an anchor or alias, which Graft does not read.");
            if (text.StartsWith('!')) throw new CiYamlException("Line " + number + " uses a YAML tag, which Graft does not read.");
            if (text.StartsWith('{')) throw new CiYamlException("Line " + number + " uses a flow mapping ({...}); write it as indented 'key: value' lines.");
            if (text.StartsWith('['))
            {
                if (!text.EndsWith(']')) throw new CiYamlException("Line " + number + " opens a list with [ and does not close it on the same line.");
                string inner = text[1..^1].Trim();
                List<YamlNode> items = [];
                if (inner.Length > 0)
                    foreach (string part in SplitFlow(inner, number))
                        items.Add(new YamlScalar(Unquote(part.Trim(), number)));
                return new YamlSeq(items);
            }
            return new YamlScalar(Unquote(text, number));
        }

        private static List<string> SplitFlow(string text, int number)
        {
            List<string> parts = [];
            StringBuilder current = new();
            char quote = '\0';
            foreach (char c in text)
            {
                if (quote != '\0') { current.Append(c); if (c == quote) quote = '\0'; continue; }
                if (c is '"' or '\'') { quote = c; current.Append(c); continue; }
                if (c is '[' or '{') throw new CiYamlException("Line " + number + " nests a list or mapping inside [...], which Graft does not read.");
                if (c == ',') { parts.Add(current.ToString()); current.Clear(); continue; }
                current.Append(c);
            }
            parts.Add(current.ToString());
            return parts;
        }

        private static string Unquote(string text, int number)
        {
            if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            {
                StringBuilder result = new();
                for (int index = 1; index < text.Length - 1; index++)
                {
                    char c = text[index];
                    if (c != '\\') { result.Append(c); continue; }
                    if (++index >= text.Length - 1) throw new CiYamlException("Line " + number + " ends a quoted value with a backslash.");
                    result.Append(text[index] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        '"' => '"',
                        '\\' => '\\',
                        '/' => '/',
                        '0' => '\0',
                        _ => throw new CiYamlException("Line " + number + " uses the escape \\" + text[index] + ", which Graft does not read.")
                    });
                }
                return result.ToString();
            }
            if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
                return text[1..^1].Replace("''", "'", StringComparison.Ordinal);
            if (text.StartsWith('"') || text.StartsWith('\''))
                throw new CiYamlException("Line " + number + " opens a quoted value and does not close it on the same line.");
            return text;
        }
    }

    /// <summary>A value as a job's environment sees it.</summary>
    public static string Describe(CiPlan plan) =>
        string.Join("; ", plan.Stages.Select(stage => stage + ": " +
            string.Join(", ", plan.Jobs.Where(job => job.Stage == stage).Select(job => job.Name)))) +
        " (" + plan.Jobs.Count.ToString(CultureInfo.InvariantCulture) + " jobs)";
}
