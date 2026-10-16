using DnaX.Data.Migrations;

namespace Graft.Storage;

public static class GraftSchema
{
    public static DnaXMigrationManifest Manifest { get; } = new(5,
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
            """),
        // Both columns default to the closed value, so every project that existed before this migration stays
        // exactly as private as it was. Publishing is never a side effect of an upgrade.
        //
        // Visibility and PagePublished are separate on purpose: a private repository may publish a curated
        // page without publishing its source, which is the case the request describes. They are stored here
        // rather than read from the repository's own .graft config because publishing is a Maintainer
        // decision and pushing a commit is a Developer one; a config that could set these would let a
        // Developer publish a private project by pushing to it.
        DnaXMigration.Sql(4, "project-visibility", "Give a project a stored visibility and a publishable page", """
            ALTER TABLE Projects ADD COLUMN Visibility TEXT NOT NULL DEFAULT 'private'
                CHECK (Visibility IN ('private', 'unlisted', 'public'));
            ALTER TABLE Projects ADD COLUMN PagePublished INTEGER NOT NULL DEFAULT 0
                CHECK (PagePublished IN (0, 1));

            CREATE INDEX IX_Projects_Published ON Projects(Visibility, PagePublished);
            """),
        // One row per setting rather than one column per setting, so adding a switch is a write and not a
        // migration. Only the Model Context Protocol switches live here so far; an absent row means the
        // default, which keeps a restored database from a build that never had a switch meaning what the
        // newer build would have stored for it.
        DnaXMigration.Sql(5, "server-settings", "Store administrator-managed server settings", """
            CREATE TABLE ServerSettings (
                Name TEXT NOT NULL PRIMARY KEY,
                Value TEXT NOT NULL,
                UpdatedByUserId INTEGER NULL REFERENCES Users(Id),
                UpdatedAtUtc TEXT NOT NULL
            );
            """)
    ]);
}
