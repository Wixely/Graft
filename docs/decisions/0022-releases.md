# 0022: Releases

- Date: 2026-10-29
- Status: Accepted user request, implemented
- Source: "build this out", naming releases with notes and assets from the collaboration depth list
- Review trigger: Any proposal to store asset bytes outside the database, to raise the per-asset bound, to delete a tag with its release, or to serve an asset with a content type

## What was missing

Tags existed and nothing sat on them. There was nowhere to say what a version was, nowhere to put the
build of it, and nothing a stranger could find that said "this is the one to take".

## Decision: a release sits on a tag and outlives it

A release names a tag; the tag must exist when the release is made, because a release of nothing is a
mistake worth catching at the moment it is made. Afterwards the tag may be deleted and the release stays,
reporting that its tag is gone and offering no commit and no archive. Deleting a release never touches the
tag. The two are different acts, done by the same people, and tying them together would make one of them
accidental.

One release per tag per project. The name defaults to the tag.

## Decision: reading follows the repository, publishing needs a push

A release is readable wherever the repository is, including anonymously on a public project, because a
release is the thing a project most wants a stranger to find. Publishing one, editing it, attaching a file
or a link, or deleting it needs the permission to push, because a release speaks for the project and a tag
is what a push makes. A Reporter can read every release and change none.

## Decision: asset bytes live in the database, bounded

An uploaded file is stored as a row in SQLite beside the release it belongs to, hashed with SHA-256 on the
way in. Keeping it there means a backup captures the file atomically with the release and a restore cannot
reinstate one without the other, which is the property avatars already rely on.

That choice only works while a row stays a row, so each asset is bounded at 32 MiB and a release holds at
most fifty. The bound is a policy of this decision rather than a limit of SQLite, and raising it is a
decision about where asset bytes live. A file of the same name replaces the earlier one, which is what
re-uploading a build means.

Links point elsewhere and store only a name, an absolute http or https address and a kind.

## Decision: an asset is served the way a raw file is

A download is always `application/octet-stream` with `Content-Disposition: attachment` and `nosniff`, with
an `ETag` of its hash. An uploaded asset is somebody else's bytes on the origin that holds the session, and
[ADR 0017](0017-getting-things-out.md)'s reasoning applies without change. The archives of the tag are
offered beside the files as the release's sources, built on request.

## Two transports, as before

The browser attaches a file through the resumable upload transport, under a profile of its own with the
same 32 MiB cap, and then names the upload to the release; an API client sends the bytes as the request
body of `PUT .../releases/:tag/assets/files/:file_name`. That is the split [ADR 0005](0005-browser-upload-transport.md)
chose, and the ingest is one function behind both.

## The API

GitLab's routes under `/api/v4/projects/:id/releases`, with `assets.links` in GitLab's shape and Graft's
own `assets.files` beside them, because GitLab has no uploaded-file concept on a release and a payload
somebody else owns should not pretend it does. `tag_exists` is Graft's own.

## What an agent can do with this

`graft_list_releases`, taking the surface from 32 to 33. There is no tool to publish a release or attach a
file: a release speaks for the project, and that is for a person to do.

## What was verified

The Git suite went from 723 to 768 across ADR 0021 and this record, which were verified together. A Reporter cannot publish; a release needs a tag that exists; a release
is created with its commit and two archives; one per tag; a private project's releases are concealed from a
stranger and a public project's are readable with none; a link is added, a non-http address and a duplicate
name are refused; a Reporter cannot attach a file; a file is attached as the request body, hashed and
measured, downloads byte for byte as an attachment of unknown type with `nosniff`, is replaced by a file of
the same name, and is removed; a release is edited; deleting the tag leaves the release reporting the tag
gone with no commit and no archives; the release is deleted and is gone.
