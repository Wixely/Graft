# 0029: Deleting a file is a commit

- Date: 2026-10-29
- Status: Accepted, implemented
- Source: The backlog's "Near, and small" list, which recorded on 2026-10-22 that files could be created and updated over the API and deleted by neither the API nor the browser; taken on without a new user decision because the backlog is the user's list
- Review trigger: A request for `last_commit_id` or `start_branch` on the write routes, a request to delete a directory in one call, or a request to commit several deletions and writes together

## What was missing

`POST` and `PUT /api/v4/projects/:id/repository/files/:file_path` wrote a file; `DELETE` on the same route
did not exist. The browser could upload and commit files and could not remove one. An agent could write a
file and could not take it back. Removing a file meant a clone, a commit and a push.

## Decision: a deletion is authored like a write, and refuses to find nothing

The object writer's one path through a commit now takes an edit of the tree rather than a list of files to
write, and a deletion is the edit that removes a leaf. Everything else is shared: the same validation of the
path, the same author line, the same reachability check before the branch moves, the same two-step
publication.

Every path in a deletion must name a file that is on the branch, or the whole batch is refused and nothing
is written. A client that asks to delete a file that is not there is looking at a different revision than
the one it is changing, and committing nothing with a message saying something was removed would be a lie
in the history. The route is one file at a time because that is GitLab's shape; the engine takes a hundred.

A deletion cannot create a branch, because there is nothing on a new branch to delete. A write still can.

## Decision: a directory emptied by a deletion disappears

Git has no empty trees. A directory whose last file is deleted is left out of its parent when the tree is
serialized, all the way up; only the root is ever written empty, which is Git's own empty-tree object for
a commit with nothing in it. Without this a deletion would write a tree Git reports as invalid, and a clone
would show a directory with nothing in it, which Git cannot represent.

## Decision: the branch and message come in a body or on the query string

GitLab documents them in a JSON body. A `DELETE` with a body is legal and some clients cannot send one, so
the same two members are read from the query string when the body is absent. The body wins when both are
present and the body names them.

## What is not done

`last_commit_id`, which lets a client refuse its own deletion if someone else changed the file first; the
write routes do not have it either, and a reader of the file today gets the selected commit rather than the
last one that touched it, so there is nothing to compare against yet. `start_branch`, `author_email` and
`author_name`: a Graft account has no email and a deletion is attributed to the account that made it.
Deleting a directory: refused by name, because a directory is not a file and the route says file.

## Surfaces

The GitLab-shaped route with a token; the same route in the browser family with a session; a `Delete`
button on the file view for anyone who may push, confirmed with a second click as a branch deletion is; and
`graft_delete_file` on the agent surface, which is not among the withheld capabilities because the history
keeps the file and the branch stays where it was.

## What was verified

Through the suites. A read-only token is refused; the file goes from the branch and the other file in its
directory stays; deleting it again is a `404` and makes no commit; a directory and a missing branch are
refused by name; a deletion without a branch is refused before anything is read; the last file in a
directory, deleted by query string, takes the directory with it; a clone of the branch has no such
directory, passes `git fsck --full --strict`, and `git log --diff-filter=D` names both deletions. An agent
deletes what it wrote, cannot read it back, and is refused the second time. The browser button was
compiled and wired to the same server function; it was not driven by a browser in the suite.
