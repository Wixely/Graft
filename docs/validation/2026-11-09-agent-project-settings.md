# Every project setting through the agent tools

Recorded: 2026-11-09. Owner: Implementation agent. Decision: [ADR 0047](../decisions/0047-agent-project-settings.md).

## What changed

Thirteen MCP tools (54 in all) for the banner, tags, review rules, webhooks, the audit trail, transfer and deletion.
Token routes for the banner (`/api/graft/projects/:id/banner`), GitLab's `GET .../approvals` and `PUT .../transfer`.
A fourth administrator switch, "Allow deleting and transferring projects", and `--allow-destructive` on the launcher.

## Verified

| Suite | Before | After |
| --- | --- | --- |
| MCP (`tests/Graft.Mcp.Acceptance`, Debug server and launcher) | 106 | **137** |
| Git/API/browser (`tests/Graft.Git.Acceptance`, Debug server) | 1385 | **1395** |

The MCP assertions drive the forge's own endpoint with writes allowed:

- A banner set from base64 is reported with its served address, which a token reads back from the route; an SVG is
  refused naming the accepted formats; removal works, and a second removal says there is none.
- A tag is created on `main` with its commit reported, listed, refused when created again with the server's reason,
  and deleted.
- Review rules set to two approvals and code owners are read back as such; eleven is refused before it is sent; both
  are reset.
- The audit trail lists `set-banner` and `remove-banner`.
- With webhooks off on the server, listing and adding both name `--webhooks true`.
- With writes on and the new switch off, deletion is refused naming the switch, and the project is still there.
- With the switch on: `graft_whoami` reports it; a deletion confirmed with the slug rather than the full path is
  refused naming the path; confirmed with the path it goes, and the project reads as absent; a transfer to an account
  that does not exist is refused; a transfer to a real one reports `receiver/gift`.
- With the switch off again, the next transfer is refused without a restart. The switch changes are audited.
- Every listing of tools, over the forge, stdio and the launcher's HTTP, has 54. The stdio launcher with writes
  refuses deletion naming `--allow-destructive`; `--allow-destructive true` without writes exits 2 at start.

The Git suite adds the token routes over HTTP: anonymous `GET` of a public project's banner state; a `read_api` token
refused (403) removing a banner and transferring; an SVG body refused (400); removal 204 then 404; setting by numeric id
returns the address; `GET .../approvals` in GitLab's shape with the two fixed facts; a transfer to a non-user refused
naming the group case, and a numeric namespace refused.

The Git suite passed with `GRAFT_REMOTE_HELPER` pointed at the NativeAOT helper in `artifacts/helper/win-x64`, as the
2026-11-08 record describes.

## Not verified

- The administrator page's new checkbox was compiled, not driven in a browser.
- No NativeAOT publish of the forge with these changes; the tools add no reflection and the contracts are
  source-generated like the rest.
- A successful transfer and deletion over HTTP by the owner were exercised through the MCP tools, which call those
  routes, rather than separately in the Git suite.
