# 0040: GitLab's clients are met on request parameters, list shapes and members

- Date: 2026-11-01
- Status: Accepted, implemented
- Source: A question on 2026-11-01 about GraphQL, naming the clients in use: LabCoat and Comeet. Reading
  LabCoat's source showed it uses no GraphQL and that Graft could not serve its REST calls; the fixes came first,
  with investigation after.
- Review trigger: a client that reads an ordering Graft ignores and depends on it; a client that sends a filter
  Graft refuses on every request; Comeet's traffic, once read; GraphQL, if a client in use needs it

## What was found

LabCoat (Commit451, Kotlin Multiplatform, read at its 2026-08-14 head) makes 95 distinct REST calls and no GraphQL.
Probing each against a disposable Graft showed 39 with no route and, on the routes that exist, four defects that
would break any GitLab client, not only LabCoat:

- `/api/v4/projects/:id/issues` and `.../merge_requests` answered a page object, `{"issues":[...],"total":...}`.
  GitLab answers a bare array and puts the count in headers. This was not recorded as a deviation; it was a defect.
- `/api/v4/search` and `.../projects/:id/search` answered one object for every scope, recorded as a deviation.
- Every write needed a JSON body and answered `415` otherwise. GitLab reads a write's parameters from JSON, a form
  or the query string; LabCoat posts forms, edits an issue through the query string, and posts approvals bare.
- Members were named by username and graded by role name. GitLab, and LabCoat, use `user_id` and `access_level`.
- Lists had GitLab's `X-*` paging headers but no `Link`, which LabCoat follows. And every list refused `order_by`
  and `sort`, which GitLab's clients send with every list.

## Decision

- **Lists and search under `/api/v4` answer bare arrays.** The browser's own routes keep their page objects; the
  two families already differ in path and credential. Search adds `X-Graft-Truncated`.
- **Writes under `/api/v4` accept forms and query strings.** One middleware turns a form, a multipart body without
  a file, or a bodyless write's query string into the JSON the handler reads; a write with no body becomes `{}`.
  Every field is a string, or an array when it repeats or is named `key[]`, and the contracts now read numbers and
  booleans from strings. Only under `/api/v4`, whose credential is a `PRIVATE-TOKEN` or `Authorization` header that
  a cross-site form cannot send, so this opens no request forgery; the browser's routes stay JSON with their CSRF
  token. Package and upload paths are left alone because there the body is the file.
- **Members take GitLab's form as well as Graft's.** `user_id` and `access_level` 20, 30 or 40 on a grant; a user id
  or a username in the path; GitLab's `PUT .../members/:user_id`. Responses carry GitLab's `id`, `name`, `state`
  and `access_level` beside Graft's fields. Guest and Owner are refused by name, because Graft has no Guest and a
  project's owner is its creator.
- **Paging carries `Link`**, absolute, with `next`, `prev`, `first` and `last`.
- **`order_by` and `sort` are accepted on every `/api/v4` list and the route keeps its own order.** This is the one
  place Graft now accepts a parameter it does not act on. It is limited to ordering because an ignored order changes
  the order of what comes back, never what comes back. Any other parameter a route does not implement is still
  refused, and the refusal now names it.

The 39 missing routes are not part of this decision; they are the next work, taken in order of use.

## What was verified

Git acceptance suite 1196 to 1207, managed and against the NativeAOT forge, whose publish has no trimming or AOT
warnings. New checks: issues created from a form and from a bodyless POST's query string; an edit through the query
string with a boolean read from a string; a listing as a bare array with `X-Total` and an absolute `Link` to the
next page, with an ordering hint accepted; an unimplemented filter refused by name; a member granted by `user_id`
and `access_level` in GitLab's shape, changed by `PUT` on its own path, Guest refused, removed by user id. Existing
checks were moved to read the bare arrays. MCP suite 104: its tools now read the arrays and the headers. The `glab`
suite, which needs WSL and Podman, was not run.
