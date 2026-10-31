using System.Text.Json.Serialization;

namespace Graft.Contracts;

// ----------------------------------------------------------------------------------------------------
// The runner protocol: what travels between a Graft runner and the server's Shrine. Snake case, as the rest
// of the API. The runner's credential and a job's token travel in the authorization header, never in a body.
// ----------------------------------------------------------------------------------------------------

/// <summary>A runner claiming its registration with the one-time token from its install script.</summary>
public sealed record RunnerRegisterRequest(string? Token, string? Name, string? Platform, string? Version);

/// <summary>What a claimed registration returns: the runner's id, its credential, shown this once, and the agreed protocol.</summary>
public sealed record RunnerRegisterResponse([property: JsonPropertyName("runner_id")] long RunnerId, string Credential, int Protocol);

/// <summary>A job handed to a runner. The source is fetched as the named relic over the same session.</summary>
public sealed record RunnerJobResponse(
    [property: JsonPropertyName("job_id")] long JobId,
    [property: JsonPropertyName("job_token")] string JobToken,
    string Name,
    string Stage,
    string Project,
    string Ref,
    string Sha,
    string[] Script,
    Dictionary<string, string> Variables,
    /// <summary>The commit's source, as one relic or as chunks; see <see cref="TransferDescriptor"/>.</summary>
    TransferDescriptor Source,
    /// <summary>The paths the job keeps after a successful run; empty for nothing.</summary>
    string[] Artifacts,
    /// <summary>The forge's chunking rule, which the runner follows for anything it sends.</summary>
    TransferPolicy Transfer);

/// <summary>
/// When a transfer is chunked and how big a chunk is. <c>threshold</c> is in bytes; a file larger than it is chunked;
/// -1 means chunking is off and every file goes whole.
/// </summary>
public sealed record TransferPolicy(long Threshold, [property: JsonPropertyName("chunk_size")] long ChunkSize);

/// <summary>
/// A file the forge offers for download. Whole: one relic named <c>name</c>. Chunked: one relic per chunk, named
/// <c>name.00000</c> onward, each <c>chunk_size</c> long but the last, each with its SHA-256. The whole file's
/// SHA-256 is checked either way.
/// </summary>
public sealed record TransferDescriptor(
    string Name,
    long Size,
    string Sha256,
    bool Chunked,
    [property: JsonPropertyName("chunk_size")] long ChunkSize,
    [property: JsonPropertyName("chunk_sha256")] string[] ChunkSha256);

/// <summary>Opens or reopens an upload of a job's artifacts archive.</summary>
public sealed record ArtifactUploadRequest(long Size, string Sha256);

/// <summary>
/// Where an upload stands. Chunked: the forge has durably kept everything below <c>committed_offset</c>, and the
/// runner sends from there. Whole: the runner sends from <c>received_offset</c> on the same session and from zero
/// after any interruption.
/// </summary>
public sealed record ArtifactUploadResponse(
    [property: JsonPropertyName("upload_id")] string UploadId,
    bool Chunked,
    [property: JsonPropertyName("chunk_size")] long ChunkSize,
    [property: JsonPropertyName("committed_offset")] long CommittedOffset,
    [property: JsonPropertyName("received_offset")] long ReceivedOffset);

/// <summary>Closes a chunk: its index and the SHA-256 of its bytes.</summary>
public sealed record ArtifactChunkCommit(int Index, string Sha256);

/// <summary>A piece of a job's log, at the offset where the runner believes the log ends.</summary>
public sealed record RunnerTraceRequest(long Offset, string Text);

/// <summary>A job's result.</summary>
public sealed record RunnerFinishRequest(string Status, [property: JsonPropertyName("exit_code")] int? ExitCode, string? Failure);

// ----------------------------------------------------------------------------------------------------
// The administrator's and the projects' side, in GitLab's shapes as far as Graft has them.
// ----------------------------------------------------------------------------------------------------

/// <summary>GitLab's request to create a runner; Graft creates instance runners only.</summary>
public sealed record RunnerCreateRequest(string? Description, [property: JsonPropertyName("runner_type")] string? RunnerType);

/// <summary>
/// A runner just created: GitLab's <c>id</c>, <c>token</c> and <c>token_expires_at</c>, and Graft's additions, the
/// Intonation the runner connects with and the two install scripts that carry both. Shown once.
/// </summary>
public sealed record RunnerCreatedResponse(
    long Id,
    string Token,
    [property: JsonPropertyName("token_expires_at")] DateTimeOffset TokenExpiresAt,
    string Intonation,
    [property: JsonPropertyName("shrine_address")] string ShrineAddress,
    [property: JsonPropertyName("install_powershell")] string InstallPowerShell,
    [property: JsonPropertyName("install_shell")] string InstallShell);

/// <summary>A runner in GitLab's shape.</summary>
public sealed record RunnerResponse(
    long Id,
    string Description,
    string? Name,
    string? Platform,
    string? Version,
    [property: JsonPropertyName("runner_type")] string RunnerType,
    [property: JsonPropertyName("is_shared")] bool IsShared,
    bool Active,
    bool Paused,
    bool Online,
    string Status,
    [property: JsonPropertyName("contacted_at")] DateTimeOffset? ContactedAt,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

/// <summary>A pipeline in GitLab's shape, with Graft's <c>error</c> for a file that could not be run.</summary>
public sealed record PipelineResponse(
    long Id,
    long Iid,
    [property: JsonPropertyName("project_id")] long ProjectId,
    string Ref,
    string Sha,
    string Status,
    string Source,
    [property: JsonPropertyName("web_url")] string WebUrl,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("finished_at")] DateTimeOffset? FinishedAt,
    string? Error);

public sealed record JobPipelineResponse(long Id, long Iid, [property: JsonPropertyName("project_id")] long ProjectId, string Ref, string Sha, string Status);

public sealed record JobRunnerResponse(long Id, string Description);

/// <summary>GitLab's description of a job's artifacts archive.</summary>
public sealed record JobArtifactsFileResponse(string Filename, long Size);

/// <summary>A job in GitLab's shape, with Graft's <c>exit_code</c> and <c>failure</c>.</summary>
public sealed record JobResponse(
    long Id,
    string Name,
    string Stage,
    string Status,
    string Ref,
    [property: JsonPropertyName("allow_failure")] bool AllowFailure,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("finished_at")] DateTimeOffset? FinishedAt,
    double? Duration,
    JobPipelineResponse Pipeline,
    JobRunnerResponse? Runner,
    [property: JsonPropertyName("web_url")] string WebUrl,
    [property: JsonPropertyName("exit_code")] int? ExitCode,
    string? Failure,
    [property: JsonPropertyName("artifacts_file")] JobArtifactsFileResponse? ArtifactsFile = null);

/// <summary>GitLab's request to run a pipeline for a ref.</summary>
public sealed record PipelineCreateRequest(string? Ref);
