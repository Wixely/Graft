# 0007: Creating a project by pushing to it

- Date: 2026-10-13
- Status: Accepted user request, implemented
- Source: "need a setting in graft to allow pushing to repos that dont exist, and create them on the fly"
- Review trigger: Any change to who may create a project, to the Git transport's authorization, or to what a namespace means

## Decision

With `--push-to-create true`, a push to a path that holds no project creates one to receive it. Off by
default.

The created project is **private**, **owned by the pusher**, and **only in the pusher's own namespace**.
None of those is configurable, and the reasons are different in each case.

- **Private**, because publishing is a deliberate act and a side effect of a push is the opposite of one.
- **Owned by the pusher**, because a namespace in Graft is an account's username, so there is no other
  owner it could sensibly have.
- **The pusher's own namespace only**, because creating in someone else's would hand them a project they
  never asked for, under their name, on the say-so of anyone who knew their username. An administrator gets
  no exception: being Maintainer-equivalent on every project that exists says nothing about conjuring one
  into somebody else's namespace.

A path the REST create route would reject is still rejected. Arriving over Git does not make a name valid.

## Why it is off by default

It creates a durable resource as a side effect of a typo. `git push origin main` at a mistyped remote
silently produces an empty project that somebody has to find and delete. That is a reasonable trade for a
team that wants it and a bad surprise for one that does not, which is what a default is for.

## Scope, and what enabling it concedes

Creation happens on a push, so it is gated on the caller being allowed to push: a token needs
`write_repository` or `api`. It is deliberately **not** gated on the `api` scope that the REST create route
requires.

That is a real concession and worth stating plainly. With this on, a `write_repository` token can create
projects, which it cannot do through any other route. The reason it is acceptable is the shape of what it
can create: a private project owned by the token's own account in that account's own namespace, which is
exactly what the same person can already make in the browser with a session. An operator unwilling to let
`write_repository` imply that leaves the setting off.

## Creation happens on the ref advertisement

Git's push begins with `GET /{namespace}/{project}.git/info/refs?service=git-receive-pack`, and only then
posts the pack. Creating on the POST alone would be too late: the advertisement would already have answered
404 and the client would have given up.

So the advertisement creates. The consequence is that something which announces an intention to push, such
as `git push --dry-run`, creates the project even though no pack follows. A fetch never does, because the
advertisement for `git-upload-pack` is not a push and is left to miss.

## A bug this uncovered

Testing this found that **no push larger than the client's `http.postBuffer`, one mebibyte by default, had
ever worked over HTTP.**

Git cannot replay a chunked request body after a `401`, so before streaming one it settles authentication
with a probe request whose body is a single flush packet. Graft answered that probe with
`400 Rejected: empty-update`, because an update command list with nothing in it is not a valid push. The
client gave up there. Small pushes were unaffected: they fit in the buffer, go out with a content length,
and are never probed.

A lone flush packet is now answered with an empty result, which is what Git's own server does. The
acceptance suite missed this for as long as every repository it pushed was a few kilobytes, so it now pushes
an incompressible two-mebibyte repository at the client's default settings and reads it back.

## What this does not change

Nothing about visibility, which stays as [ADR 0006](0006-project-pages-and-public-browsing.md) describes:
created private, published only by a Maintainer, and only on a server whose operator allows publishing.

Nothing about anonymous access. An anonymous push is refused before any of this is reached, so no project
can be created without credentials.
