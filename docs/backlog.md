# Backlog

Recorded: 2026-10-22. Owner: Implementation agent; what gets built next is the project owner's call.
Review: whenever a batch of this is taken on, and when anything here is closed.

This is the list of things Graft does not do, written down so the absence is a decision rather than a
surprise. It came out of a review on 2026-10-22 that asked what was missing. The two smallest items from
that review and the largest one were built immediately: project deletion and the audit trail in
[ADR 0013](decisions/0013-project-deletion-and-audit.md), and issues, merge requests and comments in
[ADR 0014](decisions/0014-collaboration.md). Everything else that review found is here.

Each entry says what is missing, why it matters, and roughly what it would take. Nothing here is scheduled.

## Closed on 2026-10-22

The merge request journey was walked end to end and the five gaps it found were built the same day. They
are listed here because the backlog is also a record of what was decided, not only of what is left.

- **The diff on the merge request page.** It listed files and showed no change. [ADR 0015](decisions/0015-review-journey.md).
- **Line-level comments.** Anchored to a file, a line and the commit the diff came from, and reported as
  outdated rather than moved when the branch changes. Same record.
- **Webhooks.** Off by default behind `--webhooks`. [ADR 0016](decisions/0016-webhooks.md).
- **Branch creation and deletion**, and removing the source branch with a merge. ADR 0015.
- **Approvals.** A per-project count, superseded by a push, enforced at the merge. ADR 0015.

And then, the same day, the two recorded defects and the batch that gets things out of Graft without
cloning. [ADR 0017](decisions/0017-getting-things-out.md).

- **The empty-repository page**, which reported a 404 as a permission problem.
- **The commit page's project resolution**, which failed for any reader who was not a member.
- **Tags**, listed, created and deleted, with annotated tags from a client listed alongside.
- **Raw file download**, always as an attachment of unknown type.
- **Archives**, as a gzipped tar or a zip.

## Closed on 2026-10-29

The collaboration depth list from the 2026-10-28 review, taken on as one piece of work.

- **Replies, threads and resolving a conversation.** One level deep, resolvable by whoever could push or
  whoever started the thread, hiding nothing. [ADR 0019](decisions/0019-conversation.md).
- **Editing a comment.** The author or a Maintainer, with the edit recorded. A first comment with replies
  cannot be deleted. Same record.
- **Labels, milestones, assignees, due dates, boards and confidential issues.** A Reporter files and a
  Developer organises; a label that does not exist is refused by name; a board stores no state of its own;
  a confidential issue is for members and its author. [ADR 0020](decisions/0020-planning.md).
- **Reviewers and draft state** on a merge request. Same record.
- **Squash, rebase and code owners.** A squash is a merge that forgets; a rebase is a forced push that
  replays each commit and refuses by name; code owners come from the target branch and may be required.
  [ADR 0021](decisions/0021-review-options.md).

- **Releases.** Notes, links and files on a tag; a release outlives its tag; asset bytes in the database,
  bounded. [ADR 0022](decisions/0022-releases.md).
- **Wiki, snippets, time tracking.** Wiki pages in the database with every save kept; snippets as a
  comment with a file; time as an estimate and an attributed ledger.
  [ADR 0023](decisions/0023-wiki-snippets-time.md).
- **Search.** Code, commits, issues, proposals and wiki within a project; projects, issues and proposals
  across the server. No index, bounded. [ADR 0024](decisions/0024-search.md).
- **Forks.** A clone with an address; a proposal from a fork is mirrored into the original and its author
  may talk about it and nothing else. [ADR 0025](decisions/0025-forks.md).
- **Large files.** The LFS batch API and basic transfer, objects content-addressed beside the repository.
  [ADR 0026](decisions/0026-large-files.md).
- **Package feeds and a container registry.** Generic and NuGet packages, a Docker v2 registry.
  [ADR 0027](decisions/0027-packages-and-registry.md).
- **The Git engine beyond the bounded subset.** Repositories kept as bare Git repositories that Git itself
  opens, the previous layout converted on first open, packs read through their index and written with
  delta compression, housekeeping that packs everything and prunes nothing, fetch negotiation that sends
  only what the client lacks, and protocol version 2. SHA-1 only, and no shallow or partial clones, by
  decision. [ADR 0028](decisions/0028-git-engine.md).

## Closed on 2026-10-29, later the same day

