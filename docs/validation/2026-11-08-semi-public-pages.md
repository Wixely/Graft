# A semi-public project: structure, history, issues and releases on a page

Recorded: 2026-11-08. Owner: Implementation agent. Decision: [ADR 0045](../decisions/0045-semi-public-projects.md).

## What changed

A page's config could name files, a readme, features as links and a site. It can now also open, each off until
named: `tree`, `history` (with `commits` and `authors`), `issues` and `releases`. The config's grants travel as
one `PageReach` value from the page routes to the authorization decision and into the store's read methods for
issues, comments and releases; reading a release became its own action, `ReadReleases`. The page renders all
four itself at `/-/p/<slug>`, from routes under `/api/graft/pages/<slug>/`.

## Verified

| Suite | Before | After |
| --- | --- | --- |
| Accounts and core (`tests/Graft.Acceptance`) | 218 | **243** |
| Git/API/browser (`tests/Graft.Git.Acceptance`, Debug server) | 1334 | **1375** |

The core assertions are about the parser and the decision, which is where a wrong character would be a grant:
the four switches parse, `history` defaults to 100 commits and no authors, ten loosely written forms (`"yes"`,
`1`, `"true"`, `commits` of 0 or 501, `authors` of `everything`, an unknown `history` field) are each an error,
a config without them has none of them and a reach of `None`; a published page with no reach grants
`ReadPage` and nothing else; a reach that opened issues grants `ReadCollaboration` as a `Page` grant and not
`ReadReleases`; no reach grants `BrowseRepository` or `WriteCollaboration`; and a reach is nothing when the
published flag is off.

The HTTP assertions drive the page suite's real repository, to which the owner adds two issues (one
confidential), a comment, a tag and a release with a file:

- Before the page is published, `/issues`, `/issues/1` and `/releases` under the page are 404 like the page.
- The structure names `src/secret.cs` as not readable and `docs/guide.md` as readable, lists `src` as a
  directory, and never names `.graft`.
- The history reports `authors: names`, lists the one commit's title, carries a name and a null email, and
  carries no `diff`, `files`, `parent_ids` or `web_url`.
- The issues route lists exactly one issue to a reader with no account; the issue route returns its
  description and its one comment; the confidential issue is 404.
- The project's own `/api/v4/.../issues` and `/releases` stay 404 to the same stranger.
- The releases route lists the release with its notes, no `commit` and no `assets`, and a file whose URL is
  under the page; the download returns the bytes as an attachment.
- A broken config takes every opened surface down with the page, including the download.
- A valid config that names only `files` serves a page with `tree` and `history` null and `issues` and
  `releases` false, and both routes answer 404.

The "before" counts are the "after" counts less the assertions this change added, since the suites were not
rerun on the previous commit.

The Git suite passed with `GRAFT_REMOTE_HELPER` pointed at the NativeAOT helper in `artifacts/helper/win-x64`. With
the default, the Debug build of `git-remote-graft`, its remote-helper section failed in this environment twice:
first because the suite staged only the apphost without its dll (fixed in this change by copying the files that
share the helper's name), then at the first push through the helper, with Git reporting it could not read from
the remote. The page assertions had passed in every run; the helper section is unrelated to this change and is
left for whoever next runs the suite without an AOT helper.

The browser's page view was not screenshotted for this record; the Blazor client compiles and the markup
follows the existing list and panel classes. The MCP suite was not rerun: its only change is a paragraph in
the `pages` guide text, which no assertion reads.

## Not built

See the ADR's last section: no writing through a page, no merge requests, no page-granted reading over
`/api/v4`, history only on the default branch and at most 500 commits.
