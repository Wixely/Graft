using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// Retrying, canceling and erasing jobs, and listing a project's jobs.
///
/// <para>
/// A retry is a new attempt, as on GitLab: the old job keeps its result and log and is marked retried, and a copy is
/// queued. Every rule that reads a pipeline's jobs reads only the latest attempts, so an old failure stops blocking
/// the stages after it once it has been retried. A cancel ends a pending or running job; a running job's token is
/// cleared, which is how its runner finds out. The caller decides who may do these things; the store does them.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    /// <summary>Cancels one job that has not finished. Null when there is no such job in the project.</summary>
    public async Task<CiJobInfo?> CancelJobAsync(long projectId, long jobId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        JobRow? job = await db.QuerySingleOrDefaultAsync<JobRow>(JobSelect + " WHERE j.Id = @Id;", new IdParameters(jobId), transaction);
        if (job is null || job.ProjectId != projectId) return null;
        if (job.Status is CiStatus.Pending or CiStatus.Running)
            await CompleteJobAsync(db, transaction, jobId, CiStatus.Canceled, null, "Canceled.", Now());
        await transaction.CommitAsync(cancellationToken);
        return await GetJobUncheckedAsync(db, jobId);
    }

    /// <summary>Cancels every job of a pipeline that has not finished. Null when there is no such pipeline in the project.</summary>
    public async Task<PipelineInfo?> CancelPipelineAsync(long projectId, long pipelineId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PipelineInfo? pipeline = await PipelineByIdAsync(db, transaction, pipelineId);
        if (pipeline is null || pipeline.ProjectId != projectId) return null;
        string now = Now();
        await db.ExecuteAsync("""
            UPDATE Jobs SET Status = 'canceled', Failure = 'Canceled.', TokenDigest = NULL, FinishedAtUtc = @NowUtc
            WHERE PipelineId = @Id AND Retried = 0 AND Status IN ('pending', 'running');
            """, new TouchKeyParameters(pipelineId, now), transaction);
        // A pipeline that had already settled keeps its result.
        if (!CiStatus.IsFinished(pipeline.Status)) await SettlePipelineAsync(db, transaction, pipelineId, now);
        await transaction.CommitAsync(cancellationToken);
        return await PipelineByIdAsync(db, null, pipelineId);
    }

    /// <summary>
    /// Queues a new attempt of a finished job and reopens what its result had skipped. Null when there is no such job
    /// in the project; <see cref="InvalidOperationException"/> when the job has not finished or was already retried.
    /// </summary>
    public async Task<CiJobInfo?> RetryJobAsync(long projectId, long jobId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        JobRow? job = await db.QuerySingleOrDefaultAsync<JobRow>(JobSelect + " WHERE j.Id = @Id;", new IdParameters(jobId), transaction);
        if (job is null || job.ProjectId != projectId) return null;
        if (job.Status is not (CiStatus.Success or CiStatus.Failed or CiStatus.Canceled))
            throw new InvalidOperationException("Only a job that succeeded, failed or was canceled can be retried.");
        if (await db.ExecuteScalarAsync<long>("SELECT Retried FROM Jobs WHERE Id = @Id;", new IdParameters(jobId), transaction) != 0)
            throw new InvalidOperationException("This attempt has already been retried; retry the latest one.");
        string now = Now();
        long retried = await RequeueAsync(db, transaction, jobId, now);
        await ReopenAsync(db, transaction, job.PipelineId, now);
        await transaction.CommitAsync(cancellationToken);
        return await GetJobUncheckedAsync(db, retried);
    }

    /// <summary>
    /// Queues a new attempt of every job whose latest attempt failed or was canceled, and reopens what was skipped. Null
    /// when there is no such pipeline in the project; <see cref="InvalidOperationException"/> when nothing can be retried.
    /// </summary>
    public async Task<PipelineInfo?> RetryPipelineAsync(long projectId, long pipelineId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PipelineInfo? pipeline = await PipelineByIdAsync(db, transaction, pipelineId);
        if (pipeline is null || pipeline.ProjectId != projectId) return null;
        IEnumerable<long> again = await db.QueryAsync<long>(
            "SELECT Id FROM Jobs WHERE PipelineId = @Id AND Retried = 0 AND Status IN ('failed', 'canceled') ORDER BY Id;",
            new IdParameters(pipelineId), transaction);
        long[] ids = [.. again];
        long skipped = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Jobs WHERE PipelineId = @Id AND Retried = 0 AND Status = 'skipped';",
            new IdParameters(pipelineId), transaction);
        if (ids.Length == 0 && skipped == 0) throw new InvalidOperationException("Nothing in this pipeline failed or was canceled, so there is nothing to retry.");
        string now = Now();
        foreach (long id in ids) await RequeueAsync(db, transaction, id, now);
        await ReopenAsync(db, transaction, pipelineId, now);
        await transaction.CommitAsync(cancellationToken);
        return await PipelineByIdAsync(db, null, pipelineId);
    }

    /// <summary>
    /// Removes a finished job's log and artifacts, keeping the job. Returns the job as it was, so the caller can remove
    /// the archive it kept; null when there is no such job in the project. A job still running cannot be erased.
    /// </summary>
    public async Task<CiJobInfo?> EraseJobAsync(long projectId, long jobId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        JobRow? job = await db.QuerySingleOrDefaultAsync<JobRow>(JobSelect + " WHERE j.Id = @Id;", new IdParameters(jobId), transaction);
        if (job is null || job.ProjectId != projectId) return null;
        if (!CiStatus.IsFinished(job.Status)) throw new InvalidOperationException("A job is erased once it has finished.");
        await db.ExecuteAsync("DELETE FROM JobTraces WHERE JobId = @Id;", new IdParameters(jobId), transaction);
        await db.ExecuteAsync("UPDATE Jobs SET TraceLength = 0, ArtifactsBytes = NULL, ArtifactsSha256 = NULL WHERE Id = @Id;", new IdParameters(jobId), transaction);
        await transaction.CommitAsync(cancellationToken);
        return ToJob(job);
    }

    /// <summary>Forgets one job's artifacts; returns whether it had any. The caller removes the archive.</summary>
    public async Task<bool?> ForgetJobArtifactsAsync(long projectId, long jobId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        long? owner = await db.ExecuteScalarAsync<long?>("SELECT ProjectId FROM Jobs WHERE Id = @Id;", new IdParameters(jobId));
        if (owner != projectId) return null;
        return await db.ExecuteAsync("UPDATE Jobs SET ArtifactsBytes = NULL, ArtifactsSha256 = NULL WHERE Id = @Id AND ArtifactsBytes IS NOT NULL;",
            new IdParameters(jobId)) == 1;
    }

    /// <summary>Forgets every finished job's artifacts in a project and returns those jobs' ids, so the caller can remove the archives.</summary>
    public async Task<IReadOnlyList<long>> ForgetProjectArtifactsAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        IEnumerable<long> ids = await db.QueryAsync<long>(
            "SELECT Id FROM Jobs WHERE ProjectId = @Id AND ArtifactsBytes IS NOT NULL AND Status NOT IN ('pending', 'running');",
            new IdParameters(projectId), transaction);
        long[] forgotten = [.. ids];
        await db.ExecuteAsync("""
            UPDATE Jobs SET ArtifactsBytes = NULL, ArtifactsSha256 = NULL
            WHERE ProjectId = @Id AND ArtifactsBytes IS NOT NULL AND Status NOT IN ('pending', 'running');
            """, new IdParameters(projectId), transaction);
        await transaction.CommitAsync(cancellationToken);
        return forgotten;
    }

    /// <summary>
    /// A project's jobs, newest first, latest attempts only, optionally limited to some statuses. Read by whoever may
    /// browse the repository, as pipelines are.
    /// </summary>
    public async Task<(IReadOnlyList<CiJobInfo> Jobs, long Total)> ListProjectJobsAsync(AuthenticatedIdentity? actor, long projectId,
        IReadOnlyCollection<string> statuses, int skip, int take, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        if (skip < 0 || take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireCiReaderAsync(db, actor, projectId, cancellationToken);
        // Statuses are matched against a fixed list, never interpolated, so the filter is built from known words only.
        string[] known = [.. statuses.Where(status => status is CiStatus.Created or CiStatus.Pending or CiStatus.Running or CiStatus.Success
            or CiStatus.Failed or CiStatus.Skipped or CiStatus.Canceled).Distinct(StringComparer.Ordinal)];
        string filter = known.Length == 0 ? "" : " AND j.Status IN (" + string.Join(", ", known.Select(status => "'" + status + "'")) + ")";
        IEnumerable<JobRow> rows = await db.QueryAsync<JobRow>(JobSelect + " WHERE j.ProjectId = @Id AND j.Retried = 0" + filter +
            " ORDER BY j.Id DESC LIMIT @Take OFFSET @Skip;", new ScopedPageParameters(projectId, take, skip));
        long total = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Jobs j WHERE j.ProjectId = @Id AND j.Retried = 0" + filter + ";",
            new IdParameters(projectId));
        return ([.. rows.Select(ToJob)], total);
    }

    /// <summary>A job's status, or null when there is no such job: how the runner host tells a canceled job from a bad token.</summary>
    public async Task<string?> JobStatusAsync(long jobId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        return await db.ExecuteScalarAsync<string?>("SELECT Status FROM Jobs WHERE Id = @Id;", new IdParameters(jobId));
    }

    private static async Task<long> RequeueAsync(SqliteConnection db, SqliteTransaction transaction, long jobId, string now)
    {
        await db.ExecuteAsync("UPDATE Jobs SET Retried = 1 WHERE Id = @Id;", new IdParameters(jobId), transaction);
        return await db.QuerySingleAsync<long>("""
            INSERT INTO Jobs (PipelineId, ProjectId, Name, Stage, StageIndex, Script, Variables, AllowFailure, Status, CreatedAtUtc, ArtifactPaths)
            SELECT PipelineId, ProjectId, Name, Stage, StageIndex, Script, Variables, AllowFailure, 'pending', @NowUtc, ArtifactPaths
            FROM Jobs WHERE Id = @Id
            RETURNING Id;
            """, new TouchKeyParameters(jobId, now), transaction);
    }

    /// <summary>Puts skipped jobs back in the queue and the pipeline back to pending, after something in it was retried.</summary>
    private static async Task ReopenAsync(SqliteConnection db, SqliteTransaction transaction, long pipelineId, string now)
    {
        await db.ExecuteAsync("UPDATE Jobs SET Status = 'pending', FinishedAtUtc = NULL WHERE PipelineId = @Id AND Retried = 0 AND Status = 'skipped';",
            new IdParameters(pipelineId), transaction);
        await db.ExecuteAsync("""
            UPDATE Pipelines SET Status = CASE WHEN EXISTS (SELECT 1 FROM Jobs WHERE PipelineId = @Id AND Retried = 0 AND Status = 'running')
                THEN 'running' ELSE 'pending' END, UpdatedAtUtc = @NowUtc, FinishedAtUtc = NULL
            WHERE Id = @Id;
            """, new TouchKeyParameters(pipelineId, now), transaction);
    }

    private static async Task<CiJobInfo?> GetJobUncheckedAsync(SqliteConnection db, long jobId)
    {
        JobRow? row = await db.QuerySingleOrDefaultAsync<JobRow>(JobSelect + " WHERE j.Id = @Id;", new IdParameters(jobId));
        return row is null ? null : ToJob(row);
    }
}