- **Deleting a file.** `DELETE` on the repository-files route, a button on the file view and an agent tool;
  a deletion that finds no file is refused, and a directory it empties disappears.
  [ADR 0029](decisions/0029-file-deletion.md).
- **Token rotation.** The same row, a new secret; a button in the browser and GitLab's `rotate` routes with the
  secret in the response. [ADR 0030](decisions/0030-token-rotation.md).
- **Icons filled despite asking not to be.** The stylesheet rule went; the markup's `fill="none"` was right.
  Rendering all twenty-five both ways showed the fill hiding the fold on the file, the shackle on the lock,
  the mark in the alert and the lines on the repository, and turning the chevrons and the check into wedges.
  No decision record: it was a defect, and the backlog entry had already said one side had to change.

## Closed on 2026-10-30

The three small things the 2026-10-30 review of what was left found, fixed before the larger list below was
taken on in the order given: SSH, optional email, groups, the project-scoped audit view.

- **`features` in `.graft/project.json`.** Every name was an error because there was nothing to name. The
  seven surfaces that exist are now names, and a name is a link offered only to a reader the project would
  admit, never a grant. [Project pages](project-pages.md#features). Since 2026-11-08 the config may also open
  the structure, the history, issues and releases; [ADR 0045](decisions/0045-semi-public-projects.md).
- **Pushing needs a token and nothing said so.** The clone menu now says it where Git asks for the
  password, and the readme has a section on cloning and pushing, including how to clear a credential helper
  that cached the refused password.
- **Git's indent heuristic.** The diff's two home-grown normalisation passes were replaced by a port of
  Git's `xdl_change_compact`, heuristic included. Agreement with `git diff` at its defaults went from 95% to
  99.3% over 1,758 fresh pairs. The residual is Git's search choosing a different script of the same length.

Then the first of the four large items, in the order given:

- **SSH.** Graft is the SSH server, in a dependency-free project of its own: ECDH P-256, an ECDSA host key
  beside the database, AES-GCM with AES-CTR as the fallback, Ed25519, ECDSA and RSA client keys, one session
  and one command per connection. Keys are registered to accounts through GitLab's `user/keys` routes and a
  profile tab, audited, unique across the instance. Fetches in protocol version 2 only; pushes as always.
  Off until `--ssh-port` names a port. Not done: LFS over an SSH remote, deploy keys, administrator-managed
  keys. [ADR 0031](decisions/0031-git-over-ssh.md).
- **Email, optional.** `--mail-smtp` turns it on; nothing is sent without it. An optional unique address per
  account, an in-process SMTP client, and a password reset by one-time link that an administrator hands over
  or, with mail, the account asks for itself. [ADR 0032](decisions/0032-email-is-optional.md).
- **Notifications.** An inbox row per person per event, written in the event's transaction: assignment,
  review requests, comments, approvals, merges, closes. The bell, `/-/notifications`, GitLab's `/todos`. A
  message per entry is an opt-in copy. Not done: mentions, muting, digests. [ADR 0033](decisions/0033-notifications.md).
- **A second factor.** Time-based codes from an authenticator app, enrolled in two steps, with recovery codes;
  off with the password and a code; removed by an administrator for the person who lost both. No email, no
  phone number; tokens and SSH keys unaffected. Not done: WebAuthn, requiring it of everyone, a QR picture.
  [ADR 0034](decisions/0034-second-factor.md).
- **Groups and nested namespaces.** A group is a namespace with members and may hold groups; a role in it is
  held on everything beneath; a project is created under a group and reached by its nested path over the API,
  HTTPS and SSH. Deleted only when empty. The browser addresses a nested project with its namespace as one
  encoded segment, a recorded deviation. Not done: transfers, group visibility, group-level labels and the
  rest. [ADR 0035](decisions/0035-groups.md).
- **A project's own view of the audit trail.** For whoever may manage the project: the entries that name the
  project, its issues, proposals and comments, or its releases, packages, images and snippets that still
  exist, on the settings tab and at GitLab's `audit_events` route. The whole trail stays the administrator's.
  [ADR 0036](decisions/0036-project-audit.md).

## Closed on 2026-11-08

- **A semi-public project.** Four fields in `.graft/project.json`, each off until named: `tree` lists the default
  branch's structure with content only for allowlisted files; `history` shows commit messages and dates, with authors
  hidden unless `names` or `emails` is asked for; `issues` opens the issues read only and never a confidential one;
  `releases` opens releases with their files. The config's grants travel as one value to the authorization decision
  and the store, so a store method never told about pages fails closed. Still no clone, diff, changed-file list or
  branch list, and no page reading over `/api/v4`. [ADR 0045](decisions/0045-semi-public-projects.md).
- **A project banner.** Set and removed in the project's settings, held to the avatar's formats and limits, and
  drawn behind the project's title and tab bar on every page under it. [ADR 0046](decisions/0046-project-banners.md).
- **Every project setting through the agent tools** (2026-11-09). Banner, tags, review rules, webhooks, the audit
  trail, transfer and deletion, with token routes for the banner, approvals and transfer.
  [ADR 0047](decisions/0047-agent-project-settings.md).

## Closed on 2026-11-03

- **Releases, and a durable package source.** A `v*` tag builds single-file executables of the forge, admin tool,
  runner and git-remote-graft for Windows and static Linux, an image at `ghcr.io/wixely/graft`, and a GitHub release;
  DnaX, CupriNet and CupriMark restore from Wixely's GitHub Packages feed. [ADR 0044](decisions/0044-releases.md).
- **Large pushes, fetches and LFS files behind a proxy that limits requests.** `git-remote-graft`, a Git remote helper and
  Git LFS transfer agent, sends each in parts under `--transfer-part-size` (16 MiB) through a transfer API on the forge,
  so Cloudflare's 100 MB limit and its response timeout no longer stop them. One `setup` makes ordinary `https://`
  remotes use it. [ADR 0043](decisions/0043-chunked-transfers-git-remote-graft.md).

## Closed on 2026-11-02

- **An activity calendar on the profile.** A year of the person's contributions a day at a time, shaded like
  GitHub's, at the top of `/-/profile`. It counts what the activity feed counts (one per push, issue or merge request
  opened, merge and comment, as GitLab's calendar does, not commits), in projects the reader may read, by the
  reader's own day. Browser route `GET /api/users/:id/calendar?utc_offset=`, for the person and administrators only,
  as the feed is. GitLab's `/users/:username/calendar.json` is not served.
- **Import a repository by its address, and keep it up to date.** A project can be created from an http or https
  address, fetched by Graft's own client, and kept up to date on a schedule so that a copy survives the original being
  taken down: updates never remove anything, and a rewritten branch keeps its old tip. Behind `--url-import`, off by
  default. [ADR 0042](decisions/0042-import-by-address-and-mirrors.md).

## Closed on 2026-11-01

- **Every LabCoat call answered, retry and cancel, and the directory.** The 39 missing routes, the crashing commit diff,
  project permissions, job and pipeline retry and cancel down to the runner, with Pipelines page buttons, and `/users`
  empty for members and complete for administrators. [ADR 0041](decisions/0041-labcoat-routes-retry-cancel-directory.md).
- **GitLab clients could not read Graft's lists or send it writes.** Found by reading LabCoat: `/api/v4` issue and
  merge-request lists and search answered objects instead of bare arrays, writes needed JSON, members needed a
  username, lists had no `Link` and refused `order_by`. All fixed. [ADR 0040](decisions/0040-gitlab-request-and-list-shapes.md).

## Closed on 2026-10-31

- **Deleting a branch by push, and refusals that say why.** An agent cleaning up merged branches on the live
  deployment was refused with no reason. `git push --delete` now works unless the operator turns it off with
  `--push-delete false`, with the forge's rules, and every refused reference in a push is reported by Git with
  its reason instead of a bare HTTP 400. [ADR 0039](decisions/0039-deletion-by-push.md).

## Near, and small

Taken top down.

Added on 2026-11-01, first in line: the GitLab clients in use, LabCoat and Comeet.

1. Read Comeet's traffic (Android, `com.monokaijs.comeet`, closed source): its APK from a phone, or a log
   of the `/api/v4` requests Graft has no route for, with the client's user agent. GraphQL only if it sends some.
2. Point LabCoat itself at a forge and walk its screens; every call now has a route (ADR 0041), but no screen has
   been seen working.

Added on 2026-10-31: making "the runner works on Windows, Linux and in Docker" true. Today only
the Windows runner is tested.

1. Publish the linux-x64 runner, NativeAOT, static musl like the forge.
2. Run the acceptance suite with the Linux runner, then run the runner on the Linux 3.10.108 host.
3. Exercise the install scripts' service paths: the `graft-runner` systemd unit and the `GraftRunner` Windows
   service. The suite runs the runner directly, so neither has been run.
4. A runner container image in `deploy/container`, and a test of a runner against a forge running in a
   container with its runner port published.

Jobs inside containers (`image:`) is not on this list; it is in the section below and needs a decision.

## Larger, and worth deciding deliberately

**The rest of continuous integration.** The first slice exists ([ADR 0037](decisions/0037-runners-over-cuprinet.md)):
Graft's own runner over CupriNet, install scripts, a strict subset of `.gitlab-ci.yml`, and artifacts with
chunked, resumable transfers ([ADR 0038](decisions/0038-chunked-runner-transfers-and-artifacts.md)). Still absent:
containers, artifact expiry and passing artifacts between stages, caches, `rules` and the rest of the format, group and project runners (retry and cancel exist since ADR 0041),
detecting a runner that dropped mid-job, and GitLab's runner API for the official `gitlab-runner`.

**More from an import.** [ADR 0042](decisions/0042-import-by-address-and-mirrors.md) imports and mirrors public
repositories' branches and tags. Each of these needs a decision before it is built: credentials for a private
upstream (stored how, shown to whom), Git LFS objects (bandwidth and storage on somebody else's behalf), SSH
addresses, pipelines or webhooks on a mirror update, push mirrors, and the GitLab data import of the requirements
(issues, merge requests, wiki), which is a different and much larger thing.

**GraphQL.** GitLab's newer clients increasingly prefer it. Not implemented, and implementing a useful
subset is a large piece of work with its own schema to keep honest.


## Known boundaries that are decisions, not gaps

These are recorded so nobody reopens them as oversights. Each has a decision record.

- **Merging file contents.** Refused by name, with the paths. [ADR 0014](decisions/0014-collaboration.md).
- **Anonymous writing on a public project.** Refused until there is moderation. Same record.
- **An agent deleting or transferring a project.** Allowed since 2026-11-09 only behind a fourth administrator
  switch, off by default, with the path given again to delete. [ADR 0047](decisions/0047-agent-project-settings.md),
  superseding [ADR 0013](decisions/0013-project-deletion-and-audit.md)'s outright refusal.
- **Account administration, password resets and token issue over the agent surface.** Withheld.
  [ADR 0011](decisions/0011-agent-parity.md).
- **Recoverable token secrets off by default.** [ADR 0012](decisions/0012-recoverable-tokens.md).
- **Project sites need a second hostname.** No single-hostname fallback.
  [ADR 0006](decisions/0006-project-pages-and-public-browsing.md).
- **A line comment ages rather than moves.** When the branch changes it is reported as outdated instead of
  being re-anchored, which is the opposite of what other forges do.
  [ADR 0015](decisions/0015-review-journey.md).
- **An approval is superseded by a push.** It is shown and labelled rather than deleted, and does not count.
  Same record.
- **An agent cannot approve, or delete a branch.** Withheld from the agent surface. Same record.
- **Webhook delivery is a notification, not a guarantee.** Bounded, in memory, lost on restart, oldest
  dropped when full. [ADR 0016](decisions/0016-webhooks.md).
- **Repository bytes are never served with a guessed content type.** Raw files and archives are always an
  attachment of unknown type, so a browser cannot preview them.
  [ADR 0017](decisions/0017-getting-things-out.md).
- **An archive is built on disk and streamed.** It was built in memory, which was sound only while the Git
  engine bounded a repository at a few tens of megabytes; raising that bound to 4 GiB on 2026-10-24 removed
  the assumption, and the archive was converted with it.
  [ADR 0018](decisions/0018-repository-capacity.md) supersedes that part of ADR 0017.
- **SHA-1 repositories only, no shallow or partial clones, no pruning.** The engine keeps Git's format and
  speaks both protocol versions, and stops there by decision: a SHA-256 repository is a different object
  format throughout, GitLab 18 does not host one, `deepen` and `filter` are refused by name, and an object
  nothing points at is kept because a comment may still name its commit.
  [ADR 0028](decisions/0028-git-engine.md).

## Operational, still open from the MVP plan

Unchanged by this work and carried from [the MVP plan](mvp-plan.md).

- Deployment certificate authority and DNS trust are unproven outside local runs.
- The acceptance run on the required Linux 3.10.108 host has not happened.
- The data-protection key ring sits beside the database, which bounds what encrypting token secrets
  protects against. [ADR 0012](decisions/0012-recoverable-tokens.md) states the bound.
