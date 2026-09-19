# GitLab conformance plan

Recorded: 2026-09-18. Status: MVP fixture matrix proposed; no GitLab API operations implemented. Owner: Implementation agent. Review: 2026-09-25, before implementation and each selected upstream API-version change.

Confirmed goal: GitLab APIs/URL conventions and GitLab-style apps, with most GitLab functionality. Exact GitLab versions/editions and clients remain TBD. Reference for research: [official REST documentation](https://docs.gitlab.com/api/rest/). Recheck official documentation before implementing a versioned contract.

| Surface | Evidence to record | Current status |
| --- | --- | --- |
| REST | Method/path, request/response/error shape, auth, scopes, pagination, encoding, status codes, redirects | Not implemented |
| GraphQL | Supported schema/queries/mutations, pagination and authorization | Not implemented |
| Browser/raw/archive URLs | Namespace nesting, refs, filenames, downloads, canonical redirects | Not implemented |
| Git HTTPS/SSH and LFS | Clone/fetch/push, auth and ref permissions, large-object integrity | Not implemented |
| OAuth/tokens/webhooks | Token lifecycle/scopes, application flows, payloads, auth and delivery/retry behavior | Not implemented |
| CI configuration | Includes/templates, stages/jobs, expressions, variables, artifacts, caches, environments | Not implemented |
| Runner protocol | Enrollment/polling/jobs/logs/artifacts/cancellation separately from CupriNet transport | Not implemented |
| CLI/SDK/MCP clients | Host configurable, pinned client version, concrete end-to-end workflows | Candidates: glab, GitLab SDKs, GitlabMCPSharp; none tested against Graft |

Each future matrix row should record upstream version, requirement source, fixture, implementation status, last tested date, build identifier, expected result and known deviation. A path match without behavior/auth fidelity is not compatibility. Distinguish imported CI files from executable pipeline compatibility.

First candidate workflow for GRAFT-001: authenticated user/project discovery with an existing GitLab-style client, followed by repository clone/push in the next bounded slice. Confirm client/version and avoid claiming broad support from this workflow alone.

## Proposed MVP fixture baseline

MVP-01 update: [Windows managed Git feasibility](validation/2026-09-18-managed-git.md) demonstrates the core GIT-001/002/003 protocol workflows with Git 2.53.0.windows.2 on a fixed `/fixture.git` loopback HTTP route. Product namespace routing, HTTPS/token authentication and durable storage remain unimplemented; those conformance rows are therefore still pending as complete product contracts. No GitLab API compatibility is claimed.

Provisional version candidate: **GitLab CE 18.0.0, REST v4**. This is an agent-proposed reproducible contract fixture, not a user-selected version, a recommendation to deploy an old GitLab release, or a verified comparison environment. Official rolling documentation was consulted on 2026-09-18; it is not proof of 18.0.0 behavior. Before marking any row passing, obtain version-matched source/fixtures or captures from an authorized isolated reference, hash the fixtures and record deviations. No reference GitLab instance was installed or accessed during preparation.

Client candidates: installed **Git 2.53.0.windows.2** for actual transport interoperability; **glab** `api user`, `api projects` and `api projects/<id>` for REST discovery, with an exact release version to be pinned before acceptance. A C# HttpClient harness verifies wire/error behavior independently but does not substitute for an existing-client test. GitlabMCPSharp remains a later client candidate. No broad glab support is implied.

Each row below currently has: implementation **not started**, fixture **planned**, last tested **never**, Graft build **none**. IDs are future fixture names. Baseline is the provisional version above except Git transport, whose initial wire baseline is protocol v0/SHA-1. A test report must replace these fields with exact upstream/client/build IDs, fixture hashes, date, result and deviations.

| Fixture ID | Surface / request | Required assertions | Official reference |
| --- | --- | --- | --- |
| GL-001 | `GET /api/v4/user` | Current user identity with valid `PRIVATE-TOKEN`; missing/invalid/expired/revoked token errors; never serialize credential digests | [Users](https://docs.gitlab.com/api/users/) |
| GL-002 | `GET /api/v4/projects` | Only visible projects; agreed response fields; offset pagination and headers; supported query parameters; no private-project leakage | [Projects](https://docs.gitlab.com/api/projects/) |
| GL-003 | `GET /api/v4/projects/:id` | Numeric and percent-encoded namespace/project lookup; missing/inaccessible behavior; name/path/namespace/default-branch/clone URL fields | [Projects](https://docs.gitlab.com/api/projects/) |
| GL-004 | `POST /api/v4/projects` | Supported name/path/private-visibility subset; authentication/scopes, success status/response, duplicate and invalid-name errors; reject unsupported requested behavior explicitly | [Projects](https://docs.gitlab.com/api/projects/) |
| GL-005 | Token transport | `PRIVATE-TOKEN` and selected Bearer behavior; scopes intersect membership; no token query-string support unless deliberately added and tested as a deviation | [Authentication](https://docs.gitlab.com/api/rest/authentication/) |
| GIT-001 | `GET /:namespace/:project.git/info/refs?service=git-upload-pack` and `git-receive-pack` | Auth before disclosure, pkt-line/service headers, MIME types, correct refs/capabilities and empty repo behavior | [Git HTTP](https://git-scm.com/docs/http-protocol) |
| GIT-002 | `POST /:namespace/:project.git/git-upload-pack` | Clone and fetch through ordinary client; negotiation fallback, exact reachable content; inaccessible repository rejection | [Git HTTP](https://git-scm.com/docs/http-protocol) |
| GIT-003 | `POST /:namespace/:project.git/git-receive-pack` | Initial/incremental push, branches/tags, token-as-password over HTTPS, malformed pack and stale-ref rejection; bounded data and no partial unauthorized mutation | [Git HTTP](https://git-scm.com/docs/http-protocol), [pack format](https://git-scm.com/docs/pack-format) |
| WEB-001 | `/:namespace/:project` | Browser project discovery with same authorization; refresh/deep links work; private repository data never served as public static assets | Graft MVP UX acceptance; no complete GitLab browser URL parity claimed |

Git HTTPS token authentication needs its own GitLab-version fixture; API header acceptance does not prove Git credential compatibility. REST unknown routes must return an explicit API error, never the Blazor HTML fallback. Treat unsupported query fields deliberately: document version-matched ignore/reject behavior instead of silently claiming full endpoint support.

Deferred surfaces remain not implemented: GraphQL, repository REST APIs beyond selected project discovery, SSH/LFS, OAuth, CI/runner protocol, webhooks, nested groups and complete browser URL conventions. Repository-only import via an ordinary push preserves Git content; it is not a GitLab data migration.
