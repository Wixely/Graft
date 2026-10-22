# Graft

<p align="center">
  <img src="docs/assets/graft-readme-blue.png" alt="Graft" width="600" />
</p>

Graft is a planned locally hosted GitLab alternative in C#/.NET 10. It targets GitLab APIs, URL conventions, applications and CI/CD, with simpler operations, permissions and runners, low resource use, and GitLab data import.

Status as of **2026-09-20**: [MVP-08 packaging and resource validation](docs/validation/2026-09-19-mvp08-packaging-resources.md) conditionally passes. Windows NativeAOT interactive and Windows Service, Linux static-musl interactive/systemd, and rootless Podman paths are implemented and tested; the recorded Linux static binaries retain all 76 Git/API/project-creation and 36 recovery assertions. That Windows Git/API/browser workflow passed **135 assertions** under the runtime and NativeAOT at the time, including [administrator user creation and project membership](docs/validation/2026-09-19-user-membership.md), [personal access-token lifecycle](docs/validation/2026-09-19-personal-access-tokens.md), and [account credential lifecycle](docs/validation/2026-09-20-account-credentials.md). The GitLab 18.0.0 source fixture passes against Graft and a [live reference](docs/validation/2026-09-19-gitlab-18-live-reference.md), while pinned `glab` 1.58.0 passes against both. The rendered Blazor workflow and one-use [operator bootstrap](docs/validation/2026-09-19-operator-bootstrap.md) pass. The Linux 3.10.108 run is deferred. DnaX distribution and a real CA/DNS trust environment remain gates. Remote hosting and the project licence are undecided. The repository itself is public at github.com/Wixely/Graft.

Status as of **2026-10-22**, later the same day: the review journey works end to end. The merge request
page renders the diff rather than only listing which files changed. A comment can sit on a line, and when
the branch is pushed to afterwards it is reported as written against an earlier version rather than
silently re-anchored to whatever now occupies that line number. A project can require approvals, which
count only while the commit they were given against is still the one being merged, so a push supersedes
them; an author cannot approve their own. Branches can be created and removed without a Git client, and a
merge can take the source branch with it.

A project can also ask to be told. `--webhooks true`, off by default, lets a Maintainer register a hook for
pushes, issues, merge requests and comments; each delivery carries an HMAC over its exact body so a
receiver can tell a real one from anything else that found its address. Turning the switch on lets a
project Maintainer make the server call an address of their choosing, which is why it is the operator's.
See [ADR 0015](docs/decisions/0015-review-journey.md), [ADR 0016](docs/decisions/0016-webhooks.md) and
[the evidence](docs/validation/2026-10-22-review.md).

Status as of **2026-10-22**: Graft hosts the conversation about the code, not just the code. A project now
has issues, merge requests and comments on both, numbered from one within that project. Reading them
follows the repository, so a public project's issues are readable by a stranger; writing on them needs
membership, because a public project that accepted anonymous writing would be a spam target and there is no
moderation to fall back on.

Merging is narrower than other forges and says so rather than guessing. It fast-forwards where the target
branch is an ancestor of the source, writes a two-parent merge commit where both branches moved but touched
different files, and refuses where both changed the same file, naming those files so the author can merge
locally. It does not merge file contents, because a forge that produced a silently wrong merge would be
worse than one that admits the limit. Each proposal reports which of those three it is in before anything
is merged. Real Git fetches the result, finds two parents and passes `git fsck --strict`.

Two smaller absences closed with it. A project can be deleted, by its owner or an administrator, with the
path typed back in the browser and irreversibly; and the audit trail, written on every sensitive act since
the first migration, can finally be read by an administrator. Nine new agent tools bring the Model Context
Protocol surface to 28; deleting a project is deliberately not one of them. See
[ADR 0013](docs/decisions/0013-project-deletion-and-audit.md),
[ADR 0014](docs/decisions/0014-collaboration.md) and
[the evidence](docs/validation/2026-10-22-collaboration.md).

