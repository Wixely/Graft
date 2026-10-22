# Next-agent handoff

Updated: 2026-10-22. Review: at the start and end of the next work session. Owner: Implementation agent; product choices belong to the user.

## Added on 2026-10-22, the forge gained a conversation

**Graft can now be asked what it does not do, and the answer is written down.**
[The backlog](backlog.md) is new, and it is the first place to look before proposing work. It came from a
review on 2026-10-22; the three things that review found most worth doing were done immediately and the
rest is in that file with reasoning and rough size.

**A project can be deleted.** The owner or an administrator, never a Maintainer, because managing a project
and ending it are different acts. The browser asks for the path to be typed back and the API does not, on
the grounds that a confirmation a script fills in automatically confirms nothing. The database row goes
first and the repository directory second, so a failure leaves an unreferenced directory the audit row
names rather than a project whose repository has gone. Deliberately absent from the agent surface, joining
the four capabilities ADR 0011 withheld. See [ADR 0013](decisions/0013-project-deletion-and-audit.md).

**The audit trail can be read.** It had been written on every sensitive act since the first migration and
readable by nobody without the SQLite file. Administrators only, newest first, filtered by exact action or
subject rather than searched, because a loose search lets somebody believe they have seen everything that
matched. A project-scoped view for a Maintainer is in the backlog and is not this.

**Issues, merge requests and comments exist.** Reading them follows the repository, so a public project's
conversation is readable by a stranger; writing needs membership, because a public project that accepted
anonymous writing would be a spam target and there is no moderation to fall back on. Both are numbered from
one per project. See [ADR 0014](decisions/0014-collaboration.md).

**Merging is narrower than other forges and says so.** It fast-forwards where the target is an ancestor of
the source, writes a two-parent merge commit where both branches moved but touched different files, and
refuses where both changed the same file, naming those files. It does not merge file contents and will not
pretend to; the full reasoning is in ADR 0014 and the short version is that a silently wrong merge is worse
than a refusal. `merge_method` reports which case a proposal is in before anything is merged, and a listing
deliberately declines to decide because deciding costs three tree walks a row.

**The merge commit is an ordinary Git commit.** Real Git fetches it, finds two parents, finds both sides'
changes in the tree, and passes `git fsck --strict`. That is asserted in the suite rather than assumed.

Nine new agent tools take the Model Context Protocol surface from 19 to 28. `graft_merge` is offered
because it cannot destroy anything; project deletion is not.

Migration 8 adds `Issues`, `MergeRequests` and `Comments`. Manifest version 8.

Suites: Git 483 -> 556, accounts and core 162 -> 168, agents 101, operations 41.

## Added on 2026-10-21, copying a token without putting it in the page

**A token's secret no longer travels with its creation response.** It waits in server memory, for one read
and five minutes, and the browser claims it with `POST /api/tokens/reveal` when the owner presses Copy. The
value goes straight to the clipboard and is never rendered, so it is not in the DOM, not in a screenshot,
and not reachable by anything that walks the document.

**A token nobody copies never reaches a browser at all**, which is the part that would be hard to retrofit
later. The creation response carries a one-time handle instead of a secret.

**And an existing token can be copied, where an operator asked for that.** `--recoverable-tokens true` keeps
each new token's secret encrypted, and its owner reads it back with their account password; off by default,
and a token issued while it was off can never be read back. The password is the decision that makes it
defensible: without it a stolen session would be every credential the account holds. See
[ADR 0012](decisions/0012-recoverable-tokens.md) and
[the evidence](validation/2026-10-21-recoverable-tokens.md).

**The handle is removed before the secret is written to the response**, so a dropped response loses the
secret rather than leaving a second claim behind. Losing it is the safe failure: creating another token
costs nothing, and the database only ever held a digest, so nothing could produce the old one anyway.

