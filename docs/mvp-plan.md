# MVP build plan

Date: 2026-09-18. Review: 2026-09-25 or before implementation, whichever comes first.
Owner: Implementation agent. Status: MVP-01 through MVP-07 pass within their recorded scopes. MVP-08 conditionally passes across Windows interactive and Windows Service, Linux static-musl interactive, systemd and rootless Podman; operator-managed TLS passes locally across those modes. Deployment CA/DNS trust and mandatory Linux 3.10.108 execution remain open. See [MVP-08 evidence](validation/2026-09-19-mvp08-packaging-resources.md), [TLS evidence](validation/2026-09-19-operator-tls.md) and the linked earlier evidence.
Source: User request, "Prepare to build an MVP of this product. fully c#".

## Outcome and boundaries

Deliver a small, usable private Git forge: an administrator creates users, a user creates a private project, grants another user access, pushes an existing repository over HTTPS, and browses its files and history. An ordinary Git client clones/fetches/pushes, and a narrowly specified GitLab API client discovers the same user and project. Restart and restore preserve repository content and permissions.

All Graft application logic, browser behavior, Git implementation, importer logic and tests are planned in C#/.NET 10. Use Razor components and plain CSS for the Blazor UI. The working interpretation of "fully C#" excludes a server-side Git executable and libgit2; confirmation of that dependency boundary remains open. Existing native platform libraries (SQLite, TLS, ICU, libc and .NET runtime components) remain explicit dependencies. Blazor supplies framework JavaScript; this proposal adds no custom JavaScript application or Node/Python pipeline. A literal requirement for every dependency to be C# would conflict with the current proven stack and must be resolved before implementation.

This is a repository-hosting MVP, not GitLab replacement parity. Issues, merge requests, CI execution, runners/CupriNet, registries, LFS, SSH, OAuth/SSO, webhooks, groups/subgroups, public projects and full GitLab import are subsequent releases. Preserve these goals in requirements. Do not show inactive features as working UI controls.

## Proposed architecture

See [ADR 0004](decisions/0004-csharp-mvp-proposal.md) and [investigation evidence](validation/2026-09-18-mvp-preparation.md). One C# server owns authorization, metadata and repository operations. A standalone Blazor WebAssembly client calls it over the same origin. The API server uses NativeAOT; it does not execute Razor components. Browser WebAssembly AOT is a separate, optional optimization after measuring UI needs and checking its toolchain against the tooling restriction.

Proposed projects, created only as their slice needs them:

| Project | Responsibility |
| --- | --- |
| `src/Graft.Server` | Top-level host, Minimal API routes, static client delivery, auth adapters, service lifecycle |
| `src/Graft.Core` | Users/projects, effective permissions, use cases, operation limits |
| `src/Graft.Storage` | SQLite, project-owned DnaX manifest, Dapper.AOT queries |
| `src/Graft.Git` | Managed object storage, refs, packs and smart HTTP protocol |
| `src/Graft.Client` | Standalone Blazor WebAssembly UI, Razor/CSS, accessible navigation |
| `src/Graft.Contracts` | Explicit request/response DTOs; no database or server secrets |
| `tests/Graft.Tests` | Domain, storage and malformed-input tests in C# |
| `tests/Graft.Acceptance` | C# harness exercising published server; external Git used only as a test client |

Use explicit registrations and source-generated JSON metadata. Preserve Dapper.AOT annotations and inspect interception at each added query. Use the probe's pinned dependencies initially; select and lock a tested DnaX package before adoption rather than referencing a sibling checkout in the delivered build.

## Data and authorization design

Initial entities: users, personal namespaces, projects, direct project memberships, token digests, browser sessions and audit events. Use immutable numeric IDs internally, normalized unique names for routes and opaque storage IDs for directories. Never construct storage paths from unchecked namespace, ref or file inputs. Repository refs and objects are authoritative on disk; SQL stores project metadata, not a competing ref database.

Private projects only. Proposed direct roles: Reporter (read), Developer (read/push unprotected refs), Maintainer (manage project and membership). Personal namespace ownership is explicit; administration is a separate capability. Deny by default and show the role/source explaining each effective permission. Use one server authorization path for UI, REST and Git. Define and test owner/admin behavior before exposing routes; hiding a UI button is not authorization.

