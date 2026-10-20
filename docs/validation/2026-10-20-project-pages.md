# Project pages and their static sites

Recorded: 2026-10-20. Owner: Implementation agent. Decision: [ADR 0006](../decisions/0006-project-pages-and-public-browsing.md).

## What changed

The project page was a published flag and a listing entry: a private project with a published page appeared
in the public browser, and the link it supplied led to a repository view that refused the reader. The
design in ADR 0006, a `.graft/project.json` config with a file allowlist and a static site, was never
built.

It is now, apart from `features`:

- `.graft/project.json`, read from the default branch, parsed by `PageConfiguration`.
- A bounded path matcher, `PathMatcher`, used by every page decision.
- A page surface at `/-/p/<slug>`, with `GET /api/graft/pages/<slug>` and a per-file route beside it.
- A static site at `https://<pages-origin>/<slug>/`, behind a new `--pages-origin` flag, off by default.
- The public browser now links page-only projects to their page instead of to a repository view.

## Verified

| Suite | Before | After |
| --- | --- | --- |
| Accounts and core | 129 | **162** |
| Git/API/browser | 433 | **469** |
| MCP | 96 | 96 |
| Operations | 41 | 41 |

The core assertions are about the matcher and the parser, which is where a leak would be a single wrong
character: deny by default in all three spellings, a star that does not cross a segment, a double star that
does, whole-path comparison so `docs` never admits `docs-private`, traversal and empty segments matching
nothing, and the parser refusing unknown fields, unknown features, unknown versions, an unparseable file,
`.graft` naming itself, and an image outside its own allowlist.

The HTTP assertions drive a real repository holding both the things the page names and the things it must
never serve:

- Nothing is served before the page is published, however complete the config.
- `/<slug>/-/repo` answers 302 to the project's page on the forge as it is addressed now, and
  `/<slug>/-/graft.json` reports the forge's origin, so a site never has to name a hostname.
- The page lists exactly the two allowlisted files, and `src/secret.cs`, `docs/notes.txt` and
  `.graft/project.json` are absent rather than hidden.
- The named readme is rendered; a project's readme is not offered by default.
- Files the allowlist names are served; the three it does not each answer 404.
- A published page does not open the repository view.
- The site is served from the pages host with the type each extension declares.
- Everything outside the site root answers 404, including `/../src/secret.cs` and `/site/../src/secret.cs`.
- A file whose extension is not on the list is not served.
- `/api/session`, `/api/browse`, `/healthz` and `/-/login` all answer 404 on the pages host.
- Responses carry nosniff and `frame-ancestors 'none'`.
- Pushing a broken config unpublishes both the page and the site.

The pages origin is selected by `Host`, so the suite exercises the one property that makes a site safe,
that it is a different origin, without a hosts-file entry or a second listener.

## Verified in a browser

Both surfaces were rendered in headless Chromium against a running server, signed out:

- `https://pages.graft.test:7905/lanternry/` serves the sample site with its own stylesheet and SVG, and
  its internal link works.
- `https://127.0.0.1:7905/-/p/lanternry` shows the title and description from the config, the rendered
  readme, exactly three files, and a button to the site on the other origin.

Chrome refuses the server's self-signed loopback certificate and offers no override through the automation
server, so Chrome was started directly with `--ignore-certificate-errors` and
`--host-resolver-rules="MAP pages.graft.test 127.0.0.1"`, then attached to. That resolver rule is also what
let a browser reach the pages origin without touching DNS.

## Two things found while building this

**`CreateProjectWithRepositoryAsync` returned a project with no instance-wide slug.** The record was built
by hand from the insert's arguments and the slug was omitted, so every caller using the returned value saw
an empty one until it read the project back. Fixed, and it was the acceptance suite's first failure here.

**The sample is committed.** `samples/project-page` is the repository that produced the screenshots: a
config, a readme, two allowlisted files and a four-file site, with instructions. It is the fastest way to
see the feature, and it keeps the documentation honest, because it is pushed and served rather than
described.

## Not built

**`features`.** The field parses and `KnownFeatures` is empty, so naming anything is an error. That is the
documented behaviour rather than an oversight: a config claiming to expose issues must fail loudly while
there are no issues.

**A single-hostname fallback.** ADR 0006 records a sandboxed frame as the documented alternative and says
it is a worse product because links inside the site break. Without a pages origin there is no site, and the
page half still works.