**The one case the secret is shown is when the clipboard refuses.** Then it is rendered with a warning and
an acknowledgement button, because the alternative is the reader losing a token they just made. That is the
honest fallback rather than a silent failure.

Memory only, never the database: a secret that survived a restart would make Graft a secret store, and it
is deliberately not one. Bounded at 512 uncollected secrets, with expiry pruned on each creation.

Git suite 477 -> 483.

## Added on 2026-10-20, Graft explains itself to agents

**An agent no longer has to learn the forge by failing.** One source, `GraftGuide` in
`src/Graft.McpTools`, feeds three surfaces: the protocol handshake carries a short overview as
`ServerInstructions`, `graft_help` serves six topics on demand, and `GET /llms.txt` publishes the whole
guide unauthenticated for anything that reads a URL rather than speaking the protocol.

**Same text everywhere, because they read the same constant.** A guide that drifts between surfaces is
worse than none, since an agent cannot tell which copy is stale.

**The overview is deliberately short**, because it is sent at the start of every session whether it is
wanted or not. It carries what changes behaviour: start with `graft_whoami`, the three ways to address a
project, that writes may be switched off entirely, that a refusal names what would permit the action, and
that a 404 is ambiguous by design. Depth lives in the topics.

`/llms.txt` is on the forge and not on the pages origin: it describes how Graft works, not what an instance
holds. The tool set is 19.

Suites: 477 Git/API/browser, 162 accounts and core, 41 operations, 101 MCP.

## Added on 2026-10-20, project pages and their sites

**The project page is now the thing ADR 0006 described.** It was a published flag and a listing entry: a
private project with a published page appeared in the public browser, and the link led to a repository view
that refused the reader. The config, the allowlist and the static site were designed and never built. They
are now, apart from `features`. See [project pages](project-pages.md) for the user-facing guide,
[the evidence](validation/2026-10-20-project-pages.md), and
[samples/project-page](../samples/project-page/SAMPLE.md) for a repository that produces both surfaces.

**`.graft/project.json` on the default branch says what a page offers**: a title, a description, a readme,
a file allowlist, and a `site` folder. The config can only narrow, never publish, because authoring needs
Developer and publishing needs Maintainer; a config able to publish would let a Developer expose a private
project by pushing a commit.

**One matcher, `PathMatcher`, answers every page decision.** `*` within a segment, `**` across, nothing else
a metacharacter, no negation, whole-path comparison, and traversal normalising to nothing. Two matchers
would be two answers and the leak would live in whichever one somebody forgot.

**Sites need a second hostname, and `--pages-origin` is the gate.** Off by default. On that host there is no
API, no session, no uploads and no browser shell, because the branch short-circuits before all of them; the
`__Host-` session cookie cannot travel there either. Content types come from a fixed extension list rather
than from the file. Empty is treated as unset, so a container that always passes the flag still starts.

**The dead end is closed.** A page-only project is linked from the public browser to `/-/p/<slug>` rather
than to a repository view that would refuse the reader.

**Two things found on the way.** `CreateProjectWithRepositoryAsync` returned a project with an empty
instance-wide slug, because the record was built by hand from the insert's arguments; fixed. And Chrome
cannot be told through the automation server to accept the loopback certificate, so the browser check
starts Chrome directly with `--ignore-certificate-errors` and `--host-resolver-rules`, which is also how a
browser reaches the pages origin without DNS.

**A site links back to the forge without naming it.** `/<slug>/-/repo` redirects to the project's page
and `/<slug>/-/graft.json` reports where the forge is, both resolved before any file lookup so a
repository cannot shadow them. A site that hard-coded the hostname would be wrong the day it changed.

**Still not built:** `features`, so naming one is an error, and the single-hostname fallback ADR 0006
describes as a worse product.

Suites: 469 Git/API/browser, 162 accounts and core, 41 operations, 96 MCP.

## Added on 2026-10-19, agent access brought to parity

