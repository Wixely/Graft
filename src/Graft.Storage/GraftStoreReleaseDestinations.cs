using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// Release destinations and publications (ADR 0052): where a project's releases can be published outside the
/// forge, and each attempt to publish one.
///
/// <para>
/// Everything here needs the permission to manage the project. A destination holds a credential and decides
/// what leaves the forge under the project's name, and a publication spends that credential; neither is a
/// Developer's. The endpoint and the key arrive already protected by the server's key ring and are handed back
/// only to the worker, never to a caller, and never written to the audit trail.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    /// <summary>How many publications of one release are kept. An attempt is history, not a record to keep for ever.</summary>
    private const int PublicationsKeptPerRelease = 20;

    public async Task<IReadOnlyList<ReleaseDestination>> ListReleaseDestinationsAsync(
        AuthenticatedIdentity actor,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireDestinationManagerAsync(db, null, actor, projectId, cancellationToken);
        IEnumerable<ReleaseDestinationRow> rows = await db.QueryAsync<ReleaseDestinationRow>(
            ReleaseDestinationSelect + "WHERE d.ProjectId = @Id ORDER BY d.Name;", new IdParameters(projectId));
        return [.. rows.Select(ToReleaseDestination)];
    }

    /// <summary>
    /// Creates a destination or changes one. A new destination needs its endpoint and its key; a change may
    /// leave either out to keep what is stored, which is how a setting is edited without the key being typed again.
    /// </summary>
    public async Task<(ReleaseDestination Destination, bool Created)> PutReleaseDestinationAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string name,
        string? endpointHost,
        string? protectedEndpoint,
        string? protectedKey,
        string applicationId,
        string channel,
        bool allowProductionPromotion,
        string? packageName,
        IReadOnlyList<ArtifactRule> rules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(rules);
        if (!ReleaseDestinations.IsName(name))
            throw new ArgumentException("A destination's name is up to 40 lower-case letters, digits, '-' and '_', starting with a letter or digit.", nameof(name));
        if (!ReleaseDestinations.IsApplicationId(applicationId))
            throw new ArgumentException("The application is named by its UUID, with dashes.", nameof(applicationId));
        if (!ReleaseDestinations.Channels.Contains(channel)) throw new ArgumentException("The channel is none, stable or beta.", nameof(channel));
        if (channel != "none" && !allowProductionPromotion)
            throw new ArgumentException("Publishing to the " + channel + " channel needs production promotion to be allowed for this destination.", nameof(channel));
        if (packageName is { Length: 0 }) packageName = null;
        if (packageName is not null && !ReleaseDestinations.IsPackageName(packageName))
            throw new ArgumentException("A package name is letters, digits, dots and underscores.", nameof(packageName));
        if ((protectedEndpoint is null) != (endpointHost is null)) throw new ArgumentException("An endpoint is given with its host.", nameof(endpointHost));

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity manager = await RequireDestinationManagerAsync(db, transaction, actor, projectId, cancellationToken);
        PageArtifactKeyParameters key = new(projectId, name);
        ReleaseDestinationRow? existing = await db.QuerySingleOrDefaultAsync<ReleaseDestinationRow>(
            ReleaseDestinationSelect + "WHERE d.ProjectId = @ProjectId AND d.Name = @Name;", key, transaction);
        string now = Now();
        string rulesText = ReleaseDestinations.FormatRules(rules);
        if (existing is null)
        {
            if (protectedEndpoint is null || protectedKey is null)
                throw new ArgumentException("A new destination needs its endpoint and its API key.", nameof(protectedEndpoint));
            long count = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM ReleaseDestinations WHERE ProjectId = @Id;", new IdParameters(projectId), transaction);
            if (count >= ReleaseDestinations.MaximumPerProject)
                throw new CollaborationConflictException("A project has at most " + ReleaseDestinations.MaximumPerProject + " release destinations.");
            await db.ExecuteAsync("""
                INSERT INTO ReleaseDestinations (ProjectId, Name, Kind, EndpointHost, ProtectedEndpoint, ProtectedKey, ApplicationId, Channel,
                                                 AllowProductionPromotion, PackageName, Rules, UpdatedByUserId, CreatedAtUtc, UpdatedAtUtc)
                VALUES (@ProjectId, @Name, @Kind, @EndpointHost, @ProtectedEndpoint, @ProtectedKey, @ApplicationId, @Channel,
                        @AllowProductionPromotion, @PackageName, @Rules, @UserId, @Now, @Now);
                """, new ReleaseDestinationWriteParameters(projectId, name, ReleaseDestinations.AppStoreKind, endpointHost!, protectedEndpoint, protectedKey,
                    applicationId.ToLowerInvariant(), channel, allowProductionPromotion ? 1 : 0, packageName, rulesText, manager.UserId, now), transaction);
        }
        else
        {
            await db.ExecuteAsync("""
                UPDATE ReleaseDestinations SET
                    EndpointHost = COALESCE(@EndpointHost, EndpointHost),
                    ProtectedEndpoint = COALESCE(@ProtectedEndpoint, ProtectedEndpoint),
                    ProtectedKey = COALESCE(@ProtectedKey, ProtectedKey),
                    ApplicationId = @ApplicationId, Channel = @Channel, AllowProductionPromotion = @AllowProductionPromotion,
                    PackageName = @PackageName, Rules = @Rules, UpdatedByUserId = @UserId, UpdatedAtUtc = @Now
                WHERE ProjectId = @ProjectId AND Name = @Name;
                """, new ReleaseDestinationWriteParameters(projectId, name, ReleaseDestinations.AppStoreKind, endpointHost, protectedEndpoint, protectedKey,
                    applicationId.ToLowerInvariant(), channel, allowProductionPromotion ? 1 : 0, packageName, rulesText, manager.UserId, now), transaction);
        }
        // What was set, never what it was set to: the host is as much of the endpoint as the trail holds, and the
        // key is not mentioned beyond the fact of its having changed.
        await AuditAsync(db, transaction, manager.UserId, existing is null ? "create-release-destination" : "update-release-destination", "project",
            projectId.ToString(CultureInfo.InvariantCulture),
            $"Release destination '{name}' {(existing is null ? "added" : "changed")}: host {endpointHost ?? existing!.EndpointHost}, channel {channel}, " +
            $"production promotion {(allowProductionPromotion ? "allowed" : "not allowed")}" +
            (existing is not null && protectedKey is not null ? ", API key replaced" : "") +
            (existing is not null && protectedEndpoint is not null ? ", endpoint replaced" : "") + ".", now);
        ReleaseDestinationRow stored = await db.QuerySingleAsync<ReleaseDestinationRow>(
            ReleaseDestinationSelect + "WHERE d.ProjectId = @ProjectId AND d.Name = @Name;", key, transaction);
        await transaction.CommitAsync(cancellationToken);
        return (ToReleaseDestination(stored), existing is null);
    }

    public async Task DeleteReleaseDestinationAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity manager = await RequireDestinationManagerAsync(db, transaction, actor, projectId, cancellationToken);
        PageArtifactKeyParameters key = new(projectId, name ?? "");
        await db.ExecuteAsync("""
            DELETE FROM ReleasePublications WHERE DestinationId IN (SELECT Id FROM ReleaseDestinations WHERE ProjectId = @ProjectId AND Name = @Name);
            """, key, transaction);
        int changed = await db.ExecuteAsync("DELETE FROM ReleaseDestinations WHERE ProjectId = @ProjectId AND Name = @Name;", key, transaction);
        if (changed != 1) throw new CollaborationNotFoundException("Release destination is unavailable.");
        await AuditAsync(db, transaction, manager.UserId, "delete-release-destination", "project", projectId.ToString(CultureInfo.InvariantCulture),
            $"Release destination '{name}' removed, with its stored endpoint and key.", Now());
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>A destination with its protected endpoint and key, for a manager's own check of it.</summary>
    public async Task<ReleaseDestinationSecrets?> FindReleaseDestinationSecretsAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireDestinationManagerAsync(db, null, actor, projectId, cancellationToken);
        ReleaseDestinationRow? row = await db.QuerySingleOrDefaultAsync<ReleaseDestinationRow>(
            ReleaseDestinationSelect + "WHERE d.ProjectId = @ProjectId AND d.Name = @Name;", new PageArtifactKeyParameters(projectId, name ?? ""));
        return row is null ? null : new(ToReleaseDestination(row), row.ProtectedEndpoint, row.ProtectedKey);
    }

    /// <summary>
    /// Queues a publication of a release to a destination, or a dry run of one. One at a time for a release and
    /// a destination: a second request while the first is under way is refused, not queued behind it.
    /// </summary>
    public async Task<ReleasePublication> QueueReleasePublicationAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string tagName,
        string destinationName,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity manager = await RequireDestinationManagerAsync(db, transaction, actor, projectId, cancellationToken);
        long releaseId = await ResolveReleaseAsync(db, transaction, projectId, tagName);
        ReleaseDestinationRow destination = await db.QuerySingleOrDefaultAsync<ReleaseDestinationRow>(
            ReleaseDestinationSelect + "WHERE d.ProjectId = @ProjectId AND d.Name = @Name;", new PageArtifactKeyParameters(projectId, destinationName ?? ""), transaction)
            ?? throw new CollaborationNotFoundException("Release destination is unavailable.");
        long busy = await db.QuerySingleAsync<long>("""
            SELECT COUNT(*) FROM ReleasePublications WHERE ReleaseId = @Id AND DestinationId = @ProjectId AND State IN ('queued', 'running');
            """, new ScopedIdParameters(releaseId, destination.Id), transaction);
        if (busy != 0) throw new CollaborationConflictException("This release is already being published to that destination.");
        string now = Now();
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO ReleasePublications (ProjectId, DestinationId, ReleaseId, TagName, State, DryRun, Channel, Summary, RequestedByUserId, CreatedAtUtc)
            VALUES (@ProjectId, @DestinationId, @ReleaseId, @TagName, 'queued', @DryRun, @Channel, @Summary, @UserId, @Now)
            RETURNING Id;
            """, new ReleasePublicationInsertParameters(projectId, destination.Id, releaseId, tagName.Trim(), dryRun ? 1 : 0, destination.Channel,
                dryRun ? "Waiting to be checked." : "Waiting to be published.", manager.UserId, now), transaction);
        await db.ExecuteAsync("""
            DELETE FROM ReleasePublications WHERE ReleaseId = @Id AND State IN ('succeeded', 'failed') AND Id NOT IN
                (SELECT Id FROM ReleasePublications WHERE ReleaseId = @Id ORDER BY Id DESC LIMIT @ProjectId);
            """, new ScopedIdParameters(releaseId, PublicationsKeptPerRelease), transaction);
        if (!dryRun)
            await AuditAsync(db, transaction, manager.UserId, "publish-release", "project", projectId.ToString(CultureInfo.InvariantCulture),
                $"Release on tag {tagName.Trim()} queued for publication to '{destination.Name}' (channel {destination.Channel}).", now);
        ReleasePublicationRow row = await db.QuerySingleAsync<ReleasePublicationRow>(ReleasePublicationSelect + "WHERE p.Id = @Id;", new IdParameters(id), transaction);
        await transaction.CommitAsync(cancellationToken);
        return ToReleasePublication(row);
    }

    public async Task<IReadOnlyList<ReleasePublication>> ListReleasePublicationsAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string tagName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireDestinationManagerAsync(db, null, actor, projectId, cancellationToken);
        IEnumerable<ReleasePublicationRow> rows = await db.QueryAsync<ReleasePublicationRow>(
            ReleasePublicationSelect + "WHERE p.ProjectId = @ProjectId AND p.TagName = @Name ORDER BY p.Id DESC;", new LabelLookupParameters(projectId, tagName.Trim()));
        return [.. rows.Select(ToReleasePublication)];
    }

    public async Task<ReleasePublication?> FindReleasePublicationAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long publicationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireDestinationManagerAsync(db, null, actor, projectId, cancellationToken);
        ReleasePublicationRow? row = await db.QuerySingleOrDefaultAsync<ReleasePublicationRow>(
            ReleasePublicationSelect + "WHERE p.Id = @Id AND p.ProjectId = @ProjectId;", new ScopedIdParameters(publicationId, projectId));
        return row is null ? null : ToReleasePublication(row);
    }

    // ---------------------------------------------------------------------------------------------------
    // The worker's half. These take no identity, because nobody is asking: the permission that mattered was
    // checked when the publication was queued, as a webhook's was when the hook was made.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Takes the oldest queued publication and marks it running, or returns null when there is none.</summary>
    public async Task<ReleasePublicationJob?> ClaimReleasePublicationAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        ReleasePublicationJobRow? row = await db.QuerySingleOrDefaultAsync<ReleasePublicationJobRow>(
            "SELECT Id, ProjectId, DestinationId, TagName, DryRun FROM ReleasePublications WHERE State = 'queued' ORDER BY Id LIMIT 1;", transaction: transaction);
        if (row is null) return null;
        await db.ExecuteAsync("UPDATE ReleasePublications SET State = 'running' WHERE Id = @Id;", new IdParameters(row.Id), transaction);
        await transaction.CommitAsync(cancellationToken);
        return new(row.Id, row.ProjectId, row.DestinationId, row.TagName, row.DryRun);
    }

    /// <summary>What a claimed publication needs: the destination with its protected endpoint and key, and the release.</summary>
    public async Task<(ReleaseDestinationSecrets Destination, Release Release)?> ReadReleasePublicationInputsAsync(
        ReleasePublicationJob job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        ReleaseDestinationRow? destination = await db.QuerySingleOrDefaultAsync<ReleaseDestinationRow>(
            ReleaseDestinationSelect + "WHERE d.Id = @Id AND d.ProjectId = @ProjectId;", new ScopedIdParameters(job.DestinationId, job.ProjectId));
        ReleaseRow? release = await db.QuerySingleOrDefaultAsync<ReleaseRow>(ReleaseSelect +
            "WHERE r.ProjectId = @ProjectId AND r.TagName = @Name;", new LabelLookupParameters(job.ProjectId, job.TagName));
        if (destination is null || release is null) return null;
        return (new(ToReleaseDestination(destination), destination.ProtectedEndpoint, destination.ProtectedKey), await HydrateReleaseAsync(db, release));
    }

    /// <summary>One file of the release being published, for the worker to send.</summary>
    public async Task<StoredReleaseAsset?> ReadReleaseAssetForPublicationAsync(long projectId, string tagName, string fileName, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        return await ReadStoredAssetAsync(db, projectId, tagName, fileName);
    }

    /// <summary>Records where a running publication has got to. Says nothing about how it will end.</summary>
    public async Task UpdateReleasePublicationAsync(long publicationId, string summary, IReadOnlyList<ArtifactOutcome> artifacts, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await db.ExecuteAsync("UPDATE ReleasePublications SET Summary = @Summary, Artifacts = @Artifacts WHERE Id = @Id AND State = 'running';",
            new ReleasePublicationProgressParameters(publicationId, Bounded(summary), EncodeOutcomes(artifacts)));
    }

    /// <summary>Ends a publication, and writes how it ended into the project's audit trail when it was not a dry run.</summary>
    public async Task CompleteReleasePublicationAsync(long publicationId, PublicationReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        ReleasePublicationRow? row = await db.QuerySingleOrDefaultAsync<ReleasePublicationRow>(ReleasePublicationSelect + "WHERE p.Id = @Id;", new IdParameters(publicationId), transaction);
        if (row is null) return;
        string now = Now();
        await db.ExecuteAsync("""
            UPDATE ReleasePublications SET State = @State, Summary = @Summary, Artifacts = @Artifacts, ChannelVerified = @ChannelVerified, FinishedAtUtc = @Now
            WHERE Id = @Id;
            """, new ReleasePublicationResultParameters(publicationId, report.Succeeded ? ReleasePublications.Succeeded : ReleasePublications.Failed,
                Bounded(report.Summary), EncodeOutcomes(report.Artifacts), report.ChannelVerified is null ? null : report.ChannelVerified.Value ? 1 : 0, now), transaction);
        if (!row.DryRun)
            await AuditAsync(db, transaction, row.RequestedByUserId, report.Succeeded ? "release-published" : "release-publication-failed", "project",
                row.ProjectId.ToString(CultureInfo.InvariantCulture),
                $"Release on tag {row.TagName} to '{row.DestinationName}': {Bounded(report.Summary)}", now);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// At start-up: a publication that was running when the server stopped did not finish, and says so. One
    /// that was only queued is still queued, and is taken up.
    /// </summary>
    public async Task<int> FailInterruptedReleasePublicationsAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        return await db.ExecuteAsync("""
            UPDATE ReleasePublications SET State = 'failed', FinishedAtUtc = @Now, Summary = @Summary
            WHERE State = 'running';
            """, new ReleasePublicationInterruptedParameters(
                "The server stopped while this was under way. Publish again: an upload the store already holds is carried on, not sent twice.", Now()));
    }

    private static async Task<AuthenticatedIdentity> RequireDestinationManagerAsync(SqliteConnection db, SqliteTransaction? transaction,
        AuthenticatedIdentity actor, long projectId, CancellationToken cancellationToken)
    {
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId, ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException("Release destinations and publishing to them need the Maintainer role.");
        return transaction is null ? actor : await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
    }

    private static string Bounded(string text) => text.Length <= 2000 ? text : text[..2000];

    private static string EncodeOutcomes(IReadOnlyList<ArtifactOutcome> outcomes)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer))
        {
            json.WriteStartArray();
            foreach (ArtifactOutcome outcome in outcomes)
            {
                json.WriteStartObject();
                json.WriteString("file", outcome.FileName);
                json.WriteString("format", outcome.Format);
                json.WriteString("platform", outcome.Platform);
                json.WriteString("architecture", outcome.Architecture);
                json.WriteString("state", outcome.State);
                json.WriteString("build", outcome.BuildId);
                json.WriteString("sent", outcome.UploadedSha256);
                json.WriteString("stored", outcome.StoreSha256);
                json.WriteString("problem", outcome.Problem);
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static IReadOnlyList<ArtifactOutcome> DecodeOutcomes(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            static string? Text(JsonElement entry, string name) =>
                entry.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return [.. document.RootElement.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object).Select(entry =>
                new ArtifactOutcome(Text(entry, "file") ?? "", Text(entry, "format") ?? "", Text(entry, "platform") ?? "", Text(entry, "architecture") ?? "",
                    Text(entry, "state") ?? "", Text(entry, "build"), Text(entry, "sent") ?? "", Text(entry, "stored"), Text(entry, "problem")))];
        }
        catch (JsonException) { return []; }
    }

    private const string ReleaseDestinationSelect = """
        SELECT d.Id, d.ProjectId, d.Name, d.Kind, d.EndpointHost, d.ProtectedEndpoint, d.ProtectedKey, d.ApplicationId, d.Channel,
               d.AllowProductionPromotion, d.PackageName, d.Rules, u.Username AS UpdatedByUsername, d.CreatedAtUtc, d.UpdatedAtUtc
        FROM ReleaseDestinations d JOIN Users u ON u.Id = d.UpdatedByUserId

        """;

    private const string ReleasePublicationSelect = """
        SELECT p.Id, p.ProjectId, d.Name AS DestinationName, p.TagName, p.State, p.DryRun, p.Channel, p.Summary, p.Artifacts, p.ChannelVerified,
               p.RequestedByUserId, u.Username AS RequestedByUsername, p.CreatedAtUtc, p.FinishedAtUtc
        FROM ReleasePublications p
        JOIN ReleaseDestinations d ON d.Id = p.DestinationId
        JOIN Users u ON u.Id = p.RequestedByUserId

        """;

    private static ReleaseDestination ToReleaseDestination(ReleaseDestinationRow row) =>
        new(row.Id, row.ProjectId, row.Name, row.Kind, row.EndpointHost, row.ApplicationId, row.Channel, row.AllowProductionPromotion, row.PackageName,
            ReleaseDestinations.ParseRules(row.Rules, out _) ?? [], row.UpdatedByUsername, Instant(row.CreatedAtUtc), Instant(row.UpdatedAtUtc));

    private static ReleasePublication ToReleasePublication(ReleasePublicationRow row) =>
        new(row.Id, row.ProjectId, row.DestinationName, row.TagName, row.State, row.DryRun, row.Channel, row.Summary, DecodeOutcomes(row.Artifacts),
            row.ChannelVerified is null ? null : row.ChannelVerified.Value != 0, row.RequestedByUsername, Instant(row.CreatedAtUtc),
            row.FinishedAtUtc is null ? null : Instant(row.FinishedAtUtc));
}

internal sealed record ReleaseDestinationWriteParameters(long ProjectId, string Name, string Kind, string? EndpointHost, string? ProtectedEndpoint,
    string? ProtectedKey, string ApplicationId, string Channel, int AllowProductionPromotion, string? PackageName, string Rules, long UserId, string Now);
internal sealed record ReleasePublicationInsertParameters(long ProjectId, long DestinationId, long ReleaseId, string TagName, int DryRun, string Channel,
    string Summary, long UserId, string Now);
internal sealed record ReleasePublicationProgressParameters(long Id, string Summary, string Artifacts);
internal sealed record ReleasePublicationInterruptedParameters(string Summary, string Now);
internal sealed record ReleasePublicationResultParameters(long Id, string State, string Summary, string Artifacts, int? ChannelVerified, string Now);

internal sealed class ReleaseDestinationRow
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string EndpointHost { get; set; } = "";
    public string ProtectedEndpoint { get; set; } = "";
    public string ProtectedKey { get; set; } = "";
    public string ApplicationId { get; set; } = "";
    public string Channel { get; set; } = "";
    public bool AllowProductionPromotion { get; set; }
    public string? PackageName { get; set; }
    public string Rules { get; set; } = "";
    public string UpdatedByUsername { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
    public string UpdatedAtUtc { get; set; } = "";
}

internal sealed class ReleasePublicationRow
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string DestinationName { get; set; } = "";
    public string TagName { get; set; } = "";
    public string State { get; set; } = "";
    public bool DryRun { get; set; }
    public string Channel { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Artifacts { get; set; } = "[]";
    public long? ChannelVerified { get; set; }
    public long RequestedByUserId { get; set; }
    public string RequestedByUsername { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
    public string? FinishedAtUtc { get; set; }
}

internal sealed class ReleasePublicationJobRow
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public long DestinationId { get; set; }
    public string TagName { get; set; } = "";
    public bool DryRun { get; set; }
}
