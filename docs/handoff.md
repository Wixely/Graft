# Next-agent handoff

Updated: 2026-10-08. Review: at the start and end of the next work session. Owner: Implementation agent; product choices belong to the user.

## Added on 2026-10-04

Work on branch `feat/ui-overhaul-and-uploads`, not merged to `main`.

**The browser UI was rebuilt** into a GitLab-shaped interface with light and dark themes and a routed page set under the reserved `/-/` prefix. See [UI evidence](validation/2026-10-04-ui-overhaul.md).

**Browser uploads exist for the first time.** `DnaX.Uploads` is mounted at `/api/graft/uploads` as the browser transport; API clients post ordinary bodies and do not use it. The split, the route prefix, the authentication bridge and the antiforgery key material are recorded in [ADR 0005](decisions/0005-browser-upload-transport.md) with [transport evidence](validation/2026-10-04-browser-uploads.md). Three consumers: Git bundle inspection, repository file commits and avatars.

**The server can author Git objects.** Blobs, trees and commits are written directly and are byte-identical to Git's own output, checked by comparing object ids against Git 2.53 and by cloning with real Git and running `git fsck --strict`. See [authoring evidence](validation/2026-10-04-server-authored-commits.md).

**Avatars** for users and projects, stored in the database so backups capture them atomically. Structural image validation without an imaging dependency, and the reason, in [avatar evidence](validation/2026-10-04-avatars.md).

**Project ownership can be transferred, and an account that still owns projects cannot be disabled.** This closes the orphaning gap that was the repository's own outstanding recommendation; see [ownership evidence](validation/2026-10-04-project-ownership.md). Transferring changes the project path, because a namespace is its owner's username.

Assertions now: 110 accounts/permissions, 165 Git/API/browser, 41 operations.

Two corrections to the earlier record. The solution did **not** build on the current toolchain before this work; two independent causes needed fixing together. And a remote **is** configured (`origin`, github.com/Wixely/Graft), contrary to the statement that stood here previously; nothing has been pushed.

### Open from this slice

| Item | Owner | Why it is open |
| --- | --- | --- |
| Android picker and backgrounding | User + implementation agent | The reason the DnaX transport exists, and it cannot be reproduced on desktop Chromium |
| NativeAOT runtime behaviour | Implementation agent | AOT analysis is clean, but no native binary could be produced: the C++ build tools are not installed here |
| Live backup from the web UI | Implementation agent | Feasible after all. `SnapshotDatabase` already uses SQLite's online backup API; the only blocker is `BackupManager.CreateAsync` acquiring a second maintenance lease the running server already holds. An earlier claim that this was architecturally impossible was wrong |
| Antiforgery keys in backups | Implementation agent | A restore onto a fresh host invalidates outstanding tokens; browsers recover, but the keys should travel with the backup |
| GitLab multipart avatar route | Implementation agent | Recorded as GL-011, not implemented |
| Pixel-level image sanitising | User | A licensing choice, not an engineering one; see the avatar evidence |

## Added on 2026-10-08

Work on branch `readme-rendering`, not merged to `main`.

**A repository README now renders on the project page.** This is the first place Graft turns repository content into markup rather than text, so it is the first place a stored cross-site scripting bug could live. Three layers hold it closed and each was proved by removing it and watching the suite fail: raw HTML is escaped rather than parsed, link and image destinations are judged on the parsed tree while still structured, and the output is checked against an allowlist of elements and attributes before it is sent. See [README rendering evidence](validation/2026-10-04-readme-rendering.md).

Images resolve against the repository or they do not load, which is stronger than stripping a referrer because no request to the third party is made at all. A proxy of the kind public forges run would be weaker here: it would mean this server fetching attacker-chosen URLs on a private network. Diagrams draw inside a sandboxed frame with an opaque origin and a policy of its own; the main document's policy is unchanged.

The Git/API/browser harness is now 221 assertions. Half the rendering corpus is hostile and must not survive, and half is ordinary markdown that must not be flattened, because a renderer that escaped everything would satisfy every attack case and still be useless.

**Attribution now travels with the binary.** Several bundled licences bind the binary rather than the source tree, so `THIRD-PARTY-NOTICES.md` is embedded in the server and reachable through `--licences` and `/-/licences`, covering Markdig, mermaid, Dapper, the Microsoft packages, DnaX, and the OpenSSL, ICU and SQLite that the static Linux build links in.

**One verification gap is open.** Markdig's behaviour under NativeAOT is unverified: this machine has no C++ Desktop workload, so ILC never runs. A trimmed publish crashes ILLink identically on the commit before this work, so that crash is environmental rather than caused by it. The new assertions sit inside the harness that is already run against published AOT binaries, so they will be exercised wherever AOT can be built.

## Current state

MVP-01 through MVP-07 pass within their recorded scopes. [MVP-08 packaging and resource evidence](validation/2026-09-19-mvp08-packaging-resources.md) conditionally passes Windows NativeAOT, Linux static-musl interactive/systemd, and rootless Podman. The current static Linux server/admin retain the 76 Git/API/project-creation and 36 recovery assertions. [Static systemd evidence](validation/2026-09-19-static-systemd.md) records the explicit NativeAOT notification path and complete WSL rerun.

