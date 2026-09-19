# GitLab 18.0 live reference and glab acceptance

Date: 2026-09-19. Owner: Implementation agent. Review when the selected GitLab baseline, reference fixture, container image, or supported `glab` version changes.

## Result

The seven project-creation cases in `gitlab-ce-18.0.0-rest-v4.json` pass against a disposable GitLab CE 18.0.0 instance. The C# reference harness made live REST requests and passed **17 assertions** covering the exact server version, response statuses, and successful response names and paths.

This run confirms two expectations that were previously based only on tagged source:

- a `read_api` personal access token receives `403` when it attempts to create a project;
- public project creation returns `201` with the requested name and generated path.

The same fixture still passes Graft's **87-assertion** Git/API/browser acceptance executable. Its public-visibility case remains an explicit MVP deviation: GitLab returns `201`, while Graft deliberately returns `400` because the MVP is private-only.

The official `glab` 1.58.0 container also completed authenticated user, project-list, project-read, project-create, and read-after-create API workflows against the reference instance.

## Pinned artifacts

| Artifact | Release identity | Pulled image digest |
| --- | --- | --- |
| GitLab CE | `docker.io/gitlab/gitlab-ce:18.0.0-ce.0` | `sha256:bcc6a088b6fbc8a092fe5ba9c0290c3a0c055bb5b7362afc864821e0e9ec179f` |
| GitLab CLI | `registry.gitlab.com/gitlab-org/cli:v1.58.0`, commit `676a7ced` | index `sha256:a1bc1b35decb0ceededbb22bb9a8c07eddc1279ab6e9342b32bc42e20333aa7a`; amd64 `sha256:c08bee9b7e06886a2a5b8def53332c6e75cd46012d1e36fb5878b3fb00d3f4d0` |

The GitLab container reported version `18.0.0` through its authenticated `/api/v4/version` endpoint. The CLI reported `glab 1.58.0 (676a7ced)`.

## Environment and isolation

The reference ran under rootless Podman 4.9.3 in the existing `slopworks` WSL2 distribution. Its HTTP endpoint was published only on `127.0.0.1:18080`. Container storage, the Podman network, generated projects, administrator tokens, and CLI configuration were disposable. The CLI read its synthetic token from a read-only bind mount and stored authentication configuration only inside its short-lived container.

The first configuration attempted to place NGINX on internal port 8080. That collided with GitLab's Puma backend and was corrected before testing by retaining the standard internal NGINX port 80 and mapping only the host port to 18080.

## Verification

The reference harness accepts token file paths so token values do not appear in command output:

```powershell
dotnet build tests\Graft.GitLab.Reference\Graft.GitLab.Reference.csproj -c Release
dotnet tests\Graft.GitLab.Reference\bin\Release\net10.0\Graft.GitLab.Reference.dll `
  http://localhost:18080 `
  <api-token-file> `
  <read-api-token-file> `
  tests\Graft.Git.Acceptance\Fixtures\gitlab-ce-18.0.0-rest-v4.json
```

The full solution passed with zero warnings and errors using `dotnet build Graft.slnx -c Release -m:1`. A parallel solution build can race because the server build and the solution both build the Blazor client into the same intermediate output; the serialized verification avoids that pre-existing output lock.

## Boundary and next action

This proves the selected creation fixture against GitLab CE 18.0.0 and proves the pinned CLI against that reference server. It does not yet run `glab` against Graft or extend compatibility beyond the recorded endpoints and workflows. The Linux 3.10.108 product-artifact run remains deferred at the user's request.

Recommended next action: run the same pinned `glab` workflows against a disposable Graft instance and turn any client-visible mismatch into a versioned acceptance case. Owner: next implementation agent.
