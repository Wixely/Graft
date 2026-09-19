# User and project-membership workflow evidence

Date: 2026-09-19. Owner: Implementation agent. Review when user administration, project roles, browser sessions, CSRF handling or membership authorization changes.

## Result

Administrators can create ordinary users through the authenticated Blazor workflow. Project owners, administrators and direct Maintainers can list direct members, grant Reporter/Developer/Maintainer, change a direct role and revoke membership. All mutations require the secure browser session, exact origin and session-derived CSRF value. The server continues to enforce the shared `ManageProject` decision; the UI's visibility is not the authorization boundary.

The integrated Git/API/browser harness passes **97 assertions** against both the managed server and the self-contained `win-x64` NativeAOT server. Dapper.AOT generated interceptions for all **33** current storage query/command locations. No build, trimming or AOT warnings were emitted.

MCPHub Playwright Chromium 1217 also completed the rendered workflow: the fixture administrator created `member`, opened a seeded project, granted Developer, changed the role to Maintainer and revoked it. Each operation produced a visible status message, the membership list updated, the temporary password cleared after creation, and the complete run had zero console messages.

## Covered boundaries

- Anonymous and non-administrator sessions cannot create users.
- Duplicate usernames become bounded client errors rather than storage exceptions.
- Membership targets must be active existing users; owners cannot receive a redundant direct role.
- Membership listing and mutation require effective Maintainer access.
- Grant, role replacement and revoke use the existing audited storage operations, so REST, browser, Git and core authorization observe changes immediately.

The workflow does not yet support password reset, user re-enable, ownership transfer, groups, inherited roles or invitations. Personal access tokens remain fixture-created; this prevents a newly created real user from obtaining Git HTTPS credentials without operator/test code.

Recommended next action: Owner: Implementation agent — add authenticated personal access-token creation/list/revoke with explicit scopes, bounded expiry and one-time secret display.
