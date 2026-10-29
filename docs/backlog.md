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

## Near, and small

**Releases.** Tags exist; a release does not. No notes, no attachments, no marking a tag as a release, and
no listing of them. The tag machinery is in place, so this is the notes-and-files half.

**Deleting a file through the API.** Files can be created and updated over the API; `DELETE` on the same
route is not implemented, and the browser cannot delete either. The object writer gained a whole-tree
writer for merges, which is most of what a deletion needs.

**Token rotation.** Regenerating a token in place, keeping its name, scopes and expiry while issuing a new
secret. Offered when recoverable secrets were chosen instead in
[ADR 0012](decisions/0012-recoverable-tokens.md); both could exist, and rotation is the one that helps on a
server that keeps no secrets.

**Icons are filled despite asking not to be.** The icon set is authored as stroke geometry with
`fill="none"`, and a stylesheet rule sets `fill: currentColor`, which overrides the attribute. Every closed
shape is therefore solid. The set reads acceptably this way and was designed around it in practice, but the
markup and the stylesheet disagree and one of them should change.

## Larger, and worth deciding deliberately

**A project Maintainer cannot read their project's audit events.** The trail is instance-wide and
administrator-only, because most of what it records is not about a project. A project-scoped view would need
its own query and a rule about which entries are a project's business.

**Search.** There is no search of code, of issues, or of anything but the public project listing's name and
description match. Issue search is the one people miss first.

**SSH.** Smart HTTP only. SSH means a host key, an authorized-keys store, a key management interface and a
second transport to keep correct. It is the most requested thing on this list and the most work.

**Email, and anything that depends on it.** Graft holds no email addresses and sends nothing. Password
reset is therefore administrator-driven with no self-service path, there are no notifications, and there is
no second factor. Adding email is a dependency decision as much as a feature.

**Continuous integration and a runner protocol.** Recorded as a long-term goal since
[ADR 0001](decisions/0001-name-and-compatibility.md) and still entirely absent. CupriNet remains a
user-requested candidate rather than a chosen dependency.

**GraphQL.** GitLab's newer clients increasingly prefer it. Not implemented, and implementing a useful
subset is a large piece of work with its own schema to keep honest.

**Large file storage.** No LFS. A repository with LFS pointers clones and pushes, and the pointers are
ordinary text files that resolve to nothing.

**Nested groups and namespaces.** A namespace is an account name. There are no groups, so there is no way to
own projects collectively or to grant access to a team rather than to people one at a time.

**Forks.** No fork, and therefore no proposal from outside a project's membership. With writing restricted
to members, this is the mechanism an outside contributor would otherwise use.

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
- **`features` in `.graft/project.json`.** Parses, and naming anything is an error, because a config
  claiming to expose issues had to fail loudly while there were no issues. Now that there are, this is
  worth revisiting. [Project pages](project-pages.md).
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

## Operational, still open from the MVP plan

Unchanged by this work and carried from [the MVP plan](mvp-plan.md).

- The tested DnaX packages have no approved durable source, so a clean external checkout cannot restore.
- Deployment certificate authority and DNS trust are unproven outside local runs.
- The acceptance run on the required Linux 3.10.108 host has not happened.
- The data-protection key ring sits beside the database, which bounds what encrypting token secrets
  protects against. [ADR 0012](decisions/0012-recoverable-tokens.md) states the bound.
