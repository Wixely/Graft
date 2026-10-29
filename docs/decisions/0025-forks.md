# 0025: Forks, and a proposal from outside

- Date: 2026-10-29
- Status: Accepted user request, implemented
- Source: "build out these", naming forks: "without them an outsider has no way to propose a change, since writing needs membership"
- Review trigger: Any proposal to read a fork at merge time, to let a client push a proposal reference, to let a fork's author write anywhere on the original but their own proposal, or to rebase a fork's branch from the original

## What was missing

[ADR 0014](0014-collaboration.md) made writing need membership, and said why. The cost was that somebody
who could read a project and had a change for it had no way to offer it except to ask to be made a member.

## Decision: a fork is a clone with an address

Anybody who may read a repository may fork it: a fork is a copy of the storage into a project in the
forker's own namespace, and the clone was already theirs to take. The fork keeps the original's slug where
that is free, its name, its description and its visibility, so that the people who could see the original
can see what was proposed from the copy. It remembers where it came from, and that is all the relation is:
a fork outlives its original as an ordinary project. One fork per person per project, and never into the
namespace the original lives in.

The copy is file by file and only the client references travel; the original is only read.

## Decision: a proposal from a fork is mirrored, not fetched

A proposal may name a fork as the project its source branch lives in. Before the engine looks at it, the
server mirrors the fork's branch tip into the target repository under `refs/merge-requests/<iid>/head`,
copying whatever objects the target lacks, validated the way a pushed batch is. Analysis, the diff, the
code owners and the merge then read that reference, so the target never reads the fork at merge time and a
fork that has since gone cannot take a merged commit with it. The reference follows the fork's branch:
every read brings it up to date.

Clients may fetch the reference, which is what GitLab does with the same name, and may not push one: the
receive path refuses any reference that is not a branch or a tag, and the fork copy leaves them behind.

A proposal from a fork is not rebased by the original, because the branch is the fork's to rewrite.
Removing the source branch after a merge is not offered either, for the same reason.

## Decision: the author may talk about their own proposal, and nothing else

Opening a proposal from a fork needs the permission to push to the fork and to read the original, and no
membership of the original. That is the one way somebody outside a project's membership writes on it, and
it is narrow on purpose: a proposal from a fork is a branch of work with a name on it, not an anonymous
message, which is what ADR 0014 refused.

The author may comment on their own proposal, because a review they cannot answer would be a review they
cannot pass. They may not comment on anything else on the original, open an issue, or propose from the
original's own branches. Every other rule stands: approving needs the standing to push to the original,
and merging is the original's maintainers' to do.

## The API

GitLab's `POST /projects/:id/fork` and `GET /projects/:id/forks`, `forked_from_project` and `forks_count` on
a project, and `source_project_id` on a proposal, sent to the target project's route as GitLab's clients do.
`source_project_path` is Graft's own, so a reader knows where the branch is without a second request.

## What an agent can do with this

`graft_fork`, taking the surface from 36 to 37, and `graft_open_merge_request` takes a source project.

## What was verified

A stranger cannot push to a public project and can fork it, keeping its visibility; a second fork and a
fork into the owner's own namespace are refused; the original counts and lists its forks; the fork clones
with the original's content; the stranger pushes a branch, proposes it to the original and sees how it
would merge; cannot propose from the original's own branches; may comment on their own proposal and not on
an issue; the maintainer sees the diff; the original advertises the proposal reference and refuses a push to
it; a rebase is refused; after another push to the fork the maintainer merges what now stands, the fork's
branch is not removed, the original has the work, and `git fsck --strict` passes.
