# Pinned glab acceptance against Graft

Date: 2026-09-19. Owner: Implementation agent. Review when the supported `glab` version, REST routes, TLS handling, or project-creation contract changes.

## Result

Official `glab` **1.58.0** at commit `676a7ced` passes **10 assertions** against a disposable current Graft server:

- the Graft HTTPS endpoint starts with a generated CA-signed localhost certificate;
- the pinned CLI container starts and reports the exact expected version and commit;
- `glab auth login` accepts a Graft personal access token;
- `glab api user` returns the authenticated Graft user;
- `glab api projects` lists accessible projects;
- `glab api projects -X POST` creates a private project;
- `glab api projects/<namespace%2Fpath>` reads the created project;
- the project remains ready in Graft's SQLite store after server shutdown.

No Graft product change was required. For a nonstandard HTTPS port, `glab` requires a logical `--hostname` without a port and a separate `--api-host` with the port during login. Subsequent `api --hostname` commands select that stored host entry.

## Pinned client and isolation

The run used `registry.gitlab.com/gitlab-org/cli:v1.58.0`, index digest `sha256:a1bc1b35decb0ceededbb22bb9a8c07eddc1279ab6e9342b32bc42e20333aa7a`, amd64 digest `sha256:c08bee9b7e06886a2a5b8def53332c6e75cd46012d1e36fb5878b3fb00d3f4d0`, and reported `glab 1.58.0 (676a7ced)`.

The C# acceptance executable creates a random temporary database, repository root, administrator, API token, certificate authority, server certificate, password file and port. The CLI container receives the token and CA certificate through read-only mounts, uses host networking only to reach Graft's loopback listener, and is removed after the workflow. The harness deletes all temporary server state and credentials in `finally`.

TLS verification remains enabled. `SSL_CERT_FILE` points `glab` at the generated CA, so this test does not use an insecure certificate bypass.

## Verification

The checked-in PowerShell 5.1 runner publishes the current server and harness for Linux x64, then runs them in an existing WSL distribution with rootless Podman:

```powershell
.\tests\Graft.Glab.Acceptance\run.ps1 -Distribution <existing-wsl-distribution>
```

The official client image must already be present. An approved first run may add `-PullImage`; the harness itself uses `podman run --pull=never`, preventing an implicit version substitution.

The direct commands used for the recorded run were:

```powershell
dotnet publish src\Graft.Server\Graft.Server.csproj -c Release -r linux-x64 --self-contained true -o artifacts\glab-acceptance\server
dotnet publish tests\Graft.Glab.Acceptance\Graft.Glab.Acceptance.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:GraftSkipClientPublish=true -o artifacts\glab-acceptance\harness
```

## Boundary and next action

This establishes existing-client compatibility only for `glab api` authentication and the user/list/create/read REST workflow. It does not claim general `glab` command compatibility, GraphQL, issues, merge requests, pipelines, releases, repository management commands, or Git transport through `glab`. Git transport has separate real-Git acceptance evidence.

The subsequent [rendered-browser run](2026-09-19-browser-ui.md) closes the interactive Blazor evidence item. Recommended next action: Owner: Operator + implementation agent — validate the intended CA-issued certificate, DNS name, client trust and renewal path. The Linux 3.10.108 product-artifact run remains deferred at the user's request.
