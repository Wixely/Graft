# Next-agent handoff

Updated: 2026-10-13. Review: at the start and end of the next work session. Owner: Implementation agent; product choices belong to the user.

## Added on 2026-10-13, later

**Every push larger than one mebibyte had been failing, and now does not.** Git cannot replay a chunked
request body after a `401`, so before streaming one it settles authentication with a probe whose body is a
single flush packet. Graft answered that probe `400 Rejected: empty-update`, and the client gave up. Small
pushes fit in the client's buffer, go out with a content length, are never probed, and so always worked.
A lone flush packet is now answered with an empty result, as Git's own server does. See
[push-to-create evidence](validation/2026-10-13-push-to-create.md).

The suite had missed this for as long as every repository it pushed was a few kilobytes. It now pushes an
incompressible two-mebibyte repository at default client settings and reads it back. Removing the fix makes
that assertion fail, which is how it was confirmed to test something.

Two of the newer suite sections also restart the server, which moves its port, and were handing the stale
address back to the caller; a later assertion expecting a refusal was partly satisfied by nothing
listening. Both now return the live address.

**Pushing to a path that does not exist can create the project.** `--push-to-create true|false`, off by
default, recorded in [ADR 0007](decisions/0007-push-to-create.md). Created private, owned by the pusher,
and only in the pusher's own namespace, with no exception for administrators. Creation happens on the ref
advertisement rather than the pack upload, because the advertisement is the first request Git makes.

It is gated on being allowed to push rather than on the `api` scope the REST create route requires. That is
a deliberate concession: a `write_repository` token can create projects when this is on, which it cannot do
anywhere else. What it can create is a private project under its own account, which that person can already
make in the browser. An operator unwilling to accept that leaves the setting off.

Suites: 365 Git/API/browser, 113 accounts, 41 operations.

## Added on 2026-10-13

Merged to `main`. The Git/API/browser suite is now 340 assertions, accounts 113, operations 41.

**A project can be read by someone who has no account.** Visibility is stored for the first time: `private`,
`unlisted` or `public`. Before this there was no visibility column at all, only a request field validated
against the literal `"private"` and thrown away. A public or unlisted repository now browses and clones with
no credentials, and a public one is listed and searchable at `/-/explore`. See
[visibility evidence](validation/2026-10-13-visibility-and-public-browsing.md) and
[ADR 0006](decisions/0006-project-pages-and-public-browsing.md).

**Access resolves to one grant, and the grant carries its own reach.** `Member`, then `Repository` from
visibility, then `Page` from a published project page, then nothing. They resolve in that order and the first
that applies wins, so "repository settings take precedence over project page settings" is a property of the
order rather than of a set of interacting flags, and it can be checked by reading `ProjectAuthorization.Decide`.

Two consequences are load-bearing and neither is a limitation to be fixed later. **A page can never offer
clone, history or a diff**, because all three walk the object graph: a pack is reachability-shaped rather
than path-shaped, so no filter makes `git fetch` honour a path allowlist, and a commit diff would print files
the page never named. Every graph-walking surface therefore refuses a `Page` grant outright rather than
filtering it. And **a restrictive page over a public repository protects nothing**, which the settings panel
says where the choice is made, because the opposite is the natural assumption.

**Publishing is an operator switch**, `--public-browsing true|false`, default false. With it off a stored
`public` is inert, not merely unlisted: the server withdraws any decision whose grant is not `Member`. This
is about restore. A database published on one host must not start serving strangers when it is restored onto
a host whose operator never allowed it. Migration 4 defaults every existing project to private.

**Publishing is stored in the database, not in the repository.** Authoring `.graft` needs Developer access
and publishing needs Maintainer, so a config able to set visibility would let a Developer publish a private
project by pushing a commit.

### Open from this slice

The `.graft` config, the file allowlist and the custom static site are **designed in ADR 0006 and not
implemented**. A published page currently carries the project's name, description and avatar and no
repository content. That is what makes the "or publishes a page" branch of the listing real, and it is not
yet the curated file surface the request asked for.

| Item | Owner | Why it is open |
| --- | --- | --- |
| `.graft/project.json` and the file allowlist | Implementation agent | The curated surface itself. Deny by default, one bounded matcher, unknown fields and feature names are errors, an invalid config unpublishes |
| Custom static site | Operator + implementation agent | Needs a separate pages origin configured before a line of it is written. ADR 0006 explains why the main origin is not an option |
| Issues, releases and other page features | User | The page config can name features; none exist, so every name is currently an error |

## Added on 2026-10-04

Merged to `main`. This section previously said the branch was unmerged; it is not, and the branch is gone.

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

## Added on 2026-10-09

Merged to `main`. The Git/API/browser suite is now 302 assertions.

**A [commit page](validation/2026-10-09-commit-diff.md) shows what changed.** Every commit the API reports already carried a `/-/commit/<sha>` web
URL and nothing was behind it. `RepositoryDiff` compares two trees on the object model rather than by
invoking Git, with Myers line comparison, three lines of context, and caps for binary and oversized files.

