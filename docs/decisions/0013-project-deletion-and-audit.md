# 0013: Deleting a project, and letting somebody read the audit trail

- Date: 2026-10-22
- Status: Accepted user request, implemented
- Source: "ok fix the absences", following a review that found two things the product recorded but could not do
- Review trigger: Any proposal to let a Maintainer delete a project, to make deletion recoverable or deferred, to widen the audit trail beyond administrators, or to let an agent delete anything

## The two absences

A review of what Graft could not do turned up two gaps that were small, surprising, and of a kind: in each
case the product had done the hard half and left the obvious half undone.

**A project could not be deleted.** Not from the browser, not over the API, not at all. There was no route
and no storage method. A project created by a typo was permanent, and so was one created by a push to a
mistyped path once push-to-create was on.

**The audit trail could not be read.** Every sensitive act had written a row since the first migration, and
nothing could read one back without opening the SQLite file. A trail nobody can consult is a trail that
exists for a hypothetical forensic exercise rather than for the operator who has one now.

## Decision: deleting needs the owner, not a Maintainer

Every other project operation tops out at `ManageProject`, which a Maintainer holds. Deletion does not use
it. The store requires the project's owner or an administrator, and refuses a Maintainer with a 403.

The reasoning is that managing a project and ending it are different acts. A Maintainer was trusted to run
the project: to grant membership, change visibility, publish a page. None of those destroy anything.
Ownership transfer already draws the line in the same place for the same reason, and drawing it differently
here would mean a Maintainer could end a project they could not have given away.

An administrator may delete anyone's project, which is the same authority they already hold over every
other project setting.

## Decision: the browser confirms, the API does not

The browser requires the project's full path to be typed back, and the server checks it. The API requires
no confirmation at all.

That is not an inconsistency. A browser click can be a misclick, and the typed path is what makes deletion
a deliberate act rather than a fast one. An API caller is a program that already had to name the project in
the URL it sent; a confirmation field it fills in from the same variable confirms nothing, and would only
add a step for a caller that was always going to take it.

## Decision: immediate rather than scheduled, and 204 rather than 202

GitLab answers `202 Accepted` because its deletion is scheduled and happens later. Graft's deletion is
complete when the response is written, so it answers `204 No Content`. A client that polled for the project
to disappear would be polling for something that had already happened, and reporting 202 to look compatible
would be a lie about the only thing the status code says.

This is recorded as a deviation in [the compatibility matrix](../compatibility.md).

There is no recycle bin and no undelete. Both are real features and both are absent; what exists instead is
that the act is irreversible and says so twice before it happens.

## Decision: the database first, the repository afterwards

The project row and everything hanging from it are removed in one transaction. Only then is the repository
directory removed from disk.

That order is chosen for how it fails. If the directory removal fails, the instance keeps a directory
nothing references, named by a random identifier that will never be issued again: harmless, and recorded,
because the audit row the deletion wrote names the storage directory. Nothing else will, since the row that
named it is gone. The other order could leave a project whose repository had already been removed, which is
a project that exists and cannot be read.

The cached repository is evicted and its lock taken first, so a request already inside the Git engine
finishes before the files under it are removed. A caller that will not yield within thirty seconds keeps
its directory, which is the same harmless outcome: the row is gone either way, so nothing can reach it.

Dependent rows are deleted explicitly rather than left to `ON DELETE CASCADE`. Whether SQLite enforces
foreign keys depends on a pragma the provider sets, and a project that half-disappeared because that
default changed would leave membership rows granting access to an id a later project could be given.

## Decision: the audit trail is for administrators and is not divisible

The trail spans accounts, tokens, projects and the server's own settings in one sequence. It cannot be
divided by project, because most of what it records is not about a project at all.

So it is administrator-only, instance-wide, and filtered by exact action or subject rather than searched.
A loose search would let an administrator read a page of results and believe they had seen everything that
matched, which is the specific way an audit tool misleads.

Giving a project's Maintainer a view of their own project's events is a reasonable later feature and is in
[the backlog](../backlog.md). It is not this.

## Decision: an agent cannot delete a project

[ADR 0011](0011-agent-parity.md) withheld four capabilities from the Model Context Protocol surface. Project
deletion joins them, and for the same kind of reason: it is irreversible and one tool call away.

The product can delete a project, from the browser or over the API with a person's own credential. What it
will not do is put that between an agent and a repository. An agent asked to tidy up a namespace should not
be able to act on its own reading of which projects are stale.

## What was verified

The Git suite went from 483 to 556, which includes the collaboration work in
[ADR 0014](0014-collaboration.md). For this record: a Maintainer who does not own a project is refused, an
anonymous caller is refused, a mismatched confirmation is refused, a missing CSRF header is refused, and
none of those refusals removes anything. A confirmed deletion answers 204, removes the repository from
disk, frees both the namespace path and the instance-wide slug for reuse, and leaves no issue, comment or
membership row behind. An administrator deletes somebody else's project over the API with no confirmation.

For the trail: an anonymous caller gets 401, an ordinary account 403, an administrator reads it newest
first with pagination headers, filters by action and by subject, gets an empty page rather than an error
for a filter that matches nothing, and a query the route does not implement is refused. Every entry on a
full page was searched for a token, a password or any other secret, and none carries one.

Both were driven in a browser as well. See [the evidence](../validation/2026-10-22-collaboration.md).