What Graft still does not do is now written down in one place rather than discovered: see
[the backlog](docs/backlog.md).

Status as of **2026-10-21**: a personal access token's secret never enters the page. Creating one returns a
single-use handle rather than the secret; pressing Copy claims it with one request and writes it straight
to the clipboard. A token nobody copies therefore never reaches the browser, and a copied one is never in
the document for a screenshot or an extension to find. The secret waits in server memory for five minutes
and one read, never on disk, and is shown only if the clipboard refuses, because losing a token that cannot
be produced again is worse. Separately, an operator can now switch on
`--recoverable-tokens`, which keeps each new token's secret encrypted so its owner can copy it again with
their account password. Off by default, because with it on the database and its key ring together are every
token on the instance; see [ADR 0012](docs/decisions/0012-recoverable-tokens.md).

Status as of **2026-10-20**: the project page is now what it was always meant to be. A `.graft/project.json`
in the repository says what the page offers, a file allowlist decides which files are reachable, and a
folder in the repository can be served as a static website, GitHub Pages style, from a hostname of its own
behind `--pages-origin`. Everything is deny by default: no allowlist means no files, and nothing outside the
site folder is reachable at all. A page still cannot clone, show history or show a diff, because those walk
the object graph and no filter makes a graph walk honour a path allowlist. See
[project pages](docs/project-pages.md), a [worked sample](samples/project-page/SAMPLE.md) and
[the evidence](docs/validation/2026-10-20-project-pages.md).

Graft also explains itself to agents, so one need not learn the forge by failing. The Model Context
Protocol handshake carries a short overview, a `graft_help` tool serves topics on demand, and `/llms.txt`
publishes the whole guide unauthenticated for anything that reads a URL rather than speaking the protocol.
All three read the same text, because a guide that drifts between surfaces is worse than none.

Status as of **2026-10-19**: agent access is at parity with what a credential can do. The gap was never
missing tools: almost every mutating route accepted a browser session and nothing else, so no token could
edit a description, write a file or grant a role. Six capabilities were opened to tokens as GitLab-shaped
routes, taking the tool set to eighteen, and the browser gained the name and description editing the
product itself never had. Four capabilities stay deliberately out of reach of any agent: account
administration, password resets, token issue and revocation, and the agent switches themselves. See
[ADR 0011](docs/decisions/0011-agent-parity.md) and
[the evidence](docs/validation/2026-10-19-agent-parity.md).

Status as of **2026-10-19**: every project now has a second address with no account name in it,
`/projects/<slug>`, so a link can be shared without naming its owner. Browsing, cloning and pushing all work
through it and the owned `/<owner>/<slug>` path is unchanged. The slug is assigned automatically from the
project's own path, gaining a short random suffix when that name is already taken instance-wide, and
existing projects are given one on upgrade. It is an alias and nothing more: the same permission decision,
the same refusals, and a concealed project answers identically through either address. See
[ADR 0010](docs/decisions/0010-project-slugs.md) and
[the evidence](docs/validation/2026-10-19-project-slugs.md).

Status as of **2026-10-16**: the Model Context Protocol endpoint is part of the forge. `--mcp true`, or
`GRAFT_MCP=true` in the container deployment, serves it at `/mcp` on the same port and certificate as
everything else; nothing else about a deployment changes, and the compose profile, second port and
plain-HTTP overlay are gone. Once on, an administrator decides in the browser, under Administration, Agent
access, whether agents are served, whether they may change anything, and whether callers with no token are
served at all, and each change applies to the next request with nothing restarted. The tools are the same
ones and still reach Graft over the REST API with each caller's own token, so an agent can do exactly what
that token could do with curl. What made this possible is that the MCP SDK turned out to publish NativeAOT
cleanly, on its own and with no warning inside the forge, which [ADR 0008](docs/decisions/0008-mcp-layer.md)
had named as the thing to wait for. The same first native publish of the forge in weeks found that the
upload transport, not MCP, stops the native forge from starting, and that one MSBuild property in that
package fixes it: rebuilt that way, the forge publishes native with no warnings and passes all 82 MCP and
389 Git assertions as a native binary. Releasing that package is the next action. See [ADR 0009](docs/decisions/0009-mcp-in-the-forge.md) and
[the evidence](docs/validation/2026-10-16-mcp-in-the-forge.md).

