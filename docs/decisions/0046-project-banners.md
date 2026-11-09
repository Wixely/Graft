# 0046: A project banner behind the title and the tabs

- Date: 2026-11-08
- Status: Accepted, implemented
- Source: A request on 2026-11-08 for a top banner image on a repository, "added the same way repo images are
  added", covering everything down to and including the project's tab bar
- Builds on: the project avatar (schema migration 2, `ImageContent`), and the shared project header of
  2026-11-08 that puts the title row and the tab bar in the same place on every project page
- Review trigger: a request for a banner on a group, a user or a published project page; a request to set one
  through `/api/v4` or the MCP tools; any change to which image formats are accepted

## Decision 1: a banner is set, stored and served the way a project avatar is

- It is uploaded in the project's settings through the same upload transport, under its own upload profile
  (`banner`) with the avatar's limits: one file, at most 2 MB.
- The bytes pass the same structural validator. Only PNG, GIF, JPEG and WebP are kept, at most 4096 pixels on a
  side, and SVG is never accepted. The served content type is the one the validator chose, under the global
  `nosniff` header.
- It needs the permission to manage the project, to set or to remove.
- It lives in the database, in its own `ProjectBanners` table (migration 27), so a backup holds it with the project.
  It is not a third owner type in `Avatars`, because SQLite cannot widen that table's `CHECK` constraint without
  rebuilding it. Deleting a project deletes its banner.
- `GET /api/graft/banners/project/<id>` serves it to whoever may read the project's avatar, which is anyone the
  project's page or repository admits. It answers 404 alike for a project the caller cannot read and for one with
  no banner.

A banner can be removed, which an avatar cannot yet. A banner is large enough that "go back to none" is a
reasonable thing to want, and it costs one route.

## Decision 2: the banner runs behind the title row and the tab bar, and never moves them

The browser's repository view reports `banner_url`, carrying the banner's digest as `v` so a replaced banner is a
new address. When it is set, the shared project header draws the title row and the tab bar inside one rounded
masthead, with the image behind both.

- The image is an `<img>` laid absolutely under the content, not a CSS background. The content security policy
  refuses inline styles, and a background URL would otherwise need one.
- The masthead's height comes from its padding and its contents, never from the image. The tabs therefore sit
  at the same place on every page of the project, before the image loads and after.
- A dark ground and a scrim that darkens towards the tabs sit between the image and the text. The title, the
  description and the tab labels turn white over it, so they read on any picture in either theme. Labels keep
  their own filled backgrounds.
- The picture is cropped to fill (`object-fit: cover`). The settings text suggests about 1600 by 300.

## Not built

- No banner on a group, a user or a published project page (`/-/p/<slug>`). The page has its own layout and no
  tab bar.
- No way to set one with a token. `/api/v4` has no banner in GitLab's API to follow, and the MCP tools have no
  banner tool. *Closed on 2026-11-09 by [ADR 0047](0047-agent-project-settings.md): `/api/graft/projects/:id/banner`
  and `graft_set_banner`, `graft_remove_banner`.*
- No server-side resizing or re-encoding, for the reason `ImageContent` gives: it would mean an image library as
  a dependency.
