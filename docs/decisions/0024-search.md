# 0024: Search without an index

- Date: 2026-10-29
- Status: Accepted user request, implemented
- Source: "build out these", naming search: "nothing beyond name and description on the public listing; issue search is missed first"
- Review trigger: Any proposal to add a search index, a pattern language, or a global code search across projects

## What was missing

A member could not find an issue by what it said, a line of code by what it contained, or a project by
anything but the public listing's name match.

## Decision: a search reads what is there

Graft keeps no search index. A project search reads the project: issues and proposals by title and
description through the same filters the listings use, wiki pages by title and content, commits by message
and author along the history from a revision, and code by reading every text file in the tree at a
revision and looking for the words.

The code search is bounded three ways: a file over a megabyte is skipped, no more than twenty thousand
files are read, and no more than fifty matches are returned, with the response saying when it was cut short.
That is slow in principle and fast enough in practice for repositories this engine is sized for, and it has
the property an index does not: it is never stale, because it searches what is there. An index is the thing
to add when a repository is large enough that reading it is the wrong answer, and that is a decision about
storage, not about this surface.

A match is a case-insensitive substring. There is no pattern language, because a pattern language is where
a search becomes a way to make a server work hard, and the first user of it would be whoever wanted the
server to.

## Decision: a global search sees what the caller sees

Across the server, projects, issues and proposals are searched with one visibility clause: a project is
searched when the caller is an administrator, its owner or a member, or when the operator publishes to
strangers and the project is public or unlisted. A confidential issue is found by members and its author.
That clause is the whole of the access control for a search, so it is written once and the per-project
rules are not re-derived. There is no global code search; code is searched one project at a time.

## The API

GitLab's `GET /api/v4/projects/:id/search?scope=&search=` for `issues`, `merge_requests`, `blobs`,
`wiki_blobs` and `commits`, and `GET /api/v4/search` for `projects`, `issues` and `merge_requests`. Blob
matches use GitLab's shape. The response wraps every scope's list in one object so a page can render
whichever came back, which is Graft's shape rather than GitLab's bare array.

## What an agent can do with this

`graft_search`, taking the surface from 35 to 36.

## What was verified

Code search finds a line without regard to case and skips a binary file; commit search finds a message;
issue search finds both issues for a member; wiki search finds a page by its content; an unknown scope and
an empty search are refused; a global search finds a project by name and an owner's issues including the
confidential one; a stranger's global search finds nothing in a private project; an anonymous global search
is refused while the server does not publish; a stranger cannot search a private project.
