# Architecture investigation

Status: proposals/open questions, except where tied to a confirmed decision. Recorded: 2026-09-18. Owner: Next agent. Review: GRAFT-001.

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
| Database manifests | Evaluate DnaX's generator/migration dependencies and provider adapters with AOT before selecting them. Application owns ordered migrations and restore policy. |
| Git backend | Compare correctness, licenses, footprint, old-kernel compatibility and standalone packaging; external Git is not yet authorized as a product dependency. |
| HTTP/API serialization | Confirm source-generated serializers and endpoint patterns work in a published binary. Do not disable warnings globally. |
| Authentication and permissions | Map GitLab token/membership semantics into a simple explainable model without broadening access. Avoid separate inconsistent auth implementations. |
| Windows build | Primary developer platform; the static-musl Linux approach does not specify Windows NativeAOT linking/packaging. |

A successful prototype is evidence for its exercised operations only. UI, Git, service hosting, real certificates, backup/recovery and actual clients must be tested independently.
