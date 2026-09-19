# Personal access-token workflow evidence

Date: 2026-09-19. Owner: Implementation agent. Review when browser authentication, CSRF, token scopes, expiry, digest storage or Git credential handling changes.

## Result

Signed-in users can create named personal access tokens with any non-empty combination of `read_api`, `api`, `read_repository` and `write_repository`, choose a lifetime from 1 to 365 days through the API, list their active token metadata and revoke only their own tokens. Creation returns the random `graft_pat_` secret exactly once. Later list responses contain the name, scopes and timestamps without the secret; storage continues to persist only its SHA-256 digest.

The integrated Git/API/browser harness passes **107 assertions** against both the managed server and the self-contained `win-x64` NativeAOT server. It creates a `read_repository` token through the browser-session API, confirms the scope and bounded expiry, confirms the listing omits the secret, authenticates Git 2.53.0.windows.2 over HTTPS, rejects a wrong-CSRF revocation, revokes the token and proves the same Git credential immediately fails. The 41-assertion operations workflow also remains green. Release builds and NativeAOT publication emit no warnings, and Dapper.AOT generates interceptions for all **33** current storage call locations.

MCPHub Playwright Chromium also completed the rendered workflow against the disposable production fixture. It selected a 90-day lifetime plus read/write repository scopes, created `rendered-pat`, observed the one-time secret notice and active metadata, revoked that token, confirmed the secret left the page and retained the unrelated fixture token. The page snapshots reported zero console entries. The temporary loopback certificate trust and fixture data were removed after the run.

## Boundaries

- All token routes require a live secure browser session; creation and revocation additionally require exact origin and the session-derived CSRF value.
- The active list is restricted to the current user and excludes expired or revoked tokens.
- The browser revocation route cannot use administrator privilege to revoke another user's token by guessed identifier.
- Scope authorization still intersects the user's current project role; a token cannot broaden project access.
- Token names are 1–100 characters and token lifetime is bounded to 365 days.

Rotation, administrator token inventory, scheduled future activation and a GitLab-version-matched token-management API are outside this slice. OAuth remains unimplemented.

Recommended next action: Owner: Implementation agent — add authenticated password change plus administrator password reset and user disable workflows, with explicit session/token revocation behavior.
