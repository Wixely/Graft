# 0027: Package feeds and a container registry

- Date: 2026-10-29
- Status: Accepted user request, implemented
- Source: "build out these", naming package feeds and a container registry: "listed in the requirements, not started"
- Review trigger: Any proposal to add a package format, to keep package bytes in the database, to share blobs across projects, to add a token scope for the registry, or to implement a bearer token service for Docker

## What was missing

The requirements named package feeds including NuGet and a container registry, and nothing of either
existed. A build of a project had nowhere to go but a release's files.

## Decision: bytes beside the repository, rows in the database

A package's files and an image's blobs and manifests live on disk beside the repository, under
`packages/` and `registry/`, so a backup of the repository directory carries them and a fork does not
(a fork copies code, not builds). Each is written to a temporary file while it is hashed and moved into
place only once the hash is known, so nothing half-written is ever served. The database holds what a
listing needs and the hash a download promises.

Blobs are content-addressed per project and shared by the project's images, never across projects: a
blob belongs to the project that pushed it, and a project's readers are the only readers it has. Deleting a
tag or a manifest leaves blobs; a later sweep may reclaim them.

## Decision: two formats, and the generic one first

Generic packages, which are a file under a name and a version, and NuGet, which the requirements named.
The NuGet feed is the v3 protocol: a service index naming four resources, push as the client pushes it
with the token as the API key, the flat container for versions and downloads, registrations with the
package's description and dependencies read out of its `.nuspec`, and a search. Other formats are a
decision each, made when something needs them.

## Decision: the registry is the Docker v2 API with Basic authentication

The registry speaks the Docker registry HTTP API, version 2: blob existence, chunked and monolithic
uploads completed under a digest the server checks, manifests by tag and by digest with the media type
they were pushed as, tag lists and a catalog of what the caller may see. An image name is a project path
with optional segments beneath it.

Authentication is Basic with a personal access token, which Docker and every other client speak and
which is what the Git transport already answers to. There is no bearer token service; it would be a
second credential scheme for the same tokens. Pulling needs what a fetch needs, pushing what a push needs,
and there is no registry-specific scope: a token that may push code may push the image built from it.

## Decision: reading follows the repository, publishing needs a push

A package and an image are builds of the project, and a build speaks for the project the way a release
does. Readers of the repository read them, including anonymously on a public project; publishing them
needs the permission to push. Package clients are challenged with Basic when nobody is identified,
because NuGet and Docker send credentials only once asked.

## Bounds

A package file is at most 2 GiB and a blob at most 8 GiB, streamed to disk; a manifest at most 4 MiB.

## What was verified

Over the protocols the clients speak. Generic: a Reporter cannot publish, a file is published, downloaded
byte for byte as an attachment, an anonymous client is challenged with Basic, the listing names the file
and its hash. NuGet: the service index names the resources, a package built by hand is pushed with the
token as the API key, the flat container lists the version by lowercase id, the package and its nuspec
download, the registration carries the description and dependencies, the search finds it, and a push of
something that is not a package is refused. Registry: an anonymous client is challenged, an identified one
is told the API version, an unknown blob is unknown, an upload is started, a chunk appended with its range
reported, the upload completed under its digest, a lying digest refused, the blob found with its length, a
Reporter cannot push a manifest, a manifest is pushed under a tag and pulled by tag with its type and by
digest, the layer pulled byte for byte, the tag list and the project's image listing name it, and a tag is
removed while the manifest stays reachable by digest.

Not verified: a real `docker` or `nuget` client against this server, because neither is installed where
the suite runs. The protocol checks are the ones those clients make, in the order they make them.

**Found by building, not by reading.** The NuGet push route was mapped twice, once with the trailing slash
the client sends, and ASP.NET treats the two spellings as one route: every push was an ambiguous match that
failed in routing, before any handler or middleware could answer, as an empty 500. One mapping serves both
spellings. The browser fixture forwards the server's log lines under `SERVER:` now, because the suite's
assertion could only say what status came back and not why.
A monolithic blob upload under a digest the bytes did not match deleted the blob stored under the bytes'
real digest, which an honest push may have stored a moment earlier; the suite found it by pushing the same
layer twice, once lying. The bytes are now kept under what they hash to, as the chunked path already did,
and the client is told.
