using System.Globalization;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// Packages and container images: what a listing needs and the hashes a download promises. The bytes are
/// the server's business, on disk beside the repository.
///
/// <para>
/// Reading follows reading the repository; publishing needs the permission to push. A package is a build
/// of the project and a tag is a build of an image of it, and a build speaks for the project the way a
/// release does.
/// </para>
/// </summary>
public sealed partial class GraftStore
{
    public async Task<Package> PutPackageFileAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string packageType,
        string name,
        string version,
        string fileName,
        long size,
        string sha256,
        string description = "",
        string authors = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (packageType is not ("generic" or "nuget")) throw new ArgumentException("Unknown package type.", nameof(packageType));
        string cleanName = RequirePackageName(name);
        string cleanVersion = RequirePackageVersion(version);
        string cleanFile = RequireAssetName(fileName);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity publisher = await RequirePublisherAsync(db, transaction, actor, projectId, cancellationToken);
        string now = Now();
        long id = await db.QuerySingleAsync<long>("""
            INSERT INTO Packages (ProjectId, PackageType, Name, NormalizedName, Version, NormalizedVersion, Description, Authors, CreatedByUserId, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@ProjectId, @PackageType, @Name, @NormalizedName, @Version, @NormalizedVersion, @Description, @Authors, @UserId, @Now, @Now)
            ON CONFLICT(ProjectId, PackageType, NormalizedName, NormalizedVersion) DO UPDATE SET
                UpdatedAtUtc = excluded.UpdatedAtUtc,
                Description = CASE WHEN excluded.Description <> '' THEN excluded.Description ELSE Packages.Description END,
                Authors = CASE WHEN excluded.Authors <> '' THEN excluded.Authors ELSE Packages.Authors END
            RETURNING Id;
            """, new PackageInsertParameters(projectId, packageType, cleanName, cleanName.ToLowerInvariant(), cleanVersion,
                cleanVersion.ToLowerInvariant(), description ?? "", authors ?? "", publisher.UserId, now), transaction);
        await db.ExecuteAsync("""
            INSERT INTO PackageFiles (PackageId, FileName, Size, Sha256, CreatedAtUtc)
            VALUES (@PackageId, @FileName, @Size, @Sha256, @CreatedAtUtc)
            ON CONFLICT(PackageId, FileName) DO UPDATE SET Size = excluded.Size, Sha256 = excluded.Sha256, CreatedAtUtc = excluded.CreatedAtUtc;
            """, new PackageFileParameters(id, cleanFile, size, sha256, now), transaction);
        await AuditAsync(db, transaction, publisher.UserId, "publish-package", "package", id.ToString(CultureInfo.InvariantCulture),
            $"{packageType} package {cleanName} {cleanVersion}: file '{cleanFile}' ({size} bytes).", now);
        await transaction.CommitAsync(cancellationToken);
        return (await FindPackageAsync(actor, projectId, id, cancellationToken))!;
    }

    public async Task<IReadOnlyList<Package>> ListPackagesAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        string? packageType = null,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireRepositoryReaderAsync(db, identity, projectId, cancellationToken);
        IEnumerable<PackageRow> rows = await db.QueryAsync<PackageRow>(PackageSelect + """
            WHERE p.ProjectId = @ProjectId AND (@Type = '' OR p.PackageType = @Type) AND (@Name = '' OR p.NormalizedName = @Name)
            ORDER BY p.NormalizedName, p.Id DESC;
            """, new PackageListParameters(projectId, packageType ?? "", (name ?? "").Trim().ToLowerInvariant()));
        List<Package> packages = [];
        foreach (PackageRow row in rows) packages.Add(await HydratePackageAsync(db, row));
        return packages;
    }

    public async Task<Package?> FindPackageAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        long packageId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireRepositoryReaderAsync(db, identity, projectId, cancellationToken);
        PackageRow? row = await db.QuerySingleOrDefaultAsync<PackageRow>(PackageSelect + "WHERE p.ProjectId = @ProjectId AND p.Id = @Id;",
            new ScopedIdParameters(packageId, projectId));
        return row is null ? null : await HydratePackageAsync(db, row);
    }

    public async Task<Package?> FindPackageAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        string packageType,
        string name,
        string version,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireRepositoryReaderAsync(db, identity, projectId, cancellationToken);
        PackageRow? row = await db.QuerySingleOrDefaultAsync<PackageRow>(PackageSelect + """
            WHERE p.ProjectId = @ProjectId AND p.PackageType = @Type AND p.NormalizedName = @Name AND p.NormalizedVersion = @Version;
            """, new PackageKeyParameters(projectId, packageType, name.Trim().ToLowerInvariant(), version.Trim().ToLowerInvariant()));
        return row is null ? null : await HydratePackageAsync(db, row);
    }

    /// <summary>Deletes the package's rows. The caller removes its files afterwards, the row having named them.</summary>
    public async Task<Package> DeletePackageAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long packageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        Package? package = await FindPackageAsync(actor, projectId, packageId, cancellationToken);
        if (package is null) throw new CollaborationNotFoundException("Package is unavailable.");
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequirePublisherAsync(db, transaction, actor, projectId, cancellationToken);
        await db.ExecuteAsync("DELETE FROM PackageFiles WHERE PackageId = @Id;", new IdParameters(packageId), transaction);
        await db.ExecuteAsync("DELETE FROM Packages WHERE Id = @Id;", new IdParameters(packageId), transaction);
        await AuditAsync(db, transaction, actor.UserId, "delete-package", "package", packageId.ToString(CultureInfo.InvariantCulture),
            $"{package.PackageType} package {package.Name} {package.Version} deleted.", Now());
        await transaction.CommitAsync(cancellationToken);
        return package;
    }

    // ---------------------------------------------------------------------------------------------------
    // Container registry
    // ---------------------------------------------------------------------------------------------------

    public async Task<ContainerRepository> EnsureContainerRepositoryAsync(
        AuthenticatedIdentity actor,
        long projectId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string cleanName = RequireImageName(name);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        await RequirePublisherAsync(db, transaction, actor, projectId, cancellationToken);
        await db.ExecuteAsync("INSERT OR IGNORE INTO ContainerRepositories (ProjectId, Name, CreatedAtUtc) VALUES (@ProjectId, @Name, @CreatedAtUtc);",
            new ImageInsertParameters(projectId, cleanName, Now()), transaction);
        await transaction.CommitAsync(cancellationToken);
        return (await FindContainerRepositoryAsync(actor, projectId, cleanName, cancellationToken))!;
    }

    public async Task<IReadOnlyList<ContainerRepository>> ListContainerRepositoriesAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireRepositoryReaderAsync(db, identity, projectId, cancellationToken);
        IEnumerable<ImageRow> rows = await db.QueryAsync<ImageRow>(
            "SELECT Id, ProjectId, Name, CreatedAtUtc FROM ContainerRepositories WHERE ProjectId = @Id ORDER BY Name;", new IdParameters(projectId));
        List<ContainerRepository> images = [];
        foreach (ImageRow row in rows) images.Add(await HydrateImageAsync(db, row));
        return images;
    }

    public async Task<ContainerRepository?> FindContainerRepositoryAsync(
        AuthenticatedIdentity? identity,
        long projectId,
        string name,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await RequireRepositoryReaderAsync(db, identity, projectId, cancellationToken);
        ImageRow? row = await db.QuerySingleOrDefaultAsync<ImageRow>(
            "SELECT Id, ProjectId, Name, CreatedAtUtc FROM ContainerRepositories WHERE ProjectId = @ProjectId AND Name = @Name;",
            new LabelLookupParameters(projectId, name));
        return row is null ? null : await HydrateImageAsync(db, row);
    }

    public async Task<ContainerManifest?> FindManifestAsync(long repositoryId, string reference, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        ManifestRow? row = reference.StartsWith("sha256:", StringComparison.Ordinal)
            ? await db.QuerySingleOrDefaultAsync<ManifestRow>(
                "SELECT Digest, MediaType, Size FROM ContainerManifests WHERE RepositoryId = @ProjectId AND Digest = @Name;",
                new LabelLookupParameters(repositoryId, reference))
            : await db.QuerySingleOrDefaultAsync<ManifestRow>("""
                SELECT m.Digest, m.MediaType, m.Size FROM ContainerTags t
                JOIN ContainerManifests m ON m.RepositoryId = t.RepositoryId AND m.Digest = t.Digest
                WHERE t.RepositoryId = @ProjectId AND t.Tag = @Name;
                """, new LabelLookupParameters(repositoryId, reference));
        return row is null ? null : new(row.Digest, row.MediaType, row.Size);
    }

    /// <summary>Records a manifest and, when a tag was given, points the tag at it.</summary>
    public async Task PutManifestAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long repositoryId,
        string digest,
        string mediaType,
        long size,
        string? tag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity publisher = await RequirePublisherAsync(db, transaction, actor, projectId, cancellationToken);
        string now = Now();
        await db.ExecuteAsync("""
            INSERT INTO ContainerManifests (RepositoryId, Digest, MediaType, Size, CreatedAtUtc)
            VALUES (@RepositoryId, @Digest, @MediaType, @Size, @CreatedAtUtc)
            ON CONFLICT(RepositoryId, Digest) DO UPDATE SET MediaType = excluded.MediaType, Size = excluded.Size;
            """, new ManifestParameters(repositoryId, digest, mediaType, size, now), transaction);
        if (tag is not null)
            await db.ExecuteAsync("""
                INSERT INTO ContainerTags (RepositoryId, Tag, Digest, UpdatedAtUtc) VALUES (@RepositoryId, @Tag, @Digest, @UpdatedAtUtc)
                ON CONFLICT(RepositoryId, Tag) DO UPDATE SET Digest = excluded.Digest, UpdatedAtUtc = excluded.UpdatedAtUtc;
                """, new TagParameters(repositoryId, tag, digest, now), transaction);
        await AuditAsync(db, transaction, publisher.UserId, "push-image", "container", repositoryId.ToString(CultureInfo.InvariantCulture),
            tag is null ? $"Manifest {digest} pushed." : $"Tag '{tag}' now {digest}.", now);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Removes a tag, or a manifest and every tag pointing at it. The blobs stay.</summary>
    public async Task<bool> DeleteManifestAsync(
        AuthenticatedIdentity actor,
        long projectId,
        long repositoryId,
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        AuthenticatedIdentity publisher = await RequirePublisherAsync(db, transaction, actor, projectId, cancellationToken);
        int changed;
        if (reference.StartsWith("sha256:", StringComparison.Ordinal))
        {
            await db.ExecuteAsync("DELETE FROM ContainerTags WHERE RepositoryId = @ProjectId AND Digest = @Name;", new LabelLookupParameters(repositoryId, reference), transaction);
            changed = await db.ExecuteAsync("DELETE FROM ContainerManifests WHERE RepositoryId = @ProjectId AND Digest = @Name;", new LabelLookupParameters(repositoryId, reference), transaction);
        }
        else changed = await db.ExecuteAsync("DELETE FROM ContainerTags WHERE RepositoryId = @ProjectId AND Tag = @Name;", new LabelLookupParameters(repositoryId, reference), transaction);
        if (changed > 0)
            await AuditAsync(db, transaction, publisher.UserId, "delete-image", "container", repositoryId.ToString(CultureInfo.InvariantCulture),
                $"'{reference}' removed.", Now());
        await transaction.CommitAsync(cancellationToken);
        return changed > 0;
    }

    private const string PackageSelect = """
        SELECT p.Id, p.ProjectId, p.PackageType, p.Name, p.Version, p.Description, p.Authors, u.Username AS CreatedByUsername, p.CreatedAtUtc, p.UpdatedAtUtc
        FROM Packages p
        JOIN Users u ON u.Id = p.CreatedByUserId

        """;

    private static async Task<Package> HydratePackageAsync(SqliteConnection db, PackageRow row)
    {
        IEnumerable<PackageFileRow> files = await db.QueryAsync<PackageFileRow>(
            "SELECT Id, FileName, Size, Sha256, CreatedAtUtc FROM PackageFiles WHERE PackageId = @Id ORDER BY FileName;", new IdParameters(row.Id));
        return new(row.Id, row.ProjectId, row.PackageType, row.Name, row.Version, row.Description, row.Authors, row.CreatedByUsername,
            Instant(row.CreatedAtUtc), Instant(row.UpdatedAtUtc), [.. files.Select(file => new PackageFile(file.Id, file.FileName, file.Size, file.Sha256, Instant(file.CreatedAtUtc)))]);
    }

    private static async Task<ContainerRepository> HydrateImageAsync(SqliteConnection db, ImageRow row)
    {
        IEnumerable<TagRow> tags = await db.QueryAsync<TagRow>("""
            SELECT t.Tag, t.Digest, m.MediaType, m.Size, t.UpdatedAtUtc FROM ContainerTags t
            JOIN ContainerManifests m ON m.RepositoryId = t.RepositoryId AND m.Digest = t.Digest
            WHERE t.RepositoryId = @Id ORDER BY t.Tag;
            """, new IdParameters(row.Id));
        return new(row.Id, row.ProjectId, row.Name, Instant(row.CreatedAtUtc),
            [.. tags.Select(tag => new ContainerTag(tag.Tag, tag.Digest, tag.MediaType, tag.Size, Instant(tag.UpdatedAtUtc)))]);
    }

    private static string RequirePackageName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string trimmed = value.Trim();
        if (trimmed.Length > 255 || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/') || trimmed.StartsWith('.') || trimmed.Contains("..", StringComparison.Ordinal) || trimmed.StartsWith('/') || trimmed.EndsWith('/'))
            throw new ArgumentException("A package name is letters, digits, dots, hyphens, underscores and slashes.", nameof(value));
        return trimmed;
    }

    private static string RequirePackageVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string trimmed = value.Trim();
        if (trimmed.Length > 128 || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+' or '_') || trimmed.StartsWith('.'))
            throw new ArgumentException("A version is letters, digits, dots, hyphens, pluses and underscores.", nameof(value));
        return trimmed;
    }

    private static string RequireImageName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string trimmed = value.Trim();
        if (trimmed.Length > 255 || !trimmed.Split('/').All(segment => segment.Length > 0 && segment.All(c => char.IsAsciiLetterOrDigit(c) && !char.IsUpper(c) || c is '.' or '-' or '_')))
            throw new ArgumentException("An image name is lowercase segments of letters, digits, dots, hyphens and underscores.", nameof(value));
        return trimmed;
    }
}

