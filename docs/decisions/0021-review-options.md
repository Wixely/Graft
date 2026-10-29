# 0021: Squash, rebase and code owners

- Date: 2026-10-29
- Status: Accepted user request, implemented
- Source: "build this out", naming squash, rebase and code owners from the collaboration depth list
- Review trigger: Any proposal to rebase a branch holding a merge commit, to keep an approval across a rebase, to read CODEOWNERS from the source branch, or to let a code owner approve without the permission to push

## What was missing

A proposal merged in two ways or not at all, and nothing else could be done to it on the server. A branch
that had fallen behind its target could only be brought up to date by its author's own client. Nobody was
recorded as responsible for any part of the tree, so a project could ask for a number of approvals and
could not ask for the right person's.

## Decision: a squash is a merge that forgets

`squash` on the merge route lands one commit on the target whose tree is exactly what the ordinary merge
would have produced and whose only parent is the target's tip. It is refused exactly where a merge is
refused, because it answers the same question with a different shape of commit. The message is the one
given, or the proposal's title.

The source branch keeps its commits, so nothing is lost from the repository; what the target records is
that one change arrived, written by whoever squashed it. The suite checks that the branch's own commits
are not ancestors of the target afterwards, because that is the whole point and is also the thing a
reader is most likely to be surprised by.

## Decision: a rebase replays each commit as its own three-way merge, and refuses by name

`PUT .../merge_requests/:iid/rebase` rewrites the source branch onto the target's tip. Each commit is
replayed in order as a cherry-pick: its own change, from its parent to itself, applied to the branch as
rebuilt so far, with the same file-level rule the merge uses. Where a replayed change and the target changed
the same file, the rebase refuses and names the file; the answer is the same as for a conflicting merge,
which is to do it locally where the conflict can be seen.

Authors and messages are kept; the committer is whoever rebased, which is what Git itself records. A branch
holding a merge commit is refused rather than flattened, because replaying a merge commit as a single
change would silently alter what it merged. More than two hundred commits is refused as well.

**A rebase is a forced push, and is treated as one.** It needs the permission a push needs. It changes the
source tip, so every approval becomes superseded and every line comment becomes outdated, exactly as
[ADR 0015](0015-review-journey.md) decided for a push: the things they were about are no longer on the
branch. The response says so in its shape, carrying the previous and the new tip. GitLab's rebase is
asynchronous and answers `202`; Graft's is done when the response is written and answers `200`.

A rebase is reported to webhooks as a push with `forced: true`, because a receiver that mirrors the branch
needs to know that it moved backwards.

## Decision: code owners come from the target branch, and may be required

A `CODEOWNERS` file, at the repository root or under `.graft`, `.gitlab` or `docs`, names who owns which
paths in the common syntax: a gitignore-style pattern, then usernames with `@`. The last matching rule wins.
Names are usernames and nothing else; there are no groups to name.

The file is read from the **target** branch, the branch the change would land on, because it is that
branch's owners who have the say. Reading it from the source would let a proposal rewrite who may approve
it.

A proposal reports the owners of every changed path the file names. A project may then require that every
such path has a current approval from one of its owners, which is a second gate beside the approval count
and is enforced at the merge the same way, so a client that never asks cannot merge past it. An owner
approves exactly as anybody else does, so an owner who cannot push cannot approve, and a Reporter named in
the file is a reader with an opinion rather than a gate. With no file, the rule requires nothing.

## What an agent can do with this

`graft_merge` gained `squash`. There is no tool to rebase. A rebase rewrites a branch other people may have
fetched, which is the class of act ADR 0015 withholds for deleting a branch, and an agent that can rewrite
history has a blast radius the switch that bounds it does not describe.

## What was verified

The Git suite went from 723 to 768 across this record and ADR 0022, which were verified together. A proposal names the owner of the one owned path it changes and
nothing for the unowned one; a Maintainer turns the rule on and a Developer cannot turn it off; the merge
is refused naming the path; a Reporter cannot approve; the owner's approval satisfies it. Rebasing needs
the permission to push; a rebase rewrites both commits onto the target, turns the proposal into a
fast-forward and supersedes the approval; real Git sees the rebased branch with both original messages;
rebasing again rewrites nothing. A squash lands as one commit with one parent and the message given, real
Git finds both branches' changes in its tree, the branch's own commits are not ancestors of the target, and
`git fsck --strict` passes. A rebase that would have to merge a file refuses naming the file, and a branch
holding a merge commit refuses saying why.
