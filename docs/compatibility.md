# GitLab conformance plan

Recorded: 2026-09-18. Status: matrix skeleton; no GitLab API operations implemented. Owner: Next agent. Review: GRAFT-001 and each selected upstream API-version change.

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
