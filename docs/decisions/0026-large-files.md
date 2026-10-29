# 0026: Large files

- Date: 2026-10-29
- Status: Accepted user request, implemented
- Source: "build out these", naming LFS: "pointers push and clone but resolve to nothing"
- Review trigger: Any proposal to keep large files in the database, to serve one without checking its hash on the way in, to add the locking API, or to raise the per-object bound

## What was missing

A repository that used Git LFS pushed and cloned, and every tracked file came back as the pointer that
named it, because nothing answered the batch API the client asked next.

## Decision: a large file is content beside the repository, hashed on the way in

Graft implements the LFS batch API and the basic transfer. An object is kept under `lfs/` beside the
repository's own objects, content-addressed by its SHA-256, so a backup of the repository directory carries
it and a fork copies it. It is written to a temporary file while it is hashed, and moved into place only
once the hash matches the name it was offered under and the size matches what was declared; a client that
lies about either is refused and nothing is kept. Reads stream from disk. An object is at most 4 GiB.

The database is the wrong place for this. A release asset ([ADR 0022](0022-releases.md)) is small by
policy and belongs with its release; a large file is the one thing in a repository that is allowed to be
large, and it belongs with the repository.

## Decision: the same credential and the same permission as Git

The batch request and the transfers answer to the token the Git transport answers to, and ask for it the
same way. Downloading needs what a fetch needs, so a public repository's large files are readable with no
credential, and uploading needs what a push needs. The transfer actions carry the credential the batch
arrived with, so a client is not sent back to its credential helper for every object.

Only the basic transfer, and no locking API. A client that asks for another transfer is told so by name.

## Decision: the raw route serves what the pointer stands for

A pointer in the tree stands for a file the repository may hold. The raw routes serve that file when it is
there, which is what a reader following "download" expects, and the pointer itself otherwise, which is at
least the truth about what is there. The file API still describes the pointer, because the pointer is what
is in the tree, and a client reading a blob by its hash gets the blob.

## What was verified

By hand over HTTP: a batch on a private repository asks for credentials; an upload batch offers an upload
action for a missing object; bytes that do not hash to their oid are refused; the object is stored once
hash and size match; an object already stored is offered no upload action; it downloads byte for byte. Then
with the real client: `git lfs track`, commit and push store the object content-addressed beside the
repository; a fresh clone gets the content back; the raw route serves it and names the oid; the file API
describes the pointer.

**Found by building, not by reading.** git-lfs 3.7.1 reads the `actions` object as a map of pointers and
sets a timestamp on each without checking for nil, so a batch response that writes `"download": null`
beside `"upload"` crashes the client with a Go panic rather than an error. The response omits every null
member. Separately, the batch response has to carry the LFS media type explicitly, because the JSON writer
replaces whatever content type was set when it is not told one.
