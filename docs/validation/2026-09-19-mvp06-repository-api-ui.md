# MVP-06 repository UI and GitLab API evidence

Date: 2026-09-19. Owner: Implementation agent. Review when repository parsing, API contracts, browser authentication, embedded-client publication or the selected GitLab baseline changes.

## Result

The bounded MVP-06 slice passes on Windows under the .NET runtime and as a self-contained `win-x64` NativeAOT server. The integrated acceptance executable completed **64 assertions** in each mode while retaining the prior real Git push/clone/fetch, restart, revocation, cross-project denial and interrupted-write checks.

The slice adds authenticated private-project discovery, branch and commit reads, repository trees, safe text/binary file reads, browser login/logout with persisted revocable sessions and CSRF, and an embedded Blazor WebAssembly client. The full release solution builds with zero warnings and errors. Dapper.AOT generated interceptors for all **29** current application data-access locations.

This result establishes the implementation boundary recorded below. It does not establish GitLab parity.

## Implemented behavior

- `Graft.Git.RepositoryView` resolves branches, tags and commits from the stored Git graph, parses commit identities and timestamps, walks history, lists trees and returns blob bytes without checking files out to disk.
- Repository paths reject absolute paths, empty segments, `.` and `..`. Binary bytes stay Base64 encoded across the REST and browser contracts. Razor rendering encodes text content rather than injecting it as markup.
- `Graft.Contracts` contains source-generated JSON contracts shared by server and browser client.
- The server exposes `GET /api/v4/user`, visible-project list and numeric/percent-encoded project lookup, then bounded branches, commits, tree and repository-file endpoints.
- API authentication accepts `PRIVATE-TOKEN` and Bearer transport. Token scopes intersect the current user, token and project membership state; revocation takes effect immediately.
- The browser API uses a persisted HttpOnly, Secure, SameSite=Strict session cookie. Login is origin checked and throttled; logout requires a session-derived CSRF value and revokes the session.
- The Blazor WebAssembly client provides login/logout, project discovery, branch selection, tree navigation, commit history and text/binary file views. Published client assets are embedded in the server and deep links receive the client shell.
- Unknown REST v4 routes return a JSON 404 rather than the client shell. Security headers include a restrictive content-security policy, `nosniff` and a no-referrer policy.

## Compatibility evidence

The selected response shapes and authentication behavior were derived from the current official GitLab Users, Projects, Branches, Commits, Repositories and Repository Files documentation reviewed on 2026-09-19. The C# harness covers GL-001, GL-002, GL-003 and GL-005 and the repository read rows GL-006 through GL-009 in [the conformance plan](../compatibility.md).

Covered checks include missing/revoked token failures, private-project filtering, pagination headers, numeric and percent-encoded project IDs, branch and commit identity, exact pushed HEAD, hostile HTML preserved as data, binary round trip, traversal rejection and explicit unknown-API errors. No external GitLab REST client was exercised in this original run; pinned `glab` 1.58.0 was [validated later](2026-09-19-glab-graft.md).

The provisional GitLab CE 18.0 baseline still lacks version-matched fixtures. Current rolling documentation can differ from that release. Known MVP deviations include fixed branch protection/merge fields, limited commit/tree query options, personal namespaces only, and `last_commit_id` equal to the selected commit rather than a computed path-specific last-change commit.

## Browser and publication evidence

The harness verifies the embedded index and security headers, project deep-link shell, anonymous and authenticated sessions, private project listing, combined repository snapshot, encoded hostile file content, rejected CSRF and session revocation. These are HTTPS-level checks against the published client assets and browser-facing API.

The WebAssembly client publishes as trimmed IL. `RunAOTCompilation` and `WasmBuildNative` are disabled, so the optional `wasm-tools` workload is not required. Interactive WebAssembly execution in a rendered browser remains unverified for this production slice.

## Commands

```powershell
dotnet build Graft.slnx -c Release
dotnet tests/Graft.Git.Acceptance/bin/Release/net10.0/Graft.Git.Acceptance.dll src/Graft.Server/bin/Release/net10.0/Graft.Server.dll
dotnet publish src/Graft.Server/Graft.Server.csproj -c Release -r win-x64 --self-contained true -p:GraftServerAot=true -o artifacts/mvp06/win-x64
dotnet tests/Graft.Git.Acceptance/bin/Release/net10.0/Graft.Git.Acceptance.dll artifacts/mvp06/win-x64/Graft.Server.exe
```

No AOT or trimming warning was suppressed. Client publication reports the SDK recommendation for the optional `wasm-tools` workload, but completes successfully without it.

## Evidence boundary

The server remains loopback-only with a short-lived self-signed certificate in this harness. Production TLS, service lifecycle, backup/restore, configurable resource budgets, multi-process access, static-musl publication of the integrated product and execution on Linux 3.10.108 remain open.

Repository reads support the existing bounded SHA-1 object model. Malformed object and path rejection is covered at the implemented boundaries, but this result does not prove arbitrary Git history compatibility or GitLab browser URL parity. The durable DnaX package distribution gate also remains open.

MVP-06 is a **PASS within this bounded Windows scope**. Recommended next action: Owner: Implementation agent — begin MVP-07 with a synthetic repository import inventory/loss report and a quiesced backup/restore rehearsal while retaining all 64 assertions. Owner: User/DnaX maintainer — select an approved durable source for the tested packages.
