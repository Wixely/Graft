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

## Near, and small

**Replies, threads and resolving a conversation.** A line comment is standalone: there is no reply, no
thread, and no marking a conversation settled. With line comments in place this is the next thing a reviewer
reaches for, and it is mostly a parent id on a comment plus a resolved flag with a rule about who may set it.

**Editing a comment.** It can be deleted by its author or a Maintainer and that is all. An edit wants a
record that it was edited, which is the part worth thinking about rather than the storage.

**Tags and releases.** Pushed tags are stored and understood by the Git engine; nothing lists them. There is
no tag in the API, none in the browser, and no concept of a release at all. Listing is nearly free, since
the refs are already there. A release with notes and attachments is a feature of its own.

**Raw file download and archives.** There is no way to fetch one file or a snapshot without cloning. GitLab
serves `/repository/files/:path/raw` and `/repository/archive.tar.gz`. The first is small. The second needs
a tar or zip writer over the object model, which is bounded work but not trivial.

**Deleting a file through the API.** Files can be created and updated over the API; `DELETE` on the same
route is not implemented, and the browser cannot delete either. The object writer gained a whole-tree
writer for merges, which is most of what a deletion needs.

**An empty repository's project page reads "Repository unavailable".** The browse endpoint answers 404 when
there is no tree, and the page reports that as a permission problem. A project somebody has just created
therefore looks broken. Found on 2026-10-22 while checking other work.

**`Commit.razor` resolves a project through the caller's own project list.** That means a commit link into
a public project fails for a reader who is not a member, even though the commit is readable. The newer
collaboration pages resolve through the browser repository endpoint instead, which answers for any reader;
this page should do the same.

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

## Operational, still open from the MVP plan

Unchanged by this work and carried from [the MVP plan](mvp-plan.md).

- The tested DnaX packages have no approved durable source, so a clean external checkout cannot restore.
- Deployment certificate authority and DNS trust are unproven outside local runs.
- The acceptance run on the required Linux 3.10.108 host has not happened.
- The data-protection key ring sits beside the database, which bounds what encrypting token secrets
  protects against. [ADR 0012](decisions/0012-recoverable-tokens.md) states the bound.
