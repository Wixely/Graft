# Next-agent handoff

Updated: 2026-09-19. Review: at the start and end of the next work session. Owner: Implementation agent; product choices belong to the user.

## Current state

MVP-01 through MVP-07 pass within their recorded scopes. [MVP-08 packaging and resource evidence](validation/2026-09-19-mvp08-packaging-resources.md) conditionally passes Windows NativeAOT, Linux static-musl interactive/systemd, and rootless Podman. The current static Linux server/admin retain the 76 Git/API/project-creation and 36 recovery assertions. [Static systemd evidence](validation/2026-09-19-static-systemd.md) records the explicit NativeAOT notification path and complete WSL rerun.

`src/Graft.Operations` now owns the offline maintenance lease, SHA-256 backup manifest, SQLite snapshot, safe repository-tree copying, verification, restore and Git bundle inventory. `src/Graft.Admin` exposes `backup`, `verify`, `restore` and `inspect-bundle` commands and publishes under NativeAOT. The server holds exclusive database and repository leases for its lifetime, so maintenance fails while it is running.

The import evidence is deliberately repository-only. A C# bundle parser inventories branches/tags and emits eight explicit excluded metadata categories. Git 2.53.0.windows.2 imports the repository through the supported HTTPS route; repeating the import preserves the same refs and object IDs and leaves the source unchanged. This is not a GitLab API/export metadata importer.

Backups use a consistent SQLite backup plus the complete repository tree. Every file and directory is inventoried; files are hashed. Links/reparse points, undeclared paths, tampering, live-server backup and existing restore targets are rejected. Restore verifies before publishing fresh targets and removes a database published before a later failure. The rollback rehearsal uses a synthetic incompatible schema change and a second fresh restore. Online backup, cross-process clusters and power-loss atomicity across separate database/repository paths remain outside the evidence.

The main release gates are the durable DnaX package source, a real CA/DNS/client-trust environment and integrated static-musl execution on the required Linux 3.10.108 host. The user deferred the legacy-host run for now. Windows Service, local operator-managed certificate loading, restart/replacement, systemd and container secret plumbing pass. A GitLab 18.0.0 source-backed project-creation fixture now passes; live reference-instance, pinned-client and interactive production-browser execution remain open.

Private project creation now passes through both the Blazor session API and the bounded GitLab-style REST route. The write route requires `api`, creates an empty durable repository through pending/ready publication, and rejects duplicates, invalid paths, unsupported visibility, and unsupported fields. A pinned GitLab 18.0.0 source fixture added path-only creation and raises the combined Windows JIT and NativeAOT harness to 87 assertions; see [source-fixture evidence](validation/2026-09-19-gitlab-18-source-fixture.md).

No remote is configured. Do not deploy, publish, import production data, expose a network listener or select repository visibility without explicit authorization.

## Next work

Keep the recorded static-musl server/admin pair available for the deferred Linux 3.10.108 run. For GitLab compatibility, the next evidence step is an authorized GitLab 18.0 reference instance plus a pinned `glab` executable; neither is installed or configured by this repository. Validate the intended CA-issued certificate, DNS and client trust chain before non-loopback deployment.

Continue to use the ignored reproducible DnaX feed until the user or DnaX maintainer selects an approved durable package source. Do not substitute a server-side Git executable or native Git library.

## Follow-on queue

| Work | Owner | Acceptance |
| --- | --- | --- |
| Deployment TLS trust | Operator + implementation agent | Intended CA certificate, DNS/SAN, client trust and renewal rehearsal |
| Required legacy host | Implementation agent + user | Integrated static-musl artifact inspected and run on Linux 3.10.108 with sanitized evidence |
| GitLab live/client fixture | Implementation agent | Run a GitLab 18.0 reference instance and pinned `glab`; compare full wire responses with the existing source-backed fixture |
| DnaX distribution | User/DnaX maintainer | Clean checkout restores without a sibling source tree or locally prepared feed |

Recommended next action: Owner: User — provide or authorize an isolated GitLab 18.0 reference and pinned `glab` client for live comparison. Owner: Implementation agent — run the source-backed cases against both services and record wire deviations. Remaining work: deployment CA/DNS trust, DnaX distribution, live GitLab/client comparison, interactive browser execution and the deferred Linux 3.10.108 MVP-08 gate.
