# 0010: Instance-wide project slugs

- Date: 2026-10-19
- Status: Accepted user request, implemented
- Source: "projects should be able to have their own slug under the /projects if it's not taken already. this allows us to share things without the users name in the url. this is enabled by default and a slug is assigned, given random chars at the end if taken. someone cloning or pushing to the repo using this slug should work as normal."
- Review trigger: Any proposal to let an owner choose or change a slug, to make slugs optional, to add a second kind of namespace, or to list the slug namespace as a directory

## Decision

Every project has a second name that is unique across the whole instance, and answers at `/projects/<slug>`
for browsing, cloning and pushing. The owned path, `/<owner>/<slug>`, is unchanged and still works. A slug
is an address, not a permission: the same project, the same decisions about who may read or write it.

Assigned automatically, because the request asks for one by default and a name nobody has chosen is worth
nothing to share. A project asks for its own path first, since that is the name somebody would guess. When
that name is already held instance-wide, the project keeps its path and gains a short random suffix, so
`widgets` taken means the next one is `widgets-3f9c`.

Random rather than counted. A counter would publish how many projects of that name exist, and would make
the second one guessable from the first. The suffix widens from two bytes if a name is somehow that
popular, and the unique index remains the authority.

## Decision: the shared page does not redirect to the owned path

Opening `/projects/<slug>` renders the project there. It does not redirect to `/<owner>/<slug>`, because a
redirect would put the username straight back in the address bar and undo the only thing the link is for.

Every link the page builds therefore stays on the route the reader arrived by, including the commit pages,
so following a link inside a shared project does not reveal the owner either. The project page still
*displays* the owner, which is ordinary forge metadata; what the slug keeps out of the way is the URL.

## Decision: it is an alias, not a second set of rules

Resolution by slug runs through the same permission decision as resolution by path. A private project
reached by slug is refused exactly as it is by path, and a slug that exists but may not be seen answers
identically to one that was never issued, so the flat namespace cannot be probed to learn what an instance
holds.

The Git transport needed no new route. `/projects/<slug>.git` is the same two segments as
`/<owner>/<slug>.git`, so the existing template already matches and only the lookup inside it differs.
Everything after that point, the credential, the decision, the refusals, is the code that was already
there.

One deliberate exception: **push-to-create never fires on a slug**. That path names no owner, and a project
has to be created in somebody's namespace. A push to an unused slug is a push to something that is not
there, whatever the operator's setting says.

## Decision: the REST API accepts a slug where it accepts a path

`/api/v4/projects/<identifier>` already took a numeric id or an escaped `owner/slug`. A single segment that
is not a number now resolves as an instance-wide slug. GitLab has no meaning for that shape, so reading it
takes nothing away from a GitLab client, and it means an API caller or an agent can use the same name it
was given to share. `global_slug` and `global_web_url` are added to the project response as Graft's own
fields; a client that ignores them loses nothing.

## Decision: the application's own path segments are now reserved account names

A namespace in Graft is a username at the root of the site, so an account called `projects` would own every
link the slugs live under. Account creation now refuses `projects`, `api`, `healthz`, `mcp` and
`index.html`.

Checked when an account is created, never when one is read, so an instance that already has such an account
keeps working and simply loses that one path to the application. There was no reserved list before this;
`api` and `healthz` were registrable and are now not.

## Decision: existing projects are given slugs by the migration

Upgrading does not leave older projects without a shareable name. Migration 6 gives each project its own
path where that path is free instance-wide, and otherwise its path, two hyphens and its id.

Two hyphens cannot appear in a project path, which is what makes the fallback safe: no generated value can
collide with a plain path, and ids make them unique among themselves. The result is deterministic, which a
migration should be; only names issued afterwards are random.

## What was verified

The Git suite grew from 389 to 405 assertions. They cover the slug a new project is given, the shareable URL
built from it, resolution through the REST API, a clone and a push through the slug landing in the same
repository the owned path serves, the refusal an anonymous caller gets being identical through both
addresses, an unused slug and a concealed project answering the same way, push-to-create not firing, and a
second project wanting a taken name keeping its path and gaining a suffix.

The upgrade was exercised against a database built on the previous schema holding two projects that share a
path under different owners: the older keeps the plain name, the newer becomes `shared--2`, and both
resolve. See [the evidence](../validation/2026-10-19-project-slugs.md).
