# 0036: A project's own view of the audit trail

- Date: 2026-10-30
- Status: Accepted, implemented
- Source: The user, on 2026-10-30, ordering the remaining large work; this was the fourth and last. The backlog had carried it since 2026-10-22: "a project Maintainer cannot read their project's audit events; the trail is instance-wide and administrator-only; a project-scoped view would need its own query and a rule about which entries are a project's business"
- Review trigger: A request for date filters, for a group's view of its projects' entries, for entries about deleted things to stay in a project's view, or for GitLab's instance and group `audit_events` routes

## What was missing

[ADR 0013](0013-project-deletion-and-audit.md) gave the forge one trail, read by administrators, because most
of what it records is nobody else's business: accounts, tokens, keys, the server's switches. A Maintainer who
wanted to know who changed their project's default branch, who was given a role, or who closed an issue had
to ask an administrator.

## Decision: the rule is how an entry names its target

The trail already names what each entry is about. An entry about the project itself names the project's id;
one about an issue, proposal or comment names `projectId#iid`, the reference the collaboration store has
always written; one about a release, package, container image or snippet names the row's own id, whose
project is looked up. Those are a project's business. Everything else stays the administrator's: the rule is
a query over existing rows, not a second trail and not a column added to every entry.

The rule has one edge, recorded rather than hidden: an entry about a release, package, image or snippet that
no longer exists cannot be looked up to a project, so it leaves the project's view and stays in the instance
trail. Entries about the project itself and about issues and proposals carry the project in their reference
and are unaffected.

## Decision: a Maintainer reads it, where they manage the project

The view is for whoever may manage the project, which is the same decision every settings change asks. It
sits at the bottom of the project's settings tab as recent activity, newest first, and at
`GET /api/projects/:id/audit` for the browser with an `action` filter. For GitLab's clients it is
`GET /api/v4/projects/:id/audit_events` in GitLab's event shape: `entity_type` `Project`, `entity_id` the
project, `author_id`, `created_at`, and `details` carrying what Graft knows, the action, the target type and
id, the detail as `custom_message`, and the actor's name as `author_name`. A Developer and a stranger are
answered as for any project they may not manage. The whole trail remains the administrator's page and route.

## What is not done

GitLab's `created_after` and `created_before`, its single-event route, and its instance and group
`audit_events`. A group's view of its projects' entries. The agent surface does not read the project trail;
it reads nothing of the instance trail either.

## What was verified

By the Git acceptance suite: an issue opened and closed in the project and one opened in another; the
Maintainer reads the project's events in GitLab's shape, each with `entity_type` `Project` and the project's
id, including its creation and the issue's life, with nothing about the other project or anything else and
with the actor named; a Developer and a stranger are refused as for a project they may not manage; the
browser's route gives the Maintainer the same entries, every one about the project or an issue of it, and
filters by action. The settings panel compiles against that route and was not driven by a browser.
