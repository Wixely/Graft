# 0050: Mentions, muting, and email as a digest

- Date: 2026-11-11
- Status: Accepted, implemented
- Source: A request on 2026-11-11 to build what [ADR 0033](0033-notifications.md) listed as not done: "@mentions,
  muting and digests"
- Builds on: [ADR 0033](0033-notifications.md) (the inbox and email as a copy of it),
  [ADR 0032](0032-email-is-optional.md) (email is optional)
- Supersedes: the mentions, muting and digest items of ADR 0033's "What is not done"
- Review trigger: a request for group mentions or `@all`; a request for GitLab's custom notification levels or watch
  level; a request for a digest period a person chooses; a request for push, release or wiki notifications

## Decision 1: a mention is a name in prose, heard once, by somebody who could read it

`@username` in an issue's or proposal's description, or in a comment, puts a `mentioned` entry in that person's inbox.
A name inside a fenced code block or an inline code span is an example, not a summons, and is ignored; so is an email
address, because the character before its `@` is part of a word. At most fifty distinct names in one body count.

The rule for who may be mentioned is the rule for who may read: a person hears a mention only if they could open the
issue or proposal it is in. Otherwise the mention is passed over in silence, because an inbox entry would tell a
stranger a title. A confidential issue's mention reaches members and its author. On a server whose operator has not
turned public browsing on, only members hear mentions, because the store would otherwise judge a public project
readable by a stranger whom the server then refuses at the link.

Each act tells a person once. Somebody a comment mentions hears the mention and not also the comment. An edit tells only
the names it added; the names that were already there were told the first time. The actor is never told about their
own mention. Group mentions and `@all` are not mentions; they are text.

GitLab's to-do list carries the entries as `mentioned`, which is its own name for the event.

## Decision 2: muting silences participation, not what is addressed to you

A person may mute one issue or proposal, or a whole project. Muted, they stop hearing about comments, approvals,
merges, closes and reopens there: the things they hear only because they took part. Being assigned, being asked to
review and being mentioned still arrive, because those are addressed to the person by name, and a mute that swallowed
them would make the forge unreliable in the one case it matters. The buttons say so.

A mute is the person's own and needs only what reading needs, so a mute cannot be used to learn that an issue exists.
The inbox page lists everything muted, with a way to undo each, so a mute is never invisible.

GitLab has no mute; it has subscriptions and notification levels, and Graft reads both the other way round. Unsubscribing
from an issue or merge request mutes it and subscribing unmutes it, answering 304 when nothing changed, as GitLab does.
A project's notification level `disabled` or `mention` mutes it and `participating`, `watch` or `global` unmutes it;
read back, a muted project says `mention`, which is what still arrives, and any other says `participating`. `custom` is
refused, because Graft has no per-event switches. Migration 29.

## Decision 3: email may be a digest, at a period the operator sets

Under Profile and Email a person now chooses no email, a message per notification, or a digest. A digest is one message
listing the notifications that arrived since the last one and are still unread: one already read in the inbox needs no
message. A person with nothing new gets nothing, and the clock still moves. Turning the digest on starts its clock then,
so the first one carries only what arrives from that moment, not everything the inbox ever held. Each digest is marked
sent in the same transaction that selects it, so a restart never sends one twice.

The period is the operator's: `--mail-digest-minutes`, a day by default and at most a week, fractions accepted because
a test cannot wait a day. A person cannot choose their own period; one setting is enough until somebody asks. Without a
mail server nothing is sent and the preference is kept, as with every other email preference (ADR 0032).

## What is not done

Group mentions and `@all`. Muting one kind of event rather than all participation. GitLab's watch and custom levels as
anything other than "not muted" and a refusal. A digest period per person. Notifications about pushes, releases, wiki
edits or membership. The agent surface still neither reads nor mutes the inbox.

## What was verified

By the Git acceptance suite on 2026-11-11, 1,509 assertions passing with nothing skipped. Mentions: a member named twice in a description, once in inline code and once in a fenced block hears one
mention; a non-member hears nothing and cannot mute the issue; the author is not told; a participant named in a
comment hears a mention and not also the comment; a rewording that keeps the names tells nobody; a comment edited to
add a name tells that person, now a member, and nobody else. Muting: unsubscribing mutes, twice answers 304; a comment
then goes unheard, a comment naming the person is heard; subscribing restores comments; a project muted through
`notification_settings?level=disabled` reads back `mention`, does not hear a close, still hears an assignment;
`custom` is refused; `participating` unmutes. In the browser routes: a project mute, its listing, and the state read on
an issue. Digests, against the suite's own SMTP server with a three-second period: the server announces the period; the
session shows the choice; two assignments produce one digest listing both and nothing from before the choice, and no
message per assignment; with nothing new, no further digest. In headless Edge against the browser fixture: the issue's
mute buttons toggle the issue and the project, a merge request then shows the project muted, the inbox lists both and
unmutes one, and the profile's three email choices each confirm; no console errors.
