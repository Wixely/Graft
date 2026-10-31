# 0039: A push may delete a branch or tag, and every refusal says why

- Date: 2026-10-31
- Status: Accepted, implemented
- Source: On 2026-10-31 an agent cleaning up merged branches on the live deployment reported that
  "the server rejected deletion of these remote branches without explaining why". Deletion by push was then chosen,
  configurable.
- Supersedes: in part, [ADR 0015](0015-review-journey.md)'s "branches are managed without a Git client"
- Review trigger: protected branches or tags; a request for GitLab's `receive.denyDeleteCurrent`-style project setting;
  the `atomic` capability; push options; a client that cannot read a per-reference refusal

## What happened

Graft refused any push command that deleted a reference, and did not advertise `delete-refs`, so Git refused on the
client with "remote does not support deleting refs". An agent tidying merged branches read that as an unexplained
server refusal. The same branches could be deleted through the forge's API and browser by anybody allowed to push,
so the refusal protected nothing; it only broke `git push --delete` and `git push origin :branch`.

Separately, a push the engine refused for any reason, such as a forced rewrite, was answered with HTTP 400 and
Git showed only "RPC failed". The reason never reached the person pushing.

## Decision: deletion by push follows the forge's rules, on by default, off by switch

`--push-delete true|false`, `GRAFT_PUSH_DELETE` in the container, defaults to true. On, the forge advertises
`delete-refs` over HTTPS and SSH and accepts a command that deletes a branch or tag, with exactly the rules of
deleting through the forge:

- the permission to push, which receive-pack already requires;
- never the default branch, refused by name, because a repository whose HEAD names nothing serves clients badly;
- a stale old value is refused like any stale update.

Off, `delete-refs` is not advertised, Git refuses on the client as before, and a forced deletion is refused with
a reason. The forge prints `PUSH DELETE: on` or `off` at startup.

Webhooks and pipelines already distinguished a deleted branch, which reports no new commit; a push over SSH now
reports deletions too, as HTTPS did. A deleted branch starts no pipeline.

The agent rule of ADR 0015 is unchanged: there is no MCP tool to delete a branch. An agent with a shell and a push
token could already delete through the REST API; this adds Git as a second way for the same token, not a new reach.

## Decision: refusals are per reference and carry a reason

Each command in a push is now judged on its own, as Git's receive-pack does without the `atomic` capability. The
ones that pass are applied; the ones that do not are reported with `ng <ref> <reason>` in the status report, which
Git prints beside the reference, for example
`! [remote rejected] main (this is the default branch; choose another default first)`. The reasons cover a stale
reference, a deletion when deletion is off, the default branch, a missing object, a branch not pointing at a
commit, a non-fast-forward, a moved tag and a name that conflicts with another reference.

Only what makes the whole request untrustworthy still refuses it whole: a malformed command, a bad or oversized
pack, objects that do not validate, more references than the repository allows. A client that did not ask for
`report-status` cannot be told which command failed, so for it the push stays all or nothing, as before. Every
real Git client asks.

This changes one property of the engine: a push with one good and one bad command used to apply neither and now
applies the good one. That is Git's own default behaviour; `atomic` is not offered.

## What was verified

The Git acceptance suite, 1188 to 1196 assertions, managed: a push deleting two branches, one named with a slash,
and a tag; the default branch refused with the reason in Git's output; a push creating one branch while deleting
the default applies the first and refuses the second; a forced rewrite refused with its reason; with
`--push-delete false` the forge says so and a deletion leaves the branch; a deletion-only push over SSH, which
carries no pack. The engine self-checks in `experiments/managed-git` (44) now check per-reference refusals, the
partial batch and deletion when allowed. The MCP suite (104) passes unchanged.

Not verified: a NativeAOT publish of this change; the live deployment, which needs the new build to pick it up.
