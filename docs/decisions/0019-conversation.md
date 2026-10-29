# 0019: A comment becomes a conversation

- Date: 2026-10-29
- Status: Accepted user request, implemented
- Source: "build this out", naming the collaboration depth list from a review of what a GitLab alternative still lacked; the first items were replies, threads, resolving a conversation and editing a comment
- Review trigger: Any proposal to nest replies more than one level deep, to let a resolved thread hide its comments, to let a Reporter resolve a thread they did not start, or to delete the first comment of a thread that has replies

## What was missing

A comment stood alone. There was no way to answer one, no way to say that the point it raised had been
dealt with, and no way to fix a typo in it after posting. A review that found three things wrong produced
three comments and then a fourth that said "fixed, fixed, still open", with nothing connecting any of them.

## Decision: threads are one level deep

A comment may answer another, and the answer joins that comment's thread. A reply to a reply is stored as a
reply to the thread's first comment, so however a thread is addressed it is a first comment and a flat list
of answers beneath it.

Nesting is how a conversation stops being readable. GitLab's discussions are flat for the same reason, and
flat is also what makes "resolve the thread" mean one thing: there is exactly one first comment to carry
the flag.

A reply carries no anchor of its own. It sits where its thread sits, on a line of the diff or in the general
conversation, because a reply anchored to a different line would be a new thread pretending to be an answer.
A reply that names a line is refused, and so is a reply to a comment on a different issue or proposal.

## Decision: resolving needs the standing to push, or to have started the thread

Anybody who could push to the project may mark a thread settled, because resolving is a judgement that the
point was dealt with and that is the standing a merge needs. The person who started the thread may also
resolve it, whatever their role, because they may withdraw their own point.

A Reporter who commented in somebody else's thread cannot close it. That is the rule that stops a
conversation being tidied away by the person it was about.

Resolving hides nothing. The comments stay where they were, labelled as resolved and by whom, and the thread
can be reopened. A review that could make its own objections disappear would not be a record of a review.

Threads on issues resolve exactly as threads on proposals do. Nothing gates on either, so there was no
reason to give the two different vocabularies.

## Decision: an edit is recorded, and a first comment with replies cannot be deleted

The author or a Maintainer may change a comment's words, the same two people who may delete one. The edit
sets a timestamp the interface shows as "edited". The earlier text is not kept, which is a deliberate
economy; the fact that the text changed is kept, because a comment rewritten silently after it was answered
would make the answers to it read as answers to something else.

A thread's first comment carries the replies to it. Deleting it would remove, or orphan, words other people
wrote, so it is refused while any reply exists; the author can edit it instead. A reply can always be
deleted by the same two people.

## The API

GitLab answers a thread through a separate discussions resource. Graft puts the thread on the note: a note
carries `parent_id`, `edited_at`, `resolved`, `resolved_by` and `resolved_at`, a reply is created by posting
a note with `parent_id`, and `PUT .../notes/:id` takes `body`, `resolved` or both. `DELETE .../notes/:id`
now exists on the GitLab-shaped route as well as the browser's. A note addressed beneath an issue or a
proposal it is not on answers 404, so the wrong door does not open.

## What an agent can do with this

`graft_comment` gained `reply_to`, and both reading tools now report each comment's id, what it answers,
whether its thread is resolved and whether it was edited, so an agent can answer in a thread rather than
beside it. There is no tool to resolve a thread and none to edit or delete a comment. Resolving is the
reviewer's judgement that their point was met, and an agent whose code is under review should not be the one
to say so; editing and deleting are withheld for the reason ADR 0015 withholds approving, which is that the
record of a review should not be revisable by the thing being reviewed. An agent that has more to say posts
another comment.

## What was verified

The Git suite went from 653 to 669. A reply joins its thread and carries no anchor; a reply to a reply
attaches to the first comment; a reply naming a line is refused; a reply to a comment on another issue is a
404. A Reporter cannot edit somebody else's comment, the author can and the edit is recorded with the anchor
intact, a Maintainer can edit anyone's, and an edit that changes nothing is refused. A Reporter cannot
resolve a thread they did not start; a Developer resolves it through a reply and the first comment carries
the state while the reply does not; a Maintainer reopens it; the Reporter resolves their own thread. A first
comment with replies cannot be deleted, a reply can, and a note reached beneath the wrong proposal is a 404.
The browser rendering of threads, replies, editing and resolving was built on one shared component used by
the issue page, the merge request page and the diff, and driven by hand.