Bootstrap the first administrator locally with a one-use flow. No public signup or default credentials. Use a versioned password-hash format and platform cryptography, expiring/revocable random personal tokens stored only as digests, and redacted auditing. Define GitLab `read_api`, `api`, `read_repository` and `write_repository` semantics against fixtures; scopes cannot override membership. For browser login, evaluate an AOT-compatible, server-revocable HttpOnly/Secure cookie flow with CSRF protection. Authentication is a publish gate, not assumed compatible from the HTTPS probe. Never store API tokens in browser persistent storage as a shortcut.

Use local filesystem storage, one writer process and bounded per-repository write locks initially. Stage and validate a received pack before publishing any refs. Compare old ref values under the lock; failed validation must leave refs unchanged. Define the object-before-ref flush/rename sequence and recovery of abandoned staging data. Do not assume SQL transactions make filesystem writes atomic. Project creation needs a recoverable pending/ready state so a crash cannot expose a half-created repository.

## Work packages and exit criteria

MVP-01 through MVP-07 are complete within their recorded bounded scopes. MVP-08 is conditionally complete: its packaging is implemented and the available local modes pass, while elevated Windows Service and required-host execution remain external acceptance gates. MVP-03 adoption remains held because the tested DnaX package is not available from an approved durable feed. Owner: Implementation agent unless noted. Work is local with synthetic data; target-host execution is a later user-coordinated acceptance activity, not permission to deploy.

| ID | Work and dependency | Observable exit criteria |
| --- | --- | --- |
| MVP-01 | Managed Git feasibility; PASS on Windows | [Evidence and conditional go recommendation](validation/2026-09-18-managed-git.md): Git 2.53.0.windows.2 push/clone/fetch, branches/tags, object-ID comparison, strict fsck, 42 direct checks and HTTP rejection tests pass in JIT and NativeAOT. No server Git process/native Git library. Required-host and production gates remain. |
| MVP-02 | Blazor/static delivery and auth feasibility; PASS on Windows | [Evidence](validation/2026-09-18-ui-nativeaot.md): isolated NativeAOT EXE embeds client assets; browser boot/login/refresh/logout pass; 50 HTTP assertions plus session checks cover MIME/compression/caching, route isolation, cookies, CSRF and revocation. Payload measured; no browser-native workload installed. Required-host and production auth remain unverified. |
| MVP-03 | DnaX data feasibility; technical PASS, adoption HOLD | [Evidence](validation/2026-09-19-storage-migrations.md): all migration cases pass JIT, Windows NativeAOT and local static-musl NativeAOT; all eight Dapper calls are intercepted. Exact package and hashes are locked. Clean NuGet.org restore fails because the DnaX packages are unpublished; durable-feed selection and required-host execution remain before adoption. |
| MVP-04 | Accounts/projects/membership; core PASS on Windows | [Evidence](validation/2026-09-19-mvp04-accounts-permissions.md): 66 JIT/NativeAOT assertions cover bootstrap, login/logout, persistence, private projects, roles/scopes, explanations, cross-project denial and immediate revocation across modeled REST/browse/fetch/push actions. All 26 Dapper call locations are intercepted. Real HTTP/Git adapter enforcement is an MVP-05 gate. |
| MVP-05 | Durable Git HTTPS; bounded Windows integration PASS | [Evidence](validation/2026-09-19-mvp05-durable-git.md): authenticated empty/populated clone, push/fetch, restart, fsck, stale-ref rejection, revocation, cross-project denial and injected interruption before ref publication pass in JIT and NativeAOT. Fixed feasibility limits remain; actual disk exhaustion, configurable quotas, production TLS, service/multi-process operation and required-host execution are release gates. |
| MVP-06 | Repository UI and GitLab API subset; bounded Windows integration PASS | [Evidence](validation/2026-09-19-mvp06-repository-api-ui.md): private project discovery, branches, commit history, tree and safe text/binary reads pass through REST and browser-session endpoints in JIT and NativeAOT. A later [GitLab 18.0 source fixture](validation/2026-09-19-gitlab-18-source-fixture.md) drives project-creation cases and raises the workflow to 87 assertions. The fixture passes a [live GitLab CE 18.0.0 reference](validation/2026-09-19-gitlab-18-live-reference.md), and pinned `glab` 1.58.0 API workflows pass against both that reference and [Graft](validation/2026-09-19-glab-graft.md). Interactive browser execution remains open. |
| MVP-07 | Recovery/upgrade and import rehearsal; bounded Windows integration PASS | [Evidence](validation/2026-09-19-mvp07-import-recovery.md): C# bundle inventory and explicit exclusion report, repeatable Git HTTPS import with source/ref verification, server-held maintenance leases, hashed SQLite/repository backup, tamper rejection, fresh-target restore and synthetic rollback all pass in JIT and NativeAOT. Live/online backup, production GitLab export metadata and in-place overwrite restore are deliberately unsupported. |
| MVP-08 | Packaging and resource validation; conditional PASS | [Evidence](validation/2026-09-19-mvp08-packaging-resources.md): Windows NativeAOT interactive and Windows Service, Linux static-musl interactive, systemd notify and rootless Podman lifecycle pass; Git/API and recovery suites pass on static Linux binaries. [Operator TLS evidence](validation/2026-09-19-operator-tls.md) covers PKCS#12/password separation, restart and replacement. Deployment CA/DNS trust and Linux 3.10.108 execution remain required. |

