# MVP-05 durable authenticated Git integration evidence

Date: 2026-09-19. Owner: Implementation agent. Review when Git protocol, repository format, authorization, storage ordering or server TLS changes.

## Result

The first integrated MVP-05 slice passes on Windows under the .NET runtime and as a `win-x64` NativeAOT server. Git **2.53.0.windows.2** completed authenticated empty clone, push, populated clone and fetch over loopback HTTPS. Repository objects and refs survived a server-process restart and the clone passed `git fsck --full --strict`.

The acceptance executable completed 29 assertions in each server mode. The original managed-Git regression suite still passes 42 direct checks and its independent Git-client workflow after the protocol engine moved into `src/Graft.Git`. The full solution builds with zero warnings and errors. Dapper.AOT generated interception at all 28 current application data-access call locations.

## Implemented behavior

- `src/Graft.Git` owns the C# pack, graph, ref and protocol engine plus durable repository storage.
- Objects are verified by Git object ID, written to staging, flushed and moved into opaque object paths before refs can change.
- All refs are published as one sorted snapshot using same-volume atomic replacement after the snapshot is flushed.
- Restart loading verifies object path, type and hash, validates refs and walks every reachable graph before serving the repository.
- A validated receive calls durable commit before replacing its visible in-memory dictionaries. Persistence failure leaves the serving state unchanged.
- Project creation supports `pending` metadata, repository initialization and a final `ready` transition. An initializer failure leaves the project unavailable.
- `Graft.Server` resolves only ready projects, authenticates persisted personal tokens with HTTP Basic auth, rechecks the current user/token/membership state and calls the shared MVP-04 fetch/push decision before opening an opaque storage directory.
- Each repository has a single-operation gate. Competing requests receive HTTP 429 and stale old-ref values are rejected by the protocol engine.

The acceptance workflow exercises Developer push, owner clone/fetch, server restart, a competing stale update, membership revocation blocking push, token revocation blocking fetch, an existing cross-project path denial, persisted ref/object verification, and interruption at both object-flushed and ref-snapshot-flushed phases. Each injected interruption leaves the prior ref visible in memory and after reopening from disk.

## Commands

```powershell
dotnet build Graft.slnx -c Debug
dotnet tests/Graft.Git.Acceptance/bin/Debug/net10.0/Graft.Git.Acceptance.dll src/Graft.Server/bin/Debug/net10.0/Graft.Server.dll
dotnet experiments/managed-git-client/bin/Debug/net10.0/Graft.ManagedGit.Client.dll experiments/managed-git/bin/Debug/net10.0/Graft.ManagedGit.dll
dotnet publish src/Graft.Server/Graft.Server.csproj -c Release -r win-x64 --self-contained true -p:GraftServerAot=true -o artifacts/mvp05/win-x64
dotnet tests/Graft.Git.Acceptance/bin/Debug/net10.0/Graft.Git.Acceptance.dll artifacts/mvp05/win-x64/Graft.Server.exe
```

No AOT or trimming warning was suppressed.

## Evidence boundary

This is bounded local integration evidence. The server binds loopback only and creates a short-lived self-signed certificate; the acceptance client disables certificate verification only for that disposable endpoint. Release certificate configuration and trust are not implemented.

The repository format is project-owned and currently supports one writer process, fixed feasibility limits, SHA-1, smart HTTP v0 and the protocol subset recorded in the MVP-01 evidence. Objects are durable, but unused objects left by an interrupted receive are not yet garbage-collected. The tests inject I/O failures at both pre-publication durability phases; they do not exhaust a real filesystem or prove hardware power-loss semantics. Multi-process locking, graceful service shutdown, configurable quotas, backup/restore, resource measurements, static-musl publishing and required Linux 3.10.108 execution remain open.

MVP-05 is therefore an integration **PASS within this bounded Windows scope**, with the operational and required-host gates still open. Recommended next action: Owner: Implementation agent — add repository browsing and the selected GitLab API subset in MVP-06 while retaining these authorization and durability checks. Owner: Implementation agent — close the remaining MVP-05 operational gates before release packaging.
