# Project ownership continuity evidence

Date: 2026-10-04. Owner: Implementation agent. Review when the ownership model, the project namespace rule, or the account disable rules change.

## Why this existed

A project has exactly one owner, stored as `Projects.OwnerUserId`, and only that owner or an administrator is granted `ManageProject`. Nothing stopped an administrator disabling an owner, and there was no way to appoint a replacement. Disabling an owner therefore left a project that only administrators could manage, permanently. This was the repository's own outstanding recommendation and predates the 2026-10-04 feature work.

## What changed

**Ownership can be transferred.** The current owner or an administrator can hand a project to another active account. A Maintainer membership deliberately does **not** grant this: managing a project is not the same as the right to give it away.

**An account that still owns projects cannot be disabled.** The refusal names the projects that block it, so the administrator knows what to transfer. It is a `409`, not a `400`: nothing about the request is malformed.

A hard refusal was chosen over disabling anyway, because disabling anyway is what produced the orphaning in the first place. It does not slow an urgent response to a compromised account: an administrator can already reset that account's password, which revokes every session and token immediately, and that path is unchanged.

## The consequence worth knowing

**Transferring a project changes its URL.** A project's namespace is its owner's username, so `alice/thing` becomes `bob/thing` and the previous clone URL stops resolving. Graft has no redirect for the old path. GitLab does the same thing on a namespace transfer, but it does keep redirects, so this is a deviation. The browser warns before the transfer and names the resulting path.

The previous owner is left a Maintainer membership rather than losing access outright, which also keeps the operation reversible by the new owner.

## Checks

`tests/Graft.Acceptance`, 92 to 110 assertions:

- An account owning projects cannot be disabled, and the refusal leaves the owner's access intact.
- A Maintainer member cannot transfer ownership; an owner can.
- Transfer to an unknown account, and to the account that already owns the project, are both refused.
- After transfer the new owner manages the project and the previous owner retains access through a membership rather than ownership.
- The project stops resolving at its old path and resolves at the new one.
- Transferring away unblocks the disable that was previously refused, and the former owner can then no longer log in.

Removing the disable guard makes the suite fail on that assertion, which was checked by actually removing it rather than assumed.

## Boundaries

- One owner per project. This is not multi-owner or group ownership, and "sole active owner" is therefore the same thing as "owner".
- A transfer that would collide with a project path the new owner already uses is refused on the unique constraint and reported as a conflict; the collision is not resolved automatically.
- No redirect exists for the previous path, and no audit trail is exposed in the UI; the transfer is recorded as an audit event in the database only.
- Repository storage is keyed by an opaque storage id, so a transfer moves no files on disk.

Recommended next action: Owner: User — decide whether a moved project should keep its old path working, since a redirect is the main difference from GitLab's behaviour here.