internal sealed record PackageInsertParameters(long ProjectId, string PackageType, string Name, string NormalizedName, string Version, string NormalizedVersion, string Description, string Authors, long UserId, string Now);
internal sealed record PackageFileParameters(long PackageId, string FileName, long Size, string Sha256, string CreatedAtUtc);
internal sealed record PackageListParameters(long ProjectId, string Type, string Name);
internal sealed record PackageKeyParameters(long ProjectId, string Type, string Name, string Version);
internal sealed record ImageInsertParameters(long ProjectId, string Name, string CreatedAtUtc);
internal sealed record ManifestParameters(long RepositoryId, string Digest, string MediaType, long Size, string CreatedAtUtc);
internal sealed record TagParameters(long RepositoryId, string Tag, string Digest, string UpdatedAtUtc);

internal sealed class PackageRow
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string PackageType { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string Authors { get; set; } = "";
    public string CreatedByUsername { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
    public string UpdatedAtUtc { get; set; } = "";
}

internal sealed class PackageFileRow
{
    public long Id { get; set; }
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
}

internal sealed class ImageRow
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
}

internal sealed class ManifestRow
{
    public string Digest { get; set; } = "";
    public string MediaType { get; set; } = "";
    public long Size { get; set; }
}

internal sealed class TagRow
{
    public string Tag { get; set; } = "";
    public string Digest { get; set; } = "";
    public string MediaType { get; set; } = "";
    public long Size { get; set; }
    public string UpdatedAtUtc { get; set; } = "";
}
