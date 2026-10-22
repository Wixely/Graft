using DnaX.Data.Migrations;

namespace Graft.Storage;

public static class GraftSchema
{
    public static DnaXMigrationManifest Manifest { get; } = new(10,
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
            """),
        // A second, instance-wide name for a project, so it can be shared as /projects/<slug> without the
        // owner's username in the link. It is an alias and nothing more: the owning namespace still exists,
        // still works, and still decides who may see the project.
        //
        // Every project gets one, including the ones that existed before this migration. The backfill gives
        // each project its own path where that path is free instance-wide, and otherwise its path, two
        // hyphens and its id. Two hyphens cannot appear in a project path, which is what makes the fallback
        // safe: no generated value can collide with a plain path, and ids make them unique among themselves.
        //
        // The index is UNIQUE and the column is nullable only because the backfill below fills it in two
        // passes; SQLite treats NULLs as distinct, so the intermediate state is legal. Every write after
        // this migration sets it.
        DnaXMigration.Sql(6, "project-global-slug", "Give every project an instance-wide slug", """
            ALTER TABLE Projects ADD COLUMN GlobalSlug TEXT NULL;

            UPDATE Projects SET GlobalSlug = NormalizedSlug
            WHERE Id IN (SELECT MIN(Id) FROM Projects GROUP BY NormalizedSlug);

            UPDATE Projects SET GlobalSlug = NormalizedSlug || '--' || Id WHERE GlobalSlug IS NULL;

            CREATE UNIQUE INDEX IX_Projects_GlobalSlug ON Projects(GlobalSlug);
            """),
        // Where a token's secret is kept when, and only when, an operator has asked for recoverable tokens.
        // Null is the normal state and the only state for every token created before that was switched on,
        // which is why recoverability is a property of each token rather than of the server: a column that
        // was filled only sometimes cannot be read as "the setting is on now".
        //
        // The server encrypts before this is written and decrypts after it is read; the store never sees a
        // recoverable secret in the clear and holds no key.
        DnaXMigration.Sql(7, "token-secret-cipher", "Let an operator keep token secrets recoverable", """
            ALTER TABLE AccessTokens ADD COLUMN SecretCipher TEXT NULL;
            """),
        // Where the conversation about a project lives. Until this migration Graft hosted code and said
        // nothing about changing it: no issues, no proposals, no comments.
        //
        // Issues and merge requests are separate tables rather than one table with a kind, because they
        // differ in more than a label: a merge request names two branches, can be merged, and carries a
        // merge commit. A shared table would be half-empty for every issue and its CHECK constraints could
        // not say which columns belong to which kind.
        //
        // Comments are shared, because a comment is the same thing on either: an author, a body and a time.
        // SubjectId holds the row id rather than the public number, so a comment cannot be orphaned by
        // renumbering and the join needs no project in it. SQLite cannot express a foreign key whose target
        // table depends on another column, so the two parents are deleted by hand wherever comments are.
        //
        // Iid is the number people use: per project and per kind, counted from one, the way GitLab numbers
        // them. The database holds no counter. A new row takes MAX(Iid) + 1 inside the immediate
        // transaction, which is the same transaction that inserts it, so two concurrent authors cannot be
        // handed the same number. The UNIQUE constraint is the backstop if that reasoning is ever wrong.
        //
        // State is spelled GitLab's way, 'opened' rather than 'open', so the API can report the stored
        // value without translating it and a client comparing strings sees what it expects.
        DnaXMigration.Sql(8, "collaboration", "Give a project issues, merge requests and comments", """
            CREATE TABLE Issues (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                Iid INTEGER NOT NULL,
                AuthorUserId INTEGER NOT NULL REFERENCES Users(Id),
                Title TEXT NOT NULL,
                Description TEXT NOT NULL DEFAULT '',
                State TEXT NOT NULL CHECK (State IN ('opened', 'closed')),
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                ClosedAtUtc TEXT NULL,
                UNIQUE (ProjectId, Iid)
            );

            CREATE TABLE MergeRequests (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                Iid INTEGER NOT NULL,
                AuthorUserId INTEGER NOT NULL REFERENCES Users(Id),
                Title TEXT NOT NULL,
                Description TEXT NOT NULL DEFAULT '',
                SourceBranch TEXT NOT NULL,
                TargetBranch TEXT NOT NULL,
                State TEXT NOT NULL CHECK (State IN ('opened', 'merged', 'closed')),
                MergeCommitSha TEXT NULL,
                MergedByUserId INTEGER NULL REFERENCES Users(Id),
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                ClosedAtUtc TEXT NULL,
                UNIQUE (ProjectId, Iid)
            );

            CREATE TABLE Comments (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                SubjectType TEXT NOT NULL CHECK (SubjectType IN ('issue', 'merge_request')),
                SubjectId INTEGER NOT NULL,
                AuthorUserId INTEGER NOT NULL REFERENCES Users(Id),
                Body TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL
            );

            CREATE INDEX IX_Issues_Project ON Issues(ProjectId, State);
            CREATE INDEX IX_MergeRequests_Project ON MergeRequests(ProjectId, State);
            CREATE INDEX IX_Comments_Subject ON Comments(SubjectType, SubjectId, Id);
            """),
        // Review, in the two forms it takes.
        //
        // An approval records the commit it was given against. An approval of code that has since changed
        // is not an approval of what would be merged, so the stored sha is what lets a stale one be
        // recognised rather than counted. Nothing deletes a stale approval; it stops counting and says so,
        // because deleting it would hide that somebody had looked.
        //
        // ApprovalsRequired defaults to zero, so every project that existed before this migration merges
        // exactly as it did. A gate is never the result of an upgrade.
        //
        // A comment gains an optional anchor: a path, a line, which side of the diff the line is on, and
        // the commit the diff was computed from. All four are null together for a comment on the proposal
        // as a whole, which is what every comment written before this migration was. The anchor is stored
        // rather than recomputed because the branch moves underneath it, and a comment that silently
        // re-anchored itself to whatever now occupies that line number would be worse than one that admits
        // the code moved.
        DnaXMigration.Sql(9, "review", "Record approvals, and let a comment point at a line", """
            ALTER TABLE Projects ADD COLUMN ApprovalsRequired INTEGER NOT NULL DEFAULT 0
                CHECK (ApprovalsRequired BETWEEN 0 AND 10);

            CREATE TABLE MergeRequestApprovals (
                MergeRequestId INTEGER NOT NULL REFERENCES MergeRequests(Id) ON DELETE CASCADE,
                UserId INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
                SourceCommitSha TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (MergeRequestId, UserId)
            );

            ALTER TABLE Comments ADD COLUMN Path TEXT NULL;
            ALTER TABLE Comments ADD COLUMN Line INTEGER NULL;
            ALTER TABLE Comments ADD COLUMN Side TEXT NULL CHECK (Side IS NULL OR Side IN ('old', 'new'));
            ALTER TABLE Comments ADD COLUMN AnchorCommitSha TEXT NULL;

            CREATE INDEX IX_Comments_Anchor ON Comments(SubjectType, SubjectId, Path);
            """),
        // Where a project says it wants to be told that something happened.
        //
        // The secret is stored in the clear and never returned after creation. It is not a credential for
        // Graft: it is the key the receiver uses to check the signature on a delivery, so the receiver must
        // hold the same value and Graft must be able to sign with it. Encrypting it would protect nothing
        // that the receiver's own copy does not already expose.
        //
        // Events are a comma-separated set from a fixed list rather than a table, because the set is small,
        // closed, and only ever read whole.
        //
        // The last delivery's outcome is kept on the row so an operator can see a hook failing without
        // reading a log. One row, overwritten: a delivery history would be a table that grows forever to
        // answer a question nobody asks twice.
        DnaXMigration.Sql(10, "webhooks", "Let a project ask to be told when something happens", """
            CREATE TABLE Webhooks (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                Url TEXT NOT NULL,
                Secret TEXT NOT NULL,
                Events TEXT NOT NULL,
                Active INTEGER NOT NULL DEFAULT 1 CHECK (Active IN (0, 1)),
                CreatedByUserId INTEGER NOT NULL REFERENCES Users(Id),
                CreatedAtUtc TEXT NOT NULL,
                LastStatus INTEGER NULL,
                LastError TEXT NULL,
                LastDeliveredAtUtc TEXT NULL
            );

            CREATE INDEX IX_Webhooks_Project ON Webhooks(ProjectId, Active);
            """)
    ]);
}
