# Graft

<p align="center">
  <img src="docs/assets/graft-readme-blue.png" alt="Graft" width="600" />
</p>

Graft is a planned locally hosted GitLab alternative in C#/.NET 10. It targets GitLab APIs, URL conventions, applications and CI/CD, with simpler operations, permissions and runners, low resource use, and GitLab data import.

Status as of **2026-09-20**: [MVP-08 packaging and resource validation](docs/validation/2026-09-19-mvp08-packaging-resources.md) conditionally passes. Windows NativeAOT interactive and Windows Service, Linux static-musl interactive/systemd, and rootless Podman paths are implemented and tested; the recorded Linux static binaries retain all 76 Git/API/project-creation and 36 recovery assertions. That Windows Git/API/browser workflow passed **135 assertions** under the runtime and NativeAOT at the time, including [administrator user creation and project membership](docs/validation/2026-09-19-user-membership.md), [personal access-token lifecycle](docs/validation/2026-09-19-personal-access-tokens.md), and [account credential lifecycle](docs/validation/2026-09-20-account-credentials.md). The GitLab 18.0.0 source fixture passes against Graft and a [live reference](docs/validation/2026-09-19-gitlab-18-live-reference.md), while pinned `glab` 1.58.0 passes against both. The rendered Blazor workflow and one-use [operator bootstrap](docs/validation/2026-09-19-operator-bootstrap.md) pass. The Linux 3.10.108 run is deferred. DnaX distribution and a real CA/DNS trust environment remain gates. Remote hosting, visibility and project license are undecided. An `origin` remote exists but nothing has been pushed.

Status as of **2026-10-09**: the work below is merged to `main`. Nothing has been pushed to `origin` yet.
The three executable suites pass **302**, **110** and **41** assertions.

A [rebuilt browser UI](docs/validation/2026-10-04-ui-overhaul.md) with light and dark themes;
[browser uploads](docs/validation/2026-10-04-browser-uploads.md) through DnaX
([ADR 0005](docs/decisions/0005-browser-upload-transport.md)) for Git bundle inspection, repository file
commits and avatars; [server-authored Git objects](docs/validation/2026-10-04-server-authored-commits.md)
byte-identical to Git's own; [avatars](docs/validation/2026-10-04-avatars.md);
[project ownership transfer](docs/validation/2026-10-04-project-ownership.md); and a default branch resolved
from the repository rather than assumed to be `main`.

A project page now renders its [README](docs/validation/2026-10-04-readme-rendering.md). That is the first
place repository content becomes markup, so it is held closed by three layers, each proved by removing it
and watching the suite fail. Images resolve against the repository or do not load at all, which leaks less
than stripping a referrer because no request to the third party is made. Diagrams draw inside a sandboxed
frame with a policy of its own; the main document's policy is unchanged.

A [commit page](docs/validation/2026-10-09-commit-diff.md) shows what changed, behind the
`/-/commit/<sha>` links the API had been handing out with nothing behind them. The diff is computed on the object model rather than by invoking Git, and is measured
against Git rather than assumed to match it: `tests/Graft.Diff.Fuzz` generates file pairs and compares every
result, currently agreeing on **99%** of cases with `git diff -c diff.indentHeuristic=false` and **95%** with
Git's default output. The difference between those two figures is Git's indent heuristic, which Graft does
not implement; that is queued, not hidden.

A clone menu offers the HTTPS URL, ready `git clone` commands including a single-branch one, and handoff to
Visual Studio Code and Visual Studio. [Third-party notices](THIRD-PARTY-NOTICES.md) are embedded in the
server and served at `/-/licences`, because several of those licences bind the binary rather than the source
tree.

Still untested: the Android case that motivated the upload work needs a physical device, and NativeAOT
behaviour needs a machine with the C++ build tools.

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
| `src/Graft.Git/`, `src/Graft.Server/` | Managed Git engine (read, pack transport, server-side object authoring and unified diff), durable storage, markdown rendering and authenticated HTTPS/API host |
| `src/Graft.Client/`, `src/Graft.Contracts/` | Blazor WebAssembly client (themed UI, routed pages, rendered READMEs, commit diffs, clone menu, vendored DnaX upload and diagram modules) and source-generated wire contracts |
| `src/Graft.Operations/`, `src/Graft.Admin/` | Quiescence lease, backup/restore, import inventory and NativeAOT administration CLI |
| `deploy/` | systemd, optional container and isolated static-musl build definitions |
| `tests/Graft.Acceptance/` | Disposable MVP-04 persistence and authorization acceptance executable |
| `tests/Graft.Browser.Fixture/` | Disposable production UI fixture with a seeded Git repository |
| `tests/Graft.Git.Acceptance/` | Integrated MVP-06 real Git, repository API, browser-session, restart and revocation acceptance executable |
| `tests/Graft.Diff.Fuzz/` | Generated file pairs compared against real `git diff`; a measuring instrument for the diff engine, not a gate |
| `tests/Graft.Glab.Acceptance/` | Pinned existing-client API workflow against disposable Graft in WSL/Podman |
| `tests/Graft.GitLab.Reference/` | C# live-reference harness for the versioned GitLab REST fixture |
| `tests/Graft.Operations.Acceptance/` | MVP-07 synthetic import, backup, tamper, restore and rollback acceptance executable |
| `tests/Graft.Packaging.Acceptance/` | MVP-08 lifecycle, container and resource acceptance executable |
| `experiments/compatibility/` | Probe source, pinned NuGet dependencies and build scripts |
| `experiments/managed-git/` | Bounded C# smart HTTP/pack experiment; [runbook](experiments/managed-git/README.md) |
| `experiments/managed-git-client/` | C# acceptance harness using installed Git as an independent test client |
| `experiments/storage/` | DnaX/SQLite migration and Dapper.AOT probe; [runbook](experiments/storage/README.md) |
| `experiments/ui-server/` | NativeAOT API, embedded Blazor delivery and session probe; [runbook](experiments/ui-server/README.md) |
| `experiments/ui-client/`, `ui-contracts/`, `ui-checks/` | Standalone C#/Razor client, JSON contracts, HTTP and browser checks |
| `artifacts/reference/` | Ignored local copy of the accepted archive and checksum; not committed |
| `THIRD-PARTY-NOTICES.md` | Attribution for every redistributed component; embedded in the server and served at `/-/licences` |

The original discovery notes remain in the sibling [PLAN idea](../PLAN/inbox/local-git-forge.md). This repository is now the canonical implementation/handoff context. Private infrastructure details stay in PLAN; project docs use a neutral target-host description.

Recommended next action — Owner: Implementation agent: implement Git's indent heuristic in the diff, the one open item needing nothing but this repository. Owner: User + implementation agent: run the Android picker case on a device and publish NativeAOT where the C++ build tools exist. Owner: Operator + implementation agent: validate the intended certificate/DNS/client-trust environment. Owner: User/DnaX maintainer: provide an approved durable package feed. The Linux 3.10.108 run remains deferred by the user.
