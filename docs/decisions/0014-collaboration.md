# 0014: Issues, merge requests, and a merge that says what it cannot do

- Date: 2026-10-22
- Status: Accepted user request, implemented
- Source: "then work on the collaboration points", after a review found that Graft hosted code and nothing about changing it
- Review trigger: Any proposal to merge file contents, to let a non-member write on a public project, to make a merged proposal reopenable, or to decide mergeability inside a listing

## What was missing

Graft hosted, browsed and served code, and published a page about it. It had nothing to say about changing
it: no issues, no merge requests, no comments, no review. That was the single largest difference between
what existed and what anybody expects from a forge, and unlike the rest of the gaps it was not a small
omission but a missing half of the product.

## Decision: reading follows the repository, writing does not

Two new actions join the authorization model. `ReadCollaboration` is granted wherever the repository can be
read, including anonymously on a public project. `WriteCollaboration` is granted only to members.

A forge whose code is public and whose conversation is private has the two the wrong way round, so reading
follows visibility. Writing does not, because a public project that accepted anonymous writing would be a
spam target and Graft has no moderation to fall back on: no reporting, no rate limiting per account, no
blocking, nobody to triage. Until it has those, the honest position is that participating requires somebody
to have added you.

The consequence is visible and worth stating: on a public project a read succeeds and a write is refused
with 403 rather than 404, because the caller has already demonstrated they can see the project.

The scopes follow the same logic. Collaboration is an API surface, so neither repository scope reaches it
in either direction: a token minted to clone is not a token to read somebody's issues.

## Decision: separate tables, shared comments, numbers per project

Issues and merge requests are separate tables. They differ in more than a label: a merge request names two
branches, can be merged, and carries a merge commit. One table with a kind column would be half-empty for
every issue and its constraints could not say which columns belonged to which kind.

Comments are shared, because a comment is the same thing on either: an author, a body and a time.

Both are numbered from one within their project, the way GitLab numbers them, so `#1` means two different
things in two projects. The database holds no counter; a new row takes the current maximum plus one inside
the same immediate transaction that inserts it, so two concurrent authors cannot be handed the same number.
A unique constraint is the backstop if that reasoning is ever wrong.

## Decision: the author or a Maintainer, for both words and state

The author may edit and close their own. Anybody else needs the permission to manage the project.

This is stricter than GitLab, where a Developer can close any issue. The reasoning is that a Reporter who
can file an issue should not be able to rewrite somebody else's words, and that a project still needs
somebody who can close what an author has abandoned. One rule covering both cases is easier to predict than
two, and the stricter of the two is the one that cannot surprise anybody.

A merged merge request is final. It cannot be reopened or edited, because reopening it would propose work
the target branch already contains.

## Decision: Graft merges two cases and refuses the third, by name

This is the decision that defines the feature's honesty, so it is stated plainly.

**Fast-forward.** Where the target branch is an ancestor of the source, the reference moves and no commit
is written. This is exactly what a client's own fast-forward push would have done.

**Three-way.** Where both branches have moved and no single file was changed on both sides, Graft writes a
merge commit with two parents whose tree takes each side's changes. A deletion counts as a change, so a
file one side deleted and the other edited is a disagreement rather than a quiet resolution.

**Conflict.** Where both sides changed the same file, Graft refuses and names the files.

Resolving that third case means merging line runs within a file: a second diff algorithm, conflict markers,
a half-merged state with nowhere to live, and no way to show somebody the result before it is committed. A
forge that produced a silently wrong merge would be worse than one that says it cannot, because the wrong
merge is discovered later and by somebody else. So the boundary sits at file granularity and is reported
precisely, with the paths, rather than as a status word. The author merges locally, where they can see what
they are resolving, and pushes.

`merge_method` reports which of the three a proposal is in before anything is merged. The interface says it
in a sentence rather than a label, because the sentence is the part that tells somebody what to do next.

Unrelated histories merge rather than refuse, against an empty base. Every path present on both sides then
counts as changed on both sides, so anything but a genuinely disjoint pair of trees reports a conflict,
which is the conservative reading.

## Decision: a listing does not decide mergeability

Deciding whether one proposal merges means flattening three trees. Doing that for every row of a page would
make listing proposals cost as much as the work it lists.

So a listing reports `unchecked`, which is GitLab's own vocabulary for exactly this, and reading one
proposal computes the answer. The alternative, a stored verdict, would be wrong the moment somebody pushed,
and a forge that confidently reported a stale verdict is the thing this feature is trying not to be.

## Decision: the merge is an ordinary Git commit

The merge commit is built by the same object writer that commits a file from the browser, validated by the
same reachability walk a pushed pack gets, and published through the same durable path. Its parents are the
target first and the source second, which is Git's own order, so `diff HEAD^` shows what the merge brought
in. A client fetching afterwards sees a commit with two parents and nothing that identifies it as
server-authored beyond the committer's name.

Graft accounts carry no email address and a commit needs one, so merges are attributed at the `.invalid`
domain reserved for this, as server-authored commits already were.

## What an agent can do with this

Nine tools, taking the Model Context Protocol surface from 19 to 28: list, read, open and close issues;
comment on either kind; list, read and open merge requests; and merge. The writing tools are refused unless
an administrator has allowed writes, like every other mutation.

`graft_merge` is the one that matters, and it is offered rather than withheld because it cannot destroy
anything: it either moves a branch forward or writes a commit with both parents reachable, and it refuses
rather than guessing. That is a different risk from deleting a project, which [ADR 0013](0013-project-deletion-and-audit.md)
withholds.

## What is not here

No labels, milestones, assignees or due dates. No review approvals, no required reviewers, no draft state.
No cross-references between issues and commits. No reactions. No editing a comment after posting, though it
can be deleted by its author or a Maintainer. No line-level comments on a diff, which is the one of these
that would most change how review feels and is the largest single item in [the backlog](../backlog.md).

## What was verified

The Git suite went from 483 to 556. Against a running server: issues number from one and independently from
merge requests; a read_api token reads and cannot write; a repository-only token cannot read issues at all;
a private project's issues are concealed from a stranger and a public project's are readable with no
credential while writing is still refused; a Developer cannot rewrite or close another author's issue and a
Maintainer can; comments are listed in order and counted on the issue.

For merges: a proposal naming a branch that does not exist is refused at creation, a second open proposal
for the same pair is refused, a proposal whose branches changed one file reports the conflict and names the
file, merging it is refused and the refusal names the file, a proposal over disjoint files reports
`three_way` and merges with a merge commit, one whose target has not moved reports `fast_forward` and
merges without one, a merged proposal cannot be merged twice or reopened, and merging needs the permission
a push needs. Real Git then fetches the result, finds two parents, finds both sides' changes in the tree,
and passes `git fsck --strict`.

See [the evidence](../validation/2026-10-22-collaboration.md).
