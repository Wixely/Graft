# MVP-07 import and recovery evidence

Date: 2026-09-19. Owner: Implementation agent. Review when import inventory, repository transport, storage layout, database schema, maintenance leases, backup format or restore publication changes.

## Result

The bounded MVP-07 slice passes on Windows with the managed tools and with self-contained `win-x64` NativeAOT `Graft.Server` and `Graft.Admin` executables. The operations acceptance executable completed **36 assertions** in both modes. The retained MVP-06 server suite completed all **64 assertions** against the updated NativeAOT server.

The full solution builds without warnings or errors. No AOT or trimming warning is suppressed.

## Repository-only import

The C# `GitBundleInventory` parser reads bounded Git bundle v2/v3 headers without decoding pack bytes, calculates the bundle SHA-256, inventories SHA-1 branches/tags and emits a fixed eight-category exclusion report. It rejects unsupported signatures, invalid refs, duplicate refs, oversized headers and bundles above the current 1 GiB inventory limit.

The synthetic source contains `main`, `feature`, an annotated `v1.0` tag, text and binary content. Git 2.53.0.windows.2 creates the independent bundle/mirror fixture and pushes all branches and tags through Graft's authenticated HTTPS transport. Acceptance compares every inventoried ref/object ID with `git ls-remote`, repeats the import, confirms the target refs are unchanged and hashes the complete source tree before/after to prove the source was not mutated.

The report explicitly excludes namespaces/settings, identities/memberships, collaboration metadata, wiki/uploads/releases, CI/CD, LFS/packages/registry, integrations/credentials and audit/analytics. This is repository transport rehearsal, not GitLab export/API metadata compatibility.

## Backup and restore

The server now acquires exclusive maintenance leases for both its database and repository root before opening storage. The admin tool must acquire the same pair. Acceptance proves a running server blocks backup.

A backup uses SQLite's backup API to create a consistent database snapshot and safely copies the quiesced repository tree without following reparse points. Its source-generated JSON manifest inventories directories plus every file's length and SHA-256. Verification compares exact inventories, hashes every file and runs SQLite `integrity_check`. Acceptance proves changed repository bytes are rejected.

Restore verifies the backup first, stages the database and repository tree, checks SQLite again and publishes only to absent targets. It refuses an existing target and removes a database if repository publication fails afterward. The restored token, project, permissions and every imported ref are verified, followed by an authenticated clone and `git fsck --full --strict`.

The rollback rehearsal restores a candidate, applies a synthetic incompatible schema change, observes the permission preflight failure, restores the accepted backup to a second fresh target and repeats metadata/ref verification. It demonstrates the documented restore-based response without altering production data.

## Commands

```powershell
dotnet build Graft.slnx -c Release
dotnet tests/Graft.Operations.Acceptance/bin/Release/net10.0/Graft.Operations.Acceptance.dll src/Graft.Server/bin/Release/net10.0/Graft.Server.dll src/Graft.Admin/bin/Release/net10.0/Graft.Admin.dll

dotnet publish src/Graft.Server/Graft.Server.csproj -c Release -r win-x64 --self-contained true -p:GraftServerAot=true -o artifacts/mvp07/server-win-x64
dotnet publish src/Graft.Admin/Graft.Admin.csproj -c Release -r win-x64 --self-contained true -p:GraftAdminAot=true -o artifacts/mvp07/admin-win-x64
dotnet tests/Graft.Operations.Acceptance/bin/Release/net10.0/Graft.Operations.Acceptance.dll artifacts/mvp07/server-win-x64/Graft.Server.exe artifacts/mvp07/admin-win-x64/Graft.Admin.exe
dotnet tests/Graft.Git.Acceptance/bin/Release/net10.0/Graft.Git.Acceptance.dll artifacts/mvp07/server-win-x64/Graft.Server.exe
```

## Evidence boundary

The import source is synthetic and local. No GitLab instance, export, API or production repository was accessed. The ordinary Git client is an independent acceptance client; Graft application and operations logic remain C#. Only branches, tags and reachable Git objects are imported. Deletions, LFS, submodules, wiki repositories, namespace creation and GitLab metadata remain outside this rehearsal.

Backup is offline/quiesced and assumes one Graft server process. It does not coordinate arbitrary programs that bypass the maintenance lease. Database and repositories can be on separate filesystems, so the tool provides verified staged publication and cleanup rather than claiming cross-filesystem atomicity or hardware power-loss guarantees. Restore intentionally requires fresh targets. Encryption, retention, remote media, incremental backup, scheduling and production migration orchestration are not implemented.

The integrated binaries have not yet been statically published with musl or run on Linux 3.10.108. MVP-07 is a **PASS within this bounded Windows scope**.

Recommended next action: Owner: Implementation agent — begin MVP-08 with Windows interactive/service lifecycle support and resource measurements while retaining the MVP-06/MVP-07 suites. Owner: User/DnaX maintainer — select an approved durable package source.