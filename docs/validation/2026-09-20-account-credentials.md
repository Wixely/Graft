# Account credential lifecycle evidence

Date: 2026-09-20. Owner: Implementation agent. Review when password hashing, browser authentication, administrator authorization, credential revocation, or account-state rules change.

## Result

Signed-in users can change their own password by supplying the current password. Administrators can list accounts, reset another active user's password, and disable another account. An administrator cannot use the reset or disable routes on their current account. Successful password changes, administrator resets, and disables revoke every active browser session and personal access token belonging to the affected user. Disabled accounts remain visible to administrators and cannot authenticate or receive a password reset.

The integrated Git/API/browser harness passes **127 assertions** against both the managed server and the self-contained `win-x64` NativeAOT server. It proves current-password verification, old-password rejection, fresh-password authentication, administrator boundaries, active/disabled account listing, and immediate session/token revocation after each credential transition. Release build and NativeAOT publication complete with no warnings. Dapper.AOT generates interceptions for all **39** current storage call locations.

MCPHub Playwright Chromium also completed the rendered workflow against the disposable production fixture. It created and listed an ordinary user, reset that user's password, required a second click before disabling the account, displayed the disabled state, and changed the administrator's own password. The successful self-service change returned the browser to sign-in with the expected status. The rendered page reported zero console entries. The browser instance, disposable fixture, and temporary loopback certificate trust were removed after the run.

## Boundaries

- All routes require a live secure browser session. Mutations additionally require exact origin and the session-derived CSRF value.
- Passwords remain bounded to 16–256 characters and are stored using the existing salted PBKDF2-SHA256 format.
- A rejected current password leaves the existing session and tokens active. Revocation occurs only in the same transaction as a successful change, reset, or disable.
- Password changes and resets revoke all personal access tokens rather than attempting to preserve selected automation credentials.
- Account disable is one-way in the current product UI and API. Re-enabling an account is not implemented.

Recommended next action: Owner: Implementation agent — add an administrator re-enable workflow that requires setting a fresh password, retains the audit trail, and leaves all older sessions and tokens revoked.
