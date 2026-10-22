# Backlog

Recorded: 2026-10-22. Owner: Implementation agent; what gets built next is the user's call.
Review: whenever a batch of this is taken on, and when anything here is closed.

This is the list of things Graft does not do, written down so the absence is a decision rather than a
surprise. It came out of a review on 2026-10-22 that asked what was missing. The two smallest items from
that review and the largest one were built immediately: project deletion and the audit trail in
[ADR 0013](decisions/0013-project-deletion-and-audit.md), and issues, merge requests and comments in
[ADR 0014](decisions/0014-collaboration.md). Everything else that review found is here.

Each entry says what is missing, why it matters, and roughly what it would take. Nothing here is scheduled.

## Near, and small

**Line-level comments on a diff.** Comments attach to an issue or a merge request as a whole. Anchoring one
to a file and line is the single largest remaining difference between Graft's review and the review people
expect, and it is what makes a merge request a conversation about code rather than about a change. Needs a
comment to carry a path, a line and the blob it was written against, plus a rule for what happens to it
when the branch moves underneath it. That last part is the real work.

**Tags and releases.** Pushed tags are stored and understood by the Git engine; nothing lists them. There is
no tag in the API, none in the browser, and no concept of a release at all. Listing is nearly free, since
the refs are already there. A release with notes and attachments is a feature of its own.

**Raw file download and archives.** There is no way to fetch one file or a snapshot without cloning. GitLab
serves `/repository/files/:path/raw` and `/repository/archive.tar.gz`. The first is small. The second needs
a tar or zip writer over the object model, which is bounded work but not trivial.

**Deleting a file through the API.** Files can be created and updated over the API; `DELETE` on the same
route is not implemented, and the browser cannot delete either. The object writer gained a whole-tree
writer for merges, which is most of what a deletion needs.

**Creating and deleting branches over the API.** A branch appears by being pushed to, or by a file write
that creates it. `POST` and `DELETE` on `/repository/branches` are absent. Small, and the permission rule is
already the one a push obeys.

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

**Webhooks.** Nothing can be notified that anything happened. Needs outbound requests from the forge, which
brings retries, a queue, timeouts and an egress policy an operator will want to bound.

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

## Operational, still open from the MVP plan

Unchanged by this work and carried from [the MVP plan](mvp-plan.md).

- The tested DnaX packages have no approved durable source, so a clean external checkout cannot restore.
- Deployment certificate authority and DNS trust are unproven outside local runs.
- The acceptance run on the required Linux 3.10.108 host has not happened.
- The data-protection key ring sits beside the database, which bounds what encrypting token secrets
  protects against. [ADR 0012](decisions/0012-recoverable-tokens.md) states the bound.