No calendar delivery estimate is credible until MVP-01 resolves managed Git cost. A failed gate produces evidence and a revised proposal; it does not silently substitute native Git, CoreCLR on the legacy host, or another UI framework.

## Git protocol scope and integrity gates

Propose SHA-1 repositories and smart HTTP protocol v0 first. Accept ordinary client fallback without special per-client configuration. Start with a deliberately small advertised capability set; implement every advertised capability. Include pack v2 reading/writing, OFS/REF delta handling, existing-object bases for supported thin packs, checksum/object-ID validation, reachability checks and safe ref-name parsing. Do not advertise atomic push until the multi-ref transaction/recovery implementation is tested.

Set explicit configurable budgets for request bytes, expanded object bytes, object count, delta depth, CPU/time, concurrent transfers and disk usage before accepting untrusted packs. Exercise truncated packets, invalid lengths/hashes, missing bases, traversal, duplicate/stale updates, decompression bombs, cancellation and stalled clients. Record actual limits in release documentation. Reject unsupported shallow/partial clone, SHA-256 repositories and other out-of-scope negotiation explicitly; do not report a successful operation that dropped data.

Managed Git library adoption versus a small project-owned engine is still a feasibility result, not a settled estimate. Avoid implementing worktree checkout, merge algorithms or SSH in this gate. Test symlink and gitlink objects as repository data without following links or launching submodule actions on the server.

## MVP acceptance scenario

1. Start with empty application storage; bootstrap admin and two ordinary synthetic users.
2. First user creates a private project and sees their effective rights. Anonymous and second-user requests cannot disclose its contents.
3. Owner grants Developer access; second user pushes a synthetic multi-commit repository and tag with an ordinary Git client.
4. First user clones it; compare refs/object IDs and verify the clone. Browse history and text files in Blazor, including escaped hostile HTML content.
5. Exercise authenticated current-user, list-projects and single-project GitLab REST requests through a recorded client. Verify pagination, encoded paths and errors against selected fixtures.
6. Revoke membership/token and repeat REST and Git requests. Rejection must occur without data mutation.
7. Restart, back up, restore elsewhere and repeat content/permission checks. Interrupt a receive and migration; recover without exposing partial success.
8. Repeat the published application's workflow on each claimed platform. Required-host acceptance must use the new binary, not the historical probe result.

Collect binary/client size, startup-to-ready, idle and peak RSS/private bytes, representative API latency and clone/push throughput with repository shape, concurrency, hardware and build identity. Numeric performance budgets remain proposed after baseline measurement; do not claim improvement over GitLab without a comparable test.

## Open choices and next action

| Choice | Owner | Needed before |
| --- | --- | --- |
| Confirm whether fully C# excludes all native Git dependencies (working assumption: yes) | User | Selecting production Git dependency |
| Accept this repository-hosting MVP boundary; choose a different first workflow if needed | User | Broad application implementation |
| Pin actual GitLab version/edition and desired existing client | User + implementation agent | Conformance acceptance; provisional fixture proposal is in the matrix |
| HTTP API is central; whether opt-in MCP automation is useful | User | Later automation scope; MCP is deferred from this MVP proposal |
| Approve any tooling exception needed by WebAssembly AOT, if chosen | User | Installing/running restricted transitive tooling |
| Select an approved durable source for the tested DnaX packages (recommended: publish the tagged packages) | User/DnaX maintainer | Clean external checkout and release acceptance |

MVP-01 through MVP-07 pass and MVP-08 conditionally passes within its recorded matrix. Package distribution still prevents a clean external restore; deployment CA/DNS trust and required-host execution remain. Recommended next action: User + implementation agent run the required-host acceptance; Operator supplies the intended certificate/trust environment; User/DnaX maintainer authorizes a durable source for the tagged packages. No production migration, package publication or external integration is authorized by this plan.
