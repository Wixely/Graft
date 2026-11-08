using System.Globalization;
using System.Security.Cryptography;
using Dapper;
using Graft.Core;
using Microsoft.Data.Sqlite;

namespace Graft.Storage;

/// <summary>
/// A project's banner: one wide image drawn behind its title and tab bar (ADR 0046). Set, read and removed the way a
/// project avatar is, with the same validator and the same permission, and stored beside it in the database.
/// </summary>
public sealed partial class GraftStore
{
    /// <summary>Stores a project's banner, replacing any existing one. Needs the permission to manage the project.</summary>
    public async Task SetBannerAsync(
        AuthenticatedIdentity actor,
        long projectId,
        ValidatedImage image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(image);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId, ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);

        string now = Now();
        string digest = Convert.ToHexStringLower(SHA256.HashData(image.Bytes));
        // A provider command rather than Dapper, for the reason SetAvatarAsync gives: Dapper.AOT binds a byte array
        // as one SQL variable per element, which cannot express a BLOB.
        await using (SqliteCommand command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ProjectBanners (ProjectId, ContentType, Width, Height, Bytes, Sha256, UpdatedAtUtc)
                VALUES ($projectId, $contentType, $width, $height, $bytes, $sha256, $updatedAtUtc)
                ON CONFLICT(ProjectId) DO UPDATE SET
                    ContentType = excluded.ContentType,
                    Width = excluded.Width,
                    Height = excluded.Height,
                    Bytes = excluded.Bytes,
                    Sha256 = excluded.Sha256,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            command.Parameters.AddWithValue("$projectId", projectId);
            command.Parameters.AddWithValue("$contentType", image.ContentType);
            command.Parameters.AddWithValue("$width", image.Width);
            command.Parameters.AddWithValue("$height", image.Height);
            command.Parameters.Add("$bytes", SqliteType.Blob).Value = image.Bytes;
            command.Parameters.AddWithValue("$sha256", digest);
            command.Parameters.AddWithValue("$updatedAtUtc", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await AuditAsync(db, transaction, actor.UserId, "set-banner", "project",
            projectId.ToString(CultureInfo.InvariantCulture), "Banner replaced.", now);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Removes a project's banner. Returns false when it had none. Needs the permission to manage the project.</summary>
    public async Task<bool> RemoveBannerAsync(AuthenticatedIdentity actor, long projectId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = db.BeginTransaction(deferred: false);
        PermissionDecision permission = await DecideAsync(db, transaction, actor, projectId, ProjectAction.ManageProject, cancellationToken);
        if (!permission.Allowed) throw new UnauthorizedAccessException(permission.Explanation);
        int removed = await db.ExecuteAsync("DELETE FROM ProjectBanners WHERE ProjectId = @Id;", new IdParameters(projectId), transaction);
        if (removed == 0) return false;
        await AuditAsync(db, transaction, actor.UserId, "remove-banner", "project",
            projectId.ToString(CultureInfo.InvariantCulture), "Banner removed.", Now());
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Reads a project's banner, or null when none is set. Callers decide who may see the project.</summary>
    public async Task<StoredAvatar?> GetBannerAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        AvatarRow? row = await db.QuerySingleOrDefaultAsync<AvatarRow>(
            "SELECT ContentType, Bytes, Sha256 FROM ProjectBanners WHERE ProjectId = @Id;", new IdParameters(projectId));
        return row is null ? null : new(row.ContentType, row.Bytes, row.Sha256);
    }

    /// <summary>The banner's digest, or null when none is set, without reading its bytes. The digest goes into the
    /// banner's address so a replaced banner is a new URL and no browser keeps drawing the old one.</summary>
    public async Task<string?> GetBannerDigestAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection db = await OpenConnectionAsync(cancellationToken);
        return await db.QuerySingleOrDefaultAsync<string>(
            "SELECT Sha256 FROM ProjectBanners WHERE ProjectId = @Id;", new IdParameters(projectId));
    }
}
