using System.Globalization;
using System.Security.Cryptography;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// Releases: notes, links and files on a tag.
///
/// <para>
/// Reading a release follows reading the repository, including anonymously on a public project, because
/// a release is the thing a project most wants strangers to find. Writing one needs the permission to push,
/// because a release speaks for the project in a way a comment does not, and a tag is what a push makes.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    /// <summary>The largest file a release may carry. A policy, chosen so a row stays a row; raising it is a
    /// decision about where asset bytes live, not a constant to edit.</summary>
    public const long MaximumAssetBytes = 32L * 1024 * 1024;
    public const int MaximumAssetsPerRelease = 50;
    public const int MaximumLinksPerRelease = 50;

    public async Task<Release> CreateReleaseAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string tagName,
        string? name,
        string description,
        DateTimeOffset? releasedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string tag = RequireTag(tagName);
        string cleanName = name is { Length: > 0 } ? RequireTitle(name) : tag;
        string cleanDescription = RequireBody(description, nameof(description), allowEmpty: true);

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity author = await RequirePublisherAsync(db, transaction, actor, projectId, cancellationToken);
        long existing = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM Releases WHERE ProjectId = @ProjectId AND TagName = @Name;",
            new LabelLookupParameters(projectId, tag), transaction);
        if (existing != 0) throw new CollaborationConflictException("A release already exists for this tag.");

        string now = Now();
        string released = (releasedAt ?? Instant(now)).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO Releases (ProjectId, TagName, Name, Description, AuthorUserId, CreatedAtUtc, ReleasedAtUtc)
            VALUES (@ProjectId, @TagName, @Name, @Description, @AuthorUserId, @CreatedAtUtc, @ReleasedAtUtc)
            RETURNING Id;
            """, new ReleaseInsertParameters(projectId, tag, cleanName, cleanDescription, author.UserId, now, released), transaction);
        await AuditAsync(db, transaction, author.UserId, "create-release", "release",
            id.ToString(CultureInfo.InvariantCulture), $"Release '{cleanName}' created on tag {tag}.", now);
        await transaction.CommitAsync(cancellationToken);
        return (await FindReleaseAsync(actor, projectId, tag, cancellationToken))!;
    }

    /// <summary>Lists releases, newest release date first.</summary>
    public async Task<IReadOnlyList<Release>> ListReleasesAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireRepositoryReaderAsync(db, identity, projectId, cancellationToken);
        IEnumerable<ReleaseRow> rows = await db.QueryAsync<ReleaseRow>(ReleaseSelect +
            "WHERE r.ProjectId = @Id ORDER BY r.ReleasedAtUtc DESC, r.Id DESC;", new IdParameters(projectId));
        List<Release> releases = [];
        foreach (ReleaseRow row in rows) releases.Add(await HydrateReleaseAsync(db, row));
        return releases;
    }

    public async Task<Release?> FindReleaseAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        string tagName,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireRepositoryReaderAsync(db, identity, projectId, cancellationToken);
        ReleaseRow? row = await db.QuerySingleOrDefaultAsync<ReleaseRow>(ReleaseSelect +
            "WHERE r.ProjectId = @ProjectId AND r.TagName = @Name;", new LabelLookupParameters(projectId, tagName.Trim()));
        return row is null ? null : await HydrateReleaseAsync(db, row);
    }

    public async Task<Release> UpdateReleaseAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string tagName,
        string? name,
        string? description,
        DateTimeOffset? releasedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string? cleanName = name is null ? null : RequireTitle(name);
        string? cleanDescription = description is null ? null : RequireBody(description, nameof(description), allowEmpty: true);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequirePublisherAsync(db, transaction, actor, projectId, cancellationToken);
        long id = await ResolveReleaseAsync(db, transaction, projectId, tagName);
        await db.ExecuteAsync("""
            UPDATE Releases SET Name = COALESCE(@Name, Name), Description = COALESCE(@Description, Description),
                                ReleasedAtUtc = COALESCE(@ReleasedAtUtc, ReleasedAtUtc)
            WHERE Id = @Id;
            """, new ReleaseUpdateParameters(id, cleanName, cleanDescription,
                releasedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)), transaction);
        await AuditAsync(db, transaction, actor.UserId, "update-release", "release", id.ToString(CultureInfo.InvariantCulture), "Release changed.", Now());
        await transaction.CommitAsync(cancellationToken);
        return (await FindReleaseAsync(actor, projectId, tagName, cancellationToken))!;
    }

    /// <summary>Deletes the release, its links and its files. The tag is untouched.</summary>
    public async Task DeleteReleaseAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string tagName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequirePublisherAsync(db, transaction, actor, projectId, cancellationToken);
        long id = await ResolveReleaseAsync(db, transaction, projectId, tagName);
        await db.ExecuteAsync("DELETE FROM ReleaseAssets WHERE ReleaseId = @Id;", new IdParameters(id), transaction);
        await db.ExecuteAsync("DELETE FROM ReleaseLinks WHERE ReleaseId = @Id;", new IdParameters(id), transaction);
        await db.ExecuteAsync("DELETE FROM Releases WHERE Id = @Id;", new IdParameters(id), transaction);
        await AuditAsync(db, transaction, actor.UserId, "delete-release", "release",
            id.ToString(CultureInfo.InvariantCulture), $"Release on tag {tagName.Trim()} deleted; the tag was kept.", Now());
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<Release> AddReleaseLinkAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string tagName,
        string name,
        string url,
        string linkType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string cleanName = RequireTitle(name);
        string cleanUrl = RequireLinkUrl(url);
        if (linkType is not ("other" or "runbook" or "image" or "package"))
            throw new ArgumentException("link_type is other, runbook, image or package.", nameof(linkType));
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequirePublisherAsync(db, transaction, actor, projectId, cancellationToken);
        long id = await ResolveReleaseAsync(db, transaction, projectId, tagName);
        long count = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM ReleaseLinks WHERE ReleaseId = @Id;", new IdParameters(id), transaction);
        if (count >= MaximumLinksPerRelease) throw new CollaborationConflictException("A release has at most fifty links.");
        long clash = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM ReleaseLinks WHERE ReleaseId = @ProjectId AND Name = @Name;",
            new LabelLookupParameters(id, cleanName), transaction);
        if (clash != 0) throw new CollaborationConflictException("A link with this name already exists on the release.");
        await db.ExecuteAsync("INSERT INTO ReleaseLinks (ReleaseId, Name, Url, LinkType) VALUES (@ReleaseId, @Name, @Url, @LinkType);",
            new ReleaseLinkInsertParameters(id, cleanName, cleanUrl, linkType), transaction);
        await transaction.CommitAsync(cancellationToken);
        return (await FindReleaseAsync(actor, projectId, tagName, cancellationToken))!;
    }

    public async Task<Release> DeleteReleaseLinkAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string tagName,
        long linkId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequirePublisherAsync(db, transaction, actor, projectId, cancellationToken);
        long id = await ResolveReleaseAsync(db, transaction, projectId, tagName);
        int changed = await db.ExecuteAsync("DELETE FROM ReleaseLinks WHERE Id = @Id AND ReleaseId = @ProjectId;",
            new ScopedIdParameters(linkId, id), transaction);
        if (changed != 1) throw new CollaborationNotFoundException("Link is unavailable.");
        await transaction.CommitAsync(cancellationToken);
        return (await FindReleaseAsync(actor, projectId, tagName, cancellationToken))!;
    }

    /// <summary>
    /// Stores a file on a release. The bytes are hashed here so the download can promise what it serves,
    /// and bounded here so a row stays a row. A file of the same name replaces the earlier one, which is
    /// what re-uploading a build means.
    /// </summary>
    public async Task<Release> PutReleaseAssetAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string tagName,
        string fileName,
        byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(bytes);
        string cleanName = RequireAssetName(fileName);
        if (bytes.Length == 0) throw new ArgumentException("An asset has some bytes in it.", nameof(bytes));
        if (bytes.Length > MaximumAssetBytes) throw new ArgumentException("An asset is at most 32 MiB.", nameof(bytes));

        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity uploader = await RequirePublisherAsync(db, transaction, actor, projectId, cancellationToken);
        long id = await ResolveReleaseAsync(db, transaction, projectId, tagName);
        long count = await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM ReleaseAssets WHERE ReleaseId = @ProjectId AND FileName <> @Name;",
            new LabelLookupParameters(id, cleanName), transaction);
        if (count >= MaximumAssetsPerRelease) throw new CollaborationConflictException("A release has at most fifty files.");
        string now = Now();
        // A blob parameter is bound by hand. Dapper's generator reads a byte array as a list to expand into
        // an IN clause, which is right for every other array and wrong for this one; the provider's own
        // parameter binds it as the blob it is.
        await using (SqliteCommand insert = db.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ReleaseAssets (ReleaseId, FileName, Size, Sha256, Bytes, UploadedByUserId, CreatedAtUtc)
                VALUES ($releaseId, $fileName, $size, $sha256, $bytes, $uploadedBy, $createdAt)
                ON CONFLICT(ReleaseId, FileName) DO UPDATE SET
                    Size = excluded.Size, Sha256 = excluded.Sha256, Bytes = excluded.Bytes,
                    UploadedByUserId = excluded.UploadedByUserId, CreatedAtUtc = excluded.CreatedAtUtc;
                """;
            insert.Parameters.AddWithValue("$releaseId", id);
            insert.Parameters.AddWithValue("$fileName", cleanName);
            insert.Parameters.AddWithValue("$size", (long)bytes.Length);
            insert.Parameters.AddWithValue("$sha256", Convert.ToHexStringLower(SHA256.HashData(bytes)));
            insert.Parameters.AddWithValue("$bytes", bytes);
            insert.Parameters.AddWithValue("$uploadedBy", uploader.UserId);
            insert.Parameters.AddWithValue("$createdAt", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(db, transaction, uploader.UserId, "upload-release-asset", "release",
            id.ToString(CultureInfo.InvariantCulture), $"File '{cleanName}' ({bytes.Length} bytes) stored.", now);
        await transaction.CommitAsync(cancellationToken);
        return (await FindReleaseAsync(actor, projectId, tagName, cancellationToken))!;
    }

    /// <summary>The bytes of one asset, for anybody who may read the repository.</summary>
    public async Task<(ReleaseAsset Asset, byte[] Bytes)?> ReadReleaseAssetAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        string tagName,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireRepositoryReaderAsync(db, identity, projectId, cancellationToken);
        ReleaseAssetBytesRow? row = await db.QuerySingleOrDefaultAsync<ReleaseAssetBytesRow>("""
            SELECT a.Id, a.FileName, a.Size, a.Sha256, a.Bytes, u.Username AS UploadedByUsername, a.CreatedAtUtc
            FROM ReleaseAssets a
            JOIN Releases r ON r.Id = a.ReleaseId
            JOIN Users u ON u.Id = a.UploadedByUserId
            WHERE r.ProjectId = @ProjectId AND r.TagName = @TagName AND a.FileName = @FileName;
            """, new ReleaseAssetKeyParameters(projectId, tagName.Trim(), fileName));
        return row is null ? null
            : (new ReleaseAsset(row.Id, row.FileName, row.Size, row.Sha256, row.UploadedByUsername, Instant(row.CreatedAtUtc)), row.Bytes);
    }

    public async Task<Release> DeleteReleaseAssetAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string tagName,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequirePublisherAsync(db, transaction, actor, projectId, cancellationToken);
        long id = await ResolveReleaseAsync(db, transaction, projectId, tagName);
        int changed = await db.ExecuteAsync("DELETE FROM ReleaseAssets WHERE ReleaseId = @ProjectId AND FileName = @Name;",
            new LabelLookupParameters(id, fileName), transaction);
        if (changed != 1) throw new CollaborationNotFoundException("File is unavailable.");
        await AuditAsync(db, transaction, actor.UserId, "delete-release-asset", "release",
            id.ToString(CultureInfo.InvariantCulture), $"File '{fileName}' removed.", Now());
        await transaction.CommitAsync(cancellationToken);
        return (await FindReleaseAsync(actor, projectId, tagName, cancellationToken))!;
    }

    private const string ReleaseSelect = """
        SELECT r.Id, r.ProjectId, r.TagName, r.Name, r.Description, r.AuthorUserId, u.Username AS AuthorUsername,
               r.CreatedAtUtc, r.ReleasedAtUtc
        FROM Releases r
        JOIN Users u ON u.Id = r.AuthorUserId

        """;

    private static async Task<Release> HydrateReleaseAsync(SqliteConnection db, ReleaseRow row)
    {
        IEnumerable<ReleaseLinkRow> links = await db.QueryAsync<ReleaseLinkRow>(
            "SELECT Id, Name, Url, LinkType FROM ReleaseLinks WHERE ReleaseId = @Id ORDER BY Id;", new IdParameters(row.Id));
        IEnumerable<ReleaseAssetRow> assets = await db.QueryAsync<ReleaseAssetRow>("""
            SELECT a.Id, a.FileName, a.Size, a.Sha256, u.Username AS UploadedByUsername, a.CreatedAtUtc
            FROM ReleaseAssets a JOIN Users u ON u.Id = a.UploadedByUserId
            WHERE a.ReleaseId = @Id ORDER BY a.FileName;
            """, new IdParameters(row.Id));
        return new(row.Id, row.ProjectId, row.TagName, row.Name, row.Description, row.AuthorUserId, row.AuthorUsername,
            Instant(row.CreatedAtUtc), Instant(row.ReleasedAtUtc),
            [.. links.Select(link => new ReleaseLink(link.Id, link.Name, link.Url, link.LinkType))],
            [.. assets.Select(asset => new ReleaseAsset(asset.Id, asset.FileName, asset.Size, asset.Sha256, asset.UploadedByUsername, Instant(asset.CreatedAtUtc)))]);
    }

    private static async Task<long> ResolveReleaseAsync(SqliteConnection db, SqliteTransaction transaction, long projectId, string tagName)
    {
        SubjectRow? row = await db.QuerySingleOrDefaultAsync<SubjectRow>(
            "SELECT Id FROM Releases WHERE ProjectId = @ProjectId AND TagName = @Name;",
            new LabelLookupParameters(projectId, tagName.Trim()), transaction);
        return row?.Id ?? throw new CollaborationNotFoundException("Release is unavailable.");
    }

    /// <summary>Reading a release follows reading the repository: public and unlisted projects answer a stranger.</summary>
    private static async Task RequireRepositoryReaderAsync(SqliteConnection db, AuthenticatedIdentity? identity, long projectId, CancellationToken cancellationToken)
    {
        PermissionDecision permission = await DecideAsync(db, null, identity, projectId, ProjectAction.BrowseRepository, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);
    }

    /// <summary>Publishing a release needs the permission to push, which is what a tag needs.</summary>
    private static async Task<AuthenticatedIdentity> RequirePublisherAsync(SqliteConnection db, SqliteTransaction transaction,
        AuthenticatedIdentity actor, long projectId, CancellationToken cancellationToken)
    {
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId, ProjectAction.PushRepository, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException("Publishing a release needs the permission to push.");
        return await RequireLiveIdentityAsync(db, transaction, actor, cancellationToken);
    }

    private static string RequireTag(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string trimmed = value.Trim();
        if (trimmed.Length > 255 || trimmed.Any(character => char.IsControl(character) || character is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
            throw new ArgumentException("A tag name contains a character Git does not allow.", nameof(value));
        if (trimmed.StartsWith('-') || trimmed.StartsWith('/') || trimmed.EndsWith('/') || trimmed.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("A tag name is malformed.", nameof(value));
        return trimmed;
    }

    private static string RequireLinkUrl(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string trimmed = value.Trim();
        if (trimmed.Length > 2048 || !Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("A link is an absolute http or https address.", nameof(value));
        return trimmed;
    }

    /// <summary>A file name is one path segment: no slashes, nothing hidden, nothing a header cannot carry.</summary>
    private static string RequireAssetName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string trimmed = value.Trim();
        if (trimmed.Length > 255 || trimmed is "." or ".." || trimmed.StartsWith('.') ||
            trimmed.Any(character => char.IsControl(character) || character is '/' or '\\' or '"' or ';' or ':' or '<' or '>' or '|' or '?' or '*'))
            throw new ArgumentException("A file name is a single path segment without control characters, quotes or separators.", nameof(value));
        return trimmed;
    }
}

internal sealed record ReleaseInsertParameters(long ProjectId, string TagName, string Name, string Description, long AuthorUserId, string CreatedAtUtc, string ReleasedAtUtc);
internal sealed record ReleaseUpdateParameters(long Id, string? Name, string? Description, string? ReleasedAtUtc);
internal sealed record ReleaseLinkInsertParameters(long ReleaseId, string Name, string Url, string LinkType);
internal sealed record ReleaseAssetKeyParameters(long ProjectId, string TagName, string FileName);

internal sealed class ReleaseRow
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string TagName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public long AuthorUserId { get; set; }
    public string AuthorUsername { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
    public string ReleasedAtUtc { get; set; } = "";
}

internal sealed class ReleaseLinkRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string LinkType { get; set; } = "other";
}

internal sealed class ReleaseAssetRow
{
    public long Id { get; set; }
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string UploadedByUsername { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
}

internal sealed class ReleaseAssetBytesRow
{
    public long Id { get; set; }
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public byte[] Bytes { get; set; } = [];
    public string UploadedByUsername { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
}
