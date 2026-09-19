# Next-agent handoff

Updated: 2026-09-19. Review: at the start and end of the next work session. Owner: Implementation agent; product choices belong to the user.

## Current state

MVP-01 through MVP-06 pass within their recorded Windows scopes. The latest [MVP-06 repository UI and API evidence](validation/2026-09-19-mvp06-repository-api-ui.md) records 64 assertions against both the managed server and the `win-x64` NativeAOT executable. The integrated slice includes authenticated Git push/clone/fetch, durable repository state, private project discovery, branches, commit history, tree and safe text/binary file reads, browser sessions, CSRF enforcement and revocation.

The production projects are `src/Graft.Core`, `src/Graft.Storage`, `src/Graft.Git`, `src/Graft.Contracts`, `src/Graft.Server` and `src/Graft.Client`. The server embeds the published Blazor WebAssembly client. Browser framework JavaScript is the only browser JavaScript; the repository has no Node or custom JavaScript build pipeline. The server remains NativeAOT compatible. Dapper.AOT generates interceptors for all 29 current application data-access locations.

Compatibility claims stay narrow. GL-001, GL-002, GL-003 and GL-005 plus the added branches, commits, tree and repository-file reads are tested by a C# HTTP harness against behavior derived from current official GitLab documentation. No version-matched GitLab CE 18.0 fixture or `glab` run exists. The browser assets, cookie flow, deep-link fallback and browser API are tested over HTTPS, but this slice has not executed the WebAssembly client in an interactive browser.

The main open release gates are the durable DnaX package source, production TLS and operations, static-musl publication of the integrated server, and execution on the required Linux 3.10.108 host. The server currently has one writer process, fixed feasibility limits and a private personal-namespace model. Do not broaden compatibility or platform claims without new evidence.

No remote is configured. Do not deploy, publish, import production data, expose a network listener or select repository visibility without explicit authorization.

## Next work

Begin MVP-07 with a synthetic repository-only import rehearsal. Produce an inventory and loss report that clearly excludes GitLab metadata, import via the supported Git path, verify refs and object IDs, repeat the import safely, and prove the source remains unchanged. Then add quiesced backup/restore of SQLite plus repository storage and exercise restore-based rollback around a migration preflight.

Retain the complete MVP-06 acceptance executable as a regression gate. Do not substitute a server-side Git executable or native Git library. Continue to use the ignored reproducible DnaX feed until the user or DnaX maintainer selects an approved durable package source.

## Follow-on queue

| ID | Work | Owner | Acceptance |
| --- | --- | --- | --- |
| MVP-07 | Recovery, upgrade and synthetic import rehearsal | Implementation agent | Repeatable repository import with loss report; consistent backup/restore; unchanged source; restore-based migration rollback |
| MVP-08 | Packaging and resource validation | Implementation agent + user | Windows interactive/service, Linux interactive/systemd, optional Docker and required-host checks recorded separately; resource baseline measured |
| GitLab fixture | Pin and compare an exact upstream/client version | Implementation agent | Version-matched fixtures and pinned `glab` workflow replace rolling-doc inference |
| DnaX distribution | Select an approved durable package source | User/DnaX maintainer | Clean checkout restores without a sibling source tree or locally prepared feed |

Recommended next action: Owner: Implementation agent — implement the bounded MVP-07 synthetic import and backup/restore acceptance flow. Remaining work: MVP-05 operational gates, exact GitLab fixtures, interactive browser execution, DnaX distribution, static-musl integrated publication, required-host acceptance and MVP-08 packaging.