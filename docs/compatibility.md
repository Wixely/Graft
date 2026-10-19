# GitLab conformance plan

Recorded: 2026-09-18. Updated: 2026-09-19. Status: bounded REST and browser subsets implemented; GitLab 18.0.0 source fixture passes for project creation, while reference-instance and existing-client runs remain pending. Owner: Implementation agent. Review: 2026-09-25 and before each selected upstream API-version change.

Confirmed goal: GitLab APIs/URL conventions and GitLab-style apps, with most GitLab functionality. Exact GitLab versions/editions and clients remain TBD. Reference for research: [official REST documentation](https://docs.gitlab.com/api/rest/). Recheck official documentation before implementing a versioned contract.

| Surface | Evidence to record | Current status |
| --- | --- | --- |
| REST | Method/path, request/response/error shape, auth, scopes, pagination, encoding, status codes, redirects | Bounded read/write subset passes the C# harness; GL-004 has a version-matched source fixture, while live upstream comparison remains pending |
| GraphQL | Supported schema/queries/mutations, pagination and authorization | Not implemented |
| Browser/raw/archive URLs | Namespace nesting, refs, filenames, downloads, canonical redirects | Personal namespace project/deep-link route implemented; any branch can be browsed and chosen as the default; a public or unlisted project is browsable with no account; raw/archive and nesting pending |
| Git HTTPS/SSH and LFS | Clone/fetch/push, auth and ref permissions, large-object integrity | Bounded Git HTTPS subset passes, including anonymous clone and fetch of a public or unlisted repository; SSH and LFS pending |
| OAuth/tokens/webhooks | Token lifecycle/scopes, application flows, payloads, auth and delivery/retry behavior | Bounded personal access-token creation/list/revoke is implemented; OAuth and webhooks are not |
| CI configuration | Includes/templates, stages/jobs, expressions, variables, artifacts, caches, environments | Not implemented |
| Runner protocol | Enrollment/polling/jobs/logs/artifacts/cancellation separately from CupriNet transport | Not implemented |
| CLI/SDK/MCP clients | Host configurable, pinned client version, concrete end-to-end workflows | `glab` 1.58.0 API auth/user/list/create/read passes against Graft. Graft hosts its own MCP endpoint at `/mcp` behind `--mcp true`, with a stdio launcher `src/Graft.Mcp` for local clients, both over the same REST API; see [ADR 0009](decisions/0009-mcp-in-the-forge.md). Graft also addresses a project by an instance-wide slug at `/projects/<slug>`, which GitLab has no equivalent for and which is additive to the `owner/slug` and numeric-id forms; see [ADR 0010](decisions/0010-project-slugs.md). GitLab SDKs remain candidates |

Each future matrix row should record upstream version, requirement source, fixture, implementation status, last tested date, build identifier, expected result and known deviation. A path match without behavior/auth fidelity is not compatibility. Distinguish imported CI files from executable pipeline compatibility.

The current workflow covers authenticated user/project discovery, read-only repository REST calls and Git HTTPS clone/push. The acceptance client is a C# `HttpClient` harness plus Git 2.53.0.windows.2. Pin and run an existing GitLab-style client before claiming client compatibility.

## Proposed MVP fixture baseline

MVP-06 update: [repository UI and API evidence](validation/2026-09-19-mvp06-repository-api-ui.md) demonstrates product namespace routing, HTTPS/token authentication, durable storage and the selected REST reads. This is implementation evidence against rolling official documentation, not a version-matched GitLab compatibility result.

Provisional version candidate: **GitLab CE 18.0.0, REST v4**. This remains an agent-proposed baseline rather than a user-selected deployment version. The GL-004 fixture pins the official unified repository tag `v18.0.0-ee`, commit `504fd9e5236e13d674e051c6b8a1e9892b371c58`, and hashes the tagged documentation, endpoint source, request specs, authentication and scope documentation. See [source-fixture evidence](validation/2026-09-19-gitlab-18-source-fixture.md). All seven creation cases later passed a disposable [GitLab CE 18.0.0 reference](validation/2026-09-19-gitlab-18-live-reference.md), and pinned `glab` 1.58.0 workflows passed against that reference.

Client evidence: installed **Git 2.53.0.windows.2** provides actual transport interoperability. Pinned **glab 1.58.0** passes `auth login` plus `api user`, project list, project create and project read against both the GitLab reference and Graft; see [pinned-client evidence](validation/2026-09-19-glab-graft.md). A C# HttpClient harness verifies additional wire/error behavior independently. GitlabMCPSharp remains a later client candidate. No broad glab support is implied.

The read rows marked **implemented** were first tested by the 64-assertion MVP-06 harness. The GitLab 18.0 project-creation fixture raised that workflow to 87 assertions, user/membership raised it to 97, and personal access-token lifecycle raised it to 107. Account credentials raised it to 135, and server-authored commits raise the current Windows JIT workflow to 165; the NativeAOT comparison is pending a machine with the C++ build tools. GL-004 is driven by the pinned 18.0.0 source fixture; the other rows still reference rolling official documentation. Git transport uses protocol v0/SHA-1. The live GitLab CE 18.0.0 and pinned `glab` 1.58.0 reports establish the exact bounded reference/client result described above.

| Fixture ID | Surface / request | Required assertions | Official reference |
| --- | --- | --- | --- |
| GL-001 | `GET /api/v4/user` | **Implemented:** current identity, missing token and revoked token; `PRIVATE-TOKEN` and Bearer; API scope required | [Users](https://docs.gitlab.com/api/users/) |
| GL-002 | `GET /api/v4/projects` | **Implemented subset:** visible private projects, selected fields, offset pagination headers; unsupported query parameters rejected | [Projects](https://docs.gitlab.com/api/projects/) |
| GL-003 | `GET /api/v4/projects/:id` | **Implemented subset:** numeric and percent-encoded personal namespace/project lookup; inaccessible projects concealed; selected fields | [Projects](https://docs.gitlab.com/api/projects/) |
| GL-004 | `POST /api/v4/projects` | **Implemented, GitLab 18.0 source-fixture subset:** authenticated user's personal namespace; name-only, path-only or explicit name/path; generated path, description and a visibility of `private`, `unlisted` or `public`; `api` scope, `201` response, duplicate/path errors and explicit rejection of unsupported fields. `internal` is refused, because Graft has no instance-wide signed-in audience distinct from the public and accepting a fourth name while treating it as one of the three would be a false claim. `unlisted` is Graft's own and is not a GitLab value. A non-private visibility is refused outright unless the operator started the server with `--public-browsing true`. Form payloads, namespaces, initialization options and other attributes remain explicit deviations | [Tagged Projects source](https://gitlab.com/gitlab-org/gitlab/-/blob/v18.0.0-ee/doc/api/projects.md) |
| GL-005 | Token transport | **Implemented subset:** `PRIVATE-TOKEN` and Bearer; scopes intersect live membership; query-string token unsupported | [Authentication](https://docs.gitlab.com/api/rest/authentication/) |
| GL-006 | `GET /api/v4/projects/:id/repository/branches` | **Implemented subset:** branch list, default branch and commit details; protection/merge fields are fixed MVP values | [Branches](https://docs.gitlab.com/api/branches/) |
| GL-007 | `GET /api/v4/projects/:id/repository/commits` | **Implemented subset:** selected ref, newest-first traversal and offset pagination; filter options pending | [Commits](https://docs.gitlab.com/api/commits/) |
| GL-008 | `GET /api/v4/projects/:id/repository/tree` | **Implemented subset:** ref/path tree reads with traversal rejection and offset pagination; recursive mode pending | [Repositories](https://docs.gitlab.com/api/repositories/) |
| GL-009 | `GET /api/v4/projects/:id/repository/files/:file_path` | **Implemented subset:** Base64 content, hashes, mode and commit IDs for text/binary blobs; `last_commit_id` currently equals selected commit | [Repository files](https://docs.gitlab.com/api/repository_files/) |
| GIT-001 | `GET /:namespace/:project.git/info/refs?service=git-upload-pack` and `git-receive-pack` | **Implemented bounded subset:** auth before disclosure, correct service responses, refs and empty repository | [Git HTTP](https://git-scm.com/docs/http-protocol) |
| GIT-002 | `POST /:namespace/:project.git/git-upload-pack` | **Implemented bounded subset:** clone/fetch through Git 2.53.0.windows.2, exact content and inaccessible rejection | [Git HTTP](https://git-scm.com/docs/http-protocol) |
| GIT-003 | `POST /:namespace/:project.git/git-receive-pack` | **Implemented bounded subset:** initial/incremental push, branches/tags, token auth, stale ref and interrupted publication checks | [Git HTTP](https://git-scm.com/docs/http-protocol), [pack format](https://git-scm.com/docs/pack-format) |
| WEB-001 | `/:namespace/:project` | **Implemented subset:** authenticated project/user/token creation, account password change/reset/disable/re-enable, membership and token revocation, deep-link shell, tree/history/file API, CSRF login/logout; rendered Chromium workflow passes | Graft MVP UX acceptance; no complete GitLab browser URL parity claimed |
| GL-010 | `avatar_url` on user and project payloads | **Implemented subset:** the field is present and null when no avatar is set, otherwise an absolute URL to a Graft-served image. The URL shape is Graft's own, not GitLab's `/uploads/-/system/...` convention | [Projects](https://docs.gitlab.com/api/projects/), [Users](https://docs.gitlab.com/api/users/) |
| GL-011 | Avatar upload | **Not implemented.** GitLab accepts an avatar as multipart form data on `PUT /api/v4/user` and `POST /api/v4/projects/:id`; Graft has no multipart handling and uploads avatars only through its browser transport | [Projects](https://docs.gitlab.com/api/projects/) |
| GL-012 | `POST /api/v4/projects/:id/uploads` | **Not implemented.** Deliberately left unclaimed so the path stays available; Graft's browser uploads live under `/api/graft/uploads` instead | [Projects](https://docs.gitlab.com/api/projects/) |
| GL-014b | `POST`/`PUT /api/v4/projects/:id/repository/files/:file_path` | **Implemented:** writes one file and commits it, with the content in the request as `text` or `base64`. Requires the `api` or `write_repository` scope and Developer-equivalent access, the same rules a git push obeys. **Deviation:** GitLab distinguishes creating a file from updating one and refuses the wrong verb; Graft commits what it was given either way. Deleting a file is not implemented | [Repository files](https://docs.gitlab.com/api/repository_files/) |
| GL-015 | `PUT /api/v4/projects/:id` | **Implemented subset:** `visibility` (`private`, `unlisted`, `public`), `name`, `description` and `default_branch`; any combination may be sent and only what is sent is changed. Requires the `api` scope and Maintainer-equivalent access; every other attribute, `path` included, is refused by name rather than ignored, so this route never moves a project. GitLab accepts dozens of attributes here, all of which remain explicit deviations. The scope is checked before the project is resolved, so an insufficient scope answers `403` while an inaccessible project still answers `404` | [Projects](https://docs.gitlab.com/api/projects/) |
| GL-013 | Anonymous read of a public project | **Implemented subset:** with `--public-browsing true`, `GET /api/v4/projects/:id`, `/repository/tree`, `/repository/commits`, `/repository/branches` and `/repository/files/:path` answer a caller presenting no token when the project's visibility is `public` or `unlisted`. A token that does not resolve stays a `401` rather than being treated as anonymous. GitLab's instance-wide `internal` visibility has no equivalent | [Projects](https://docs.gitlab.com/api/projects/) |
| GL-014 | `GET /api/v4/projects` listing for an anonymous caller | **Not implemented.** The authenticated listing still returns only projects the caller owns or is a member of, and never the public set. Graft's own `GET /api/browse` serves discovery instead, under its own path and shape | [Projects](https://docs.gitlab.com/api/projects/) |

Git HTTPS token authentication needs its own GitLab-version fixture; API header acceptance does not prove Git credential compatibility. REST unknown routes must return an explicit API error, never the Blazor HTML fallback. Treat unsupported query fields deliberately: document version-matched ignore/reject behavior instead of silently claiming full endpoint support.

## Graft-specific routes

These are Graft's own surface, not GitLab compatibility. They live under `/api/graft/` so they cannot collide with a GitLab path, and they exist because the browser needs capabilities the REST surface does not yet offer. An API client should not depend on them.

| Route | Purpose |
| --- | --- |
| `/api/graft/uploads/**` | Browser upload transport; see [ADR 0005](decisions/0005-browser-upload-transport.md) |
| `POST /api/graft/admin/import/inspect` | Reference inventory and exclusion report for an uploaded Git bundle |
| `POST /api/graft/projects/:id/repository/files` | Commit uploaded files to a branch |
| `GET`, `HEAD`, `POST /api/graft/avatars/{user|project}/:id` | Serve and set avatars |
| `POST /api/projects/:id/default-branch` | Choose which branch a project opens at |
| `POST /api/projects/:id/owner` | Transfer project ownership |

GitLab owns `/uploads/...` and `/api/v4/projects/:id/uploads`. Neither is served by Graft's upload group, and both remain available for later compatibility work.

Deferred surfaces remain not implemented: GraphQL, most write-side project/repository REST APIs beyond creation and the bounded edit above, raw/archive routes, SSH/LFS, OAuth, CI/runner protocol, webhooks, nested groups and complete browser URL conventions. Repository-only import via an ordinary push preserves Git content; it is not a GitLab data migration.