Status as of **2026-10-13**: the Model Context Protocol server ships in the standard container image and
starts under a compose profile, so enabling agent access is a restart rather than a rebuild and an operator
who wants none has nothing listening. It carries no credential of its own: every caller sends their own
Graft token, and a caller presenting none is refused unless an operator explicitly allows that. A second
overlay serves the endpoint over plain HTTP for an internal network, which also has to be asked for. The
reference compose file is hardened by default and every posture was verified in a real container runtime;
see [deployment evidence](docs/validation/2026-10-13-mcp-in-the-deployment.md).

Graft also ships the same tools as a separate launcher, `src/Graft.Mcp`, speaking stdio for clients that
start their MCP server locally, and Streamable HTTP on its own. It reaches Graft through the ordinary REST
API with a personal access token, so an agent can do exactly what that token's owner can do and nothing
more, and mutating tools are refused unless it is started with writes allowed. Two settings that were
previously browser-only gained token equivalents to support it, one of which is a route GitLab already has.
See [ADR 0008](docs/decisions/0008-mcp-layer.md), partly superseded by ADR 0009 above.

Status as of **2026-10-13**: signing in now lasts. A session was expiring fifteen minutes after sign-in
whatever you were doing, because the expiry was set once and never moved; it is now an idle window renewed
by use, 12 hours by default via `--session-minutes`, with a 30 day ceiling from sign-in.

Status as of **2026-10-13**: a fix worth calling out on its own. Every push larger than one mebibyte had
been failing over HTTP, because Git settles authentication with an empty probe request before streaming a
body it cannot replay, and Graft answered that probe with an error. Small pushes fit in the client's buffer
and were never probed, which is why it went unnoticed. A lone flush packet is now answered with an empty
result, as Git's own server does, and the suite pushes an incompressible two-mebibyte repository to keep
that path exercised.

A push to a path holding no project can also create one, behind `--push-to-create`, off by default. The
project is created private, owned by the pusher, in the pusher's own namespace only. See
[ADR 0007](docs/decisions/0007-push-to-create.md).

Status as of **2026-10-13**: a project can now be read by someone with no account. Visibility is stored for
the first time, as `private`, `unlisted` or `public`, and a public or unlisted repository browses and clones
with no credentials. A public one is listed and searchable on an `/-/explore` page that anonymous visitors
reach. The four executable suites pass **389**, **129**, **41** and **36** assertions. See
[visibility evidence](docs/validation/2026-10-13-visibility-and-public-browsing.md) and
[ADR 0006](docs/decisions/0006-project-pages-and-public-browsing.md).

Access resolves to exactly one grant, and the grant carries its own reach: membership, then the repository's
visibility, then a published project page, then nothing. The first that applies wins, so "the repository's
settings take precedence over the page's" is a property of the order rather than of a pile of flags. A
published page can never offer clone, history or a diff, because all three walk the object graph and a pack
is reachability-shaped rather than path-shaped. Publishing is also an operator switch, off by default, and
with it off a stored `public` is inert rather than merely unlisted, so a database restored onto a host whose
operator never allowed publishing does not start serving strangers.

The curated file surface that a project page is meant to carry, configured from a `.graft` folder in the
repository, is designed in ADR 0006 and **not yet built**. A published page currently shows a project's name,
description and avatar and no repository content.

Status as of **2026-10-09**: the work below is merged to `main`.
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
