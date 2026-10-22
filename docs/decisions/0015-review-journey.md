# 0015: Closing the review journey

- Date: 2026-10-22
- Status: Accepted user request, implemented
- Source: "close these out please", after a walk through the merge request journey found what each step was missing
- Review trigger: Any proposal to move a line comment when its code moves, to let a stale approval count, to let an author approve their own proposal, or to delete a branch that a project opens at

## What the journey was missing

[ADR 0014](0014-collaboration.md) built merge requests. Walking the whole journey afterwards found that the
pieces around them did not connect.

A branch could not be made except by pushing one. The proposal page listed which files changed and by how
many lines, and showed none of the change: the hunks were in the response and the page did not render them,
so review happened by cloning. Comments attached to the proposal as a whole. Nothing recorded that anybody
had looked, so nothing could require that somebody did. After a merge the source branch stayed, with no way
to remove it short of a push.

Each of those is small. Together they meant the forge could host a proposal and not a review.

## Decision: the diff is the page

The proposal page now renders the change, with the same markup and the same engine as the commit page. The
summary of files stays above it as a table of contents, with a link into each file.

That is presentation rather than capability, and it was the most valuable thing on the list, which is worth
recording: the server had been returning everything needed since the day merge requests existed.

## Decision: a line comment records its commit, and ages rather than moves

A comment may name a file, a line, and which side of the diff the line is on. The server records the source
branch tip it computed that diff from, and the anchor is the server's own reading rather than a value the
client sends, so a client cannot anchor a comment to a commit it never read.

When the branch is pushed to afterwards, the comment does not move. It is reported as outdated and shown
beside the file rather than inside the diff.

**That is the decision, and the alternative is worse than it looks.** Re-anchoring a comment by tracking the
line through a diff is possible and other forges do it. It is also how a comment ends up against code its
author never saw, which is a way of putting words in somebody's mouth. A comment that says plainly "this was
about an earlier version" is less convenient and cannot mislead. Nothing deletes an outdated comment either:
somebody said it, and the record of having looked is part of what review is.

Anchored comments are only for merge proposals. An issue has no diff, and a comment naming a line of one
would be a position with nothing to position it against.

## Decision: approvals count only against the branch being merged

A project may require a number of approvals. Each approval records the commit it was given against, and
counts only while that commit is still the source branch tip. A push supersedes the approvals of what came
before.

A superseded approval is shown and labelled rather than deleted, for the same reason an outdated comment is.
The person did look; what they looked at is no longer what would be merged.

This is stricter than GitLab's default, where an approval survives a push unless the project asks otherwise.
The reasoning is that the lenient default is the one that produces the surprising outcome: code merging with
an approval nobody gave it. Making the strict behaviour the only behaviour means the gate means one thing.

**An author cannot approve their own proposal.** A gate its subject can satisfy is not a gate.

**Approving needs the permission to push, not merely to comment.** An approval is what permits a merge, so
it needs the standing of somebody who could have merged it themselves. A Reporter can still say "this looks
right" in a comment, which is an opinion rather than a key.

**The gate is enforced at the merge, not only in the interface.** The count is re-read inside the merge
path, so a client that never asks about approvals cannot merge past them.

## Decision: branches are managed without a Git client, and deleting one refuses the default

Creating a branch points a new reference at a commit the repository already holds; deleting one removes a
reference and leaves the objects. Neither writes an object, and both need the permission a push needs,
because both are what a push would otherwise have done.

Creating refuses a name that already exists rather than moving it, because moving a branch is a different
act with different consequences and should not be reachable by a creation that was meant to fail.

Deleting refuses the branch the project opens at. A repository whose HEAD names nothing is handled badly by
several clients and well by none, so the default branch is changed first and deleted second.

A merge can take the source branch with it, and a merged proposal offers to remove it afterwards. The
removal happens inside the same lock as the merge, so nothing can push to that branch between the two and
have the push silently removed.

## What an agent can and cannot do with this

`graft_comment` gained a file, a line and a side, so an agent can review a line rather than a change.
`graft_create_branch` was added, because opening a place to work from a chosen starting point is ordinary.

There is no tool to approve, and that is deliberate. An approval exists to record that a person looked, and
a gate an agent could satisfy would not be a gate. There is no tool to delete a branch either, for the same
reason there is none to delete a project: an agent should not be one call from removing the branch somebody
is working on.

## What was verified

The Git suite went from 556 to 603. Branches: a read-only token cannot create one, a branch is created from
a named revision, a duplicate is refused, an unresolvable starting point is refused, a branch is deleted and
deleting it again is a 404, and the default branch is refused. Comments: half a position is refused, an
unknown side is refused, an anchored comment carries its path, line, side and commit, a Reporter may write
one, and an issue refuses one. Approvals: a Maintainer sets the rule and a Developer cannot, a merge is
refused while approvals are outstanding, an author cannot approve their own, a Reporter cannot approve, a
reviewer can, a push supersedes the approval and the merge is refused again, and an approval can be
withdrawn and given again. Merging with `should_remove_source_branch` removes the branch and the work
survives on the target.

The diff rendering, a line comment, the approval gate and the settings panels were driven by hand in a
browser. See [the evidence](../validation/2026-10-22-review.md).
