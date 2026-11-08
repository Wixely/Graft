# A project banner behind the title and the tabs

Recorded: 2026-11-08. Owner: Implementation agent. Decision: [ADR 0046](../decisions/0046-project-banners.md).

## What changed

A project may have a banner image, set from its settings through the upload transport and validated like its
avatar. It is stored in a new `ProjectBanners` table (migration 27), served at `/api/graft/banners/project/<id>`,
reported as `banner_url` in the browser's repository view, and drawn behind the shared project header's title row
and tab bar. The browser fixture takes `GRAFT_BROWSER_FIXTURE_BANNER`, an image path, to seed one.

## Verified

| Suite | Before | After |
| --- | --- | --- |
| Accounts and core (`tests/Graft.Acceptance`) | 243 | **256** |
| Git/API/browser (`tests/Graft.Git.Acceptance`, Debug server) | 1375 | **1385** |

The core assertions drive the store:

- A non-member is refused setting and removing a banner, and nothing is stored.
- A 1600 by 300 PNG of more than 250 KB round-trips byte for byte, which also covers the BLOB binding that
  Dapper.AOT cannot do.
- The digest is reported without the bytes.
- A replacement replaces the banner, and a banner is not the project's avatar.
- A removed banner is gone, and removing it again reports nothing removed.

The HTTP assertions use the public project:

- With no banner, the route answers 404 and the repository view reports a null `banner_url`.
- After one is set, a visitor with no account is given its address with a version.
- The banner is served as the stored PNG with an ETag.
- The route refuses an unknown query parameter, and a project that does not exist is a 404.
- A removal with no browser session is refused, and the banner stays.

The Git suite ran with `GRAFT_REMOTE_HELPER` pointed at the NativeAOT helper, as in the semi-public record.

The browser fixture was run with a generated 1600 by 300 JPEG and screenshotted signed out in headless Edge, on
the public project's code and issues pages, in light and dark. The banner fills the masthead behind the title row
and the tabs. The text is legible in both themes, and the tab bar is at the same height on both pages. Setting a
banner through the settings upload and the remove button were not driven in a browser, because the route behind
them is the avatar's ingest with a different profile name. The MCP suite was not rerun, because nothing it reads
changed.

## Not built

See the ADR's last section: nothing for groups, users or the published page, no token or MCP route, and no
resizing.