`src/Graft.Operations` now owns the offline maintenance lease, SHA-256 backup manifest, SQLite snapshot, safe repository-tree copying, verification, restore and Git bundle inventory. `src/Graft.Admin` exposes `bootstrap`, `backup`, `verify`, `restore` and `inspect-bundle` commands and publishes under NativeAOT. The server holds exclusive database and repository leases for its lifetime, so maintenance fails while it is running.

`Graft.Admin bootstrap` creates the first administrator from a protected password file while holding both maintenance leases. The 41-assertion operations workflow proves live-server exclusion, login, administrator status, hashed-at-rest storage and one-use refusal with both managed and Windows NativeAOT administration binaries.

The authenticated product exposes administrator-only ordinary-user creation and account management, project-owner membership management, and user-managed personal access tokens. Users can change their password with the current password; administrators can list, reset, disable, and re-enable other accounts. Re-enable requires a fresh password and never restores older sessions or tokens. The current Git/API/browser harness passes 135 assertions under the runtime and Windows NativeAOT server. Rendered MCPHub Chromium also passes token and full account credential workflows. See [account credential evidence](validation/2026-09-20-account-credentials.md).

The import evidence is deliberately repository-only. A C# bundle parser inventories branches/tags and emits eight explicit excluded metadata categories. Git 2.53.0.windows.2 imports the repository through the supported HTTPS route; repeating the import preserves the same refs and object IDs and leaves the source unchanged. This is not a GitLab API/export metadata importer.

Backups use a consistent SQLite backup plus the complete repository tree. Every file and directory is inventoried; files are hashed. Links/reparse points, undeclared paths, tampering, live-server backup and existing restore targets are rejected. Restore verifies before publishing fresh targets and removes a database published before a later failure. The rollback rehearsal uses a synthetic incompatible schema change and a second fresh restore. Online backup, cross-process clusters and power-loss atomicity across separate database/repository paths remain outside the evidence.

The main release gates are the durable DnaX package source, a real CA/DNS/client-trust environment and integrated static-musl execution on the required Linux 3.10.108 host. The user deferred the legacy-host run for now. Windows Service, local operator-managed certificate loading, restart/replacement, systemd and container secret plumbing pass. A GitLab 18.0.0 source-backed project-creation fixture now passes against both Graft and a live GitLab CE 18.0.0 reference. Pinned `glab` 1.58.0 API workflows pass against both services. The production Blazor workflow now passes [rendered Chromium validation](validation/2026-09-19-browser-ui.md); that run found and fixed an invalid project-path HTML pattern.

Private project creation now passes through both the Blazor session API and the bounded GitLab-style REST route. The write route requires `api`, creates an empty durable repository through pending/ready publication, and rejects duplicates, invalid paths, unsupported visibility, and unsupported fields. A pinned GitLab 18.0.0 source fixture added path-only creation and raises the combined Windows JIT and NativeAOT harness to 87 assertions; see [source-fixture evidence](validation/2026-09-19-gitlab-18-source-fixture.md) and [live-reference evidence](validation/2026-09-19-gitlab-18-live-reference.md).

An `origin` remote exists (github.com/Wixely/Graft) but nothing has been pushed. Do not deploy, publish, import production data, expose a network listener or select repository visibility without explicit authorization.

## Next work

Keep the recorded static-musl server/admin pair available for the deferred Linux 3.10.108 run. Add project ownership transfer and prevent disabling a sole active project owner so account administration cannot orphan project management. Validate the intended CA-issued certificate, DNS, client trust and renewal path before non-loopback deployment.

Continue to use the ignored reproducible DnaX feed until the user or DnaX maintainer selects an approved durable package source. Do not substitute a server-side Git executable or native Git library.

## Follow-on queue

| Work | Owner | Acceptance |
| --- | --- | --- |
| ~~Project ownership continuity~~ | Done 2026-10-04 | [Evidence](validation/2026-10-04-project-ownership.md): transfer is owner-or-administrator only, and an account owning projects cannot be disabled |
| Git's indent heuristic in the diff | Implementation agent | `tests/Graft.Diff.Fuzz` agrees with `git diff` at its default settings as closely as it already does with the heuristic turned off |
| Markdig under NativeAOT | User + implementation agent | `tests/Graft.Git.Acceptance` passes against a published NativeAOT server built where the C++ workload exists |
| Deployment TLS trust | Operator + implementation agent | Intended CA certificate, DNS/SAN, client trust and renewal rehearsal |
| Required legacy host | Implementation agent + user | Integrated static-musl artifact inspected and run on Linux 3.10.108 with sanitized evidence |
| DnaX distribution | User/DnaX maintainer | Clean checkout restores without a sibling source tree or locally prepared feed |

Recommended next action: Owner: User + implementation agent — run the Android picker case on a device and publish NativeAOT on a machine with the C++ build tools, since both gate claims this work cannot make. Remaining release gates: deployment CA/DNS trust, DnaX distribution and the deferred Linux 3.10.108 MVP-08 run.
