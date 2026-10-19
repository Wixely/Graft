# Instance-wide project slugs

Recorded: 2026-10-19. Owner: Implementation agent. Decision: [ADR 0010](../decisions/0010-project-slugs.md).

## What changed

Every project now has an instance-wide slug and answers at `/projects/<slug>` for browsing, cloning and
pushing, alongside its owned `/<owner>/<slug>` path. Assigned automatically from the project's own path, with
a short random suffix when that name is already held. Stored in a new `GlobalSlug` column with a unique
index (migration 6), which also backfills every project that existed before.

`global_slug` and `global_web_url` join the project response. The clone menu offers the shared link and its
clone URL. Account creation now refuses the names the application itself answers to.

## Verified in the Git/API/browser suite

`Graft.Git.Acceptance` goes from 389 to **405 assertions**, all passing against the Debug server:

- A new project is given its own path as its slug when that name is free, with a shareable page URL built
  from it, and that URL contains no account name.
- The REST API resolves a project by its slug and returns the same project the id does.
- A repository **clones** through `/projects/<slug>.git`, and a **push** through it lands in the repository
  the owned path serves, confirmed by comparing `ls-remote` through both addresses.
- An anonymous caller gets the **same** refusal through the slug as through the owned path. Asserted as
  equality of the two answers rather than a fixed status, because the invariant is that the slug is a name
  and not a looser way in.
- An unused slug is not found, and another account's private project answers the same way through the slug
  namespace as it does by path, so the flat namespace cannot be probed.
- Pushing or fetching at an unused slug creates nothing, even with push-to-create available.
- A second project wanting a name another account already holds is still creatable, keeps its owned path
  untouched, and is given that path plus a suffix.

## Verified on upgrade

The backfill is the part a live instance depends on, so it was exercised rather than reasoned about. A
database was built with the **previous** schema, from a worktree at the last commit, holding three projects
including two that share the path `shared` under different owners. Opening it with the new storage layer
applied migration 6:

```text
alice/shared -> global slug 'shared'
bob/shared   -> global slug 'shared--2'
alice/unique -> global slug 'unique'
lookup 'shared'    -> alice/shared
lookup 'unique'    -> alice/unique
lookup 'shared--2' -> bob/shared
```

The older project keeps the plain name, the newer one is distinguished by its id, and all three resolve by
slug afterwards. No project is left without one.

## Verified against a running server

Against a Debug server with a project created through the browser API:

| Check | Result |
| --- | --- |
| `global_slug` / `global_web_url` on creation | `widgets` / `https://127.0.0.1:7893/projects/widgets` |
| `/projects/widgets` | 200, serves the application shell |
| `git push https://.../projects/widgets.git` | succeeds |
| `/api/graft/slugs/widgets` after that push | 200, resolves to `alice/widgets` |
| Same route, anonymous, project public | 200 |
| `/api/graft/slugs/not-a-project` | 404 |
| `/projects/widgets.git/info/refs` unauthenticated on a private project | 401 |

One early 404 on the shared page turned out not to be a slug defect: the same page by its owned path
answered identically because the repository had no commits yet. Both routes agreeing, including when they
refuse, is the behaviour being asserted.

## Not verified

**The shared page was not rendered in a browser.** Both automation servers available here failed during this
session: the Playwright runtime stopped responding, and Chrome DevTools could attach but refused the
server's self-signed loopback certificate, which it offers no option to override. What is known instead is
that the route templates `/projects/{Slug}` and `/projects/{Slug}/-/commit/{Sha}` and the share field are
compiled into the published client assembly, and that every endpoint the page calls answers correctly. The
rendering itself is untested by hand and should be looked at before this is relied on.
