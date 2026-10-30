# 0033: A notification is an inbox row; email is a copy of it

- Date: 2026-10-30
- Status: Accepted, implemented
- Source: The second slice of the user's email item of 2026-10-30, under the condition that a non-email path exist. [ADR 0032](0032-email-is-optional.md) scoped notifications out of its first slice and said what they would be
- Review trigger: A request for mentions (`@name` in a body), for notifications about pushes or releases, for per-project muting, for a digest instead of a message per event, or for GitLab's `/todos` fields this does not fill

## What was missing

Nothing in Graft told a person that something needed them. An assignee learned of an issue by looking; a
reviewer learned of a proposal by being told in some other channel; an author learned of a comment by
returning to the page. The backlog listed notifications under email because every other forge sends them by
email, and a forge that sends nothing seemed to have nowhere to put them.

## Decision: the inbox is the notification

Every event that concerns a person writes a row for that person, in the same transaction as the event, so
the inbox is as true as the audit trail and cannot say something happened that did not. The events: being
assigned an issue or proposal; being asked to review a proposal; a comment on an issue or proposal the person
is part of; an approval of their proposal; a merge, close or reopen of an issue or proposal they are part of.
"Part of" means its author, its assignees and reviewers, and anyone who has commented on it. The actor is
never told about their own act.

The inbox is at `/-/notifications`, with the unread count on the session and therefore on the header's bell
without a request of its own. Opening an entry marks it read and goes where it points; the destination applies
its own rules again, so an entry about something the reader may no longer see arrives at a refusal rather than
at a leak. Nothing is deleted; read rows beyond five hundred per person are trimmed oldest first.

## Decision: email is a preference, and only ever a copy

A person may ask, under Profile and Email, for a message per notification. The preference is accepted on
every server, because it is the person's and a server that gains a mail server later honours it from then on.
A message goes out only when the server can send, the person asked, and the person has an address, and it
goes out after the row's transaction committed, through a hook the store raises and the server handles. The
message names who did what to which issue or proposal, links to it, and says how to turn messages off. A
server with no mail server has the same inbox as one with. Off is the default.

## Decision: GitLab's to-do list is the API shape

GitLab's clients look at `GET /api/v4/todos` for "what needs me", so the inbox answers there in GitLab's item
shape: `id`, `project`, `author`, `action_name`, `target_type`, `target`, `target_url`, `body`, `state`,
timestamps. `state=pending` is the default and `state=done` the read ones; `POST .../todos/:id/mark_as_done`
and `POST .../todos/mark_as_done` are marking read. Where GitLab has a name for the event it is used:
`assigned`, `review_requested`, `review_submitted` for an approval. Where GitLab makes no to-do for the event,
Graft's own name is used and said plainly: `commented`, `merged`, `closed`, `reopened`. Reading needs
`read_api`; marking done needs `api`. The browser has its own family under `/api/notifications`.

## What is not done

Mentions, which need a body parser and a rule for who may be mentioned. Notifications about pushes, releases,
wiki edits or membership. Muting a project or a thread. Digests. GitLab's `/todos` filters by `action`,
`author_id`, `project_id`, `type` and `group_id`, and its `restore` route. The agent surface does not read or
mark the inbox, which is a reasonable thing to add and was not asked for.

## What was verified

By the Git acceptance suite, on a server with no mail server: nobody has anything pending before anything
happens; an assignment appears for the assignee as `assigned`, pointing at the issue, and not for the
assigner; a comment reaches the issue's author and not the commenter nor a stranger; a to-do belongs to its
recipient alone, marking done needs `api`, a marked to-do comes back `done` and moves from pending to done;
a review request reaches the reviewer, an approval reaches the author as `review_submitted`, a merge reaches
the reviewer, a close reaches the assignee; everything is marked done at once; the browser's session carries
the unread count, its inbox lists the unread entries, marking all read brings the count to zero, and email per
notification is off by default. Then with the suite's own SMTP server: an assignment before opting in sends
nothing, opting in is accepted, the next assignment is mailed to the address on the account naming who did
what to which issue with a link to it, and both assignments are in the inbox whether or not they were mailed.
