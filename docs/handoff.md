# Next-agent handoff

Updated: 2026-09-19. Review: at the start/end of the next work session. Owner: Next agent; product choices belong to the user.

## Current state

MVP-05 bounded integration update: [durable authenticated Git](validation/2026-09-19-mvp05-durable-git.md) passes with Git 2.53.0.windows.2 against both the JIT and Windows NativeAOT server. Authenticated empty clone, push, restart, populated clone/fetch, fsck, stale-ref rejection, live membership/token revocation, cross-project denial and injected interruptions before ref publication are covered. The managed protocol engine now lives in `src/Graft.Git`; `src/Graft.Server` connects it to SQLite authorization over loopback HTTPS. Operational, static-musl and required-host gates remain explicit.

MVP-04 core update: [accounts, projects and permissions](validation/2026-09-19-mvp04-accounts-permissions.md) pass 66 assertions in Windows JIT and NativeAOT. `src/Graft.Core` contains the shared authorization policy; `src/Graft.Storage` contains the DnaX/SQLite manifest and Dapper.AOT store. Persistence, roles, token scopes, cross-project denial and immediate revocation are covered. MVP-05 now connects the fetch/push actions to real Git transport; browser/API adapter work remains.

Latest MVP-03 update: [DnaX migration feasibility](validation/2026-09-19-storage-migrations.md) passes all six scenarios in Windows JIT, Windows NativeAOT and local static-musl NativeAOT; every application Dapper call has a generated interceptor. Adoption is held because the exact tagged DnaX packages are absent from NuGet.org/GitHub release assets and the new binary has not run on the required host. The next material package decision is an approved durable source; local MVP work currently uses the reproducible ignored package feed.

MVP-02 [Blazor UI/auth feasibility](validation/2026-09-18-ui-nativeaot.md) passes on Windows: an isolated NativeAOT executable embeds the published client and serves HTTPS; browser login/refresh/logout and 50 HTTP assertions plus direct session checks pass. No browser-native workload was installed. Sessions and data remain synthetic/in-memory.

Latest update: user authorized the recommended MVP-01 work. The [managed C# Git feasibility experiment](validation/2026-09-18-managed-git.md) passes 42 direct checks and Git push/clone/fetch/branch/tag/fsck workflows on Windows in JIT and NativeAOT. No server Git subprocess or native Git library is used. It is loopback HTTP with bounded in-memory storage, not durable or authenticated hosting. Next: MVP-02; all required-host and product acceptance work remains.

Update from the MVP preparation request on 2026-09-18: [MVP scope and work packages](mvp-plan.md), [proposed ADR 0004](decisions/0004-csharp-mvp-proposal.md), expanded [conformance matrix](compatibility.md) and [inspection/build evidence](validation/2026-09-18-mvp-preparation.md) are ready for review. The Windows probe build and all six checks passed. Fully C# is now explicit user direction; strict managed Git is the working interpretation. Material architecture choices remain proposed, and no app implementation was started.

The user asked for a local repository with enough context for another agent to begin work. This repository contains the agreed direction, historical decisions, a runnable compatibility probe and a tested native executable path. Graft itself is not implemented. There are no remote credentials, configured remotes, hosted environments or production data here.

The key discovery is complete: a statically linked musl NativeAOT probe runs directly on the required Linux 3.10.108 x86_64 host and passes all six checks. The ordinary self-contained CoreCLR probe fails on that host's older glibc. Do not spend the next turn repeating the same failed CoreCLR approach or asking the user to upgrade the kernel/install Docker. Preserve the [evidence boundary](validation/2026-09-18-nativeaot.md).

## GRAFT-001 — define the first application slice

Status: Reviewable proposal prepared; feasibility and architecture acceptance remain. Owner: Implementation agent. Review: 2026-09-25 or next work session. Work locally; use synthetic data. The original checklist below is retained as the preparation scope; unverified items are now explicit gates in the MVP plan.

1. Read the start-here documents and inspect `git status`. Build and run the existing probe using the runbook to establish that the new checkout is healthy.
2. Inspect .NET 10's actual NativeAOT/UI constraints and propose how a browser UI will coexist with the demonstrated standalone server. Evaluate the inherited Blazor default explicitly. A separate browser client served as static files is a candidate, not a selected architecture. Do not silently adopt a non-Blazor framework or Node pipeline.
3. Evaluate DnaX migrations against Dapper.AOT/static publishing. Record the smallest viable data/schema path, native dependencies and migration ownership. Do not let a convenience library reintroduce reflection or dynamic loading unnoticed.
4. Investigate the Git backend boundary: managed implementation versus an external/native Git component, protocol fidelity, licensing, size, AOT behavior and old-kernel requirements. No implementation has been selected.
5. Seed a GitLab versioned conformance matrix from real clients and official docs. The exact baseline/client list is still open; use a clearly labeled provisional fixture rather than inventing a confirmed user preference.
6. Write the recommended first-slice ADR and acceptance cases. A proposed slice is: create a local user/project, authenticate a supported GitLab client request, persist/query metadata, and clone/push a test repository. Keep import/runner/upgrade feasibility as subsequent bounded slices unless justified otherwise.

Completion: a concrete reviewable architecture/scope proposal, updated matrix and task list, justified dependency choices and any small local feasibility code with build/run evidence. Resolve ordinary reversible implementation details autonomously; surface material architecture/product choices. Do not treat every open question as a reason to halt useful independent work.

## Follow-on queue (proposed)

| ID | Work | Owner | Entry condition / acceptance |
| --- | --- | --- | --- |
| GRAFT-002 | Implement first application slice | Next agent | Architecture boundary settled; real published-binary test plus successful client workflow |
| GRAFT-003 | GitLab importer rehearsal | Next agent | Synthetic fixtures, dry-run/loss report, repeatable import, unchanged source |
| GRAFT-004 | Runner enrollment/job transport | Next agent | Evaluate CupriNet plus ordinary outbound transport; job auth, logs, cancellation, disconnect recovery |
| GRAFT-005 | Permission UX and upgrade/recovery | Next agent | Explain effective access; preflight before mutation; backup/restore and interrupted-migration rehearsal |
| GRAFT-006 | Packaging and performance budgets | Next agent + user | Measure baseline; Windows/Linux service and optional Docker acceptance; re-run on required host after relevant changes |

No full implementation, migration or deployment is claimed by completing repository setup. The user can launch the next agent directly in this repository with: “Read AGENTS.md and docs/handoff.md, then begin GRAFT-001.”

## Continuity and source of truth

Implementation decisions and work progress now belong here. The sibling PLAN checkout retains reusable preferences, original discovery history and private infrastructure knowledge. The original sibling `Graft.Compatibility` folder remains intact; its maintained probe source was copied into this repository. Use this repository for future probe edits, and clearly distinguish new artifacts from the already-accepted archive.

Recommended next action: Implementation agent begins MVP-06 repository browsing and the selected GitLab API subset, retaining the real Git/revocation/restart acceptance. User/DnaX maintainer selects an approved durable package distribution route for the tested tag. Remaining work: MVP-05 operational gates, DnaX distribution/required-host acceptance and MVP-06 through MVP-08.
