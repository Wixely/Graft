# Visibility and the public browser

Recorded: 2026-10-13. Owner: Implementation agent. Scope: the stored visibility model, anonymous read, the
public listing, and the operator switch that governs all three. Decision: [ADR 0006](../decisions/0006-project-pages-and-public-browsing.md).

## What changed

Graft was private-only by construction. There was no visibility column: the REST create route validated
`visibility` against the literal `"private"` and discarded it, the response field was a hardcoded string, and
the authorization function denied a null identity before examining anything else.

A project now stores a visibility of `private`, `unlisted` or `public`, and separately stores whether a
curated project page is published. Anonymous callers can read, clone, list and search, within limits that are
asserted rather than asserted-at.

## The grant, and why it is a type

Access resolves to exactly one grant, and the grant carries its own reach.

| Grant | Who holds it | Reach |
| --- | --- | --- |
| `Member` | Owner, member, administrator | Everything their role allows, as before |
| `Repository` | Anyone, when visibility is `public` or `unlisted` | The whole repository, read-only, clone included |
| `Page` | Anyone, when the page is published | The curated page only. No clone, history, diff or branch list |
| `None` | Everyone else | Concealed as a 404 |

They resolve in that order and the first that applies wins. Because `Repository` is strictly broader than
`Page` and is consulted first, "repository settings take precedence over project page settings" is a property
of the resolution order rather than of any individual condition, which is what makes it checkable by reading
`ProjectAuthorization.Decide` rather than by tracing a set of interacting flags.

One consequence is worth stating because the opposite is the natural assumption: **a restrictive page over a
public repository protects nothing.** The source is already readable, so the page is presentation. The
settings panel says so where the choice is made.

A second: **a page cannot offer clone, history or a diff.** Those walk the object graph. A pack is
reachability-shaped rather than path-shaped, so no filter makes `git fetch` honour a path allowlist, and a
commit diff would print the contents of files the page never named. The page is therefore a separate read
path rather than a filter over the repository view, and every surface that walks the graph refuses a `Page`
grant outright instead of filtering it.

## The operator switch

`--public-browsing true|false`, default false. With it off, a stored `public` is inert: `DecideFor` in the
server withdraws any decision whose grant is not `Member`, the listing route answers 404, and the routes that
would set a visibility refuse.

This is about restore, not about the fresh install. A database published on a host whose operator allowed
publishing must not begin serving strangers when it is restored onto a host whose operator never did. The
suite proves this directly: it writes `public`, `unlisted` and page-published rows straight through the store
while the server is still refusing to publish, and asserts that none of them are served.

Migration 4 defaults both columns to the closed value, so every project that existed beforehand is `private`
and no page is published. Publishing is never a side effect of an upgrade.

## Where publishing is decided

The switch that makes anything public is a Maintainer action stored in the database, not a field in the
repository's own `.graft` config. Authoring repository content needs Developer access; publishing needs
Maintainer. A config able to set this would let a Developer publish a private project by pushing a commit.

## Concealment is unchanged

A project a caller may not see returns the same 404 as one that does not exist, and never appears in a
listing or a count. Over Git, an anonymous caller who is refused still receives a `401` Basic challenge
before project existence is consulted, so an existing and a nonexistent repository answer identically; only
the order changed, because a public repository has to clone without credentials and visibility therefore has
to be consulted first.

Two smaller oracles were closed along the way. A user avatar is readable anonymously only for an account that
owns a published project, since the listing already names those accounts, which stops the route being a way
to enumerate accounts one id at a time. And search escapes `%` and `_` before building its `LIKE` pattern, so
a caller-supplied wildcard is searched for rather than interpreted.

## Evidence

The Git/API/browser suite is **340 assertions**, up from 302. The 38 new ones are in `CheckPublicBrowsing`
and run with no session and no token, because that is the only vantage point from which "not available" can
be believed. They cover, in both operator states:

- The store's own view of the precedence rule: an anonymous fetch of a public repository is granted through
  `Repository`; an anonymous push is refused; the owner of a public project still resolves as `Member`; a
  published page grants `ReadPage` and refuses `BrowseRepository`.
- A stored public project that does not serve, clone or list while publishing is disabled.
- Anonymous `git clone` of a public repository, and `ls-remote` of an unlisted one, both with no credentials.
- A page-published project that refuses `ls-remote`, the repository view, a commit diff, and the REST tree.
- A listing containing the public and the page-only project, and never the unlisted or private ones.
- Search matching a published description, not reaching a private project, and treating `%` as a character.
- The REST surface answering an anonymous caller for a public project while concealing a private one, and
  distinguishing no token from a token that does not resolve.

```
dotnet build tests\Graft.Git.Acceptance\Graft.Git.Acceptance.csproj -c Debug
dotnet tests\Graft.Git.Acceptance\bin\Debug\net10.0\Graft.Git.Acceptance.dll src\Graft.Server\bin\Debug\net10.0\Graft.Server.dll
RESULT: PASS; assertions: 340
```

The accounts and permissions suite is 113 assertions, up from 110, and the operations workflow is unchanged
at 41. Both pass.

## Rendered browser check

Chromium against a Debug server started with `--public-browsing true`, with three projects published one way
each. No console errors on any page.

- `/-/explore` with no session lists the public and the page-only project and not the unlisted one, labelled
  `source public` and `page only` so a card never implies code that is not readable.
- `/admin/open` with no session renders the project, its README and the clone menu, offering only Code and
  Commits. The badge reads `Public`.
- The same page signed in as the owner shows the Settings tab with the new "Who can see this" panel, the
  three-way selector reflecting the stored value, the page publish control, and the warning that a page
  protects nothing while the repository is public.

Three labels were corrected during this check rather than after it. The project header and the project list
both printed a literal `Private` on every row, which was true only while private was the only thing a project
could be, and the shell printed `Private forge` on a server serving a public listing.

## What this does not cover

The `.graft` config, the file allowlist it describes, and the custom static site are **not implemented**. A
published page currently shows the project's name, description and avatar, and no repository content at all.
That is a usable minimum and it is what makes the "or has a published page" branch of the listing real, but
it is not the curated file surface ADR 0006 specifies.

NativeAOT is unverified here for the same environmental reason as everything else in this repository: the C++
build tools are absent, so ILC never runs. The new assertions sit inside the harness that is already run
against published AOT binaries, so they will be exercised wherever AOT can be built.

A pages origin is not configured or implemented, so no repository-authored HTML is served from anywhere.
