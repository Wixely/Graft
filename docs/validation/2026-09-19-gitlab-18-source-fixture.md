# GitLab 18.0 source-backed REST fixture

Date: 2026-09-19. Owner: Implementation agent. Review when the selected GitLab baseline, fixture sources, project-creation contract, or token scopes change.

## Result

Graft now has a version-pinned source fixture for the GitLab 18.0.0 REST v4 project-creation contract. The fixture is embedded in the C# acceptance executable and drives live HTTPS requests against Graft. The complete Git/API workflow passes **87 assertions** under the .NET runtime and Windows `win-x64` NativeAOT.

The fixture identified and closed one compatibility gap: GitLab accepts `path` without `name` and assigns the path as the project name. Graft now does the same. Name-only creation continues to generate a lowercase dashed path, and explicit name/path creation remains supported.

## Provenance

The source baseline is the official GitLab unified repository tag [`v18.0.0-ee`](https://gitlab.com/gitlab-org/gitlab/-/tree/v18.0.0-ee) at commit [`504fd9e5236e13d674e051c6b8a1e9892b371c58`](https://gitlab.com/gitlab-org/gitlab/-/commit/504fd9e5236e13d674e051c6b8a1e9892b371c58). The endpoint and request specs used here are in the core project and apply to the CE surface. Files were downloaded through GitLab's repository API and SHA-256 hashed; the large upstream sources are not vendored.

| Exact tagged source | SHA-256 |
| --- | --- |
| `doc/api/projects.md` | `9082a894d23db8a5ca278fb3f566dbb393700b81c2c16623a45c387624deac56` |
| `lib/api/projects.rb` | `7ccd191eb31fcebf42170685e6276ec02b8e3bf369bc850ed64db0fbfd7c3ef8` |
| `spec/requests/api/projects_spec.rb` | `2d2b8f31db6204b68c0b1cb6b57711a94978bc245cd18e62242fb3202d984a09` |
| `doc/api/rest/authentication.md` | `2d5bf76b251662200cb92353ea312fe16698ba0e21cdc61c234fc756e3516bcd` |
| `doc/user/profile/personal_access_tokens.md` | `51748982d7770865b793659cd123ba45760a7005132418d4f8b68c42d6c37fe0` |

The compact executable fixture is [gitlab-ce-18.0.0-rest-v4.json](../../tests/Graft.Git.Acceptance/Fixtures/gitlab-ce-18.0.0-rest-v4.json). It records the upstream and Graft status separately for every case so intentional deviations cannot appear as conformance.

## Covered contract

- `POST /projects` returns `201` for name-only, path-only, and explicit name/path requests.
- A name-only request generates the expected lowercase dashed path; a path-only request uses the path as the name.
- Missing both name and path returns `400`; a missing token returns `401`.
- `api` grants write access and `read_api` does not. The fixture's `403` expectation for the latter is an inference from the exact tagged scope definition and write operation rather than a dedicated project request spec.
- Private visibility returns `201`. GitLab's tagged spec also accepts public/internal visibility, while Graft deliberately returns `400` because this MVP is private-only.

Graft still exposes a selected response field subset, accepts JSON for this fixture, limits personal project paths to its smaller MVP rules, and rejects unsupported creation attributes. Those are recorded product boundaries.

## Verification

```powershell
dotnet build Graft.slnx -c Release
dotnet tests/Graft.Git.Acceptance/bin/Release/net10.0/Graft.Git.Acceptance.dll src/Graft.Server/bin/Release/net10.0/Graft.Server.dll
dotnet publish src/Graft.Server/Graft.Server.csproj -c Release -r win-x64 --self-contained true -p:GraftServerAot=true -o artifacts/gitlab-18-fixture/win-x64
dotnet tests/Graft.Git.Acceptance/bin/Release/net10.0/Graft.Git.Acceptance.dll artifacts/gitlab-18-fixture/win-x64/Graft.Server.exe
```

## Boundary

This is version-matched source and request-spec evidence. No GitLab 18.0 reference instance or pinned `glab` executable was run, so full wire-response equivalence and existing-client compatibility remain open. The user deferred the Linux 3.10.108 run for now.
