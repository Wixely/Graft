# 0035: Groups own projects, and a group can hold groups

- Date: 2026-10-30
- Status: Accepted, implemented
- Source: The user, on 2026-10-30, ordering the remaining large work: SSH, optional email, "groups and nested namespaces", the project-scoped audit view. The backlog had carried groups since 2026-10-22: "a namespace is an account name; there is no way to own projects collectively or to grant access to a team"
- Review trigger: A request for group-level visibility of projects (a public group listing its projects to strangers), for group-level labels, milestones, boards or runners, for transferring a project between namespaces, for GitLab's `share with group` on a project, or for the natural nested URL in the browser

## What was missing

Every project lived under one account. Access was granted to people one at a time, per project, and a team
of five with twenty projects meant a hundred grants kept by hand. There was nowhere to put a project that
belonged to a team rather than to a person.

## Decision: a group is a namespace with members, and may hold groups

A group has a path and a name, optional members with a role, and optionally a parent group. Its full path is
its ancestors' paths and its own joined by `/`, which is what a project under it is addressed by:
`team/platform/api`. A top-level group's path shares the namespace of usernames and may not collide with
one, in either direction: a group cannot take a username and an account cannot take a group's path. Both are
checked where each is created.

Roles are GitLab's where Graft has them: Reporter, Developer, Maintainer, and Owner, which is new and belongs
to groups alone. An Owner manages the group: its members, its subgroups, its projects' creation, and its
deletion. On the group's projects an Owner acts as a Maintainer. A role is inherited downward: a member of
`team` holds the same role in `team/platform` and on every project under both. On a project, a person's
effective role is the greater of their direct membership and their inherited group role, and the project's
owner and the administrators are what they always were. Nothing is taken away by a group: a direct grant
that is higher than the inherited one stands.

Anyone signed in may create a top-level group, as on GitLab. Creating a subgroup needs Owner of the parent.
Creating a project in a group needs Developer or better in the group, and the project's `OwnerUserId` stays
the creator, who is a Maintainer of it as before; the group's members reach it through their roles.

## Decision: deleting a group deletes nothing else

A group is deleted only when it holds no projects and no subgroups. GitLab deletes everything beneath; Graft
refuses by name, because [ADR 0013](0013-project-deletion-and-audit.md) made project deletion a deliberate
act with its own confirmation and audit entry, and a group deletion that took twenty projects with it would
be twenty of those with none of that. Move or delete the projects first.

## Decision: the paths GitLab's clients send work; the browser's natural nested URL redirects

`GET /api/v4/projects/:id` with the URL-encoded full path (`team%2Fplatform%2Fapi`) works, as does the
numeric id, so `glab` and every client that encodes the path finds a nested project. The Git transport
answers both the natural form, `https://host/team/platform/api.git`, and the encoded one: a middleware ahead
of routing folds a path with more than one namespace segment before `.git` into one encoded segment, so the
existing two-segment Git routes serve it. Over SSH, `git@host:team/platform/api.git` is split at its last
segment. Group routes are GitLab's `/api/v4/groups` family: list, create, read by id or encoded path, the
group's projects, its subgroups, and its members.

In the browser, a project's address carries its namespace as one encoded segment, `/team%2Fplatform/api`,
because the client router matches templates by segment count and the twelve project pages share
`/{Namespace}/{Slug}/...`. A visitor who types or follows the natural form, `/team/platform/api/-/issues`,
lands on a page that redirects them to the encoded form of the same place. This is the one shape GitLab has
that Graft shows differently, and it is recorded as a deviation rather than papered over, because the
alternative was a router of Graft's own in front of every page.

## Decision: the database keeps a project's slug unique in two ways

`Projects` has had `UNIQUE (OwnerUserId, NormalizedSlug)` since the first migration, and SQLite cannot drop a
constraint without rebuilding the table, which the migration runner does not do inside its transaction. A
group project keeps its creator as `OwnerUserId`, so two projects of the same slug in two groups by one
creator would have collided. Instead a group project's `NormalizedSlug` is stored prefixed by its group's id
and a slash, which no account's slug can contain, and a second unique index on `(GroupId, NormalizedSlug)`
keeps slugs unique within a group. Every lookup of a group project goes through the group, so the prefix is
never seen outside the table. It is a workaround and is marked as one in the schema.

## What is not done

Transferring a project into or out of a group. Group-level visibility: a group's projects are private unless
each says otherwise, and a group itself is visible only to its members and administrators. Group labels,
milestones, boards, wikis, packages or runners. Sharing a project with a group it is not under. GitLab's
`/groups/:id` `PUT`, its `avatar`, `transfer`, `restore`, `descendant_groups`, `shared_projects` and
`billable_members` routes. The agent surface has no group tools in this slice; project membership is already
on it ([ADR 0011](0011-agent-parity.md)), so group membership would be a reasonable follow-on rather than a
withholding.

## What was verified

By the Git acceptance suite: a top-level group cannot take an account's name and an account cannot take a
group's; a signed-in account creates a top-level group and owns it; a group path is unique at its level; an
outsider cannot create a subgroup and is not told the group exists; the Owner creates a subgroup with a nested
full path; the Owner lists both groups and an outsider none; a group is read by its encoded full path and not
found by an outsider; the Owner makes bob a Developer of the parent, Guest is refused by name, and bob sees
the subgroup with his role marked inherited; a stranger cannot create a project in the group; a Developer of
the parent creates a project in the subgroup, addressed by its nested path with a group namespace; the project
is found by its encoded path and not by a stranger; the group lists it and both member and Owner see it among
their projects; a push over HTTPS at the natural nested path, a clone by the encoded form, a stranger finding
nothing, and `ls-remote` over SSH at the nested path; the last Owner cannot be removed; removing bob removes
his reach into the subgroup's project; a group with a subgroup or a project is not deleted, and both are once
emptied; a group is created and listed from the browser's family. The group pages compile against those
routes and were not driven by a browser.
