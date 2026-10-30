# Backlog

Recorded: 2026-10-22. Owner: Implementation agent; what gets built next is the user's call.
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
taken on in the order the user gave: SSH, optional email, groups, the project-scoped audit view.

- **`features` in `.graft/project.json`.** Every name was an error because there was nothing to name. The
  seven surfaces that exist are now names, and a name is a link offered only to a reader the project would
  admit, never a grant. [Project pages](project-pages.md#features).
- **Pushing needs a token and nothing said so.** The clone menu now says it where Git asks for the
  password, and the readme has a section on cloning and pushing, including how to clear a credential helper
  that cached the refused password.
- **Git's indent heuristic.** The diff's two home-grown normalisation passes were replaced by a port of
  Git's `xdl_change_compact`, heuristic included. Agreement with `git diff` at its defaults went from 95% to
  99.3% over 1,758 fresh pairs. The residual is Git's search choosing a different script of the same length.

Then the first of the four large items, in the order the user gave:

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

## Near, and small

Nothing is here at the moment. Everything below needs a decision before it is built.

## Larger, and worth deciding deliberately

**A project Maintainer cannot read their project's audit events.** The trail is instance-wide and
administrator-only, because most of what it records is not about a project. A project-scoped view would need
its own query and a rule about which entries are a project's business.

**Continuous integration and a runner protocol.** Recorded as a long-term goal since
[ADR 0001](decisions/0001-name-and-compatibility.md) and still entirely absent. CupriNet remains a
user-requested candidate rather than a chosen dependency.

**GraphQL.** GitLab's newer clients increasingly prefer it. Not implemented, and implementing a useful
subset is a large piece of work with its own schema to keep honest.

**Nested groups and namespaces.** A namespace is an account name. There are no groups, so there is no way to
own projects collectively or to grant access to a team rather than to people one at a time.


## Known boundaries that are decisions, not gaps

These are recorded so nobody reopens them as oversights. Each has a decision record.

- **Merging file contents.** Refused by name, with the paths. [ADR 0014](decisions/0014-collaboration.md).
- **Anonymous writing on a public project.** Refused until there is moderation. Same record.
- **An agent deleting a project.** Withheld from the Model Context Protocol surface.
  [ADR 0013](decisions/0013-project-deletion-and-audit.md), joining the four in
  [ADR 0011](decisions/0011-agent-parity.md).
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

- The tested DnaX packages have no approved durable source in this repository, so a clean external checkout
  cannot restore. Since 2026-10-29 DnaX's own CI publishes every version to GitHub Packages under Wixely,
  which is a durable source; adopting it means adding an authenticated feed to `nuget.config`, which is the
  user's call. Until then Graft restores `10.0.0-alpha.13` from the local, ignored feed.
- Deployment certificate authority and DNS trust are unproven outside local runs.
- The acceptance run on the required Linux 3.10.108 host has not happened.
- The data-protection key ring sits beside the database, which bounds what encrypting token secrets
  protects against. [ADR 0012](decisions/0012-recoverable-tokens.md) states the bound.
