using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// Runners, pipelines, jobs and their logs.
///
/// <para>
/// A runner exists because an administrator created it, and acts because it claimed that registration with a
/// one-time token and holds the credential it received. Neither the token nor the credential is kept, only
/// their digests. A runner is never identified by its transport: the CupriNet session that carries its requests
/// authenticates the server to the runner, and this store authenticates the runner to the server.
/// </para>
///
/// <para>
/// A job is leased to one runner at a time and only once every job of an earlier stage has succeeded, or failed
/// where it was allowed to. A failure that is not allowed skips every later stage. A job's token lets its runner
/// append to the log and finish the job, and nothing else; it stops working when the job finishes.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    /// <summary>How long a registration token, and the install script that carries it, stays usable.</summary>
    public static readonly TimeSpan RunnerRegistrationLifetime = TimeSpan.FromHours(24);

    /// <summary>Largest log kept per job; output past it is dropped with a note at the end.</summary>
    public const int MaximumTraceBytes = 4 * 1024 * 1024;

    // ------------------------------------------------------------------------------------------------
    // Runners
    // ------------------------------------------------------------------------------------------------

    public async Task<RunnerRegistration> CreateRunnerAsync(AuthenticatedIdentity actor, string description, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        description = (description ?? "").Trim();
        if (description.Length > 200) throw new ArgumentException("A runner description is at most 200 characters.", nameof(description));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        if (!liveActor.IsAdministrator) throw new UnauthorizedAccessException("Administrator access is required.");
        string token = NewSecret("graft_rreg_");
        string now = Now();
        DateTimeOffset expires = DateTimeOffset.UtcNow + RunnerRegistrationLifetime;
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO Runners (Description, RegistrationDigest, RegistrationExpiresAtUtc, CreatedByUserId, CreatedAtUtc)
            VALUES (@Description, @RegistrationDigest, @RegistrationExpiresAtUtc, @CreatedByUserId, @CreatedAtUtc)
            RETURNING Id;
            """, new RunnerInsertParameters(description, Digest(token), expires.ToString("O", CultureInfo.InvariantCulture), liveActor.UserId, now), transaction);
        await AuditAsync(db, transaction, liveActor.UserId, "create-runner", "runner", id.ToString(CultureInfo.InvariantCulture),
            "Runner registration created" + (description.Length == 0 ? "." : ": " + description + "."), now);
        await transaction.CommitAsync(cancellationToken);
        return new(new(id, description, null, null, null, Instant(now), null, null, expires), token);
    }

    /// <summary>
    /// The runner side of registration: the one-time token for a credential. Returns null for a token that is
    /// unknown, already used or expired, which the caller reports as one refusal.
    /// </summary>
    public async Task<(RunnerInfo Runner, string Credential)?> ClaimRunnerAsync(string registrationToken, string? name, string? platform, string? version,
        CancellationToken cancellationToken = default)
    {
        if (!ValidSecret(registrationToken ?? "", "graft_rreg_")) return null;
        name = Bounded(name, 100);
        platform = Bounded(platform, 100);
        version = Bounded(version, 50);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        string now = Now();
        long? id = await db.ExecuteScalarAsync<long?>("""
            SELECT Id FROM Runners
            WHERE RegistrationDigest = @SecretDigest AND CredentialDigest IS NULL AND RegistrationExpiresAtUtc > @NowUtc;
            """, new ResolveSecretParameters(Digest(registrationToken!), now), transaction);
        if (id is null) return null;
        string credential = NewSecret("graft_runner_");
        await db.ExecuteAsync("""
            UPDATE Runners SET RegistrationDigest = NULL, CredentialDigest = @CredentialDigest, Name = @Name, Platform = @Platform,
                               Version = @Version, ClaimedAtUtc = @NowUtc, ContactedAtUtc = @NowUtc
            WHERE Id = @Id;
            """, new RunnerClaimParameters(id.Value, Digest(credential), name, platform, version, now), transaction);
        await AuditAsync(db, transaction, null, "claim-runner", "runner", id.Value.ToString(CultureInfo.InvariantCulture),
            "Runner registration claimed by " + (name ?? "an unnamed runner") + (platform is null ? "" : " on " + platform) + ".", now);
        await transaction.CommitAsync(cancellationToken);
        RunnerInfo runner = (await RunnerByIdAsync(db, null, id.Value))!;
        return (runner, credential);
    }

    /// <summary>The runner a credential belongs to, recording the contact; null for an unknown credential.</summary>
    public async Task<RunnerInfo?> ResolveRunnerAsync(string credential, CancellationToken cancellationToken = default)
    {
        if (!ValidSecret(credential ?? "", "graft_runner_")) return null;
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        string now = Now();
        long? id = await db.ExecuteScalarAsync<long?>("SELECT Id FROM Runners WHERE CredentialDigest = @SecretDigest;",
            new ResolveSecretParameters(Digest(credential!), now));
        if (id is null) return null;
        await db.ExecuteAsync("UPDATE Runners SET ContactedAtUtc = @NowUtc WHERE Id = @Id;", new TouchKeyParameters(id.Value, now));
        return await RunnerByIdAsync(db, null, id.Value);
    }

    public async Task<IReadOnlyList<RunnerInfo>> ListRunnersAsync(AuthenticatedIdentity actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, null, actor, cancellationToken);
        if (!liveActor.IsAdministrator) throw new UnauthorizedAccessException("Administrator access is required.");
        IEnumerable<RunnerRow> rows = await db.QueryAsync<RunnerRow>(RunnerSelect + " ORDER BY Id;");
        return rows.Select(ToRunner).ToArray();
    }

    /// <summary>Removes a runner. Its credential stops working at once; a job it was running fails, saying why.</summary>
    public async Task<bool> DeleteRunnerAsync(AuthenticatedIdentity actor, long runnerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        if (!liveActor.IsAdministrator) throw new UnauthorizedAccessException("Administrator access is required.");
        if (await RunnerByIdAsync(db, transaction, runnerId) is null) return false;
        string now = Now();
        IEnumerable<long> running = await db.QueryAsync<long>("SELECT Id FROM Jobs WHERE RunnerId = @Id AND Status = 'running';", new IdParameters(runnerId), transaction);
        foreach (long jobId in running.ToList())
            await CompleteJobAsync(db, transaction, jobId, CiStatus.Failed, null, "The runner running this job was removed.", now);
        await db.ExecuteAsync("UPDATE Jobs SET RunnerId = NULL WHERE RunnerId = @Id;", new IdParameters(runnerId), transaction);
        await db.ExecuteAsync("DELETE FROM Runners WHERE Id = @Id;", new IdParameters(runnerId), transaction);
        await AuditAsync(db, transaction, liveActor.UserId, "delete-runner", "runner", runnerId.ToString(CultureInfo.InvariantCulture), "Runner removed.", now);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // ------------------------------------------------------------------------------------------------
    // Pipelines
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Records a pipeline for a commit. The caller has already decided the push or request was allowed. A file
    /// that could not be read makes a failed pipeline carrying the reason; a plan makes a pending pipeline with
    /// a pending job for each job in it.
    /// </summary>
    public async Task<PipelineInfo> CreatePipelineAsync(long projectId, long? createdByUserId, string reference, string sha, string source,
        CiParseResult parsed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        string now = Now();
        long iid = await db.ExecuteScalarAsync<long>("SELECT COALESCE(MAX(Iid), 0) + 1 FROM Pipelines WHERE ProjectId = @Id;", new IdParameters(projectId), transaction);
        string status = parsed.Ok ? CiStatus.Pending : CiStatus.Failed;
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO Pipelines (ProjectId, Iid, Ref, Sha, Status, Source, Error, CreatedByUserId, CreatedAtUtc, UpdatedAtUtc, FinishedAtUtc)
            VALUES (@ProjectId, @Iid, @Ref, @Sha, @Status, @Source, @Error, @CreatedByUserId, @NowUtc, @NowUtc, @FinishedAtUtc)
            RETURNING Id;
            """, new PipelineInsertParameters(projectId, iid, reference, sha, status, source, parsed.Error, createdByUserId, now,
                parsed.Ok ? null : now), transaction);
        if (parsed.Plan is CiPlan plan)
            foreach (CiJobDefinition job in plan.Jobs)
                await db.ExecuteAsync("""
                    INSERT INTO Jobs (PipelineId, ProjectId, Name, Stage, StageIndex, Script, Variables, AllowFailure, Status, CreatedAtUtc, ArtifactPaths)
                    VALUES (@PipelineId, @ProjectId, @Name, @Stage, @StageIndex, @Script, @Variables, @AllowFailure, 'pending', @CreatedAtUtc, @ArtifactPaths);
                    """, new JobInsertParameters(id, projectId, job.Name, job.Stage, job.StageIndex, EncodeLines(job.Script),
                        EncodeVariables(job.Variables), job.AllowFailure ? 1 : 0, now, EncodeLines(job.ArtifactPaths ?? [])), transaction);
        await transaction.CommitAsync(cancellationToken);
        return (await PipelineByIdAsync(db, null, id))!;
    }

    public async Task<(IReadOnlyList<PipelineInfo> Pipelines, long Total)> ListPipelinesAsync(AuthenticatedIdentity? actor, long projectId, int skip, int take,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0 || take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireCiReaderAsync(db, actor, projectId, cancellationToken);
        IEnumerable<PipelineRow> rows = await db.QueryAsync<PipelineRow>(PipelineSelect + " WHERE ProjectId = @Id ORDER BY Id DESC LIMIT @Take OFFSET @Skip;",
            new ScopedPageParameters(projectId, take, skip));
        long total = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Pipelines WHERE ProjectId = @Id;", new IdParameters(projectId));
        return (rows.Select(ToPipeline).ToArray(), total);
    }

    public async Task<PipelineInfo?> GetPipelineAsync(AuthenticatedIdentity? actor, long projectId, long pipelineId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireCiReaderAsync(db, actor, projectId, cancellationToken);
        PipelineInfo? pipeline = await PipelineByIdAsync(db, null, pipelineId);
        return pipeline?.ProjectId == projectId ? pipeline : null;
    }

    public async Task<IReadOnlyList<CiJobInfo>> ListPipelineJobsAsync(AuthenticatedIdentity? actor, long projectId, long pipelineId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireCiReaderAsync(db, actor, projectId, cancellationToken);
        IEnumerable<JobRow> rows = await db.QueryAsync<JobRow>(JobSelect + " WHERE j.PipelineId = @PipelineId AND j.ProjectId = @ProjectId ORDER BY j.StageIndex, j.Id;",
            new PipelineJobsParameters(pipelineId, projectId));
        return rows.Select(ToJob).ToArray();
    }

    public async Task<CiJobInfo?> GetJobAsync(AuthenticatedIdentity? actor, long projectId, long jobId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireCiReaderAsync(db, actor, projectId, cancellationToken);
        JobRow? row = await db.QuerySingleOrDefaultAsync<JobRow>(JobSelect + " WHERE j.Id = @Id;", new IdParameters(jobId));
        return row is null || row.ProjectId != projectId ? null : ToJob(row);
    }

    /// <summary>A job's log so far, or null when there is no such job in the project.</summary>
    public async Task<string?> ReadTraceAsync(AuthenticatedIdentity? actor, long projectId, long jobId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireCiReaderAsync(db, actor, projectId, cancellationToken);
        long owner = await db.ExecuteScalarAsync<long>("SELECT COALESCE((SELECT ProjectId FROM Jobs WHERE Id = @Id), 0);", new IdParameters(jobId));
        if (owner != projectId) return null;
        IEnumerable<string> parts = await db.QueryAsync<string>("SELECT Text FROM JobTraces WHERE JobId = @Id ORDER BY Sequence;", new IdParameters(jobId));
        return string.Concat(parts);
    }

    // ------------------------------------------------------------------------------------------------
    // The runner's side of a job
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The next job this runner may take, or null when there is none: the oldest pending job whose earlier stages
    /// have all succeeded or failed where that was allowed. Marks it running for this runner and returns a fresh
    /// token for it.
    /// </summary>
    public async Task<CiLease?> LeaseJobAsync(long runnerId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        long? jobId = await db.ExecuteScalarAsync<long?>("""
            SELECT j.Id FROM Jobs j
            JOIN Pipelines p ON p.Id = j.PipelineId
            WHERE j.Status = 'pending' AND p.Status IN ('pending', 'running')
              AND NOT EXISTS (
                  SELECT 1 FROM Jobs e
                  WHERE e.PipelineId = j.PipelineId AND e.StageIndex < j.StageIndex
                    AND NOT (e.Status = 'success' OR (e.Status = 'failed' AND e.AllowFailure = 1)))
            ORDER BY j.Id
            LIMIT 1;
            """, transaction: transaction);
        if (jobId is null) return null;
        string token = NewSecret("graft_job_");
        string now = Now();
        await db.ExecuteAsync("""
            UPDATE Jobs SET Status = 'running', RunnerId = @RunnerId, TokenDigest = @TokenDigest, StartedAtUtc = @NowUtc WHERE Id = @Id;
            """, new JobLeaseParameters(jobId.Value, runnerId, Digest(token), now), transaction);
        JobRow job = (await db.QuerySingleOrDefaultAsync<JobRow>(JobSelect + " WHERE j.Id = @Id;", new IdParameters(jobId.Value), transaction))!;
        await db.ExecuteAsync("UPDATE Pipelines SET Status = 'running', UpdatedAtUtc = @NowUtc WHERE Id = @Id;", new TouchKeyParameters(job.PipelineId, now), transaction);
        await transaction.CommitAsync(cancellationToken);
        PipelineInfo pipeline = (await PipelineByIdAsync(db, null, job.PipelineId))!;
        ProjectInfo project = (await GetReadyProjectAsync(job.ProjectId, cancellationToken))!.Project;
        return new(ToJob(job), pipeline, project, DecodeLines(job.Script), DecodeVariables(job.Variables), token, DecodeLines(job.ArtifactPaths));
    }

    /// <summary>The outcome of appending to a job's log.</summary>
    public enum TraceAppend { Accepted, Duplicate, Conflict, Unauthorized }

    /// <summary>
    /// Appends <paramref name="text"/> at <paramref name="offset"/>, which must be where the log ends. A retry of
    /// a chunk already stored is accepted as a duplicate. Past the log's bound the text is dropped, once, with a note.
    /// </summary>
    public async Task<TraceAppend> AppendTraceAsync(long jobId, string jobToken, long offset, string text, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        JobTokenRow? job = await JobForTokenAsync(db, transaction, jobId, jobToken);
        if (job is null) return TraceAppend.Unauthorized;
        if (offset < job.TraceLength) return offset + text.Length <= job.TraceLength ? TraceAppend.Duplicate : TraceAppend.Conflict;
        if (offset > job.TraceLength) return TraceAppend.Conflict;
        if (text.Length == 0) return TraceAppend.Accepted;
        string stored = text;
        long length = job.TraceLength + text.Length;
        if (job.TraceLength >= MaximumTraceBytes) return TraceAppend.Accepted;
        if (length > MaximumTraceBytes)
        {
            stored = text[..(int)(MaximumTraceBytes - job.TraceLength)] + "\n[Graft: the log reached 4 MiB; further output is not kept]\n";
            length = MaximumTraceBytes;
        }
        long sequence = await db.ExecuteScalarAsync<long>("SELECT COALESCE(MAX(Sequence), 0) + 1 FROM JobTraces WHERE JobId = @Id;", new IdParameters(jobId), transaction);
        await db.ExecuteAsync("INSERT INTO JobTraces (JobId, Sequence, Text) VALUES (@JobId, @Sequence, @Text);",
            new TraceInsertParameters(jobId, sequence, stored), transaction);
        // The length the runner counts in is what it sent, so a retry after a truncation still lines up.
        await db.ExecuteAsync("UPDATE Jobs SET TraceLength = @Length WHERE Id = @Id;", new TraceLengthParameters(jobId, job.TraceLength + text.Length), transaction);
        await transaction.CommitAsync(cancellationToken);
        return TraceAppend.Accepted;
    }

    /// <summary>Finishes a running job with its result. False when the token is not this running job's.</summary>
    public async Task<bool> FinishJobAsync(long jobId, string jobToken, bool succeeded, int? exitCode, string? failure, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        if (await JobForTokenAsync(db, transaction, jobId, jobToken) is null) return false;
        await CompleteJobAsync(db, transaction, jobId, succeeded ? CiStatus.Success : CiStatus.Failed, exitCode, Bounded(failure, 1000), Now());
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Records the artifacts archive a running job uploaded, once its bytes are stored and verified. False when the
    /// token is not this running job's. A second upload replaces the first.
    /// </summary>
    public async Task<bool> RecordArtifactsAsync(long jobId, string jobToken, long bytes, string sha256, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        if (await JobForTokenAsync(db, transaction, jobId, jobToken) is null) return false;
        await db.ExecuteAsync("UPDATE Jobs SET ArtifactsBytes = @Bytes, ArtifactsSha256 = @Sha256 WHERE Id = @Id;",
            new ArtifactRecordParameters(jobId, bytes, sha256), transaction);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>The project a job belongs to, or null when there is no such job.</summary>
    public async Task<long?> FindJobProjectIdAsync(long jobId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        return await db.ExecuteScalarAsync<long?>("SELECT ProjectId FROM Jobs WHERE Id = @Id;", new IdParameters(jobId));
    }

    /// <summary>Whether this token belongs to this running job, for a caller about to accept bytes for it.</summary>
    public async Task<bool> IsRunningJobTokenAsync(long jobId, string jobToken, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: true);
        return await JobForTokenAsync(db, transaction, jobId, jobToken) is not null;
    }

    /// <summary>Fails a job the server could not hand over, such as one whose source is too large to send.</summary>
    public async Task FailLeasedJobAsync(long jobId, string reason, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await CompleteJobAsync(db, transaction, jobId, CiStatus.Failed, null, Bounded(reason, 1000), Now());
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Sets a job's result, skips what a disallowed failure blocks, and settles the pipeline when nothing is left.</summary>
    private static async Task CompleteJobAsync(SqliteConnection db, SqliteTransaction transaction, long jobId, string status, int? exitCode, string? failure, string now)
    {
        JobRow job = (await db.QuerySingleOrDefaultAsync<JobRow>(JobSelect + " WHERE j.Id = @Id;", new IdParameters(jobId), transaction))!;
        await db.ExecuteAsync("""
            UPDATE Jobs SET Status = @Status, ExitCode = @ExitCode, Failure = @Failure, TokenDigest = NULL, FinishedAtUtc = @NowUtc
            WHERE Id = @Id AND Status IN ('pending', 'running');
            """, new JobFinishParameters(jobId, status, exitCode, failure, now), transaction);
        if (status == CiStatus.Failed && job.AllowFailure == 0)
            await db.ExecuteAsync("""
                UPDATE Jobs SET Status = 'skipped', FinishedAtUtc = @NowUtc WHERE PipelineId = @PipelineId AND StageIndex > @StageIndex AND Status = 'pending';
                """, new SkipLaterParameters(job.PipelineId, job.StageIndex, now), transaction);
        long open = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Jobs WHERE PipelineId = @Id AND Status IN ('pending', 'running');",
            new IdParameters(job.PipelineId), transaction);
        if (open > 0)
        {
            await db.ExecuteAsync("UPDATE Pipelines SET UpdatedAtUtc = @NowUtc WHERE Id = @Id;", new TouchKeyParameters(job.PipelineId, now), transaction);
            return;
        }
        long failed = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Jobs WHERE PipelineId = @Id AND Status = 'failed' AND AllowFailure = 0;",
            new IdParameters(job.PipelineId), transaction);
        await db.ExecuteAsync("UPDATE Pipelines SET Status = @Status, UpdatedAtUtc = @NowUtc, FinishedAtUtc = @NowUtc WHERE Id = @Id;",
            new PipelineSettleParameters(job.PipelineId, failed > 0 ? CiStatus.Failed : CiStatus.Success, now), transaction);
    }

    private static async Task<JobTokenRow?> JobForTokenAsync(SqliteConnection db, SqliteTransaction transaction, long jobId, string jobToken)
    {
        if (!ValidSecret(jobToken ?? "", "graft_job_")) return null;
        return await db.QuerySingleOrDefaultAsync<JobTokenRow>(
            "SELECT Id, TraceLength FROM Jobs WHERE Id = @Id AND TokenDigest = @TokenDigest AND Status = 'running';",
            new JobTokenParameters(jobId, Digest(jobToken!)), transaction);
    }

    /// <summary>Pipelines are read by whoever may browse the repository, which is what they are about.</summary>
    private async Task RequireCiReaderAsync(SqliteConnection db, AuthenticatedIdentity? actor, long projectId, CancellationToken cancellationToken)
    {
        PermissionDecision decision = await DecideAsync(db, null, actor, projectId, ProjectAction.BrowseRepository, cancellationToken);
        if (!decision.Allowed) throw new UnauthorizedAccessException(decision.Explanation);
    }

    // ------------------------------------------------------------------------------------------------
    // Rows
    // ------------------------------------------------------------------------------------------------

    private const string RunnerSelect = """
        SELECT Id, Description, Name, Platform, Version, CreatedAtUtc, ClaimedAtUtc, ContactedAtUtc, RegistrationExpiresAtUtc FROM Runners
        """;
    private const string PipelineSelect = """
        SELECT Id, ProjectId, Iid, Ref, Sha, Status, Source, Error, CreatedAtUtc, UpdatedAtUtc, FinishedAtUtc FROM Pipelines
        """;
    private const string JobSelect = """
        SELECT j.Id, j.PipelineId, j.ProjectId, j.Name, j.Stage, j.StageIndex, j.Script, j.Variables, j.AllowFailure, j.Status, j.RunnerId,
               r.Description AS RunnerDescription, j.ExitCode, j.Failure, j.CreatedAtUtc, j.StartedAtUtc, j.FinishedAtUtc,
               j.ArtifactPaths, j.ArtifactsBytes, j.ArtifactsSha256
        FROM Jobs j
        LEFT JOIN Runners r ON r.Id = j.RunnerId
        """;

    private static async Task<RunnerInfo?> RunnerByIdAsync(SqliteConnection db, SqliteTransaction? transaction, long id)
    {
        RunnerRow? row = await db.QuerySingleOrDefaultAsync<RunnerRow>(RunnerSelect + " WHERE Id = @Id;", new IdParameters(id), transaction);
        return row is null ? null : ToRunner(row);
    }

    private static async Task<PipelineInfo?> PipelineByIdAsync(SqliteConnection db, SqliteTransaction? transaction, long id)
    {
        PipelineRow? row = await db.QuerySingleOrDefaultAsync<PipelineRow>(PipelineSelect + " WHERE Id = @Id;", new IdParameters(id), transaction);
        return row is null ? null : ToPipeline(row);
    }

    private static RunnerInfo ToRunner(RunnerRow row) => new(row.Id, row.Description, row.Name, row.Platform, row.Version, Instant(row.CreatedAtUtc),
        row.ClaimedAtUtc is null ? null : Instant(row.ClaimedAtUtc), row.ContactedAtUtc is null ? null : Instant(row.ContactedAtUtc),
        Instant(row.RegistrationExpiresAtUtc));

    private static PipelineInfo ToPipeline(PipelineRow row) => new(row.Id, row.ProjectId, row.Iid, row.Ref, row.Sha, row.Status, row.Source, row.Error,
        Instant(row.CreatedAtUtc), Instant(row.UpdatedAtUtc), row.FinishedAtUtc is null ? null : Instant(row.FinishedAtUtc));

    private static CiJobInfo ToJob(JobRow row) => new(row.Id, row.PipelineId, row.ProjectId, row.Name, row.Stage, (int)row.StageIndex, row.Status,
        row.AllowFailure != 0, row.RunnerId, row.RunnerDescription, row.ExitCode is null ? null : (int)row.ExitCode.Value, row.Failure,
        Instant(row.CreatedAtUtc), row.StartedAtUtc is null ? null : Instant(row.StartedAtUtc), row.FinishedAtUtc is null ? null : Instant(row.FinishedAtUtc),
        row.ArtifactsBytes, row.ArtifactsSha256);

    private static string? Bounded(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim();
        return trimmed.Length <= maximum ? trimmed : trimmed[..maximum];
    }

    // Scripts and variables are stored as JSON written and read by hand, because the storage layer has no
    // serializer context and a reflective one would not survive the NativeAOT build.
    private static string EncodeLines(IReadOnlyList<string> lines)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartArray();
            foreach (string line in lines) writer.WriteStringValue(line);
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static IReadOnlyList<string> DecodeLines(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
    }

    private static string EncodeVariables(IReadOnlyDictionary<string, string> variables)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            foreach ((string key, string value) in variables) writer.WriteString(key, value);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static IReadOnlyDictionary<string, string> DecodeVariables(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString() ?? "", StringComparer.Ordinal);
    }
}

