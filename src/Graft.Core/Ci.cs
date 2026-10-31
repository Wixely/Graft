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
    DateTimeOffset RegistrationExpiresAtUtc);

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
    DateTimeOffset? FinishedAtUtc);

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
    string JobToken);
