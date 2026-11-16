namespace Graft.Core;

/// <summary>
/// A runner Graft knows: registered by an administrator, claimed by the runner itself with a one-time token,
/// and from then on authenticated by a credential only the runner holds. <see cref="Name"/> and
/// <see cref="Platform"/> are what the runner said about itself when it claimed the registration.
/// </summary>
public sealed record RunnerInfo(
    long Id,
    string Description,
    string? Name,
    string? Platform,
    string? Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ClaimedAtUtc,
    DateTimeOffset? ContactedAtUtc,
    DateTimeOffset RegistrationExpiresAtUtc,
    /// <summary>The tags an administrator gave the runner; a job is routed by them.</summary>
    IReadOnlyList<string>? Tags = null,
    /// <summary>Whether the runner also takes jobs that ask for no tags.</summary>
    bool RunUntagged = true);

/// <summary>
/// The rule for runner and job tags, shared by the pipeline reader and the runner settings so a tag a job asks for
/// is spelled the way a runner's is. A tag is trimmed and lower-cased, as GitLab compares tags without regard to
/// case; it is at most 64 characters with no comma, because GitLab's API sends a runner's tags as one
/// comma-separated string; a list holds at most 20 distinct tags.
/// </summary>
public static class RunnerTags
{
    public const int MaximumTags = 20;
    public const int MaximumLength = 64;

    public static bool TryNormalise(IEnumerable<string> tags, out IReadOnlyList<string> normal, out string error)
    {
        ArgumentNullException.ThrowIfNull(tags);
        List<string> result = [];
        normal = result;
        error = "";
        foreach (string written in tags)
        {
            string tag = (written ?? "").Trim().ToLowerInvariant();
            if (tag.Length == 0) { error = "a tag is empty."; return false; }
            if (tag.Length > MaximumLength) { error = "'" + tag[..20] + "…' is longer than 64 characters."; return false; }
            if (tag.Contains(',') || tag.Any(char.IsControl)) { error = "'" + tag + "' contains a comma or a control character."; return false; }
            if (!result.Contains(tag, StringComparer.Ordinal)) result.Add(tag);
        }
        if (result.Count > MaximumTags) { error = "more than 20 tags."; return false; }
        return true;
    }

    /// <summary>GitLab's <c>tag_list</c> as a string: tags separated by commas.</summary>
    public static bool TryParseList(string? list, out IReadOnlyList<string> normal, out string error) =>
        TryNormalise((list ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), out normal, out error);

    /// <summary>Whether a runner with these tags may take a job asking for <paramref name="jobTags"/>.</summary>
    public static bool Satisfies(IReadOnlyCollection<string> runnerTags, bool runUntagged, IReadOnlyCollection<string> jobTags) =>
        jobTags.Count == 0 ? runUntagged : jobTags.All(runnerTags.Contains);
}

/// <summary>A runner just created: its row, and the one-time registration token the install script carries.</summary>
public sealed record RunnerRegistration(RunnerInfo Runner, string RegistrationToken);

/// <summary>The status words GitLab uses for pipelines and jobs, as far as Graft has them.</summary>
public static class CiStatus
{
    public const string Created = "created";
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Success = "success";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Canceled = "canceled";

    public static bool IsFinished(string status) => status is Success or Failed or Skipped or Canceled;
}

/// <summary>A pipeline: the jobs one push asked for, or the reason the file could not be run.</summary>
public sealed record PipelineInfo(
    long Id,
    long ProjectId,
    long Iid,
    string Ref,
    string Sha,
    string Status,
    string Source,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? FinishedAtUtc);

/// <summary>A job of a pipeline, and who ran it.</summary>
public sealed record CiJobInfo(
    long Id,
    long PipelineId,
    long ProjectId,
    string Name,
    string Stage,
    int StageIndex,
    string Status,
    bool AllowFailure,
    long? RunnerId,
    string? RunnerDescription,
    int? ExitCode,
    string? Failure,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    /// <summary>The size of the job's kept artifacts archive, or null when it kept none.</summary>
    long? ArtifactsBytes = null,
    string? ArtifactsSha256 = null,
    /// <summary>The tags a runner needs to take the job.</summary>
    IReadOnlyList<string>? Tags = null,
    /// <summary>Pending, and no registered runner has the tags to take it, so it waits until one does.</summary>
    bool Stuck = false);

/// <summary>
/// A job handed to a runner: what to run, against which commit of which project, and the token that lets the
/// runner report on this job and on nothing else.
/// </summary>
public sealed record CiLease(
    CiJobInfo Job,
    PipelineInfo Pipeline,
    ProjectInfo Project,
    IReadOnlyList<string> Script,
    IReadOnlyDictionary<string, string> Variables,
    string JobToken,
    IReadOnlyList<string> ArtifactPaths);
