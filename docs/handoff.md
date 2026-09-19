# Next-agent handoff

Updated: 2026-09-19. Review: at the start and end of the next work session. Owner: Implementation agent; product choices belong to the user.

## Current state

MVP-01 through MVP-07 pass within their recorded Windows scopes. The latest [MVP-07 import and recovery evidence](validation/2026-09-19-mvp07-import-recovery.md) records 36 assertions against both managed and `win-x64` NativeAOT server/admin binaries. The retained MVP-06 suite still passes 64 assertions.

`src/Graft.Operations` now owns the offline maintenance lease, SHA-256 backup manifest, SQLite snapshot, safe repository-tree copying, verification, restore and Git bundle inventory. `src/Graft.Admin` exposes `backup`, `verify`, `restore` and `inspect-bundle` commands and publishes under NativeAOT. The server holds exclusive database and repository leases for its lifetime, so maintenance fails while it is running.

The import evidence is deliberately repository-only. A C# bundle parser inventories branches/tags and emits eight explicit excluded metadata categories. Git 2.53.0.windows.2 imports the repository through the supported HTTPS route; repeating the import preserves the same refs and object IDs and leaves the source unchanged. This is not a GitLab API/export metadata importer.

Backups use a consistent SQLite backup plus the complete repository tree. Every file and directory is inventoried; files are hashed. Links/reparse points, undeclared paths, tampering, live-server backup and existing restore targets are rejected. Restore verifies before publishing fresh targets and removes a database published before a later failure. The rollback rehearsal uses a synthetic incompatible schema change and a second fresh restore. Online backup, cross-process clusters and power-loss atomicity across separate database/repository paths remain outside the evidence.

The main release gates are the durable DnaX package source, production TLS, Windows Service and Linux systemd lifecycle, resource budgets, optional Docker packaging, integrated static-musl publication and execution on the required Linux 3.10.108 host. Exact GitLab fixtures and interactive production-browser execution also remain open.

No remote is configured. Do not deploy, publish, import production data, expose a network listener or select repository visibility without explicit authorization.

## Next work

Begin MVP-08 with Windows interactive/service lifecycle support and measured idle/load memory and latency. Preserve the current interactive mode and both MVP-06/MVP-07 acceptance suites. Then add Linux interactive/systemd packaging, optional Docker deployment and the static-musl integrated build before coordinating required-host execution with the user.

Continue to use the ignored reproducible DnaX feed until the user or DnaX maintainer selects an approved durable package source. Do not substitute a server-side Git executable or native Git library.

## Follow-on queue

| Work | Owner | Acceptance |
| --- | --- | --- |
| Windows lifecycle and measurements | Implementation agent | Interactive and Windows Service modes; clean stop; measured idle/load memory and latency |
| Linux and Docker packaging | Implementation agent | Interactive/systemd and optional Docker tested separately; operational paths documented |
| Required legacy host | Implementation agent + user | Integrated static-musl artifact inspected and run on Linux 3.10.108 with sanitized evidence |
| GitLab fixture | Implementation agent | Version-matched fixtures and pinned `glab` workflow replace rolling-document inference |
| DnaX distribution | User/DnaX maintainer | Clean checkout restores without a sibling source tree or locally prepared feed |

Recommended next action: Owner: Implementation agent — implement the Windows MVP-08 service lifecycle and resource baseline. Remaining work: MVP-08 packaging, DnaX distribution, exact GitLab fixtures, interactive browser execution, integrated static-musl publication and required-host acceptance.