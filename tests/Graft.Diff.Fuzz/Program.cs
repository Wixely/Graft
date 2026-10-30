using System.Diagnostics;
using System.Text;
using Graft.Git;

// Compares Graft's diff against real git's on generated file pairs.
//
// The alphabet is deliberately tiny and full of blank lines, because that is what creates the ambiguity
// this is here to measure: when a change sits beside identical lines, several equally minimal scripts
// exist and the question is whether Graft picks the same one git does.

if (args.Length == 3 && args[0] == "file")
{
    // Compare two files on disk, for checking a case taken from a real repository.
    string a = File.ReadAllText(args[1]);
    string b = File.ReadAllText(args[2]);
    Console.WriteLine(Render(RepositoryDiff.CompareText(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b))));
    return 0;
}

const char SPLIT = '\n';
const string JOIN = "\n";
const string FILE = "f.txt";
const string GIT = "git";

// Git has applied its indent heuristic by default since 2.14, and since 2026-10-30 so does Graft, so the
// default comparison is the like-for-like one. Turning Git's off is kept for measuring the heuristic's own
// effect: with it off here and on in Graft, agreement drops, which is the heuristic doing its work.
string[] GitDiff = Environment.GetEnvironmentVariable("GRAFT_FUZZ_NO_INDENT") == "1"
    ? ["-c", "diff.indentHeuristic=false", "diff", "--no-color", "--no-renames", "--unified=3", "--", FILE]
    : ["diff", "--no-color", "--no-renames", "--unified=3", "--", FILE];

int seed = args.Length > 0 ? int.Parse(args[0]) : 1;
int cases = args.Length > 1 ? int.Parse(args[1]) : 300;
bool showFirst = args.Length > 2 && args[2] == "show";

