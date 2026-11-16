# Operator operations

Updated: 2026-11-16. Owner: Operator. Review when storage layout, schema, import scope or service lifecycle changes.

## Safety boundary

`Graft.Server` holds exclusive maintenance leases beside both the database and repository root for its lifetime. Stop the server before backup or restore. `Graft.Admin` refuses maintenance while either lease is held. Restore accepts only paths that do not exist; it never overwrites active data.

Backups are directories containing `database.sqlite`, `repositories/` and `manifest.json`. The manifest inventories all repository directories and records every file's length and SHA-256. Verification rejects missing, extra, changed or linked content and runs SQLite `integrity_check`.

A backup does **not** hold what the server keeps beside the database: the key ring in `data-protection-keys/`, the
runner Shrine's key in `runner-shrine/`, and the SSH host key `ssh_host_ecdsa_key`. Copy them with every backup and
put them beside the restored database. Without the key ring, recoverable tokens and release destinations cannot be
read; without the Shrine key, every runner must be installed again; without the host key, SSH clients are told the
host has changed.

Keep backups on storage with access controls appropriate for password hashes, token/session digests, audit data and private repository content. The backup is not encrypted by Graft.

## First administrator

Before the first server start, create the initial administrator from a protected UTF-8 password file:

```powershell
dotnet src\Graft.Admin\bin\Release\net10.0\Graft.Admin.dll bootstrap `
  --database C:\GraftData\graft.db `
  --repositories C:\GraftData\repositories `
  --username operator `
  --password-file C:\GraftSecrets\bootstrap-password.txt
```

The password must be 16-256 characters on one line; one trailing line ending is accepted. The command stores only the versioned password hash, never prints the password, and succeeds only while the user database is empty. It takes the same exclusive maintenance leases as backup and restore, so stop Graft before running it. Securely remove the password file after verifying the administrator can sign in.

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

In an installed deployment use the release's `graft-admin-win-x64.exe` or `graft-admin-linux-x64`. The DLL form above is for a development checkout.

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
Alternatively, with `--url-import public|any`, a project can be created from a repository's address and kept as a
mirror ([ADR 0042](decisions/0042-url-import-and-mirrors.md)); like the bundle path, it brings branches and tags only.

This path imports Git repository objects and refs only. It excludes groups/subgroups, users and memberships, issues and merge requests, reviews and discussions, labels and milestones, wiki/uploads/releases, LFS, CI/CD history and secrets, packages/registry data, hooks/integrations, audit data and analytics. It does not rewrite history or mutate the source. A future GitLab metadata importer must add authenticated export/API acquisition, identity mapping, resumable checkpoints and reconciliation separately.
