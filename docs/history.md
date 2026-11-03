# History

The README's running status log, moved here on 2026-11-03 when the README became an installation guide. Newest
entries were added near the top, so read upward for chronology. Decisions are in [decisions](decisions/README.md);
what changed since is in [the handoff](handoff.md) and the git log.

Status as of **2026-09-20**: [MVP-08 packaging and resource validation](validation/2026-09-19-mvp08-packaging-resources.md) conditionally passes. Windows NativeAOT interactive and Windows Service, Linux static-musl interactive/systemd, and rootless Podman paths are implemented and tested; the recorded Linux static binaries retain all 76 Git/API/project-creation and 36 recovery assertions. That Windows Git/API/browser workflow passed **135 assertions** under the runtime and NativeAOT at the time, including [administrator user creation and project membership](validation/2026-09-19-user-membership.md), [personal access-token lifecycle](validation/2026-09-19-personal-access-tokens.md), and [account credential lifecycle](validation/2026-09-20-account-credentials.md). The GitLab 18.0.0 source fixture passes against Graft and a [live reference](validation/2026-09-19-gitlab-18-live-reference.md), while pinned `glab` 1.58.0 passes against both. The rendered Blazor workflow and one-use [operator bootstrap](validation/2026-09-19-operator-bootstrap.md) pass. The Linux 3.10.108 run is deferred. DnaX distribution and a real CA/DNS trust environment remain gates. Remote hosting and the project licence are undecided. The repository itself is public at github.com/Wixely/Graft.

Status as of **2026-10-22**, last of the day: things can be got out of Graft without cloning it. A file
downloads as its exact bytes, the whole tree downloads as a gzipped tar or a zip, and tags are listed,
created and deleted, with an annotated tag pushed by a client described correctly beside one made here.
Repository bytes are always served as an attachment of unknown type, never as the type the content looks
like, because this origin holds the session cookie and the API and that is the same reason project sites
have a hostname of their own. Two defects went with it: an empty repository used to report a permission
problem, and a commit link into a public project used to fail for anyone who was not a member. See
[ADR 0017](decisions/0017-getting-things-out.md) and
[the evidence](validation/2026-10-22-fetching.md).

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
See [ADR 0015](decisions/0015-review-journey.md), [ADR 0016](decisions/0016-webhooks.md) and
[the evidence](validation/2026-10-22-review.md).

Email is optional. Graft sends nothing unless `--mail-smtp` names a server, and it works without one: a
forgotten password is reset with a one-time link an administrator makes and hands over, so the administrator
never knows the password the person chooses. With a mail server, the sign-in page lets people ask for that
link themselves, sent to the address on their account. See [ADR 0032](decisions/0032-email-is-optional.md).

What needs you arrives in an inbox, with or without mail: being assigned, being asked to review, and whatever
happens to an issue or proposal you are part of, written in the same transaction as the event and read from the
bell in the header, `/-/notifications`, or GitLab's `/api/v4/todos`. A message per entry is a preference, off
by default, and only ever a copy of the inbox. See [ADR 0033](decisions/0033-notifications.md).

An account can ask for a code from an authenticator app after its password. The factor is the app and nothing
else: no email, no phone number, recovery codes for the day the app is gone, and an administrator to remove it
for the person who lost both. Tokens and SSH keys are unaffected. See [ADR 0034](decisions/0034-second-factor.md).

Projects can belong to a group rather than a person, and a group can hold groups. A role in a group is held
on every group and project beneath it, so a team is granted once. A project under `team/platform` is
`team/platform/api` to the API, to Git over HTTPS and to Git over SSH; in the browser its namespace travels as
one encoded segment, and the natural address redirects there. See [ADR 0035](decisions/0035-groups.md).

