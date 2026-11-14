using System.Globalization;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// Page artifacts (ADR 0051): named folders of files uploaded beside a repository, private until a Maintainer
/// promotes one, and then served on the pages origin as part of the project's site. The database holds the
/// name, the promotion and which directory is current; the bytes are the server's business, on disk.
///
/// <para>
/// Uploading needs the permission to push, the same as a package: an artifact is a build of the project.
/// Promoting, withdrawing and deleting need the permission to manage it, because those decide what a stranger
/// can read. Replacing an artifact that is already promoted keeps it promoted, the way a push to the default
/// branch changes a committed site.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    /// <summary>How many page artifacts one project may keep.</summary>
    public const int MaximumPageArtifacts = 50;

    public async Task<IReadOnlyList<PageArtifact>> ListPageArtifactsAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireRepositoryReaderAsync(db, identity, projectId, cancellationToken);
        IEnumerable<PageArtifactRow> rows = await db.QueryAsync<PageArtifactRow>(
            PageArtifactSelect + "WHERE a.ProjectId = @Id ORDER BY a.Name;", new IdParameters(projectId));
        return [.. rows.Select(ToPageArtifact)];
    }

    public async Task<PageArtifact?> FindPageArtifactAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        string name,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireRepositoryReaderAsync(db, identity, projectId, cancellationToken);
        PageArtifactRow? row = await db.QuerySingleOrDefaultAsync<PageArtifactRow>(
            PageArtifactSelect + "WHERE a.ProjectId = @ProjectId AND a.Name = @Name;", new PageArtifactKeyParameters(projectId, name ?? ""));
        return row is null ? null : ToPageArtifact(row);
    }

    /// <summary>
    /// The artifact a stranger may read under this name, or null. No identity is asked for: the caller has
    /// already decided the project's page is published, and promotion is the rest of the answer.
    /// </summary>
    public async Task<PageArtifact?> FindPromotedPageArtifactAsync(long projectId, string name, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        PageArtifactRow? row = await db.QuerySingleOrDefaultAsync<PageArtifactRow>(
            PageArtifactSelect + "WHERE a.ProjectId = @ProjectId AND a.Name = @Name AND a.Promoted = 1;", new PageArtifactKeyParameters(projectId, name ?? ""));
        return row is null ? null : ToPageArtifact(row);
    }

    /// <summary>
    /// Records an upload whose files are already on disk under <paramref name="generation"/>. Returns the
    /// artifact and the generation it replaced, which the caller removes; when this throws, the caller removes
    /// the new one instead.
    /// </summary>
    public async Task<(PageArtifact Artifact, string? Replaced)> PutPageArtifactAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string name,
        string generation,
        int fileCount,
        long bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!PageArtifacts.IsName(name)) throw new ArgumentException(PageArtifactNameRule, nameof(name));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId, ProjectAction.PushRepository, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException("Uploading a page artifact needs the permission to push.");
        AuthenticatedIdentity uploader = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);

        PageArtifactKeyParameters key = new(projectId, name);
        PageArtifactRow? existing = await db.QuerySingleOrDefaultAsync<PageArtifactRow>(
            PageArtifactSelect + "WHERE a.ProjectId = @ProjectId AND a.Name = @Name;", key, transaction);
        if (existing is null)
        {
            long count = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM PageArtifacts WHERE ProjectId = @Id;", new IdParameters(projectId), transaction);
            if (count >= MaximumPageArtifacts)
                throw new ArgumentException("A project keeps at most " + MaximumPageArtifacts + " page artifacts; delete one first.", nameof(name));
        }

        string now = Now();
        await db.ExecuteAsync("""
            INSERT INTO PageArtifacts (ProjectId, Name, Generation, Promoted, FileCount, Bytes, UpdatedByUserId, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@ProjectId, @Name, @Generation, 0, @FileCount, @Bytes, @UserId, @Now, @Now)
            ON CONFLICT(ProjectId, Name) DO UPDATE SET
                Generation = excluded.Generation,
                FileCount = excluded.FileCount,
                Bytes = excluded.Bytes,
                UpdatedByUserId = excluded.UpdatedByUserId,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """, new PageArtifactWriteParameters(projectId, name, generation, fileCount, bytes, uploader.UserId, now), transaction);
        await AuditAsync(db, transaction, uploader.UserId, "upload-page-artifact", "project", projectId.ToString(CultureInfo.InvariantCulture),
            $"Page artifact '{name}' {(existing is null ? "uploaded" : existing.Promoted ? "replaced while promoted" : "replaced")}: {fileCount} files, {bytes} bytes.", now);
        PageArtifactRow stored = await db.QuerySingleAsync<PageArtifactRow>(
            PageArtifactSelect + "WHERE a.ProjectId = @ProjectId AND a.Name = @Name;", key, transaction);
        await transaction.CommitAsync(cancellationToken);
        return (ToPageArtifact(stored), existing?.Generation);
    }

    /// <summary>Promotes an artifact to the pages origin, or withdraws it. Needs the permission to manage the project.</summary>
    public async Task<PageArtifact> SetPageArtifactPromotedAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string name,
        bool promoted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId, ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);
        PageArtifactKeyParameters key = new(projectId, name ?? "");
        string now = Now();
        int changed = await db.ExecuteAsync(
            "UPDATE PageArtifacts SET Promoted = @Promoted WHERE ProjectId = @ProjectId AND Name = @Name;",
            new PageArtifactPromotionParameters(projectId, name ?? "", promoted ? 1 : 0), transaction);
        if (changed == 0) throw new CollaborationNotFoundException("Page artifact is unavailable.");
        await AuditAsync(db, transaction, actor.UserId, promoted ? "promote-page-artifact" : "withdraw-page-artifact", "project",
            projectId.ToString(CultureInfo.InvariantCulture),
            promoted ? $"Page artifact '{name}' promoted: it is served on the pages origin." : $"Page artifact '{name}' withdrawn from the pages origin.", now);
        PageArtifactRow stored = await db.QuerySingleAsync<PageArtifactRow>(
            PageArtifactSelect + "WHERE a.ProjectId = @ProjectId AND a.Name = @Name;", key, transaction);
        await transaction.CommitAsync(cancellationToken);
        return ToPageArtifact(stored);
    }

    /// <summary>Deletes the artifact's row. The caller removes its files afterwards, the row having named them.</summary>
    public async Task<PageArtifact> DeletePageArtifactAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId, ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);
        PageArtifactKeyParameters key = new(projectId, name ?? "");
        PageArtifactRow? existing = await db.QuerySingleOrDefaultAsync<PageArtifactRow>(
            PageArtifactSelect + "WHERE a.ProjectId = @ProjectId AND a.Name = @Name;", key, transaction);
        if (existing is null) throw new CollaborationNotFoundException("Page artifact is unavailable.");
        await db.ExecuteAsync("DELETE FROM PageArtifacts WHERE ProjectId = @ProjectId AND Name = @Name;", key, transaction);
        await AuditAsync(db, transaction, actor.UserId, "delete-page-artifact", "project", projectId.ToString(CultureInfo.InvariantCulture),
            $"Page artifact '{existing.Name}' deleted{(existing.Promoted ? " while promoted" : "")}.", Now());
        await transaction.CommitAsync(cancellationToken);
        return ToPageArtifact(existing);
    }

    /// <summary>
    /// What an administrator decided about the pages origin. Read per request, like the agent switches, so a
    /// change applies to the next request; a value this version cannot read falls back to the default rather
    /// than taking the pages origin down.
    /// </summary>
    public async Task<PageServingSettings> GetPageServingSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        IEnumerable<SettingRow> rows = await db.QueryAsync<SettingRow>(
            "SELECT Name, Value FROM ServerSettings WHERE Name IN ('pages.artifact-prefix', 'pages.artifact-max-mebibytes', 'pages.file-types');");
        string prefix = PageServingSettings.DefaultArtifactPrefix;
        int maximum = PageServingSettings.DefaultArtifactMaximumMebibytes;
        List<SiteFileType> types = [];
        foreach (SettingRow row in rows)
        {
            if (row.Name == "pages.artifact-prefix") prefix = row.Value;
            else if (row.Name == "pages.artifact-max-mebibytes")
                maximum = int.TryParse(row.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : maximum;
            else
                foreach (string line in row.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    int space = line.IndexOf(' ');
                    if (space > 0) types.Add(new(line[..space], line[(space + 1)..]));
                }
        }
        return PageServingSettings.Normalize(prefix, maximum, types, out _) ?? PageServingSettings.Default;
    }

    public async Task<PageServingSettings> SetPageServingSettingsAsync(
        AuthenticatedIdentity actor,
        PageServingSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        PageServingSettings clean = PageServingSettings.Normalize(settings.ArtifactPrefix, settings.ArtifactMaximumMebibytes, settings.FileTypes, out string problem)
            ?? throw new ArgumentException(problem, nameof(settings));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity liveActor = await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
        if (!liveActor.IsAdministrator) throw new UnauthorizedAccessException("Administrator access is required.");

        string now = Now();
        string types = string.Join('\n', clean.FileTypes.Select(type => type.Extension + " " + type.ContentType));
        foreach ((string name, string value) in new[]
        {
            ("pages.artifact-prefix", clean.ArtifactPrefix),
            ("pages.artifact-max-mebibytes", clean.ArtifactMaximumMebibytes.ToString(CultureInfo.InvariantCulture)),
            ("pages.file-types", types)
        })
        {
            await db.ExecuteAsync("""
                INSERT INTO ServerSettings (Name, Value, UpdatedByUserId, UpdatedAtUtc)
                VALUES (@Name, @Value, @UpdatedByUserId, @UpdatedAtUtc)
                ON CONFLICT (Name) DO UPDATE SET
                    Value = excluded.Value,
                    UpdatedByUserId = excluded.UpdatedByUserId,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
                """, new SettingWriteParameters(name, value, liveActor.UserId, now), transaction);
        }
        await AuditAsync(db, transaction, liveActor.UserId, "page-serving-settings", "server", null,
            "artifact-prefix=" + clean.ArtifactPrefix +
            "; artifact-max-mebibytes=" + clean.ArtifactMaximumMebibytes.ToString(CultureInfo.InvariantCulture) +
            "; file-types=" + (clean.FileTypes.Count == 0 ? "none added" : string.Join(", ", clean.FileTypes.Select(type => type.Extension + "=" + type.ContentType))), now);
        await transaction.CommitAsync(cancellationToken);
        return clean;
    }

    private const string PageArtifactNameRule =
        "A page artifact's name is up to 40 lower-case letters, digits, '-' and '_', starting with a letter or digit.";

    private const string PageArtifactSelect = """
        SELECT a.Id, a.ProjectId, a.Name, a.Generation, a.Promoted, a.FileCount, a.Bytes, u.Username AS UpdatedByUsername, a.CreatedAtUtc, a.UpdatedAtUtc
        FROM PageArtifacts a JOIN Users u ON u.Id = a.UpdatedByUserId

        """;

    private static PageArtifact ToPageArtifact(PageArtifactRow row) =>
        new(row.Id, row.ProjectId, row.Name, row.Generation, row.Promoted, row.FileCount, row.Bytes, row.UpdatedByUsername,
            Instant(row.CreatedAtUtc), Instant(row.UpdatedAtUtc));
}

internal sealed record PageArtifactKeyParameters(long ProjectId, string Name);
internal sealed record PageArtifactPromotionParameters(long ProjectId, string Name, int Promoted);
internal sealed record PageArtifactWriteParameters(long ProjectId, string Name, string Generation, int FileCount, long Bytes, long UserId, string Now);

internal sealed class PageArtifactRow
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string Generation { get; set; } = "";
    public bool Promoted { get; set; }
    public int FileCount { get; set; }
    public long Bytes { get; set; }
    public string UpdatedByUsername { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
    public string UpdatedAtUtc { get; set; } = "";
}
