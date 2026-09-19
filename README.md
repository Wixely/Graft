# Graft

Graft is a planned locally hosted GitLab alternative in C#/.NET 10. It targets GitLab APIs, URL conventions, applications and CI/CD, with simpler operations, permissions and runners, low resource use, and GitLab data import.

Status as of **2026-09-19**: [MVP-08 packaging and resource validation](docs/validation/2026-09-19-mvp08-packaging-resources.md) conditionally passes. Windows NativeAOT interactive and Windows Service, Linux static-musl interactive, systemd and rootless Podman paths are implemented and tested; the Linux static binaries retain all 64 MVP-06 and 36 MVP-07 assertions. [Operator-managed TLS](docs/validation/2026-09-19-operator-tls.md) passes locally across those modes, including restart and certificate replacement. The integrated static binaries still require execution on the mandatory Linux 3.10.108 host. DnaX distribution, a real CA/DNS trust environment, a version-matched GitLab fixture and interactive browser execution remain gates. Remote hosting, visibility and project license are undecided. No remote is configured.

## Start here

1. Read [AGENTS.md](AGENTS.md).
2. Read [the next-agent handoff](docs/handoff.md), [requirements](docs/requirements.md), and [decisions](docs/decisions/README.md).
3. Consult [architecture questions](docs/architecture.md), [compatibility scope](docs/compatibility.md), and [integration candidates](docs/integrations.md).
4. Follow [the build and verification runbook](docs/build-and-test.md).

## What is already proven

A single Linux x64 NativeAOT executable, statically linked with musl, SQLite, OpenSSL and ICU, passed all six compatibility checks on the required **Linux 3.10.108 x86_64** host. Dapper.AOT query generation, full globalization and ASP.NET Core loopback HTTPS were exercised. No Docker, runtime installation, kernel upgrade or system-library replacement was required. [Acceptance evidence](docs/validation/2026-09-18-nativeaot.md) records the exact boundary.

The earlier self-contained CoreCLR build failed there because host libraries lacked `GLIBC_2.27`. Do not confuse single-file packaging, self-contained CoreCLR, Dapper.AOT generation and NativeAOT compilation.

## Run the existing experiment

From this repository using .NET SDK 10.0.300 or a compatible newer .NET 10 feature band:

```powershell
dotnet build Graft.slnx
dotnet run --project experiments/compatibility/Graft.Compatibility.csproj --no-build
```

This is a short-lived local probe, not a server startup command. It writes its own temporary database/files, opens a loopback-only HTTPS listener, reports PASS/FAIL and exits. VS Code's **Debug compatibility probe** launch configuration uses the same project.

Linux static NativeAOT requires the separate toolchain/runbook; a Windows build is not evidence for the old Linux host. No Python, Node.js or frontend asset pipeline is needed.

## Repository map

| Path | Purpose |
| --- | --- |
| `docs/` | Requirements, decisions, architecture questions, work queue and evidence |
| `src/Graft.Core/`, `src/Graft.Storage/` | MVP authorization policy and DnaX/SQLite persistence |
| `src/Graft.Git/`, `src/Graft.Server/` | Managed Git engine, repository read model, durable storage and authenticated HTTPS/API host |
| `src/Graft.Client/`, `src/Graft.Contracts/` | Blazor WebAssembly repository browser and source-generated wire contracts |
| `src/Graft.Operations/`, `src/Graft.Admin/` | Quiescence lease, backup/restore, import inventory and NativeAOT administration CLI |
| `deploy/` | systemd, optional container and isolated static-musl build definitions |
| `tests/Graft.Acceptance/` | Disposable MVP-04 persistence and authorization acceptance executable |
| `tests/Graft.Git.Acceptance/` | Integrated MVP-06 real Git, repository API, browser-session, restart and revocation acceptance executable |
| `tests/Graft.Operations.Acceptance/` | MVP-07 synthetic import, backup, tamper, restore and rollback acceptance executable |
| `tests/Graft.Packaging.Acceptance/` | MVP-08 lifecycle, container and resource acceptance executable |
| `experiments/compatibility/` | Probe source, pinned NuGet dependencies and build scripts |
| `experiments/managed-git/` | Bounded C# smart HTTP/pack experiment; [runbook](experiments/managed-git/README.md) |
| `experiments/managed-git-client/` | C# acceptance harness using installed Git as an independent test client |
| `experiments/storage/` | DnaX/SQLite migration and Dapper.AOT probe; [runbook](experiments/storage/README.md) |
| `experiments/ui-server/` | NativeAOT API, embedded Blazor delivery and session probe; [runbook](experiments/ui-server/README.md) |
| `experiments/ui-client/`, `ui-contracts/`, `ui-checks/` | Standalone C#/Razor client, JSON contracts, HTTP and browser checks |
| `artifacts/reference/` | Ignored local copy of the accepted archive and checksum; not committed |

The original discovery notes remain in the sibling [PLAN idea](../PLAN/inbox/local-git-forge.md). This repository is now the canonical implementation/handoff context. Private infrastructure details stay in PLAN; project docs use a neutral target-host description.

Recommended next action — Owner: User + implementation agent: run the integrated static-musl acceptance on Linux 3.10.108. Owner: Operator: provide the intended CA-issued certificate, DNS name and client trust environment. Owner: User/DnaX maintainer: make the tested DnaX packages available from an approved durable feed. Review [the deployment runbook](docs/deployment.md), [MVP plan](docs/mvp-plan.md) and [TLS evidence](docs/validation/2026-09-19-operator-tls.md).