Graft runs pipelines on runners of its own. An administrator creates a runner and copies an install script to
the build machine; the script carries the forge's CupriNet Intonation and a one-time token, registers the runner
and installs it as a service. The runner then reaches the forge over CupriNet, pinning the forge's key, with no
certificate, DNS name or HTTPS route needed, fetches each job's source over the same connection, and streams the
log back. A push carrying `.gitlab-ci.yml` makes a pipeline, run stage by stage; Graft reads a strict subset of
the format and names anything else it is asked for. See [ADR 0037](decisions/0037-runners-over-cuprinet.md).
A job can keep artifacts, downloadable from its page and GitLab's job artifacts route. Sources and artifacts go
whole up to 80 MiB and in verified, resumable chunks above it, a line the operator can move or turn off
([ADR 0038](decisions/0038-chunked-runner-transfers-and-artifacts.md)).

Whoever may manage a project can read what has been done to it: the audit entries that name the project, its
issues, proposals and comments, or its releases, packages, images and snippets, at the bottom of the settings
tab and at GitLab's `audit_events` route. The whole trail stays the administrator's. See
[ADR 0036](decisions/0036-project-audit.md).

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
[ADR 0013](decisions/0013-project-deletion-and-audit.md),
[ADR 0014](decisions/0014-collaboration.md) and
[the evidence](validation/2026-10-22-collaboration.md).

What Graft still does not do is now written down in one place rather than discovered: see
[the backlog](backlog.md).

Status as of **2026-10-21**: a personal access token's secret never enters the page. Creating one returns a
single-use handle rather than the secret; pressing Copy claims it with one request and writes it straight
to the clipboard. A token nobody copies therefore never reaches the browser, and a copied one is never in
the document for a screenshot or an extension to find. The secret waits in server memory for five minutes
and one read, never on disk, and is shown only if the clipboard refuses, because losing a token that cannot
be produced again is worse. Separately, an operator can now switch on
`--recoverable-tokens`, which keeps each new token's secret encrypted so its owner can copy it again with
their account password. Off by default, because with it on the database and its key ring together are every
token on the instance; see [ADR 0012](decisions/0012-recoverable-tokens.md).

Status as of **2026-10-20**: the project page is now what it was always meant to be. A `.graft/project.json`
in the repository says what the page offers, a file allowlist decides which files are reachable, and a
folder in the repository can be served as a static website, GitHub Pages style, from a hostname of its own
behind `--pages-origin`. Everything is deny by default: no allowlist means no files, and nothing outside the
site folder is reachable at all. A page still cannot clone, show history or show a diff, because those walk
the object graph and no filter makes a graph walk honour a path allowlist. See
[project pages](project-pages.md), a [worked sample](../samples/project-page/SAMPLE.md) and
[the evidence](validation/2026-10-20-project-pages.md).

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
[ADR 0011](decisions/0011-agent-parity.md) and
[the evidence](validation/2026-10-19-agent-parity.md).

Status as of **2026-10-19**: every project now has a second address with no account name in it,
`/projects/<slug>`, so a link can be shared without naming its owner. Browsing, cloning and pushing all work
through it and the owned `/<owner>/<slug>` path is unchanged. The slug is assigned automatically from the
project's own path, gaining a short random suffix when that name is already taken instance-wide, and
existing projects are given one on upgrade. It is an alias and nothing more: the same permission decision,
the same refusals, and a concealed project answers identically through either address. See
[ADR 0010](decisions/0010-project-slugs.md) and
[the evidence](validation/2026-10-19-project-slugs.md).

Status as of **2026-10-16**: the Model Context Protocol endpoint is part of the forge. `--mcp true`, or
`GRAFT_MCP=true` in the container deployment, serves it at `/mcp` on the same port and certificate as
everything else; nothing else about a deployment changes, and the compose profile, second port and
plain-HTTP overlay are gone. Once on, an administrator decides in the browser, under Administration, Agent
access, whether agents are served, whether they may change anything, and whether callers with no token are
served at all, and each change applies to the next request with nothing restarted. The tools are the same
ones and still reach Graft over the REST API with each caller's own token, so an agent can do exactly what
that token could do with curl. What made this possible is that the MCP SDK turned out to publish NativeAOT
cleanly, on its own and with no warning inside the forge, which [ADR 0008](decisions/0008-mcp-layer.md)
had named as the thing to wait for. The same first native publish of the forge in weeks found that the
upload transport, not MCP, stops the native forge from starting, and that one MSBuild property in that
package fixes it: rebuilt that way, the forge publishes native with no warnings and passes all 82 MCP and
389 Git assertions as a native binary. Releasing that package is the next action. See [ADR 0009](decisions/0009-mcp-in-the-forge.md) and
[the evidence](validation/2026-10-16-mcp-in-the-forge.md).

