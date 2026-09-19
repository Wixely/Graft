# Import and recovery operations

Updated: 2026-09-19. Owner: Operator. Review when storage layout, schema, import scope or service lifecycle changes.

## Safety boundary

`Graft.Server` holds exclusive maintenance leases beside both the database and repository root for its lifetime. Stop the server before backup or restore. `Graft.Admin` refuses maintenance while either lease is held. Restore accepts only paths that do not exist; it never overwrites active data.

Backups are directories containing `database.sqlite`, `repositories/` and `manifest.json`. The manifest inventories all repository directories and records every file's length and SHA-256. Verification rejects missing, extra, changed or linked content and runs SQLite `integrity_check`.

Keep backups on storage with access controls appropriate for password hashes, token/session digests, audit data and private repository content. The backup is not encrypted by Graft.

## Backup and verification

After stopping Graft:

```powershell
dotnet src\Graft.Admin\bin\Release\net10.0\Graft.Admin.dll backup `
  --database C:\GraftData\graft.db `
  --repositories C:\GraftData\repositories `
  --output D:\GraftBackups\graft-2026-09-19

dotnet src\Graft.Admin\bin\Release\net10.0\Graft.Admin.dll verify `
  --input D:\GraftBackups\graft-2026-09-19
```

Use the published `Graft.Admin.exe` in an installed NativeAOT deployment. The DLL form above is for a development checkout.

## Restore and rollback

Restore into fresh targets, verify them, and then configure the server to use those paths:

```powershell
dotnet src\Graft.Admin\bin\Release\net10.0\Graft.Admin.dll restore `
  --input D:\GraftBackups\graft-2026-09-19 `
  --database C:\GraftRestore\graft.db `
  --repositories C:\GraftRestore\repositories
```

Start Graft against the restored database and repository paths, then run clone/fetch and permission checks before retiring the prior data. For rollback, stop Graft and restore the accepted backup to another fresh pair of paths. Preserve the failed data until diagnosis is complete. The tool does not delete or overwrite it.

A verified directory backup does not prove filesystem or media durability. Copy it to the intended backup medium and re-run `verify` there.

## Repository-only import inventory

Create a bundle from a synthetic or authorized source and produce a dry-run inventory:

```powershell
git -C C:\SourceRepository bundle create C:\Import\source.bundle --all

dotnet src\Graft.Admin\bin\Release\net10.0\Graft.Admin.dll inspect-bundle `
  --input C:\Import\source.bundle `
  --output C:\Import\source-inventory.json
```

Review the bundle digest, refs and every exclusion before creating the destination project or transferring data. The current rehearsal imports branches and tags using an ordinary Git HTTPS client, first with `git push --all` and then `git push --tags`. Repeat both pushes and compare `git ls-remote` with the inventory.

This path imports Git repository objects and refs only. It excludes groups/subgroups, users and memberships, issues and merge requests, reviews and discussions, labels and milestones, wiki/uploads/releases, LFS, CI/CD history and secrets, packages/registry data, hooks/integrations, audit data and analytics. It does not rewrite history or mutate the source. A future GitLab metadata importer must add authenticated export/API acquisition, identity mapping, resumable checkpoints and reconciliation separately.