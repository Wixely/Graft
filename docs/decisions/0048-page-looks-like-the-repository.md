# 0048: A published page looks like the repository, and serves its pictures

- Date: 2026-11-09
- Status: Accepted, implemented
- Source: A request on 2026-11-09: "i need the public page to look exactly like the repo page (unless a
  specialised html is included). we also need to serve images/media (for readme images)"
- Builds on: [ADR 0006](0006-project-pages-and-public-browsing.md) (the page and the site),
  [ADR 0045](0045-semi-public-projects.md) (what a page may show), [ADR 0046](0046-project-banners.md) (the banner)
- Review trigger: a request for video or SVG on a page; a request for a picture to load without being named in
  `files`; a request for markdown files other than the readme to render on a page; any change to what the
  repository page's code tab shows

## Decision 1: the page wears the repository's layout

`/-/p/<slug>` draws the same masthead (banner, avatar, title, description), the same tab bar, the same file
table with breadcrumbs, folders first, and the same readme panel beneath it as the project's code tab. Opening
a file shows it in the same panel.

What fills it is unchanged from ADR 0045, and that is the point: same look, same grants.

- The **file table** is the allowlisted files, laid out by folder. With `tree: true` it is the whole structure,
  and a file the allowlist does not admit is listed by name, greyed, and cannot be opened.
- The **tabs** are only what the config opened: Code always, then Commits (`history`), Issues and Releases.
  The `features` links follow as links, as before.
- There is **no branch selector, clone menu, archive download or commit link**, because a page cannot offer any
  of them (ADR 0006 Decision 3, ADR 0045). The header says "default branch" and counts the files.
- The title is the page's, never `owner / name`, because the slug is the address that names no account.

The masthead is written out in the page rather than shared with `ProjectHeader`, which takes the repository
response a page reader may not have. The markup and classes are the same, so it is the same masthead.

## Decision 2: a page with a site of its own sends readers to the site

When the config names `site` and the operator has configured a pages origin, `/-/p/<slug>` with no `?tab=`
replaces itself with the site. That is what "unless a specialised HTML is included" asked for: the owner
designed a page, so that is the page.

Any `?tab=` keeps the reader on the forge's page, so the site can link to its files, issues or releases. The
site's own back-link, `/<slug>/-/repo`, and the `page` field of `-/graft.json` now carry `?tab=code` for the
same reason; without it the two would send a reader back and forth. The forward replaces the history entry, so
Back does not bounce either.

## Decision 3: pictures through the page's own route, and only named ones

`GET /api/graft/pages/<slug>/image?path=<path>` serves an image to anyone the page admits. It asks the
allowlist first, exactly as the file route does, then runs the bytes through `ImageContent`: PNG, GIF, JPEG or
WebP, the type the bytes prove, never SVG, as for avatars and the repository image route. A file the allowlist
admits that is not such an image is a 415; one it does not admit is a 404, indistinguishable from absent.

The readme's images now resolve to this route. Before, they pointed at the repository's image route, which a
page reader may not browse, so every picture on a page-only project was broken. A reference is resolved
against the readme's own folder (`./`, `..`, a query or fragment dropped); one that climbs out of the
repository, or names a file the allowlist does not admit, is downgraded to a link by the renderer, as an
off-site picture already was.

**A picture is published by naming it in `files`, like any other file.** Letting the readme publish what it
references was considered and refused: it is the implicit grant the config parser already forbids `image` and
`readme` from making (each must itself match `files`), and it would let a Developer publish a file by mentioning
it in a commit, where only `files` was meant to publish anything.
`"docs/**/*.png"` or `"**/*.png"` is the one line it takes.

## Not built

- **Video and audio.** Markdown's image syntax renders `<img>`, raw HTML is disabled, and there is no player
  element in the renderer's allowlist. A video would need a validator of its own and a decision about range
  requests.
- **SVG**, for the reason ADR 0046 and `ImageContent` give: it is a document that can carry script.
- **Rendering other markdown files** on a page. The repository's code tab shows them as text too; the two stay
  the same.
- **Readmes in subfolders.** The page shows the readme the config named, at the root, as the repository shows
  its root readme.