**The diff is measured against Git, not assumed to match it.** `tests/Graft.Diff.Fuzz` generates file pairs
from a deliberately repetitive alphabet, because repeated and blank lines are what make a change's placement
ambiguous, and compares every result with `git diff`. Agreement is 99% against
`git diff -c diff.indentHeuristic=false` and 95% against Git's default. The gap between those two is Git's
indent heuristic, which this does not implement; it is in the queue below with a concrete acceptance test.

Writing that harness paid for itself three times. It found that a minimal diff is not enough to agree with
Git, which led to the two normalisation passes now in the engine. It then showed that an early version of
the alignment pass slid groups *partway* towards unrelated changes, which looked fine on the seeds it was
written against and was actually dragging edits off the right position; making the move all-or-nothing took
like-for-like agreement from 95% to 99%. And it corrected a claim made in a commit message along the way:
the residual is not Git choosing a different script of the same length, it is the indent heuristic.

**A clone menu** offers the HTTPS URL, a `git clone` command, a single-branch command for the branch in
view, and handoff to Visual Studio Code and Visual Studio through the handler URLs each registers. It is a
`<details>` disclosure so it needs no scripting to open, close or take focus; only copying does, and that
reports a refusal rather than claiming a copy that never happened.

**Still open and unchanged:** nothing has been pushed to `origin`. The demo fixture is disposable by design,
so anything pushed into it is destroyed when it stops.

## Added on 2026-10-08

Merged to `main`. This section previously said the branch was unmerged; it is not, and the branch is gone.

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

Project creation passes through both the Blazor session API and the bounded GitLab-style REST route. The write route requires `api`, creates an empty durable repository through pending/ready publication, and rejects duplicates, invalid paths, unsupported visibility, and unsupported fields. Since 2026-10-13 a project is created private and may then be made `unlisted` or `public` by a Maintainer, or created non-private directly, but only on a server whose operator enabled publishing. A pinned GitLab 18.0.0 source fixture added path-only creation and raises the combined Windows JIT and NativeAOT harness to 87 assertions; see [source-fixture evidence](validation/2026-09-19-gitlab-18-source-fixture.md) and [live-reference evidence](validation/2026-09-19-gitlab-18-live-reference.md).

An `origin` remote exists (github.com/Wixely/Graft) but nothing has been pushed. Do not deploy, publish, import production data, expose a network listener or select repository visibility without explicit authorization. Graft can now serve anonymous readers, so that caution covers starting a server with `--public-browsing true` on anything but loopback.

## Next work

Build the `.graft` config and the file allowlist, which is the half of the project-page request that is
designed and not yet written. Keep the recorded static-musl server/admin pair available for the deferred
Linux 3.10.108 run. Validate the intended CA-issued certificate, DNS, client trust and renewal path before
non-loopback deployment, which now matters more than it did: a public listing is only reachable by strangers
on a non-loopback address.

Continue to use the ignored reproducible DnaX feed until the user or DnaX maintainer selects an approved durable package source. Do not substitute a server-side Git executable or native Git library.

## Follow-on queue

| Work | Owner | Acceptance |
| --- | --- | --- |
| ~~Project ownership continuity~~ | Done 2026-10-04 | [Evidence](validation/2026-10-04-project-ownership.md): transfer is owner-or-administrator only, and an account owning projects cannot be disabled |
| Git's indent heuristic in the diff | Implementation agent | `tests/Graft.Diff.Fuzz` agrees with `git diff` at its default settings as closely as it already does with the heuristic turned off |
| `.graft` config and file allowlist | Implementation agent | A page serves exactly the paths its config names and 404s every other path, proved by a corpus where most files must not be reachable |
| Custom static site on a pages origin | Operator + implementation agent | A site serves only from the configured second hostname, only from under `site.root`, and the main origin serves no repository-authored HTML |
| Markdig under NativeAOT | User + implementation agent | `tests/Graft.Git.Acceptance` passes against a published NativeAOT server built where the C++ workload exists |
| Deployment TLS trust | Operator + implementation agent | Intended CA certificate, DNS/SAN, client trust and renewal rehearsal |
| Required legacy host | Implementation agent + user | Integrated static-musl artifact inspected and run on Linux 3.10.108 with sanitized evidence |
| DnaX distribution | User/DnaX maintainer | Clean checkout restores without a sibling source tree or locally prepared feed |

Recommended next action: Owner: Implementation agent — build `.graft/project.json` and the file allowlist, which is the remaining half of the project-page request and the only queued item that is fully buildable here. Worth pairing with a look at the Git transport for other requests Git makes that Graft has never been sent, since the chunked-push probe was one of those and went unnoticed for the life of the project. The user-gated items are unchanged: the Android picker needs a device and NativeAOT needs the C++ build tools. Remaining release gates: deployment CA/DNS trust, DnaX distribution and the deferred Linux 3.10.108 MVP-08 run.
