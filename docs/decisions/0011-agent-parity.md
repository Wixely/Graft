# 0011: Bringing agent access to parity with the API

- Date: 2026-10-19
- Status: Accepted user request, implemented, with four capabilities deliberately withheld
- Source: "examine what the MCP access is missing ... investigate what we can do to bring MCP up to parity with what an admin could do given the same permissions"
- Review trigger: Any proposal to expose account administration, token issue or the agent switches themselves to a token; a request for agents to delete or rename projects; or an OAuth story that changes what a credential means

## What the gap actually was

The question assumed agents were missing tools. They were not. All ten tools mapped exactly onto the
token-authenticated surface, and that surface was nearly the whole of it. The gap was one layer down: of
the server's routes, almost every **mutating** one authenticated a browser session and nothing else.

So an agent could not edit a description because no credential could. The browser could, through a route
that takes a session cookie and an antiforgery header, and a token has neither.

That reframing decided the work. Parity is reached by building the token half of capabilities the product
already has, which is mostly a matter of implementing more of the GitLab API that Graft intends to
implement anyway, and then giving each a tool.

## Decision: six capabilities, in order of what an agent most needs

| Added | Route | Tool |
| --- | --- | --- |
| Project text and default branch | `PUT /api/v4/projects/:id`, widened | `graft_update_project` |
| Identity | existing `/api/v4/user` | `graft_whoami` |
| Writing a file | `POST`/`PUT .../repository/files/:path` | `graft_write_file` |
| Commit diffs | `GET .../repository/commits/:sha/diff` | `graft_read_commit` |
| Membership | `/api/v4/projects/:id/members` | three tools |
| Project avatar | `POST`/`PUT /api/v4/projects/:id/avatar` | `graft_set_avatar` |

Every one is a GitLab route with GitLab's meaning, except the diff's richer response shape, and every
deviation is recorded in [compatibility.md](../compatibility.md) rather than left to be discovered.

**The update route was the highest leverage.** It already parsed the body and refused every attribute but
`visibility` by name. Widening that allowlist to `name`, `description` and `default_branch` closed three
gaps in one handler. `path` stays refused: it is an address, and this route does not move projects.

That also closed a **product** gap rather than only an agent one. A description could be set at creation
and never corrected, and a name could not be changed at all, by anybody. The browser gained the same
ability in the project settings tab at the same time, because a capability agents have and people do not
would be an odd thing to ship.

**Two of these needed the half of [ADR 0005](0005-browser-upload-transport.md) that was never built.**
Writing a file and setting an avatar were browser-only not by policy but by transport: both routes take an
upload receipt from the resumable browser queue rather than bytes, and that queue needs a browser
antiforgery token. ADR 0005 says DnaX for the browser and plain HTTP for API clients; the API clients half
did not exist for repository content or images. It does now, with the content inline for a file and as the
request body for an image, bounded before it is read and validated by exactly the same code the browser
route uses.

## Decision: four capabilities stay out of reach, whatever the switch says

Account creation and disabling, password resets, personal access token issue and revocation, and the agent
settings themselves remain browser-only.

The first three would let an agent manufacture a credential beyond the one it was handed, which is the
single property [ADR 0008](0008-mcp-layer.md) exists to protect: an agent can do exactly what its token's
owner can do, and the blast radius is a credential an administrator can see and revoke. An agent that can
mint tokens has no blast radius.

The fourth is circular. An agent that can switch on its own write access makes the switch decorative.

This is a deliberate asymmetry rather than an oversight, and the administrator page now says so in the one
sentence a human actually reads about agent powers.

## Decision: nothing about the permission model changed

Every new route goes through the same resolution and the same decision as its browser counterpart. A
read-only token cannot write a file or enumerate a membership. Reading a membership is itself a Maintainer
act, so it answers `404` to a token that may only read the project. A diff is a repository surface, so a
published-page grant can never reach it. The administrator's write switch still bounds every mutating
tool, and each refusal still names the page that would permit it.

One small thing had to be settled: granting a role to an account that does not exist answers `400`, not the
project's `404`. The project was found; refusing with its not-found would say something untrue about it.

## What was verified

The Git suite grew from 405 to 433 assertions and the MCP suite from 82 to 96. Both cover the happy path
and the refusals: a read-only token denied at each new route, content that is not base64 refused rather
than stored, an image that is not an image refused rather than served back later as one, a role Graft does
not have refused by name, a commit that is not there reported rather than invented.

The store and operations suites are unchanged at 129 and 41. See
[the evidence](../validation/2026-10-19-agent-parity.md).
