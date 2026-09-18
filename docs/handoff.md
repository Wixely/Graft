# Next-agent handoff

Updated: 2026-09-18. Review: at the start/end of the next work session. Owner: Next agent; product choices belong to the user.

## Current state

The user asked for a local repository with enough context for another agent to begin work. This repository contains the agreed direction, historical decisions, a runnable compatibility probe and a tested native executable path. Graft itself is not implemented. There are no remote credentials, configured remotes, hosted environments or production data here.

The key discovery is complete: a statically linked musl NativeAOT probe runs directly on the required Linux 3.10.108 x86_64 host and passes all six checks. The ordinary self-contained CoreCLR probe fails on that host's older glibc. Do not spend the next turn repeating the same failed CoreCLR approach or asking the user to upgrade the kernel/install Docker. Preserve the [evidence boundary](validation/2026-09-18-nativeaot.md).

## GRAFT-001 — define the first application slice

Status: Ready. Owner: Next agent. Due: next work session. Work locally; use synthetic data.

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

Recommended next action: Next agent begins GRAFT-001. No user action is required to repeat the unchanged compatibility test.
