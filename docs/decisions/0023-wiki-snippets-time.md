# 0023: A wiki with history, snippets, and time tracking

- Date: 2026-10-29
- Status: Accepted user request, implemented
- Source: "build this out", naming wiki, snippets and time tracking from the collaboration depth list
- Review trigger: Any proposal to keep the wiki in a second Git repository, to render wiki content under looser rules than a README, to let a wiki slug change with its title, or to let one person's time entry be removed by another who is not the author or a Maintainer

## What was missing

There was nowhere to write about a project except in its repository or in an issue, nowhere to paste a
file worth sharing without committing it, and no way to say how long something was expected to take or
did.

## Decision: the wiki is a document in the database, with every save kept

GitLab keeps a wiki as a second Git repository beside the project's. Graft keeps wiki pages in the
database. The repository is the project's code; a wiki is prose about it, written in a browser by people
who may never clone, and keeping it in the database puts it in the same backup, under the same permissions
and behind the same conversation-reading rule as everything else said about the project. It also means the
Git engine, whose scope is a stopping point rather than a destination, is not asked to host a second
repository per project before it hosts the first one well.

Every save keeps the previous text as a version with a message and an author, so a page can be read as it
was and a bad edit undone by restoring an earlier version, which is itself a new version. The newest five
hundred versions are kept, so a page edited forever does not grow a history forever.

A page's address is a slug made from its title at creation and never changed afterwards, so links keep
working through a rename. Two titles that make one slug are refused.

Reading the wiki follows reading the conversation; writing it needs the permission to push, which is the
rule [ADR 0020](0020-planning.md) set for organising a project: prose about the project is organised the
way the project is.

A page is rendered by the README renderer under the README's rules, with raw HTML disabled, every link
and image destination checked on the parsed tree and the output scanned before it is sent. A wiki page has
no repository path for an image to resolve to, so every image becomes a link.

## Decision: a snippet is a comment with a file

A snippet is one file with a title and a description, as visible as its project. Any participant may make
one, and its author or a Maintainer may change or delete it, exactly as for a comment. The raw form is
served as a download of unknown type, named for the file, under [ADR 0017](0017-getting-things-out.md)'s
rule. A file name is one path segment.

## Decision: time is an estimate and a ledger

An issue or a proposal carries an estimate, set as a whole by whoever may triage it, and a ledger of time
spent, each entry attributed to the person who recorded it, dated, and carrying a word about what. The
total is the sum of the ledger, so it can always be explained. A negative entry takes time back, which is
GitLab's own way to correct a mistake, and is refused when it would take the total below zero.

Any participant records their own time. Clearing the ledger removes other people's records, so it needs
the author or a Maintainer. Durations use GitLab's vocabulary, `1mo 2w 3d 4h 5m`, with a month of four
weeks, a week of five days and a day of eight hours, which are GitLab's conversions and the ones a team's
estimates are already in. The totals travel on the issue and the proposal as `time_stats`.

## The API

GitLab's `wikis` routes with Graft's own `versions` sub-resource and `?version=` read, and `html` beside
the content; GitLab's `snippets` routes with `raw`; and GitLab's four time verbs with a `time_stats` read,
plus Graft's own `time_entries` listing of the ledger.

## What an agent can do with this

`graft_read_wiki` and `graft_write_wiki`, taking the surface from 33 to 35. Writing a page is the same kind
of act as writing a file, which agents already do, and is bounded the same way: the previous text is kept,
and a page is replaced whole so an agent reads before it writes. There are no snippet or time tools:
a snippet is a person's paste, and time is a person's own record of their own hours.

## What was verified

The Git suite went from 768 to 795. A Reporter cannot write the
wiki; a page is created with a slug from its title and rendered without the script it contained; a second
title making the same slug is refused; an edit keeps a version and names the editor; versions list newest
first with their messages and an earlier one reads back as it was; a listing carries no content; a private
project's wiki is concealed from a stranger; a page is deleted with its history. A Reporter creates a
snippet; a file name with a slash is refused; a Developer cannot rewrite another author's and a Maintainer
can; the raw form downloads named for the file; a listing carries no content; the author deletes it. The
author estimates an issue in GitLab's vocabulary; a non-duration is refused; a member records time, takes
some back, and cannot go below zero; the ledger names who and why; the issue carries the totals; a
Developer cannot clear everybody's time and a Maintainer can.
