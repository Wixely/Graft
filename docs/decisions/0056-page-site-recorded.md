# 0056: A page's site is recorded, so readers go straight to it

- Date: 2026-11-18
- Status: Accepted, implemented
- Source: The project owner reported on 2026-11-16 that opening a published page with a site of its own showed the
  repository-style page for a moment before the site. That flash was fixed in v0.1.4, which still showed Graft's top
  bar and "Opening the project's site…" on the way. On 2026-11-18 they asked for Explore, and the page's own address,
  to go straight to the site.
- Builds on: [ADR 0048](0048-page-looks-like-the-repository.md) (a page with a site forwards to it unless a
  tab is named), [ADR 0051](0051-page-artifacts.md) (an artifact as the site's front door) and
  [ADR 0053](0053-listing-and-repository-open-cost.md) (a listing does not open repositories)
- Review trigger: a site address that depends on something other than the default branch's `.graft/project.json`,
  the page's publication and the pages origin; a change of the default branch that does not go through the two
  routes that set it; a report of a card or a page address sending a reader to an old site

## What was missing

Whether a page has a site, and where it starts, is written in the repository's `.graft/project.json`. Explore lists
projects from the database and could not know without opening each repository, which ADR 0053 removed from listings
for good reason. So a card linked to the page, the browser application loaded, asked the forge for the page, and only
then went to the site. The page's own address did the same, so a shared link showed Graft before the site.

## Decision: the forge records where each page's site starts

`Projects.PageSite` holds what the config says: nothing for no site, the site's root, or the name of the page
artifact that is its front door. The URL is built when it is used, from the pages origin and the artifact prefix
as they are configured then, so moving either needs no update.

It is read again from the repository whenever the answer could change:
- any branch moving, since every way that happens (push over HTTPS, SSH or the chunked transfer, a web edit, a
  merge) sends the push notification, which now asks for the refresh whether or not webhooks are on;
- the default branch being changed, by the browser or the API;
- the page being published or withdrawn, by the browser or the API;
- a mirror updating;
- once for every published page when the server starts, which also fills the column for an older database.

Each refresh runs in the background, because some callers still hold the repository. Reading on every request
instead would have opened the repository on every Explore listing and every page visit.

## Decision: Explore and the page's address use it

- An Explore card for a project that publishes only a page links to the site when one is recorded. A card for a
  project whose source is public still opens the repository, as before.
- `GET /-/p/<slug>` with no query string answers `302 Found` with the site's address before the application loads,
  when the page is published, a site is recorded, a pages origin is configured and the reader may see the page.
  With `?tab=`, `?file=` or `?path=` it is always the page, because those are how a site links back to its files.
  Found rather than permanent, because a site can be withdrawn.

The page's own data route still reads the config live, and the application still forwards from the browser. A
missed refresh therefore leaves at worst an old answer and the detour of v0.1.4, never a broken link.

## What was verified

The Git acceptance suite, Debug build on Windows:
- before the forge has recorded a site, the bare page address is the application;
- after publishing through the forge, the bare address answers `302` to the site's root on the pages origin;
- with `?tab=code`, `?file=` and `?path=` it does not;
- Explore's listing carries the site's address for the card.

Also checked: the live deployment's proxy passes a cross-host `Location` unchanged, which the redirect depends on.
The rewrite the deployment guide warns about (found 2026-11-13) did not happen there on 2026-11-18.