**The gap was never missing tools.** Every tool already mapped onto the token-authenticated surface; almost
every *mutating* route in the server accepted a browser session and nothing else, so no credential could
edit a description, write a file, or grant a role. Parity meant building the token half of capabilities the
product already had. See [ADR 0011](decisions/0011-agent-parity.md) and
[the evidence](validation/2026-10-19-agent-parity.md).

**Six capabilities opened, eighteen tools now.** `PUT /api/v4/projects/:id` widened to `name`,
`description` and `default_branch` beside `visibility`; `/api/v4/user` given a tool; file writes and commit
diffs added; `/api/v4/projects/:id/members` added for list, grant and revoke; and a project avatar route
that takes image bytes. All GitLab routes with GitLab meanings, deviations recorded in
[compatibility.md](compatibility.md).

**A product gap closed with it.** A description could be set at creation and never corrected, and a name
could not be changed at all, by anybody. The browser gained a name and description form in the project
settings tab, through a new `/api/projects/:id/details` route and `SetProjectDetailsAsync`.

**Two of these needed the unbuilt half of ADR 0005.** Writing a file and setting an avatar were browser-only
by transport, not policy: both routes take an upload receipt from the resumable queue, which needs a browser
antiforgery token. The plain-HTTP path for API clients now exists for both, bounded before it reads and
validated by the same code the browser uses.

**Four capabilities stay withheld on purpose:** account administration, password resets, token issue and
revocation, and the agent switches themselves. The first three would let an agent manufacture a credential
beyond the one it was handed, which is the property ADR 0008 exists to protect; the fourth is circular.

**Three traps worth knowing.** The file commit route was never plain JSON despite looking like it. The read
and write sides of the files route disagreed about URL escaping, so a nested path written one way could not
be read back the other. And the MCP suite asserts the advertised tool count in **three** places, so adding a
tool and updating one of them fails somewhere unrelated.

Suites: 433 Git/API/browser, 129 accounts, 41 operations, 96 MCP.

## Added on 2026-10-19, instance-wide project slugs

**Every project has a second address, `/projects/<slug>`, with no account name in it.** Browsing, cloning
and pushing all work through it, and the owned `/<owner>/<slug>` path is unchanged. Assigned automatically
from the project's own path, with a short random suffix when that name is already held instance-wide. See
[ADR 0010](decisions/0010-project-slugs.md) and [the evidence](validation/2026-10-19-project-slugs.md).

**It is an alias, never a second set of rules.** Resolution by slug runs through the same permission
decision, a concealed project answers the same way through both addresses, and push-to-create deliberately
never fires on a slug because that path names no owner to create under. The Git transport needed no new
route: `/projects/<slug>.git` is the same two segments as `/<owner>/<slug>.git`, so only the lookup differs.

**The shared page does not redirect to the owned path**, which would put the username back in the address
bar and defeat the point. Links built on that page, including commit pages, stay on the route the reader
arrived by.

**Migration 6 backfills existing projects**: each gets its own path where that is free instance-wide, else
its path, two hyphens and its id. Two hyphens cannot occur in a project path, so no generated value can
collide with a plain one. Exercised against a database built on the previous schema, including two projects
sharing a path under different owners.

**Account creation now refuses `projects`, `api`, `healthz`, `mcp` and `index.html`.** A namespace is a
username at the root of the site, so an account called `projects` would own every link the slugs live under.
There was no reserved list before; checked only on creation, so an instance that already has such an account
keeps working.

**Not done, and worth doing:** the shared page was never rendered in a browser this session, because both
automation servers failed (Playwright stopped responding; Chrome DevTools cannot be told to accept the
self-signed loopback certificate). The route templates and the share field are compiled into the client and
every endpoint behind the page answers correctly, but the rendering is unverified by hand. **Next action,
owner: implementation agent:** open `/projects/<slug>` against a running server, confirm the page renders,
the links stay under `/projects/`, and the clone menu's share section shows the ownerless URLs.

