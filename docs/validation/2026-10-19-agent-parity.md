# Agent access brought to parity with the API

Recorded: 2026-10-19. Owner: Implementation agent. Decision: [ADR 0011](../decisions/0011-agent-parity.md).

## What the inventory found

Every route in the server was listed with the credential it accepts. Of the mutating ones, almost all took
a browser session and nothing else; the token surface was list and create a project, set visibility,
publish a page, and read repository content. The ten existing tools covered that surface exactly.

So agents were not missing tools. The API was missing routes, and the tools could only be as capable as the
API they call.

## What was added

| Capability | Route | Tool | Store work |
| --- | --- | --- | --- |
| Name, description, default branch | `PUT /api/v4/projects/:id`, widened | `graft_update_project` | `SetProjectDetailsAsync` |
| Identity | existing `/api/v4/user` | `graft_whoami` | none |
| Write a file | `POST`/`PUT .../repository/files/:path` | `graft_write_file` | none |
| Commit diff | `GET .../repository/commits/:sha/diff` | `graft_read_commit` | none |
| Membership | `/api/v4/projects/:id/members` | `graft_list_members`, `graft_set_member`, `graft_remove_member` | none |
| Avatar | `POST`/`PUT /api/v4/projects/:id/avatar` | `graft_set_avatar` | none |

Eight tools join the set, taking it from ten to eighteen. The browser gained a name and description form in
the project settings tab, through a new `/api/projects/:id/details` route, because the product itself could
not edit either after creation.

## Verified

| Suite | Before | After |
| --- | --- | --- |
| Git/API/browser | 389 | **433** |
| MCP | 82 | **96** |
| Accounts/permissions | 129 | 129 |
| Operations | 41 | 41 |

The new assertions are as much about refusals as about capabilities:

- A read-only token cannot write a file, set an avatar, or enumerate a project's membership.
- Content declared `base64` that is not base64 is refused rather than stored.
- An avatar that is not an image is refused rather than stored and served back later as one.
- A role Graft does not have is refused by name, as is a project attribute the update route does not
  implement; `path` is the attribute now used to assert that, since `name` is implemented.
- Granting a role to an account that does not exist answers `400` rather than the project's `404`.
- A commit that is not there is reported rather than invented.
- An empty update body is refused rather than treated as a successful no-op.
- Through the tools: every mutation is refused while the administrator's write switch is off, and the
  refusal names the page rather than a flag.

Two behaviours were asserted by round trip rather than by the route's own answer. A file written through
the API is read back through the route that was already there, and a description edited through a tool is
read back through `graft_get_project`.

## Three things found while building this

**The file commit route was never plain JSON.** It looked like it from its contract, but
`RepositoryFileUpload` carries an `upload_id`, not content, so the browser's resumable queue is the only
way into it. Writing a file for a token therefore needed the plain-HTTP half of ADR 0005, not just a second
route in front of the existing handler. The same was true of avatars.

**The read and write sides of the files route disagreed about escaping.** GitLab documents that path as
URL-encoded; the existing read route used the raw route value, so `docs%2Fthing.md` found nothing. Both now
unescape, which is what lets a file be read back by the spelling it was written with.

**Three tool-count assertions, not one.** The MCP suite asserts the advertised tool count in three places,
for the forge endpoint, the stdio launcher and the launcher's HTTP listener. Adding a tool and updating one
of them fails the suite somewhere unrelated to the change.

## Not done

**Four capabilities are deliberately withheld** and are not a gap to be closed later without a decision:
account administration, password resets, token issue and revocation, and the agent settings themselves. The
reasoning is in [ADR 0011](../decisions/0011-agent-parity.md).

**No browser check of the new settings form.** The name and description panel was added to the project
settings tab and is covered by its API route's assertions, but the page was not driven in a browser this
session. The same gap stands for the project page under an instance-wide slug, recorded on
[2026-10-19](2026-10-19-project-slugs.md).

**Ownership transfer** remains browser-only. It is the one remaining project-management capability a token
cannot reach, left out because GitLab's shape for it differs from Graft's and no agent need for it has been
stated.
