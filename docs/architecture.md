# Architecture investigation

Status: proposals/open questions, except where tied to a confirmed decision. Recorded: 2026-09-18. Owner: Next agent. Review: GRAFT-001.

The current concrete recommendation is [ADR 0004](decisions/0004-csharp-mvp-proposal.md): a NativeAOT API/static-file host, standalone Blazor WebAssembly client, DnaX trial and managed C# Git feasibility gate. See [the MVP plan](mvp-plan.md) for scope and [research/build evidence](validation/2026-09-18-mvp-preparation.md) for what is verified. This is a proposal, including an explicit review of the inherited Blazor-host deployment rule; no full-stack compatibility is claimed.

MVP-02 [demonstrates the browser/API boundary on Windows](validation/2026-09-18-ui-nativeaot.md): standalone Blazor client assets embedded in a NativeAOT executable, with opaque revocable sessions, HTTPS and source-generated JSON. MVP-03 [demonstrates DnaX migrations with Dapper.AOT](validation/2026-09-19-storage-migrations.md) in Windows and local static-musl NativeAOT. DnaX package distribution and required-host execution remain gates before product adoption.

MVP-07 adds an offline operations boundary: the server holds exclusive database and repository maintenance leases for its lifetime; the NativeAOT `Graft.Admin` tool must acquire both before backup or restore. Backups contain a SQLite snapshot plus the repository tree and a SHA-256 manifest. Restore verifies before publishing into fresh targets and removes a partially published database on failure. This is a single-process quiesced design; online backup and clustered writers remain outside the demonstrated boundary.

MVP-08 keeps one host implementation across console, Windows Service and systemd lifetimes. Explicit HTTPS listener/origin arguments make service and container bindings deterministic. `/healthz` reports process readiness. Linux systemd uses `Type=notify`; the optional container runs the self-contained CoreCLR publication as a non-root user with a read-only root filesystem. The legacy-host path is a separate static-musl NativeAOT server/admin pair. Local lifecycle and static functional tests pass, while elevated Windows Service and Linux 3.10.108 execution remain acceptance gates.

TLS configuration uses an operator PKCS#12 file and a separate password file. The password is not accepted on the command line. Non-loopback binding requires the operator certificate, and startup validates the private key, validity interval, server-authentication EKU when present, and subject alternative name against the public origin. Certificates load at startup, so renewal uses a coordinated file replacement and service restart. systemd paths, container secrets and the static-musl import pass locally; CA/DNS trust remains an environment acceptance concern.

## Starting constraints

Use C#/.NET 10 and Dapper.AOT. Preserve the demonstrated static-musl NativeAOT path for the required Linux x64 host. The tested probe statically links native libraries: “managed solution” does not mean SQLite/TLS/libc disappear. Track those dependencies and their security-update/rebuild path.

## Proposed boundaries

- One authoritative domain/authorization layer for UI, GitLab-compatible APIs, imports, background tasks and any MCP tools. Keep compatibility serialization/routing at the boundary.
- A compact server with bounded queues and streaming objects/logs/assets. Add distributed components only after measured need.
- SQLite metadata store with project-owned migrations; Git objects and large binary assets likely separate files. Storage/backup atomicity and concurrency remain design work.
- An explicit repository backend abstraction only if it helps evaluate native Git versus managed alternatives. Avoid rebuilding Git protocols before reviewing fidelity and cost.
- A runner is a separate trust/execution boundary. Transport identity is not job authorization, and job leases do not provide exactly-once side effects.
- GitLab-compatible routes cannot be randomized for obscurity. Optional private admin/MCP surfaces can have separate exposure controls while preserving shared permission checks.

## Decisions still needed

| Boundary | Investigation |
| --- | --- |
| Browser UI and NativeAOT | Check current .NET 10 support. Blazor default remains relevant; server-side rendering/hosting must not be assumed compatible because minimal HTTP endpoints passed. Compare options and document tradeoffs. |
| Static native libraries | Keep full globalization; audit OpenSSL providers, certificate trust, DNS and library updates. The probe's embedded ICU fix is essential. |
| Database manifests | DnaX 10.0.0-alpha.7 passed the migration/AOT experiment. Adoption waits for a reproducible approved package feed and target-host execution. The application owns ordered migrations, preservation fixtures and restore policy. |
| Git backend | Compare correctness, licenses, footprint, old-kernel compatibility and standalone packaging; external Git is not yet authorized as a product dependency. |
| HTTP/API serialization | Confirm source-generated serializers and endpoint patterns work in a published binary. Do not disable warnings globally. |
| Authentication and permissions | Map GitLab token/membership semantics into a simple explainable model without broadening access. Avoid separate inconsistent auth implementations. |
| Windows build | Primary developer platform; the static-musl Linux approach does not specify Windows NativeAOT linking/packaging. |

A successful prototype is evidence for its exercised operations only. UI, Git, service hosting, real certificates, backup/recovery and actual clients must be tested independently.
