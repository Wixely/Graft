# Next-agent handoff

Updated: 2026-09-19. Review: at the start and end of the next work session. Owner: Implementation agent; product choices belong to the user.

## Current state

MVP-01 through MVP-07 pass within their recorded scopes. [MVP-08 packaging and resource evidence](validation/2026-09-19-mvp08-packaging-resources.md) conditionally passes Windows NativeAOT and Linux static-musl interactive runs, Linux systemd and rootless Podman. The static Linux server/admin retain the 64 MVP-06 and 36 MVP-07 assertions.

`src/Graft.Operations` now owns the offline maintenance lease, SHA-256 backup manifest, SQLite snapshot, safe repository-tree copying, verification, restore and Git bundle inventory. `src/Graft.Admin` exposes `backup`, `verify`, `restore` and `inspect-bundle` commands and publishes under NativeAOT. The server holds exclusive database and repository leases for its lifetime, so maintenance fails while it is running.

The import evidence is deliberately repository-only. A C# bundle parser inventories branches/tags and emits eight explicit excluded metadata categories. Git 2.53.0.windows.2 imports the repository through the supported HTTPS route; repeating the import preserves the same refs and object IDs and leaves the source unchanged. This is not a GitLab API/export metadata importer.

Backups use a consistent SQLite backup plus the complete repository tree. Every file and directory is inventoried; files are hashed. Links/reparse points, undeclared paths, tampering, live-server backup and existing restore targets are rejected. Restore verifies before publishing fresh targets and removes a database published before a later failure. The rollback rehearsal uses a synthetic incompatible schema change and a second fresh restore. Online backup, cross-process clusters and power-loss atomicity across separate database/repository paths remain outside the evidence.

The main release gates are the durable DnaX package source, a real CA/DNS/client-trust environment and integrated static-musl execution on the required Linux 3.10.108 host. Windows Service, local operator-managed certificate loading, restart/replacement, systemd and container secret plumbing pass. Exact GitLab fixtures and interactive production-browser execution also remain open.

No remote is configured. Do not deploy, publish, import production data, expose a network listener or select repository visibility without explicit authorization.

## Next work

Transfer the integrated static-musl server/admin pair to the Linux 3.10.108 host and repeat TLS, interactive, Git and operations acceptance. Validate the intended CA-issued certificate, DNS and client trust chain before non-loopback deployment.

Continue to use the ignored reproducible DnaX feed until the user or DnaX maintainer selects an approved durable package source. Do not substitute a server-side Git executable or native Git library.

## Follow-on queue

| Work | Owner | Acceptance |
| --- | --- | --- |
| Deployment TLS trust | Operator + implementation agent | Intended CA certificate, DNS/SAN, client trust and renewal rehearsal |
| Required legacy host | Implementation agent + user | Integrated static-musl artifact inspected and run on Linux 3.10.108 with sanitized evidence |
| GitLab fixture | Implementation agent | Version-matched fixtures and pinned `glab` workflow replace rolling-document inference |
| DnaX distribution | User/DnaX maintainer | Clean checkout restores without a sibling source tree or locally prepared feed |

Recommended next action: Owner: User + implementation agent — run required-host acceptance. Remaining work: deployment CA/DNS trust, DnaX distribution, exact GitLab fixtures, interactive browser execution and the Linux 3.10.108 MVP-08 gate.