Suites: 405 Git/API/browser, 129 accounts, 41 operations, 82 MCP.

## Added on 2026-10-16, MCP in the forge

**The Model Context Protocol endpoint is hosted by the forge itself**, at `/mcp` on the same port and
certificate, behind `--mcp true` or `GRAFT_MCP=true`. Off by default. The compose `mcp` profile, the second
service and port, the extracted CA and the plain-HTTP overlay are gone; the Containerfile no longer carries
the launcher. See [ADR 0009](decisions/0009-mcp-in-the-forge.md) and
[the evidence](validation/2026-10-16-mcp-in-the-forge.md).

**Beneath the operator's gate, three administrator switches live in the database** and are edited in the
browser at Administration, Agent access: serve agents (default on), allow writes (off), serve callers with no
token (off). The endpoint reads them on every request, so a change applies to the next call with nothing
restarted. Every change is audited with the resulting posture, and the forge prints
`MCP: <endpoint>; enabled=...; credentials=...; writes=...` on startup. A new `ServerSettings` table,
migration 5, holds them one row per setting.

**The tools still call the HTTP API from inside the process.** `GraftTools` and `GraftClient` moved to
`src/Graft.McpTools`, shared by the forge and the launcher. The forge connects to its own listener addressed
to the public origin, so result links are the ones a person could open, over a socket opened straight to the
bound port and accepting only the certificate this process serves. Every authorization decision stays in the
API route, made once for agents and people alike.

**What unblocked it: NativeAOT runs here now.** The MSVC toolchain is installed, so ILC runs to completion
for the first time in this repository. ADR 0008 had
named exactly that as its review trigger. The SDK finds the linker through `vswhere.exe`, which this Visual
Studio installer lacks, so the publish runs inside `vcvars64.bat` with `IlcUseEnvironmentalTools=true` and
`Platform` cleared; the command is in [build-and-test](build-and-test.md). `Graft.Mcp` publishes native with
zero ILC warnings, and the MCP SDK produces none inside the forge either.

**And it found, then solved, a break in the forge's NativeAOT claim.** All sixteen ILC warnings were in
`DnaX.Uploads`, whose `MapDnaXUploads` registers minimal-API lambdas returning `Task<IResult>`, and the
native forge died at startup building its endpoint table, with `--mcp true` and `--mcp false` alike. The
Request Delegate Generator replaces that runtime factory at compile time, but only in the compilation it
runs in, so a referenced library has to enable it for itself. `DnaX.Uploads` does not; the DnaX repository's
own MCPFab projects do. Rebuilding the package with `EnableRequestDelegateGenerator=true` takes it from
32 compile-level trim/AOT warnings to zero, and Graft then publishes NativeAOT with no ILC warnings at all,
starts, serves, and **passes all 82 MCP and all 389 Git assertions as a native binary**. See
[the evidence](validation/2026-10-16-mcp-in-the-forge.md).

