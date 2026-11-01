# 0041: LabCoat's routes, retry and cancel, and a directory only administrators can read

- Date: 2026-11-01
- Status: Accepted, implemented
- Source: The decision on 2026-11-01, after the LabCoat probe of [ADR 0040](0040-gitlab-request-and-list-shapes.md),
  to do all of it: the user list empty for ordinary accounts and complete for an administrator, and pipeline retry
  and cancel implemented.
- Review trigger: a client that needs another user's profile; GitLab's events filters; reactions on issues and merge
  requests themselves; renames in diffs; a runner protocol that pushes instead of being asked; a request for upload
  listing or deletion

## What was missing

LabCoat makes 95 REST calls. After ADR 0040, 39 had no route and two answers crashed or emptied a screen: the
commit diff was an object where LabCoat reads an array, and marking a to-do done could answer 204 with no body.
`GET /projects?membership=true`, LabCoat's home screen, was refused. A field-by-field reading of LabCoat's
models found that projects had no `permissions`, so LabCoat treated every user as unable to manage anything; and
that LabCoat fails to parse an explicit `null` in a field it declares non-null, so new responses never write one
for a number, a flag or a list.

## Decision: the user directory is the administrator's

`GET /users` answers an administrator with every active account, matched by `search` or `username`, and anybody
else with an empty list, as decided. `GET /users/:id`, `/users/:id/projects` and `/users/:id/events` answer
a person about themselves and an administrator about anybody; anybody else gets 404, the same answer as for no
such account. GitLab lets any signed-in user list and look up everyone; this is a recorded deviation, consistent
with Graft never letting its users enumerate one another.

## Decision: retry and cancel, down to the runner

A retry is a new attempt, as on GitLab. The old job keeps its result and log and is marked `Retried`; a copy is
queued; jobs its failure had skipped go back to pending, and the pipeline reopens. Every rule that reads a
pipeline's jobs, leasing, settling and listing, reads only latest attempts. Retrying a pipeline retries every
latest attempt that failed or was canceled.

A cancel ends a pending or running job with the status `canceled`, which blocks later stages as a disallowed
failure does; a pipeline with a canceled job and no disallowed failure settles `canceled`. A running job's token
is cleared. The runner hears of it through its log channel: the forge answers a canceled job's log upload with
410 rather than 401, and the runner, which now checks in with an empty piece of log every five seconds when the
job prints nothing, cancels the job's scope, which kills the process tree and stops any transfer. It reports
nothing further, because the forge already holds the result, and asks for its next job. The suite cancels a job
sleeping for five minutes and sees the runner stop it and move on within seconds.

Retry and cancel are for whoever may push, as starting a pipeline is. Erasing a job, which removes its log and
artifacts, and deleting artifacts are a Maintainer's. All are on GitLab's routes and on the browser's, and the
Pipelines page has Retry and Cancel buttons for a pipeline and for the job whose log is shown.

## Decision: what each of the other routes is

- **Projects.** `membership=true` is accepted as what the listing always was; `starred` and `search` filter it.
  Every project carries `star_count`, `owner` for a personal namespace, and GitLab's `permissions` with the
  caller's access level, read from the identity token authentication leaves on the request: 50 for the owner or
  an administrator, then 40, 30 and 20 for managing, pushing and taking part.
- **Stars** are a table of who starred what.
- **Activity** is derived, not copied: issues and merge requests opened, merges, comments, and pushes, from a new
  record of the branches each push changed, over HTTPS and SSH. Confidential issues and their comments are left
  out of every feed, because who may read them is decided per issue.
- **Reactions** are on comments, one name per person, removed by the person who reacted.
- **Discussions** are Graft's one-level threads in GitLab's shape. A line comment is anchored to the source tip,
  as every Graft line comment is, and reported as `head_sha`; a position arrives as GitLab's object or as the
  flat `position[...]` fields a form carries.
- **Approvals** report current approvals only, as `approved_by: [{user}]`.
- **Merge request changes and merge** answer the merge request's fields at the top level, with `changes` in
  GitLab's diff shape, beside Graft's own keys, so Graft's tools and GitLab's clients read the same body.
- **The commit diff** is GitLab's array, with Graft's per-file additions and the truncation in a header. Graft's
  MCP tool reads it that way, with the commit from the new single-commit route.
- **Uploads** are kept beside the repository, at most 10 MiB, and served at GitLab's
  `/<project>/uploads/<secret>/<name>` to whoever may read the conversation, sandboxed, with only images and plain
  text inline.
- **Smaller ones:** contributors with commit counts and zero line counts; snippet comments in their own table,
  because comments are constrained to issues and merge requests; a milestone's issues; the cross-project issue
  and merge request lists; `scope`, `iids`, `author_username` and `wip` on the project lists; deleting an issue,
  for the owner or an administrator, as GitLab gives it to the Owner role; canceling an automatic merge, which
  answers the merge request because none is ever scheduled; and `mark_as_done` always answering the to-do.

Migration 25 adds `ProjectStars`, `AwardEmoji`, `SnippetNotes`, `PushEvents`, `ProjectUploads` and `Jobs.Retried`.

## What was verified

- A fresh forge answers every one of LabCoat's 95 calls on a route; none falls through to "endpoint not found".
- Git acceptance suite 1207 to 1272, managed and against the NativeAOT forge with the NativeAOT runner, both
  published with no trimming or AOT warnings. The new checks cover each route above, the directory's two answers,
  a reaction refused twice and addressed beneath the wrong issue, a line discussion from flat position fields, an
  upload served to a reader and refused to a stranger, and in CI: a job retried, the retried attempt refused a
  second retry, the skipped stage reopened, a pipeline retried and canceled, a running job canceled and stopped
  by its runner, a job erased and a project's artifacts deleted.
- MCP suite 104.

Not verified: LabCoat itself against a forge. The probe and the model reading are evidence of shape, not of every
screen working.
