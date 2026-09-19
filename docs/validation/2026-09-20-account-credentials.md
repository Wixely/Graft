# Account credential lifecycle evidence

Date: 2026-09-20. Owner: Implementation agent. Review when password hashing, browser authentication, administrator authorization, credential revocation, or account-state rules change.

## Result

Signed-in users can change their own password by supplying the current password. Administrators can list accounts, reset another active user's password, disable another account, and re-enable a disabled account only while setting a fresh password. An administrator cannot use the reset or disable routes on their current account. Successful password changes, administrator resets, and disables revoke every active browser session and personal access token belonging to the affected user. Re-enabling does not restore any older credential.

The integrated Git/API/browser harness passes **135 assertions** against both the managed server and the self-contained `win-x64` NativeAOT server. It proves current-password verification, old-password rejection, fresh-password authentication, administrator and CSRF boundaries, active/disabled/re-enabled account listing, immediate session/token revocation, and non-restoration of older credentials. Release build and NativeAOT publication complete with no warnings. Dapper.AOT generates interceptions for all **40** current storage call locations.

MCPHub Playwright Chromium also completed the rendered workflow against disposable production fixtures. It created and listed an ordinary user, reset that user's password, required a second click before disabling the account, displayed the disabled state, required and accepted a fresh password to re-enable the account, and changed the administrator's own password. The rendered pages reported zero console entries. The browser instances, disposable fixtures, and temporary loopback certificate trust were removed after each run.

## Boundaries

- All routes require a live secure browser session. Mutations additionally require exact origin and the session-derived CSRF value.
- Passwords remain bounded to 16–256 characters and are stored using the existing salted PBKDF2-SHA256 format.
- A rejected current password leaves the existing session and tokens active. Revocation occurs only in the same transaction as a successful change, reset, or disable.
- Password changes and resets revoke all personal access tokens rather than attempting to preserve selected automation credentials.
- Re-enable applies only to a disabled account, writes a distinct audit event, and atomically replaces its password while retaining the revoked state of every older session and token.

Recommended next action: Owner: Implementation agent — add project ownership transfer and block disabling a sole active project owner so account administration cannot orphan project management.
