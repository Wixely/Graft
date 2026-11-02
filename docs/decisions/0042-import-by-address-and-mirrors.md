# 0042: Import a repository by its address, and keep a copy that outlives the original

- Date: 2026-11-02
- Status: Accepted, implemented
- Source: A request on 2026-11-02 to import a repository by URL and keep it up to date, mainly so that a relatively
  recent copy survives if the upstream is ever taken down.
- Review trigger: a request to import a private repository (credentials); importing over SSH; Git LFS objects in an
  import; an upstream with more than 4,096 branches and tags or more than the repository budget; a request for the
  mirror to delete what the upstream deleted; pipelines or webhooks on mirror updates; push mirroring

## What was missing

A repository could reach Graft only by being pushed. Nothing could fetch from another server: the Git engine has
always been the server half of the protocol. So there was no way to bring in a repository by its address, and no
way to keep a copy of somebody else's project in case it disappears.

## Decision: Graft fetches with its own client, over smart HTTP

`GitRemote` in the engine is the client half of Git's smart HTTP protocol, for fetching only. It asks for protocol
version 2 and uses it when the server offers it (`ls-refs`, then one `fetch` with `want`, `have` and `done`, the pack
in side-band packets), and otherwise speaks version 0 (the reference advertisement, one stateless request, the pack
bare or in side-band packets as the server offered). It lists branches and tags only, follows a redirect of the
first request and sends everything after it to where it ended up, refuses SHA-256 repositories, and bounds the
reference list at 64 MiB and the pack at the 5 GiB a push may send. The pack is spooled to a temporary file without
holding the repository, then read and validated the way a pushed pack is, under the repository's gate.

No `git` executable, no SSH, no dumb HTTP, no shallow or partial clone, no credentials: only repositories anybody
may clone over http or https. An address with a user name or password in it is refused rather than stored, because
it would be shown wherever the address is.

## Decision: a mirror adds and follows; it never removes

The point of keeping the copy up to date is that it survives the original. So an update cannot take anything away:

- A branch or tag the upstream adds is added. A branch it advances is advanced.
- A branch or tag the upstream **deletes** stays here, untouched, and is reported as kept.
- A branch or tag the upstream **rewrites** (its new target does not contain the old one) follows the upstream, and
  the old target is kept under `refs/graft-preserved/<UTC time>/heads/<name>` or `.../tags/<name>`. That namespace
  is written by the server alone, like `refs/merge-requests/`; any client may fetch it, none may push into it.
- A kept branch that stands in the way of a new upstream name (`a` against `a/b`) moves into the preserved namespace.
- Objects are never pruned, as everywhere in Graft.

A takedown that answers 404, 403, 451 or 401, a server that cannot be reached, an upstream emptied of branches or
force-pushed to a tombstone commit: each leaves every branch, tag and commit this copy already had in place.

The rules apply to anything else in the repository too. A branch somebody commits to here that the upstream later
moves elsewhere follows the upstream, and the local commits are preserved the same way. Pushes to a mirror are not
refused, which is GitLab's pull mirror behaviour with "overwrite diverged branches" on.

## Decision: imports run on a worker, on a schedule in the database

Creating a project with an import address creates it empty and schedules the import; GitLab's `import_status` says
where it is. One worker runs one update at a time: it claims whatever is due, records the result and the time of the
next attempt, and sleeps until the next one is due or somebody asks for an update. A restart loses nothing; an
update it interrupted runs again. An update may take two hours before it is abandoned.

A mirror kept up to date is updated every `--mirror-interval` minutes (default 360, between 15 and 10,080). After a
failure the interval doubles for each failure in a row, at most sixteen times and never past a day, and the mirror
is never given up on: a repository that has gone is exactly the case a mirror is for, and it may come back. An import
not kept up to date runs once, and again whenever a Maintainer asks.

On its first success an import takes the upstream's default branch, unless one was already chosen.

## Decision: the operator decides whether the server fetches at all, and from where

