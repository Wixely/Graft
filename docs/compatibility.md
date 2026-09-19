# GitLab conformance plan

Recorded: 2026-09-18. Updated: 2026-09-19. Status: bounded REST and browser subsets implemented; GitLab 18.0.0 source fixture passes for project creation, while reference-instance and existing-client runs remain pending. Owner: Implementation agent. Review: 2026-09-25 and before each selected upstream API-version change.

Confirmed goal: GitLab APIs/URL conventions and GitLab-style apps, with most GitLab functionality. Exact GitLab versions/editions and clients remain TBD. Reference for research: [official REST documentation](https://docs.gitlab.com/api/rest/). Recheck official documentation before implementing a versioned contract.

| Surface | Evidence to record | Current status |
| --- | --- | --- |
| REST | Method/path, request/response/error shape, auth, scopes, pagination, encoding, status codes, redirects | Bounded read/write subset passes the C# harness; GL-004 has a version-matched source fixture, while live upstream comparison remains pending |
| GraphQL | Supported schema/queries/mutations, pagination and authorization | Not implemented |
| Browser/raw/archive URLs | Namespace nesting, refs, filenames, downloads, canonical redirects | Personal namespace project/deep-link route implemented; raw/archive and nesting pending |
| Git HTTPS/SSH and LFS | Clone/fetch/push, auth and ref permissions, large-object integrity | Bounded Git HTTPS subset passes; SSH and LFS pending |
| OAuth/tokens/webhooks | Token lifecycle/scopes, application flows, payloads, auth and delivery/retry behavior | Bounded personal access-token creation/list/revoke is implemented; OAuth and webhooks are not |
| CI configuration | Includes/templates, stages/jobs, expressions, variables, artifacts, caches, environments | Not implemented |
| Runner protocol | Enrollment/polling/jobs/logs/artifacts/cancellation separately from CupriNet transport | Not implemented |
| CLI/SDK/MCP clients | Host configurable, pinned client version, concrete end-to-end workflows | `glab` 1.58.0 API auth/user/list/create/read passes against Graft; GitLab SDKs and GitlabMCPSharp remain candidates |

Each future matrix row should record upstream version, requirement source, fixture, implementation status, last tested date, build identifier, expected result and known deviation. A path match without behavior/auth fidelity is not compatibility. Distinguish imported CI files from executable pipeline compatibility.

The current workflow covers authenticated user/project discovery, read-only repository REST calls and Git HTTPS clone/push. The acceptance client is a C# `HttpClient` harness plus Git 2.53.0.windows.2. Pin and run an existing GitLab-style client before claiming client compatibility.

## Proposed MVP fixture baseline

MVP-06 update: [repository UI and API evidence](validation/2026-09-19-mvp06-repository-api-ui.md) demonstrates product namespace routing, HTTPS/token authentication, durable storage and the selected REST reads. This is implementation evidence against rolling official documentation, not a version-matched GitLab compatibility result.

Provisional version candidate: **GitLab CE 18.0.0, REST v4**. This remains an agent-proposed baseline rather than a user-selected deployment version. The GL-004 fixture pins the official unified repository tag `v18.0.0-ee`, commit `504fd9e5236e13d674e051c6b8a1e9892b371c58`, and hashes the tagged documentation, endpoint source, request specs, authentication and scope documentation. See [source-fixture evidence](validation/2026-09-19-gitlab-18-source-fixture.md). All seven creation cases later passed a disposable [GitLab CE 18.0.0 reference](validation/2026-09-19-gitlab-18-live-reference.md), and pinned `glab` 1.58.0 workflows passed against that reference.

Client evidence: installed **Git 2.53.0.windows.2** provides actual transport interoperability. Pinned **glab 1.58.0** passes `auth login` plus `api user`, project list, project create and project read against both the GitLab reference and Graft; see [pinned-client evidence](validation/2026-09-19-glab-graft.md). A C# HttpClient harness verifies additional wire/error behavior independently. GitlabMCPSharp remains a later client candidate. No broad glab support is implied.

The read rows marked **implemented** were first tested by the 64-assertion MVP-06 harness. The GitLab 18.0 project-creation fixture raised that workflow to 87 assertions, user/membership raised it to 97, and personal access-token lifecycle raises the current Windows JIT and NativeAOT workflow to 107. GL-004 is driven by the pinned 18.0.0 source fixture; the other rows still reference rolling official documentation. Git transport uses protocol v0/SHA-1. The live GitLab CE 18.0.0 and pinned `glab` 1.58.0 reports establish the exact bounded reference/client result described above.

| Fixture ID | Surface / request | Required assertions | Official reference |
| --- | --- | --- | --- |
| GL-001 | `GET /api/v4/user` | **Implemented:** current identity, missing token and revoked token; `PRIVATE-TOKEN` and Bearer; API scope required | [Users](https://docs.gitlab.com/api/users/) |
| GL-002 | `GET /api/v4/projects` | **Implemented subset:** visible private projects, selected fields, offset pagination headers; unsupported query parameters rejected | [Projects](https://docs.gitlab.com/api/projects/) |
| GL-003 | `GET /api/v4/projects/:id` | **Implemented subset:** numeric and percent-encoded personal namespace/project lookup; inaccessible projects concealed; selected fields | [Projects](https://docs.gitlab.com/api/projects/) |
| GL-004 | `POST /api/v4/projects` | **Implemented, GitLab 18.0 source-fixture subset:** authenticated user's personal namespace; name-only, path-only or explicit name/path; generated path, description and private visibility; `api` scope, `201` response, duplicate/path errors and explicit rejection of unsupported fields. Public/internal visibility, form payloads, namespaces, initialization options and other attributes remain explicit deviations | [Tagged Projects source](https://gitlab.com/gitlab-org/gitlab/-/blob/v18.0.0-ee/doc/api/projects.md) |
| GL-005 | Token transport | **Implemented subset:** `PRIVATE-TOKEN` and Bearer; scopes intersect live membership; query-string token unsupported | [Authentication](https://docs.gitlab.com/api/rest/authentication/) |
| GL-006 | `GET /api/v4/projects/:id/repository/branches` | **Implemented subset:** branch list, default branch and commit details; protection/merge fields are fixed MVP values | [Branches](https://docs.gitlab.com/api/branches/) |
| GL-007 | `GET /api/v4/projects/:id/repository/commits` | **Implemented subset:** selected ref, newest-first traversal and offset pagination; filter options pending | [Commits](https://docs.gitlab.com/api/commits/) |
| GL-008 | `GET /api/v4/projects/:id/repository/tree` | **Implemented subset:** ref/path tree reads with traversal rejection and offset pagination; recursive mode pending | [Repositories](https://docs.gitlab.com/api/repositories/) |
| GL-009 | `GET /api/v4/projects/:id/repository/files/:file_path` | **Implemented subset:** Base64 content, hashes, mode and commit IDs for text/binary blobs; `last_commit_id` currently equals selected commit | [Repository files](https://docs.gitlab.com/api/repository_files/) |
| GIT-001 | `GET /:namespace/:project.git/info/refs?service=git-upload-pack` and `git-receive-pack` | **Implemented bounded subset:** auth before disclosure, correct service responses, refs and empty repository | [Git HTTP](https://git-scm.com/docs/http-protocol) |
| GIT-002 | `POST /:namespace/:project.git/git-upload-pack` | **Implemented bounded subset:** clone/fetch through Git 2.53.0.windows.2, exact content and inaccessible rejection | [Git HTTP](https://git-scm.com/docs/http-protocol) |
| GIT-003 | `POST /:namespace/:project.git/git-receive-pack` | **Implemented bounded subset:** initial/incremental push, branches/tags, token auth, stale ref and interrupted publication checks | [Git HTTP](https://git-scm.com/docs/http-protocol), [pack format](https://git-scm.com/docs/pack-format) |
| WEB-001 | `/:namespace/:project` | **Implemented subset:** authenticated project/user/token creation, account password change/reset/disable, membership and token revocation, deep-link shell, tree/history/file API, CSRF login/logout; rendered Chromium workflow passes | Graft MVP UX acceptance; account re-enable and complete GitLab browser URL parity are not claimed |

Git HTTPS token authentication needs its own GitLab-version fixture; API header acceptance does not prove Git credential compatibility. REST unknown routes must return an explicit API error, never the Blazor HTML fallback. Treat unsupported query fields deliberately: document version-matched ignore/reject behavior instead of silently claiming full endpoint support.

Deferred surfaces remain not implemented: GraphQL, additional write-side project/repository REST APIs, raw/archive routes, SSH/LFS, OAuth, CI/runner protocol, webhooks, nested groups and complete browser URL conventions. Repository-only import via an ordinary push preserves Git content; it is not a GitLab data migration.