**Filed upstream as [Wixely/DnaX#17](https://github.com/Wixely/DnaX/issues/17)**, which also found that
`DnaX.Diagnostics` has the same defect (12 warnings, same fix) and `DnaX.RemoteAccess` a sibling one in
`OptionsBuilder.Bind`, cleared by `EnableConfigurationBindingGenerator`. **Next action, owner: implementation
agent once that issue is resolved:** move Graft's pin to the released version and publish the forge native.
Nothing in Graft needs to change. Until then Graft pins `10.0.0-alpha.7` and the native forge cannot start,
which is a published-dependency problem rather than a Graft one.

**The client gained an admin page**, `/-/admin/agents`, shown only when the session reports
`mcp_available`, and the session response carries that flag beside `public_browsing`.

Suites: 389 Git/API/browser, 129 accounts, 41 operations, 82 MCP.

## Added on 2026-10-13, login focus

**The sign-in page puts the caret in the username box.** Nothing focused anything before, so focus stayed
wherever the previous page left it, usually the link that was clicked to get there. The HTML `autofocus`
attribute would not have fixed it: arriving at a route in a single-page application is a client-side
navigation rather than a document load, and a browser applies autofocus only on the latter.

**A refused sign-in left focus nowhere at all,** which was the same defect one step later. The submit button
disables itself while the request is in flight, and a browser moves focus to the document body when the
focused element becomes disabled, so a failed attempt dropped the caret entirely and the reader had to go
find the form again. Focus now moves to the password box, which is the field that was just cleared. The
refusal is still announced by the banner's `role="status"` live region, which needs no focus of its own.

Verified in Chromium against a running server: `#username` is the active element on arrival, `#password` is
the active element after a refusal with the password cleared and the banner present, a successful sign-in
still lands on the projects page, and the only console errors are the deliberately wrong passwords.

## Added on 2026-10-13, MCP deployment posture

**Credentials are required and the override is explicit.** `--allow-anonymous`, off by default, serves
callers who present nothing; they reach Graft as strangers and see only public projects. The refusal names
the switch. **`--allow-plaintext`**, also off by default, permits an `http://` listener off loopback for an
internal endpoint, and `deploy/container/compose.internal-http.yaml` is an overlay that uses it. The two are
independent: the plaintext overlay still demands a credential. The server prints
`POSTURE: credentials=...; writes=...; transport=...` on startup.

`compose.yaml` is now the hardened reference: every capability dropped, no privilege escalation, read-only
root, bounded logs, memory and process counts, both ports on host loopback, shared through a YAML anchor so
the two services cannot drift. All of it verified in Docker.

**Three things this found.** The MCP server's argument parser printed usage and no reason, which makes a
container that restarts every two seconds nearly undiagnosable; it now says what it refused and why.
`graft_list_projects` was pointed at `/api/projects`, a browser-session route that refuses a token outright,
so that tool could never have worked, and nothing noticed because the suite called only two of the seven
read tools. `graft_read_file` and `graft_list_tree` omitted the `ref` the file route requires. The MCP suite
now calls every read tool against a seeded repository and is 36 assertions.

**A container trap, now closed.** The compose services carried only a `build:` section, so compose invented
an image name from the directory and the service and never referenced a separately built tag. Several runs
of the deployment check reported a server rejecting arguments it understood, because the container was older
than the binary on disk. Both services now name the image through `GRAFT_IMAGE`, so a tagged build and a
compose build are the same object, and one image serves both services rather than the Containerfile being
built twice. `up` still does not rebuild on its own, so `compose build` or `up --build` after republishing.

Suites: 389 Git/API/browser, 129 accounts, 41 operations, 36 MCP.

## Added on 2026-10-13, MCP in the deployment

**The MCP server ships in the standard container image and starts under a compose profile.** `docker compose
--profile mcp up`. Without the profile nothing listens on 8444 and the image is identical, so enabling it is
a restart rather than a rebuild. See [deployment evidence](validation/2026-10-13-mcp-in-the-deployment.md).

Verified in a real container runtime, Docker inside WSL, building the real Containerfile and running the
real compose file: profiles select the right services, the forge is healthy with the profile off and nothing
answers on 8444, and with it on the endpoint refuses an uncredentialed caller, completes the handshake for a
credentialed one, reaches the forge over verified TLS, and still refuses writes in a read-only deployment.

**Two pre-existing problems surfaced while doing it.** The documented container publish command did not
work: `PublishSingleFile` propagated into the nested Blazor client publish, which cannot use an application
host, failing with `NETSDK1098`. The nested publish already stripped `RuntimeIdentifier`, `SelfContained`,
`PublishAot` and `PublishTrimmed`, and `PublishSingleFile` was missing from that list; it and
`PublishReadyToRun` are stripped now. Separately, compose secrets outside swarm are host bind mounts, so the
operator's certificate and password files must be readable by UID 1654 or the forge fails at startup with
what looks like a certificate fault. The runbook says so now.

`deploy/container/.env.example` is new and documents every variable, including the two that only matter
under the MCP profile.

## Added on 2026-10-13, the MCP layer

**Graft has a Model Context Protocol server**, `src/Graft.Mcp`, as a separate executable speaking both
stdio and Streamable HTTP. Ten tools, seven reads and three writes, all calling the ordinary REST API with a
personal access token. See [MCP evidence](validation/2026-10-13-mcp-layer.md) and
[ADR 0008](decisions/0008-mcp-layer.md).

**The two transports differ in who holds the credential, and that is the design rather than an accident.**
Over stdio there is no caller to carry one, so the process holds a token from a file. Over HTTP each request
carries its own token and the server stores none, which makes the endpoint a protocol translator rather than
a credential vault: two callers get exactly the access their own credentials carry. `--token-file` is
refused in HTTP mode rather than ignored, because a stored token there would be lent to anyone who reached
the port. A request with no token is refused before the protocol is spoken.

**It is not built on DnaX, and the request asked for DnaX.** Both DnaX MCP packages were read at the pinned
commit first. `DnaX.MCPFab` is an application template, not a library: it creates its own WebApplication,
Kestrel listener, Serilog logger, configuration chain and default authentication scheme, so adopting it
would make Graft an MCPFab server rather than give Graft an MCP layer, at a cost of roughly twenty runtime
assemblies. `DnaX.RemoteAccess.Mcp` is the right shape and twenty-four lines, but those lines are the
official SDK's own API and it brings a second SQLite-backed credential store beside the one Graft has. The
official SDK is therefore the smaller dependency; DnaX's own package depends on it and adds to it. DnaX's
build policy was still worth reading, and its rules are followed.

**It is a separate process because Graft.Server publishes NativeAOT and that is a release claim.** Nobody
has AOT-published an MCP library; DnaX verifies trimmed and single-file and explicitly leaves
`IsAotCompatible` off. The forge's dependency closure is unchanged by any of this. `Graft.Mcp` itself builds
clean with the AOT, trim and single-file analyzers on and reflection JSON off, and those analyzers caught a
real hazard during the work. ILC still never runs here.

**Two API routes existed only in the browser and now have token equivalents.** `PUT /api/v4/projects/:id`
takes `visibility`, which is a route and attribute GitLab has, so this makes Graft more compatible rather
than less. Publishing the curated page has no upstream equivalent and stays at
`PUT /api/graft/projects/:id/page`. Both check the token's scope before resolving the project, so an
insufficient scope is reported as such while an inaccessible project still answers 404.

Writes are off by default: the MCP server refuses every mutating tool unless started with
`--allow-writes true`, and names the switch that would permit it.

Suites: 389 Git/API/browser, 129 accounts, 41 operations, and a new 16-assertion `tests/Graft.Mcp.Acceptance` driving both MCP transports against a real forge.

## Added on 2026-10-13, sessions

**A session used to expire fifteen minutes after sign-in no matter what you were doing.** Not an idle
timeout: the stored expiry was set once at login and nothing ever moved it, so continuous work was thrown
out mid-sentence at the fifteen minute mark. The number was a bare magic value with no comment, unstated in
any document and unpinned by any assertion. See [session evidence](validation/2026-10-13-session-renewal.md).

A session is now an idle window, renewed by use once past the halfway point, defaulting to 12 hours and set
by `--session-minutes`. An absolute ceiling of 30 days from sign-in bounds how long renewal can keep one
alive. Renewal is halfway-gated because resolving happens on nearly every request and extending on each
would be a database write per request against a single-writer store.

**The first attempt at the fix did not work, and the reason is worth remembering.** It moved the stored
expiry but never sent a refreshed cookie, because `app.UseAuthentication()` resolves the session on every
request before any route runs, and it consumed the renewal; the route's own check then saw nothing due.
`ResolveSessionAsync` is a pure read again, which is what the authentication handler needs, and renewal
lives only in `ResolveSessionDetailedAsync`, used by the one caller that can also write the cookie. The
general shape of that trap applies to anything else that becomes a side effect of resolving a session.

Suites: 372 Git/API/browser, 129 accounts, 41 operations.

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

Two corrections to the earlier record. The solution did **not** build on the current toolchain before this work; two independent causes needed fixing together. And a remote **is** configured (`origin`, github.com/Wixely/Graft), contrary to the statement that stood here previously.

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

**Still open and unchanged:** the demo fixture is disposable by design, so anything pushed into it is
destroyed when it stops.

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

This repository is published at github.com/Wixely/Graft and the GitHub repository is **public**, so every commit here is world-readable the moment it is pushed. Treat anything added to it accordingly. Do not deploy, import production data, expose a network listener or select repository visibility without explicit authorization. Graft can now serve anonymous readers, so that caution covers starting a server with `--public-browsing true` on anything but loopback.

## Next work

Read [the backlog](backlog.md) first. It is the maintained list of what Graft does not do, with reasoning
and rough size, and it replaces the habit of rediscovering gaps one at a time. The `.graft` config and file
allowlist that this section used to ask for were built on 2026-10-20.

Keep the recorded static-musl server/admin pair available for the deferred Linux 3.10.108 run. Validate the
intended CA-issued certificate, DNS, client trust and renewal path before non-loopback deployment, which
now matters more than it did: a public listing is only reachable by strangers on a non-loopback address.

Continue to use the ignored reproducible DnaX feed until the user or DnaX maintainer selects an approved durable package source. Do not substitute a server-side Git executable or native Git library.

## Follow-on queue

| Work | Owner | Acceptance |
| --- | --- | --- |
| ~~Project ownership continuity~~ | Done 2026-10-04 | [Evidence](validation/2026-10-04-project-ownership.md): transfer is owner-or-administrator only, and an account owning projects cannot be disabled |
| ~~`.graft` config and file allowlist~~ | Done 2026-10-20 | [Evidence](validation/2026-10-20-project-pages.md) |
| ~~Custom static site on a pages origin~~ | Done 2026-10-20 | [Evidence](validation/2026-10-20-project-pages.md) |
| Git's indent heuristic in the diff | Implementation agent | `tests/Graft.Diff.Fuzz` agrees with `git diff` at its default settings as closely as it already does with the heuristic turned off |
| Line-level comments on a diff | Implementation agent | A comment anchors to a file and line on a merge request, and survives the branch moving underneath it in a way somebody can predict |
| Markdig under NativeAOT | User + implementation agent | `tests/Graft.Git.Acceptance` passes against a published NativeAOT server built where the C++ workload exists |
| Deployment TLS trust | Operator + implementation agent | Intended CA certificate, DNS/SAN, client trust and renewal rehearsal |
| Required legacy host | Implementation agent + user | Integrated static-musl artifact inspected and run on Linux 3.10.108 with sanitized evidence |
| DnaX distribution | User/DnaX maintainer | Clean checkout restores without a sibling source tree or locally prepared feed |

Recommended next action: Owner: Implementation agent — take the "near, and small" section of [the backlog](backlog.md) as one batch. Tags, raw file download and archives are the three that together turn the browser into something a reader can fetch from rather than only look at, and each is bounded work against machinery that already exists. The two bugs recorded beside them are an afternoon: an empty repository's page reports a 404 as a permission problem, and `Commit.razor` resolves a project through the caller's own project list, so a commit link into a public project fails for a non-member. Line-level comments on a diff are the largest single thing that would change how review feels, and are a deliberate decision rather than a small task. The user-gated items are unchanged: the Android picker needs a device and NativeAOT needs the C++ build tools. Remaining release gates: deployment CA/DNS trust, DnaX distribution and the deferred Linux 3.10.108 MVP-08 run.