`--url-import off|public|any`, `GRAFT_URL_IMPORT` in the container, defaults to `off`, for the reason outbound
webhooks are off by default: with it on, a person can make the server send requests. `public` reaches only hosts
whose every address is on the public internet. The check is made on every connection after the name is resolved,
and the connection is made to the addresses checked, so a redirect to an internal host or a name that resolves
differently a second later is refused the same way. No proxy is used, because the check is on the destination.
Refused are loopback, private ranges, link-local (where cloud metadata services answer), carrier-grade NAT,
unique-local, multicast, documentation, translation and unspecified addresses. `any` also reaches the server's
own network, for importing from a forge beside it.

Anybody who may create a project may import one. Configuring a mirror, asking for an update and turning updates
off are a Maintainer's, as every project setting is. Anybody who may read the project may read its mirror state.

## Decision: GitLab's shapes

- `POST /api/v4/projects` takes `import_url` and `mirror`, GitLab's names; with no name or path the address's last
  segment, without `.git`, is the path. Projects carry `import_url`, `import_status` (`none`, `scheduled`,
  `started`, `finished`, `failed`), `import_error` and `mirror`.
- `GET /api/v4/projects/:id/mirror/pull` answers GitLab's pull mirror object, plus `next_update_at`,
  `consecutive_failures` and `last_changed_at`. `PUT` takes `enabled` and `url`; GitLab's switches are accepted
  when they ask for what Graft does (`mirror_overwrites_diverged_branches: true`, `mirror_trigger_builds: false`,
  `only_mirror_protected_branches: false`, no `mirror_branch_regex`) and refused by name otherwise. `POST` asks for
  an update now. The browser family has the same, with configuration at `POST /api/projects/:id/mirror`.
- The browser's new-project form has an import address and "Keep it up to date"; the project page says what it is
  a mirror of and how the last update went, with "Update now"; the settings tab has "Import and mirroring".
- MCP tools `graft_import_project`, `graft_mirror_status` and `graft_update_mirror`.

Migration 26 adds `ProjectMirrors`.

The engine's delta chain limit is raised from 32 to 250, superseding that line of
[ADR 0018](0018-repository-capacity.md): GitHub served libgit2 with a 50-deep chain, Git's default depth, and the
first import of it was refused.

## What a mirror update does not do

It fires no webhooks, starts no pipelines, writes no push events into the activity feed and sends no
notifications: GitLab's pull mirrors start pipelines only when asked, and Graft has no switch for it yet. It fetches
no Git LFS objects, so an imported repository that uses LFS has pointers without content. It does not carry issues,
merge requests, wikis or anything else that is not in the repository; that is the GitLab import of the
requirements, still to come.

## What was verified

- Engine self-checks 44 to 58: an import, a no-op update, a fast-forward, a rewrite preserved, a deleted tag kept,
  the preserved namespace fetchable and not pushable, a vanished upstream reported as unavailable, each over
  protocol versions 2 and 0, and an HTML page refused as not a repository.
- MVP-04 suite 169 to 215: what an import address may be, and every address class `public` refuses or allows.
- Git acceptance suite: a server without the switch refuses an import; with `any`, an upstream served by the suite
  over plain HTTP is imported with its branches, annotated tag and default branch, cloned and checked with
  `git fsck --strict`; it advances, deletes a tag and rewrites a branch, and the update follows, keeps and preserves;
  it is taken down and the update fails with the server's 404 while every reference stays; it comes back speaking
  only protocol version 0 and is followed; GitLab's switches; a stranger refused; a server limited to `public`
  refuses to fetch from loopback.

- Against GitHub, from a disposable local forge with `--url-import public`: `octocat/Hello-World` imported in under
  a second; `libgit2/libgit2` (271 branches and tags, 134,989 objects, a 77 MiB pack, protocol version 2) was first
  refused for its 50-deep delta chains, which raised the limit, and then imported with every reference identical to
  `git ls-remote` against GitHub, the default branch taken, and a mirror clone passing `git fsck --strict`. An update
  with nothing new took under a second.
- **The first import of a large repository is slow**: 63 minutes for libgit2 on this Windows machine, and 7.5 minutes
  to clone it back out. The download takes seconds; the time is the engine's pack reader, which inflates through a
  one-byte source and spills every object and every delta result to its own temporary file (ADR 0018). A push of
  the same repository takes the same path. It is correct and bounded, and it is the next thing to make faster.

Not verified: the live deployment, and Linux.