string root = Path.Combine(Path.GetTempPath(), "difffuzz-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Run("git", root, "init", "-q", ".");
Run("git", root, "config", "user.email", "f@f.invalid");
Run("git", root, "config", "user.name", "F");
Run("git", root, "config", "core.autocrlf", "false");

string[] alphabet = ["", "", "a", "b", "c", "}", "    x", "    y", "# h"];
Random random = new(seed);
int matched = 0;
int mismatched = 0;
string firstFailure = "";

for (int round = 0; round < cases; round++)
{
    string before = Generate(random, alphabet);
    string after = Mutate(random, before, alphabet);
    if (before == after) continue;

    string path = Path.Combine(root, "f.txt");
    File.WriteAllText(path, before);
    Run("git", root, "add", "-A");
    Run("git", root, "commit", "-q", "-m", "x", "--allow-empty");
    File.WriteAllText(path, after);

    string expected = Normalise(Run(GIT, root, GitDiff));
    string actual = Normalise(Render(RepositoryDiff.CompareText(
        Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(after))));

    if (expected == actual) matched++;
    else
    {
        mismatched++;
        if (firstFailure.Length == 0)
        {
            (string small, string smaller) = Shrink(root, before, after);
            firstFailure = Report(root, small, smaller);
        }
    }
}

Console.WriteLine($"seed {seed}: matched {matched}, mismatched {mismatched}");
if (showFirst && firstFailure.Length > 0) Console.WriteLine(firstFailure);
try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
return mismatched == 0 ? 0 : 1;

// A seventeen-line failure is hard to reason about. Repeatedly drop a line from either side and keep the
// result whenever it still disagrees, which usually leaves something small enough to read in one go.
(string Before, string After) Shrink(string root, string before, string after)
{
    bool reduced = true;
    while (reduced)
    {
        reduced = false;
        foreach (bool fromBefore in new[] { true, false })
        {
            string[] lines = (fromBefore ? before : after).TrimEnd(SPLIT).Split(SPLIT);
            if (lines.Length <= 1) continue;
            for (int drop = 0; drop < lines.Length; drop++)
            {
                string candidate = string.Join(JOIN, lines.Where((_, index) => index != drop));
                if (candidate.Length > 0) candidate += JOIN;
                string candidateBefore = fromBefore ? candidate : before;
                string candidateAfter = fromBefore ? after : candidate;
                if (candidateBefore == candidateAfter) continue;
                if (!Disagrees(root, candidateBefore, candidateAfter)) continue;
                before = candidateBefore;
                after = candidateAfter;
                reduced = true;
                break;
            }
            if (reduced) break;
        }
    }
    return (before, after);
}

/// <summary>Runs both diffs for one pair and reports only whether they differ.</summary>
bool Disagrees(string root, string before, string after)
{
    (string expected, string actual) = Both(root, before, after);
    return expected != actual;
}

(string Expected, string Actual) Both(string root, string before, string after)
{
    string path = Path.Combine(root, FILE);
    File.WriteAllText(path, before);
    Run(GIT, root, "add", "-A");
    Run(GIT, root, "commit", "-q", "-m", "step", "--allow-empty");
    File.WriteAllText(path, after);
    string expected = Normalise(Run(GIT, root, GitDiff));
    string actual = Normalise(Render(RepositoryDiff.CompareText(
        Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(after))));
    return (expected, actual);
}

string Report(string root, string before, string after)
{
    (string expected, string actual) = Both(root, before, after);
    return "--- before ---" + JOIN + Numbered(before) + "--- after ---" + JOIN + Numbered(after) +
        "--- git ---" + JOIN + expected + "--- graft ---" + JOIN + actual;
}

/// <summary>Numbers the lines and names the blank ones, which are the ones that make placement ambiguous.</summary>
static string Numbered(string text)
{
    StringBuilder result = new();
    string[] lines = text.TrimEnd(SPLIT).Split(SPLIT);
    for (int index = 0; index < lines.Length; index++)
        result.Append((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(": ").Append(lines[index].Length == 0 ? "(blank)" : lines[index]).Append(JOIN);
    return result.ToString();
}

static string Generate(Random random, string[] alphabet)
{
    int count = random.Next(1, 18);
    StringBuilder text = new();
    for (int index = 0; index < count; index++) text.Append(alphabet[random.Next(alphabet.Length)]).Append('\n');
    return text.ToString();
}

static string Mutate(Random random, string original, string[] alphabet)
{
    List<string> lines = original.TrimEnd('\n').Split('\n').ToList();
    int edits = random.Next(1, 4);
    for (int edit = 0; edit < edits; edit++)
    {
        int kind = random.Next(3);
        if (lines.Count == 0 || kind == 0) lines.Insert(random.Next(lines.Count + 1), alphabet[random.Next(alphabet.Length)]);
        else if (kind == 1) lines.RemoveAt(random.Next(lines.Count));
        else lines[random.Next(lines.Count)] = alphabet[random.Next(alphabet.Length)];
    }
    return lines.Count == 0 ? "" : string.Join("\n", lines) + "\n";
}

// Only the hunk headers and the +/- and context lines; everything else git prints is formatting Graft
// does not claim to reproduce.
static string Normalise(string diff)
{
    StringBuilder result = new();
    bool started = false;
    foreach (string raw in diff.Split('\n'))
    {
        string line = raw.TrimEnd('\r');
        if (line.StartsWith("@@ ", StringComparison.Ordinal))
        {
            started = true;
            int end = line.IndexOf("@@", 3, StringComparison.Ordinal);
            result.Append(line[..(end + 2)]).Append('\n');
            continue;
        }
        if (!started) continue;
        if (line.Length == 0) continue;
        result.Append(line).Append('\n');
    }
    return result.ToString();
}

static string Render(IReadOnlyList<DiffHunk> hunks)
{
    StringBuilder result = new();
    foreach (DiffHunk hunk in hunks)
    {
        result.Append("@@ -").Append(Range(hunk.OldStart, hunk.OldLines))
              .Append(" +").Append(Range(hunk.NewStart, hunk.NewLines)).Append(" @@\n");
        foreach (DiffLine line in hunk.Lines)
        {
            result.Append(line.Kind).Append(line.Text).Append('\n');
            if (line.NoNewline) result.Append("\\ No newline at end of file\n");
        }
    }
    return result.ToString();

    static string Range(int start, int count) => count == 1
        ? start.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : start.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            count.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

static string Run(string file, string directory, params string[] arguments)
{
    ProcessStartInfo start = new(file)
    {
        WorkingDirectory = directory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using Process process = Process.Start(start)!;
    string output = process.StandardOutput.ReadToEnd();
    process.StandardError.ReadToEnd();
    process.WaitForExit();
    return output;
}
