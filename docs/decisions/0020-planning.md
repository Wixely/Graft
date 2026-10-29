# 0020: Planning, and the rule that a Reporter files while a Developer organises

- Date: 2026-10-29
- Status: Accepted user request, implemented
- Source: "build this out", naming labels, milestones, assignees, due dates, boards and confidential issues from the collaboration depth list
- Review trigger: Any proposal to create a label on first use, to let a Reporter relabel somebody else's issue, to show a confidential issue to a public-visibility reader, to give a board state of its own, or to make a draft mergeable

## What was missing

An issue had a title, a description and a state. Nothing sorted issues, nothing said who was doing one or
by when, nothing grouped them towards a point, and every issue on a public project was as public as the
code. The API refused every one of these attributes by name, which was honest and not useful.

## Decision: labels are a project's vocabulary, and a Developer owns it

A label belongs to a project and has a name, a colour and a description. Its name is unique within the
project without regard to case, so "Bug" and "bug" are one label, and it cannot contain a comma, because
GitLab's own API separates labels with commas and a name holding one could never be addressed there.

Creating, renaming and deleting a label needs the permission to push, as do milestones and boards. The rule
throughout this record is that **a Reporter files and a Developer organises**: a Reporter can open an issue
and comment on one, and a Developer decides what the project's words are and what goes where.

A label that does not exist is refused by name. GitLab creates one on first use; Graft does not, because a
typo in a label name should be an error rather than a new word in the vocabulary that nobody chose.

Deleting a label removes it from everything it was on, and from any board column bound to it. The colour is
validated as six hexadecimal digits before it is stored and the text colour that reads against it is
computed on the server, so a colour can never carry markup into the page that paints it.

## Decision: triaging needs the permission to push, or to be the author

Putting labels on an issue, giving it a milestone, assigning people and setting a due date are one kind of
act, triage, and one rule covers them: the author of the issue may triage it, and anybody who could push
may triage any issue. A Reporter organises their own issue and nobody else's.

This sits between the two rules [ADR 0014](0014-collaboration.md) set. Words and state belong to the author
or a Maintainer; triage belongs to the author or a Developer; and confidentiality, below, follows the words.

Assignees and reviewers must be members of the project, by username. Graft names people by username
everywhere else it names them, where GitLab takes ids; the deviation is recorded in the compatibility
matrix rather than hidden.

## Decision: a confidential issue is for members and its author

Marking an issue confidential removes it from the view a public or unlisted project gives a stranger. A
member of any role still sees it, and so does its author. A stranger reading the listing is not shown it,
reading it directly is told it does not exist, and reading its conversation is told the same.

Changing confidentiality follows the rule for words, the author or a Maintainer, because it changes who may
read the issue at all, which is a heavier thing than triage.

The check is made where issues are read, not only where they are listed, so a client that guesses a number
learns nothing. The conversation on a confidential issue is as confidential as the issue.

## Decision: milestones are numbered, and deleting one keeps its work

A milestone has a title, a description, a start date and a due date, and is active or closed. It is
numbered from one within its project the way issues are, because that is how people refer to one. It counts
the open and closed issues and the proposals in it, so a milestone page needs no second request.

Deleting a milestone leaves its issues and proposals with none. A plan can be abandoned without losing the
work that was planned, which is the difference between a milestone and a folder.

## Decision: a board stores no state of its own

A board is a name and an ordered set of columns, each bound to one label. What appears in a column is the
open issues carrying that label; what appears in the first column is the open issues carrying none of the
board's labels; the last column is the closed issues. Moving an issue between columns removes one label and
adds another, and nothing else is written.

So the same issue reads the same way from the list, from the board and from the API, and a board cannot
disagree with the labels it is built on. GitLab's boards work the same way, which is also what lets a
GitLab client's idea of a board apply here.

## Decision: a draft is a flag, and it is not mergeable

A proposal may be marked a draft, by its author or a Maintainer. A draft is refused at the merge however
mergeable its branches are, with a message that says why, and the browser does not offer the button.

GitLab reads draft state off a title prefix, so a proposal's readiness changes when somebody edits its
wording. Graft stores a flag that changes only when somebody changes it. The API takes and reports `draft`.

Reviewers are recorded on a proposal, as people asked to look. Being named a reviewer grants nothing: an
approval still needs the permission to push, exactly as [ADR 0015](0015-review-journey.md) decided, so a
Reporter asked to review can say what they think in a comment and cannot open the gate.

## The API

Labels, milestones and boards have GitLab's routes under `/api/v4/projects/:id/`, with `PUT` for an edit
and a label addressable by id or by name. Issues take `labels` as a comma-separated string, `add_labels`
and `remove_labels` to adjust rather than replace, `milestone_id` with zero to clear, `assignee_usernames`,
`due_date` with an empty string to clear, and `confidential`; a listing filters by `labels`,
`without_labels`, `milestone_id`, `assignee_username`, `search` and `confidential`. Proposals take the same
with `reviewer_usernames` and `draft` and no due date. `GET /projects/:id/users` lists who can be assigned,
for anybody who may write on the project.

## What an agent can do with this

Three tools, taking the surface from 29 to 32: `graft_list_labels`, `graft_list_milestones` and
`graft_triage`, which labels, assigns, sets a milestone or a due date on an issue or a proposal under the
same rule a person works under. There is no tool to create a label or a milestone and none to mark an issue
confidential or a proposal ready: the vocabulary is the team's to choose, and readiness and confidentiality
are judgements about people rather than about code.

## What was verified

The Git suite went from 669 to 723. A Reporter cannot create a label or a milestone and a Developer can;
names are unique without regard to case; a bad colour and a comma are refused; a label is addressed by name
and a short colour expanded; an issue is opened with labels, a milestone, an assignee and a due date; an
unknown label, a non-member assignee and a malformed date are refused; a Reporter cannot relabel another
author's issue and can label their own; a Developer triages another author's issue; listings filter by
label, milestone and assignee, by no milestone and a search, and by labels absent; a milestone counts its
issues and its proposals; zero and an empty string clear a milestone and a due date; a confidential issue is
read by a Reporter who is a member, cannot be set by a Developer on another author's issue, is absent from a
stranger's listing on a public project and does not exist when read directly or through its conversation;
members list who can be assigned and a stranger cannot; a board gains columns by label id and by name, one
per label, moves one to the front, loses one, and a Reporter cannot delete it; a draft proposal with a
reviewer, a label and a milestone is refused at the merge in words, cannot be marked ready by a Developer,
is filtered by reviewer and is marked ready by its author; deleting a milestone leaves the proposal with
none and deleting a label removes it from the issue it was on.

The labels, milestones and boards pages and the details sidebar on an issue and a proposal were driven in a
browser against the fixture.
