using DnaX.Data.Migrations;

namespace Graft.Storage;

public static class GraftSchema
{
    public static DnaXMigrationManifest Manifest { get; } = new(3,
    [
        DnaXMigration.Sql(1, "initial-identity-project-schema", "Create identity and private project schema", """
            CREATE TABLE Users (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Username TEXT NOT NULL,
                NormalizedUsername TEXT NOT NULL UNIQUE,
                PasswordHash TEXT NOT NULL,
                IsAdministrator INTEGER NOT NULL CHECK (IsAdministrator IN (0, 1)),
                Disabled INTEGER NOT NULL DEFAULT 0 CHECK (Disabled IN (0, 1)),
                CreatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE Projects (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                OwnerUserId INTEGER NOT NULL REFERENCES Users(Id),
                Slug TEXT NOT NULL,
                NormalizedSlug TEXT NOT NULL,
                Name TEXT NOT NULL,
                Description TEXT NOT NULL DEFAULT '',
                StorageId TEXT NOT NULL UNIQUE,
                State TEXT NOT NULL CHECK (State IN ('pending', 'ready')),
                CreatedAtUtc TEXT NOT NULL,
                UNIQUE (OwnerUserId, NormalizedSlug)
            );

            CREATE TABLE ProjectMemberships (
                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                UserId INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
                Role INTEGER NOT NULL CHECK (Role IN (10, 20, 30)),
                GrantedByUserId INTEGER NOT NULL REFERENCES Users(Id),
                CreatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (ProjectId, UserId)
            );

            CREATE TABLE BrowserSessions (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
                SecretDigest TEXT NOT NULL UNIQUE,
                ExpiresAtUtc TEXT NOT NULL,
                RevokedAtUtc TEXT NULL,
                CreatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE AccessTokens (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
                Name TEXT NOT NULL,
                SecretDigest TEXT NOT NULL UNIQUE,
                Scopes INTEGER NOT NULL,
                ExpiresAtUtc TEXT NOT NULL,
                RevokedAtUtc TEXT NULL,
                CreatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE AuditEvents (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ActorUserId INTEGER NULL REFERENCES Users(Id),
                Action TEXT NOT NULL,
                TargetType TEXT NOT NULL,
                TargetId TEXT NULL,
                Detail TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL
            );

            CREATE INDEX IX_ProjectMemberships_UserId ON ProjectMemberships(UserId);
            CREATE INDEX IX_BrowserSessions_UserId ON BrowserSessions(UserId);
            CREATE INDEX IX_AccessTokens_UserId ON AccessTokens(UserId);
            CREATE INDEX IX_AuditEvents_CreatedAtUtc ON AuditEvents(CreatedAtUtc);
            """),
        // Avatars live in the database rather than on disk so a backup captures them atomically with the
        // accounts and projects they belong to, and a restore cannot reinstate one without the other. They
        // are small by policy, so the row size is bounded well before it becomes a storage concern.
        DnaXMigration.Sql(2, "avatars", "Store user and project avatars", """
            CREATE TABLE Avatars (
                OwnerType TEXT NOT NULL CHECK (OwnerType IN ('user', 'project')),
                OwnerId INTEGER NOT NULL,
                ContentType TEXT NOT NULL,
                Width INTEGER NOT NULL,
                Height INTEGER NOT NULL,
                Bytes BLOB NOT NULL,
                Sha256 TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (OwnerType, OwnerId)
            );
            """),
        // Nullable on purpose: null means "follow convention", which lets a repository that has only
        // master, or only some other branch, resolve sensibly without an administrator setting anything.
        // A stored name is a preference, not a guarantee; the branch can be deleted after it is chosen.
        DnaXMigration.Sql(3, "project-default-branch", "Let a project choose its default branch", """
            ALTER TABLE Projects ADD COLUMN DefaultBranch TEXT NULL;
            """)
    ]);
}
