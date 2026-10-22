# 0017: Getting things out, without cloning

- Date: 2026-10-22
- Status: Accepted user request, implemented
- Source: "do recommended", choosing the two defects and the fetchable batch from [the backlog](../backlog.md)
- Review trigger: Any proposal to serve repository content with a guessed content type, to render it inline, or to build an archive without a bound on its size

## What was missing

There was no way to get anything out of Graft except by cloning it. No raw file, no archive, and no listing
of tags even though pushed tags were stored and understood by the Git engine. A reader who wanted one file
cloned the repository; a reader who wanted a snapshot did the same and deleted the history afterwards.

## Decision: repository bytes are always a download, never a document

The raw and archive routes always answer `application/octet-stream` with `Content-Disposition: attachment`
and `X-Content-Type-Options: nosniff`. The content type is never derived from the file.

**This is the decision the rest of the feature hangs on.** Repository content is somebody else's HTML and
script. This origin holds the `__Host-` session cookie and the whole API, which is precisely why project
sites were given a hostname of their own in [ADR 0006](0006-project-pages-and-public-browsing.md). A raw
route that guessed a content type would undo that in one line: a reader following a link to a file in a
public project would be running its author's script on the forge's origin.

The cost is that a browser cannot preview a raw file, which is a real loss and the right trade. A reader
who wants to look at a file uses the repository browser, which renders it as text. A reader who wants the
bytes gets the bytes. A command-line client is unaffected: `curl` prints an octet-stream to the terminal
exactly as it prints anything else.

The filename in the disposition header is reduced to letters, digits, dots, hyphens and underscores, so a
repository path cannot inject a header.

## Decision: an archive is built in memory, because the repository is bounded

The whole tree is flattened, each blob read, and a gzipped tar or a zip assembled in memory before
anything is written.

That would be indefensible in general and is sound here: the Git engine bounds a repository at a few tens
of megabytes of expanded objects, so an archive is bounded with it. Building under the repository's lock
and writing afterwards also keeps a slow client from holding the repository while it reads, which a
streaming implementation would have had to solve separately.

Two formats, `.tar.gz` and `.zip`, chosen by the extension on the route rather than by a parameter inside a
path segment. Three literal routes cannot be matched wrongly; a parameter that matches by backtracking over
a literal is the kind of route that works in testing and surprises somebody later.

Everything sits under one directory named for the project and the short commit, which is Git's own
convention and means unpacking two archives in one place cannot merge them. The tar carries the one
permission bit Git keeps, so an executable file unpacks executable.

## Decision: tags are listed with both what they point at and what that resolves to

A tag reference may point at a commit or at an annotated tag object, which points at the commit. The
listing reports `target`, the object the reference names, and `commit`, what it resolves to.

Reporting only one would be wrong about the repository in one of the two cases, and the two are not
interchangeable: `target` is what a client will fetch and `commit` is what a reader means. A tag whose
target has gone is skipped rather than failing the listing, because one unreadable tag should not hide
the rest.

Tags can be created and deleted through the API, lightweight only. An annotated tag carries a message and a
tagger, which belong to a release, and releases are a feature of their own that this is not. A tag pushed
by a client keeps whatever form the client gave it and is listed alongside.

## Decision: the browser gets its own routes for both

The GitLab-shaped routes answer a token and nothing else, so a signed-in reader could not use them. The
download controls therefore point at Graft's own `/api/projects/:id/raw/**` and `/api/projects/:id/archive`,
which answer a session.

This was found by pressing the button rather than by reading the code: the first version of the page linked
to the GitLab route and would have answered 404 for every reader of a private project. A page that offers a
download which then refuses the reader is worse than a page with no download.

## The two defects fixed alongside

**An empty repository reported a permission problem.** The browse endpoint answered 404 when there was no
tree, and the page reports every 404 on that route as "You do not have access to this project", because for
every other case that is what it means. A project somebody had just created therefore looked like one they
could not see. An empty repository is now a state the page renders, with the two commands that end it.

**The commit page resolved a project through the caller's own project list.** That listing returns only
what the caller owns or belongs to, so a commit link into a public project failed for everybody else even
though the commit was readable. It now resolves through the browser repository endpoint, which answers for
any reader the project is visible to, as the collaboration pages already did.

## What was verified

The Git suite went from 603 to 641. Tags: an empty listing, a read-only token refused, creation at a named
revision, a duplicate refused, an unresolvable revision refused, a client-pushed annotated tag listed beside
an API-created one with its target and commit differing correctly, deletion, and a second deletion
answering 404. Raw: exact bytes for text and for binary, the content type, the disposition and the nosniff
header all asserted, a missing file, a concealed project, and the browser's own route. Archives: the zip
opened and its entries read, the tarball decompressed and its executable bit checked, the directory prefix,
an unresolvable revision, a concealed project, the extensionless default, and an archive taken at a tag.
The empty repository answers with a page carrying no branches, no commits, no tree and no readme, while
still being concealed from a stranger.

The empty-repository page, the download controls and the tag panel were driven by hand in a browser. See
[the evidence](../validation/2026-10-22-fetching.md).
