# Private project creation evidence

Date: 2026-09-19. Owner: Implementation agent. Review when project path rules, token scopes, repository initialization, browser CSRF, or the selected GitLab baseline changes.

## Result

Authenticated users can create private projects through the Blazor workflow and the bounded GitLab-style `POST /api/v4/projects` endpoint. The integrated Git, REST, and browser-session harness passes **76 assertions** under the .NET runtime and the self-contained `win-x64` NativeAOT server. The release solution builds with no warnings or errors, and Dapper.AOT generates interceptors for all **31** application data-access locations.

The server creates metadata in the existing pending state, initializes an empty durable repository under its opaque storage identifier, then publishes the project as ready. Initialization failures remain concealed. A namespace/path uniqueness violation becomes a bounded client error rather than an internal server error.

## Supported contract

- JSON fields are limited to required `name`, optional `path`, optional `description`, and optional `visibility`.
- A missing path is generated from the name as lowercase text with whitespace replaced by dashes.
- Paths are 2-39 ASCII letters, numbers, dots, underscores, or hyphens; they start and end with a letter or number and do not contain consecutive punctuation.
- Visibility is private. Public/internal projects, alternate namespaces, import URLs, templates, README initialization, and all other GitLab project attributes are rejected rather than ignored.
- The REST route requires a live token with `api`; `read_api`, repository-only, missing, and revoked credentials cannot create a project.
- Browser creation requires the live secure session, exact origin, and session-derived CSRF value.

This began as a deliberately narrow JSON subset of the current official GitLab Projects API and token-scope documentation reviewed on 2026-09-19. A later [GitLab 18.0 source-backed fixture](2026-09-19-gitlab-18-source-fixture.md) pinned the upstream sources, added path-only creation and raised the current workflow to 87 assertions. GitLab still accepts broader fields and request forms that Graft does not support.

## Verification

```powershell
dotnet build Graft.slnx -c Release
dotnet tests/Graft.Git.Acceptance/bin/Release/net10.0/Graft.Git.Acceptance.dll src/Graft.Server/bin/Release/net10.0/Graft.Server.dll
dotnet publish src/Graft.Server/Graft.Server.csproj -c Release -r win-x64 --self-contained true -p:GraftServerAot=true -o artifacts/project-create-20260919-verified/win-x64
dotnet tests/Graft.Git.Acceptance/bin/Release/net10.0/Graft.Git.Acceptance.dll artifacts/project-create-20260919-verified/win-x64/Graft.Server.exe
```

The checks cover authentication and write scope, the `201` project shape, an empty initialized repository, path derivation, duplicate and invalid paths, unsupported visibility and fields, browser CSRF rejection, browser creation, and all earlier Git push/clone/restart/revocation/recovery behavior.

## Remaining boundary

Interactive execution of the Blazor form in a rendered browser remains unverified because the installed browser-control skill's required in-app runtime was unavailable in this session. HTTPS-level browser API and embedded asset checks still pass. Exact GitLab version fixtures, a pinned `glab` run, deployment CA/DNS trust, the durable DnaX feed, and integrated execution on Linux 3.10.108 remain open.
