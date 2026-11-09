# 0047: Every project setting through the agent tools, deletion and transfer behind a switch of their own

- Date: 2026-11-09
- Status: Accepted, implemented
- Source: The project owner on 2026-11-09, "i should be able to update all of these features via mcp", then
  choosing, from four groups offered, all of them: banner, tags and review rules; read-only extras (the audit
  trail, webhook and tag listings); webhooks; and transfer and delete project
- Supersedes: [ADR 0013](0013-project-deletion-and-audit.md)'s "an agent cannot delete a project", and the
  agent guide's sentence that called the absence of a deletion tool a boundary
- Builds on: [ADR 0008](0008-mcp-layer.md) and [ADR 0009](0009-mcp-in-the-forge.md) (tools call the HTTP API with
  the caller's token), [ADR 0046](0046-project-banners.md) (the banner, whose "not settable with a token" this
  closes)
- Review trigger: an agent deleting or transferring a project nobody asked it to; a request for a deletion that
  can be undone; a request to let agents delete a branch, approve, or administer accounts or tokens

## What was missing

The project's Settings tab had fourteen panels. The agent tools covered name and description, default branch,
visibility, the page, the avatar, members and the mirror. Banner, tags, review rules, webhooks, the audit trail,
transfer and deletion had none, and two of them (the banner and transfer) had no route a token could use at all.

## Decision 1: thirteen tools, each a call to a route a token can already use or now can

| Tool | Route | Needs |
| --- | --- | --- |
| `graft_set_banner`, `graft_remove_banner` | `PUT`, `DELETE /api/graft/projects/:id/banner` (new) | Maintainer, writes |
| `graft_list_tags` | `GET /api/v4/projects/:id/repository/tags` | read |
| `graft_create_tag`, `graft_delete_tag` | `POST`, `DELETE .../repository/tags` | push, writes |
| `graft_get_review_rules` | `GET /api/v4/projects/:id/approvals` (new) | read collaboration |
| `graft_set_review_rules` | `PUT .../approvals` | Maintainer, writes |
| `graft_list_activity` | `GET .../audit_events` | Maintainer |
| `graft_list_webhooks`, `graft_add_webhook`, `graft_remove_webhook` | `.../hooks` | Maintainer, `--webhooks true`, writes to change |
| `graft_transfer_project` | `PUT /api/v4/projects/:id/transfer` (new) | owner or administrator, writes, destructive |
| `graft_delete_project` | `DELETE /api/v4/projects/:id` | owner or administrator, writes, destructive |

- The banner's token route is under Graft's own prefix beside `/page`, because GitLab has no banner. The image is
  the request body, bounded before it is read, through the same validator as the browser's upload. `GET` on the
  same path answers `{project_id, banner_url}` to whoever may read the project.
- `GET .../approvals` is GitLab's project approval settings: `approvals_before_merge` and Graft's
  `require_code_owner_approval`, plus `reset_approvals_on_push: true` and `merge_requests_author_approval: false`,
  which are not settings here but what Graft always does ([ADR 0015](0015-review-journey.md)).
- `PUT .../transfer` is GitLab's, with `namespace` taken as a username. A numeric namespace id is refused, and so is
  anything that is not an active user, naming the group case: Graft moves projects between accounts, not into groups,
  by this route. The store's rule is unchanged: only the owner or an administrator.
- A refusal from a settings route passes the server's own reason on (a tag that exists, only the owner may transfer),
  because the generic "the token's scopes" sentence would send an agent after the wrong fix. A webhook route that is
  absent because the operator never enabled webhooks answers 404 like an unseen project, so the tool names both.

## Decision 2: deleting and transferring need a fourth switch, off by default

A server that allowed agent writes before today would otherwise gain project deletion by updating. So:

- **The forge's endpoint.** Administration, Agent access has "Allow deleting and transferring projects"
  (`mcp.allow-destructive` in `ServerSettings`, `allow_destructive` on `/api/graft/admin/mcp`). It is disabled while
  writes are off, saved as off with them, and an absent field reads as off. The audit row and the startup line carry it.
- **The launcher.** `Graft.Mcp --allow-destructive true`, refused at start without `--allow-writes true`.
- **Both.** A refused call names the switch that would permit it. `graft_whoami` reports `delete_and_transfer_allowed`.
- **Deleting asks again.** `confirm` must equal the project's full path as the server reports it, read before the
  deletion, so repeating an id or a slug confirms nothing. The guide tells an agent never to do either unprompted.

The switch bounds the server, not the caller: within it an agent still does exactly what its token's owner can, and a
Maintainer's token still cannot delete or transfer a project it does not own.

## What stays withheld

ADR 0011's four (accounts, password resets, tokens, the agent switches), deleting a branch, and approving. Those
reasons are unchanged. Tag deletion is not among them: a tag is recreated with one call and its commit is untouched.

## What was verified

See [the validation record](../validation/2026-11-09-agent-project-settings.md).
