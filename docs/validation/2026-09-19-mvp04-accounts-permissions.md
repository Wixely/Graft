# MVP-04 accounts, projects and permissions evidence

Date: 2026-09-19. Owner: Implementation agent. Review when authentication, authorization, schema or Git transport changes.

## Result

The integrated core for MVP-04 passes on Windows under the .NET runtime and as a `win-x64` NativeAOT executable. The acceptance executable completed 66 assertions in each mode. The Release build generated Dapper.AOT interception for all 26 application query/command call locations and the NativeAOT publish completed without trim or AOT warnings.

The implemented slice provides:

- a one-use first-administrator bootstrap and administrator-created users;
- PBKDF2-SHA256 password hashes with a versioned format;
- persisted, expiring browser sessions and personal access tokens stored only as SHA-256 digests;
- private projects with opaque storage identifiers;
- Reporter, Developer and Maintainer direct roles with an explanation of the effective permission source;
- token scopes that restrict, but cannot expand, project membership;
- immediate membership, token, session and disabled-user revocation checks;
- persisted redacted audit events for sensitive changes; and
- one authorization decision used for REST-read, repository browsing, fetch, push and project-management actions.

The DnaX-owned SQLite manifest creates users, projects, memberships, sessions, tokens and audit events. Restart checks prove that users, sessions, tokens, memberships and authorization state survive closing and reopening the store.

## Commands and observations

```powershell
dotnet build Graft.slnx -c Debug
dotnet tests/Graft.Acceptance/bin/Debug/net10.0/Graft.Acceptance.dll
dotnet publish tests/Graft.Acceptance/Graft.Acceptance.csproj -c Release -r win-x64 --self-contained true -p:AcceptanceAot=true -o artifacts/mvp04/win-x64
& .\artifacts\mvp04\win-x64\Graft.Acceptance.exe
```

Both executions reported `RESULT: PASS; assertions: 66`. The complete solution build reported zero warnings and zero errors.

The first NativeAOT attempt correctly failed because private nested Dapper row/parameter types prevented generation for most calls. Those implementation types were made assembly-visible, raising generated interception from 4 to 26 call locations. The repeated NativeAOT publish then completed cleanly and the published executable passed the same acceptance flow. No linker warnings were suppressed.

## Acceptance coverage

The disposable test database covers failed and successful login, logout, bootstrap reuse refusal, private-project isolation, cross-project denial, role grant/change/revoke, token scope intersection, token revocation, user disablement, persistence after restart, stale-identity revalidation, secret-at-rest checks and audit creation. It checks all modeled REST, browse, fetch and push actions after membership and account revocation.

## Evidence boundary

This is the shared domain/storage authorization core. It does not yet expose production HTTP routes, secure browser cookies, CSRF handling, a file browser or Git smart HTTP. The fetch and push assertions exercise the decision that those adapters must call; MVP-05 must prove that real transports cannot bypass it. Project rows are currently created directly in the `ready` state because recoverable filesystem repository creation belongs to MVP-05.

The build currently consumes locally packed DnaX packages from `artifacts/storage/dnax-feed` through the repository `NuGet.config`. That ignored feed is reproducible from the exact public tag using `experiments/storage/prepare-dnax-packages.ps1`, but it is not a durable distribution solution. Required Linux 3.10.108 execution is also outstanding. This evidence does not close either MVP-03 gate.

Recommended next action: Owner: Implementation agent — begin MVP-05 by connecting the managed Git protocol work to durable repository storage and this authorization service, including pending/ready project recovery. Owner: User/DnaX maintainer — provide an approved durable feed before a clean external checkout or release build is accepted.
