# The published page looks like the repository

Recorded: 2026-11-09. Owner: Implementation agent. Decision: [ADR 0048](../decisions/0048-page-looks-like-the-repository.md).

## What changed

- `/-/p/<slug>` redrawn in the code tab's layout: masthead with banner and avatar, tabs for what the config opened,
  file table with folders and breadcrumbs, readme beneath, files and images opened in place.
- `GET /api/graft/pages/<slug>/image?path=`: allowlisted PNG, GIF, JPEG and WebP for page readers. Readme pictures
  resolve to it, relative to the readme's folder.
- The page response carries `avatar_url` and `banner_url`.
- A page with a site forwards to it unless `?tab=` is given; `/<slug>/-/repo` and `-/graft.json` carry `?tab=code`.
- `ViaSlug` in thirteen client pages tolerates the null `Namespace` the `/projects/<slug>` route leaves, which had
  crashed every slug-addressed project page in the browser.
- The committed sample (`samples/project-page`) gains a readme picture and admits `docs/**/*.png`.

## Verified

| Suite | Before | After |
| --- | --- | --- |
| Git/API/browser (`tests/Graft.Git.Acceptance`, Debug server) | 1395 | **1409** |

Run with `GRAFT_REMOTE_HELPER` pointed at the NativeAOT helper in `artifacts/helper/win-x64`. New assertions, all
anonymous: the readme's admitted picture points at the page image route, resolved from `./`; nothing in it points
at a repository route; the hidden and climbing pictures are not images; the page reports avatar and banner; the
image route serves the allowlisted PNG byte for byte as `image/png`, refuses an allowlisted fake PNG and an
allowlisted text file with 415, and answers 404 for an unlisted image, the config, a `..` path and an empty path;
the route goes down with a broken config and with a config that stops naming it; the site's back-link and
`graft.json` carry `?tab=code`.

In a browser (headless Chrome against the disposable browser fixture, signed out): `/-/p/page-only?tab=code`
shows the masthead, tab, file table and readme with its picture loaded; the `docs` folder opens with breadcrumbs;
`lanternry.png` opens as an image. `/-/p/page-only` forwards to the site, and the site's "This project on Graft"
link returns to the page's Code tab without bouncing. `/projects/open`, which crashed before the `ViaSlug` fix,
renders.

## Not verified

- Issues, Releases and Commits tabs in a browser; their markup is the previous page's, moved under tabs.
- A banner on a page in a browser (the fixture sets one only when `GRAFT_BROWSER_FIXTURE_BANNER` is given).
- No NativeAOT publish of the forge with these changes.
