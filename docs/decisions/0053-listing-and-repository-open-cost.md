# 0053: What a project listing costs, and what opening a repository costs

- Date: 2026-11-16
- Status: Accepted, implemented
- Source: The project owner reported on 2026-11-16 that the project list loaded so slowly that the page said
  "Graft is unavailable. The server could not be reached", which was not true, and asked for a review of the
  inefficiencies behind it; all four fixes proposed in that review were then asked for
- Builds on: [ADR 0028](0028-git-engine.md) (the Git engine, packs and housekeeping), whose reason for
  repacking at a thousand loose objects this amends

## What was wrong

`GET /api/projects` described each of up to a hundred projects in turn, and describing one did three
expensive things:

1. It took the repository's lock and opened the repository, to learn one fact: the default branch. Opening a
   repository built its whole object table and then walked every commit and tree from every reference.
2. It waited on that lock with no limit, so a push, an import or a repack on any one listed project held up
   the whole list.
3. It asked the database separately for the avatar, the owner's avatar, the fork, the stars, the import and
   up to three permission decisions, each on its own connection, and each decision looked up the caller and
   the project again: around fifteen queries a project.

Opening was far slower than it needed to be, for two reasons in the engine. The pack cache moved an entry to
the recent end of its list by searching the list for it, so with tens of thousands of small delta bases
cached every read was a linear search and reading a history was quadratic. And describing a packed object
walked its delta chain to the end, a read per link, for every object, though the objects are described in
pack order and every base had already been described. Loose objects were hashed in full on every open.

Measured on a generated repository of 8,000 commits and 155,061 objects in a 27 MiB pack: opening took about
130 seconds, 7 in building the table and the rest in the walk. A proxy that gives up after 100 seconds then
answered on Graft's behalf, and the client read any error status as the server being unreachable.

## Decision: a listing does not open repositories

A project's description finds its default branch without the lock. An open repository's references are
replaced whole, never edited in place, so they are read directly; one nobody has opened is read from its
reference file alone (`DurableRepositoryStorage.ReadDefaultBranch`), falling back to the copy a replacement
keeps and to the layout before the current one. Routes that already hold the repository use it as before.

## Decision: a listing asks the database once

`GraftStore.GetProjectListingFactsAsync` answers every fact a description needs for many projects on one
connection: one query for the rows, avatars, stars and forks, one for the imports, and one role lookup per
owning group. Its decisions come from the same `ProjectAuthorization.Decide` with the same inputs as
`GetPermissionAsync`, and the server applies the same public-browsing rule to them. Every list of projects
uses it; a single project goes through it with one id, so there is one way to describe a project. Group
lists do the same with `GetGroupRolesAsync`.

## Decision: opening a repository costs what it must

- The pack cache keeps each entry's own list node, so a hit is moved without a search.
- Describing a pack in order remembers each resolved type by offset, so a delta's type is one step away.
- A loose object is no longer hashed when the repository opens. Its name is kept with it and checked the
  first time its content or id is read, and a mismatch is refused then with the same `stored-object-hash`
  as before. A damaged file still never passes for the real one; it is found when used rather than at open.
  ADR 0028 gave hashing at open as the reason the repack threshold is a thousand rather than Git's 6,700;
  the threshold stays, because opening still reads every loose file's header, one file each.
- The connectivity walk from every reference at open stays. It is the check that a repository's references
  name complete histories, and with the cache fixed it is no longer the cost it was.

The same repository now opens in about 4 seconds: 1.2 building the table, 3 in the walk. The pack checksum
verified when a pack opens is unchanged.

## Decision: the browser says what actually happened

`GraftApi.GetAsync` marked the server unreachable for any error status, and the mark stayed until the next
request succeeded, so a refusal or a proxy's timeout on one page could make the next say "Graft is
unavailable". Now only a request that got no answer at all marks it unreachable. `ReadAsync` returns the
failure's message, and a 504 or a proxy's 524 reads "Graft took too long to answer". The project list shows
that message, and says the projects could not be loaded rather than that there are none. It no longer waits
for the caller's groups before showing anything; they are fetched when the new-project form opens.

## Not done

- The other per-item loops (issues, merge requests, members) still ask per item. They were not the reported
  problem; the same pattern applies if one becomes one.
- `ReadableProjects` still decides per id. It serves starred lists and is bounded by what one person stars.
- The walk at open is still a full walk. A repository much larger than the one measured will still take
  seconds to open the first time after a restart; opening in the background at startup, or remembering the
  result of the walk, would be decisions of their own.

## Review triggers

- A listing or project page that is slow again on a warm server.
- A change to how references are written, which `ReadDefaultBranch` reads without the storage's lock.
- A repository whose first open after a restart is reported as slow.
