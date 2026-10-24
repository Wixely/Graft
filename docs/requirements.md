# Product requirements

Recorded: 2026-09-18. Source: explicit user requests and corrections during Graft discovery. Review: before first-slice scope approval and after any product-direction change.

## Confirmed

- On 2026-10-24 the user requested support for 4 GB repositories. The implemented quota is
  4 GiB of distinct uncompressed object payloads including history, with a 64 MiB per-object
  bound. See [ADR 0018](decisions/0018-repository-capacity.md) for related limits and validation.

- **Graft** is a locally hosted GitLab alternative written in managed C#/.NET 10.
- On 2026-09-18 the user requested MVP preparation, "fully c#". The [MVP plan](mvp-plan.md) uses C# application logic and proposes managed C# Git; the precise native dependency boundary remains an explicit open interpretation.
- Target GitLab-style applications, API operations and URL conventions. Include most GitLab functionality. GitHub compatibility is not required; all initial references to GitHub were corrected by the user.
- Support importing GitLab data.
- Reduce memory use and latency. Performance improvements must be measured; no numeric budget is chosen yet.
- Make permissions substantially easier to understand and manage.
- Make upgrades predictable and easy, avoiding manual day-long chains and late discovery of host incompatibility.
- Simplify runner setup/networking; investigate CupriNet integration.
- Use Dapper.AOT and attempt NativeAOT application publishing. Preserve the now-demonstrated standalone-executable path on the required old-kernel host.
- Use .NET 10's platforms as the general support baseline, while including the required Linux 3.10.108 x86_64 host without upgrading its kernel. See [acceptance](validation/2026-09-18-nativeaot.md).

The user's reported GitLab problems are product motivations: Ruby/kernel dependency issues (a randomness requirement was cited), resource consumption, runner complexity, upgrade pain and confusing permissions. No comparative benchmark or diagnosis of the original Ruby failure has been performed.

## Feature inventory to prioritize

| Area | Candidate scope under the broad GitLab goal |
| --- | --- |
| Git hosting | Repositories, refs, forks, Git HTTP/SSH, LFS, browser, diffs, blame, archives, search |
| Collaboration | Merge requests/reviews/approvals, issues, labels, milestones, epics, boards, discussions, wiki, notifications |
| CI/CD | `.gitlab-ci.yml`, templates/includes, stages/jobs, variables/secrets, artifacts/caches, logs, environments, schedules, deployment approvals |
| Distribution | Releases/assets, package feeds including NuGet, container registry, Pages-style hosting |
| Identity/access | Users, groups/subgroups, project membership, tokens, OAuth, SSH keys, audits, quotas, external identity options |
| Administration | Backup/restore, imports/exports, compatibility preflight, resumable migrations, health/diagnostics |
| Security | Dependency/advisory workflows and scanner integrations; scope and providers TBD |

This inventory is not a promise that every feature ships in the first release. Hosted AI/development environments and external services need explicit scope decisions.

## GitLab import requirements to refine

Cover repositories/refs/LFS, namespace/group structure, users/attribution/membership, issues, merge requests/reviews/comments, labels/milestones, wiki, uploads/releases/packages, CI files/history/artifacts, hooks and settings. Preserve source IDs and links where feasible. Explore both running-instance APIs/exports and offline formats; do not treat a GitLab backup as directly restorable into Graft.

Propose dry runs, resumable checkpoints, identity mapping, permission previews, content/count verification, reconciliation and an unsupported-data report. Keep source data intact. Credential/secret portability and CI executable semantics are separate from copying metadata/configuration files. Never silently broaden imported access or rewrite Git history.

## Unresolved product choices

| Question | Owner | Needed by |
| --- | --- | --- |
| Exact GitLab version/edition baseline and first existing clients | User; agent can prepare inventory | Conformance scope approval |
| UI approach consistent with NativeAOT and inherited Blazor preference | Agent proposal; user for material change | Application architecture |
| First-release feature boundary and memory/latency targets | Agent proposal; user priorities | First-slice review |
| Git backend choice and acceptable native Git dependency | Agent investigation; user if material tradeoff | Repository hosting implementation |
| Remote host, visibility, license | User | Any remote creation/publication |
| Name/domain/package availability | Agent | Branding investment/public release |

No license or hosting choice is implied by the name, GitLab compatibility, or the creation of this local repository.