Status as of **2026-10-13**: the Model Context Protocol server ships in the standard container image and
starts under a compose profile, so enabling agent access is a restart rather than a rebuild and an operator
who wants none has nothing listening. It carries no credential of its own: every caller sends their own
Graft token, and a caller presenting none is refused unless an operator explicitly allows that. A second
overlay serves the endpoint over plain HTTP for an internal network, which also has to be asked for. The
reference compose file is hardened by default and every posture was verified in a real container runtime;
see [deployment evidence](validation/2026-10-13-mcp-in-the-deployment.md).

Graft also ships the same tools as a separate launcher, `src/Graft.Mcp`, speaking stdio for clients that
start their MCP server locally, and Streamable HTTP on its own. It reaches Graft through the ordinary REST
API with a personal access token, so an agent can do exactly what that token's owner can do and nothing
more, and mutating tools are refused unless it is started with writes allowed. Two settings that were
previously browser-only gained token equivalents to support it, one of which is a route GitLab already has.
See [ADR 0008](decisions/0008-mcp-layer.md), partly superseded by ADR 0009 above.

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
[ADR 0007](decisions/0007-push-to-create.md).

Status as of **2026-10-13**: a project can now be read by someone with no account. Visibility is stored for
the first time, as `private`, `unlisted` or `public`, and a public or unlisted repository browses and clones
with no credentials. A public one is listed and searchable on an `/-/explore` page that anonymous visitors
reach. The four executable suites pass **389**, **129**, **41** and **36** assertions. See
[visibility evidence](validation/2026-10-13-visibility-and-public-browsing.md) and
[ADR 0006](decisions/0006-project-pages-and-public-browsing.md).

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

A [rebuilt browser UI](validation/2026-10-04-ui-overhaul.md) with light and dark themes;
[browser uploads](validation/2026-10-04-browser-uploads.md) through DnaX
([ADR 0005](decisions/0005-browser-upload-transport.md)) for Git bundle inspection, repository file
commits and avatars; [server-authored Git objects](validation/2026-10-04-server-authored-commits.md)
byte-identical to Git's own; [avatars](validation/2026-10-04-avatars.md);
[project ownership transfer](validation/2026-10-04-project-ownership.md); and a default branch resolved
from the repository rather than assumed to be `main`.

A project page now renders its [README](validation/2026-10-04-readme-rendering.md). That is the first
place repository content becomes markup, so it is held closed by three layers, each proved by removing it
and watching the suite fail. Images resolve against the repository or do not load at all, which leaks less
than stripping a referrer because no request to the third party is made. Diagrams draw inside a sandboxed
frame with a policy of its own; the main document's policy is unchanged.

A [commit page](validation/2026-10-09-commit-diff.md) shows what changed, behind the
`/-/commit/<sha>` links the API had been handing out with nothing behind them. The diff is computed on the object model rather than by invoking Git, and is measured
against Git rather than assumed to match it: `tests/Graft.Diff.Fuzz` generates file pairs and compares every
result, agreeing on about **99%** of cases with `git diff` at its default settings. Graft's placement of a
change is a port of Git's own compaction step, indent heuristic included, since 2026-10-30; the residual is
Git's search settling on a different script of the same length, which placement cannot reconcile.

A clone menu offers the HTTPS URL, ready `git clone` commands including a single-branch one, and handoff to
Visual Studio Code and Visual Studio. [Third-party notices](../THIRD-PARTY-NOTICES.md) are embedded in the
server and served at `/-/licences`, because several of those licences bind the binary rather than the source
tree.

Still untested: the Android case that motivated the upload work needs a physical device, and NativeAOT
behaviour needs a machine with the C++ build tools.