internal sealed record RunnerInsertParameters(string Description, string RegistrationDigest, string RegistrationExpiresAtUtc, long CreatedByUserId, string CreatedAtUtc);
internal sealed record RunnerClaimParameters(long Id, string CredentialDigest, string? Name, string? Platform, string? Version, string NowUtc);
internal sealed record PipelineInsertParameters(long ProjectId, long Iid, string Ref, string Sha, string Status, string Source, string? Error, long? CreatedByUserId,
    string NowUtc, string? FinishedAtUtc);
internal sealed record JobInsertParameters(long PipelineId, long ProjectId, string Name, string Stage, int StageIndex, string Script, string Variables,
    int AllowFailure, string CreatedAtUtc, string ArtifactPaths);
internal sealed record ScopedPageParameters(long Id, int Take, int Skip);
internal sealed record PipelineJobsParameters(long PipelineId, long ProjectId);
internal sealed record JobLeaseParameters(long Id, long RunnerId, string TokenDigest, string NowUtc);
internal sealed record JobTokenParameters(long Id, string TokenDigest);
internal sealed record TraceInsertParameters(long JobId, long Sequence, string Text);
internal sealed record TraceLengthParameters(long Id, long Length);
internal sealed record JobFinishParameters(long Id, string Status, int? ExitCode, string? Failure, string NowUtc);
internal sealed record SkipLaterParameters(long PipelineId, long StageIndex, string NowUtc);
internal sealed record PipelineSettleParameters(long Id, string Status, string NowUtc);
internal sealed record RunnerRow(long Id, string Description, string? Name, string? Platform, string? Version, string CreatedAtUtc, string? ClaimedAtUtc,
    string? ContactedAtUtc, string RegistrationExpiresAtUtc);
internal sealed record PipelineRow(long Id, long ProjectId, long Iid, string Ref, string Sha, string Status, string Source, string? Error, string CreatedAtUtc,
    string UpdatedAtUtc, string? FinishedAtUtc);
internal sealed record JobRow(long Id, long PipelineId, long ProjectId, string Name, string Stage, long StageIndex, string Script, string Variables, long AllowFailure,
    string Status, long? RunnerId, string? RunnerDescription, long? ExitCode, string? Failure, string CreatedAtUtc, string? StartedAtUtc, string? FinishedAtUtc,
    string ArtifactPaths, long? ArtifactsBytes, string? ArtifactsSha256);
internal sealed record JobTokenRow(long Id, long TraceLength);
internal sealed record ArtifactRecordParameters(long Id, long Bytes, string Sha256);
