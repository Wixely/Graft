# Compressed Git requests

Recorded: 2026-10-25. Owner: Implementation agent. Fixes a defect reported from a live deployment.

## What was wrong

The Git transport refused any request carrying a `Content-Encoding` header:

```csharp
Limits.Require(!context.Request.Headers.ContainsKey("Content-Encoding"), "content-encoding");
```

Git compresses a fetch negotiation body with gzip once it passes about a kilobyte, and offers no client
setting to stop it. So the refusal fell on exactly the fetches that need several rounds of negotiation,
which is what happens once two histories have diverged: the early rounds are small and answered normally,
and the round that grows past a kilobyte is compressed and answered `400 Rejected: content-encoding`.

A clone of a new repository never reaches that point. The defect therefore looked like nothing at all until
a repository had been used for a while, and then presented as a pull that failed against a server the same
client had been using successfully.

## Why the header was refused in the first place

The read loop bounds the request by counting bytes as it reads them. A compressed body would have been
counted compressed, so a small input could have expanded past the ceiling after the check had passed. The
refusal was a correct response to that, taken before there was a reason to accept compression.

## What changed

`gzip` is accepted and decompressed; every other encoding is still refused, and now by name, so a refusal
says which encoding it refused.

The ceiling moved to the output of the decompressor rather than the input. The existing loop already
counted what it read, so reading through a `GZipStream` bounds the decompressed size by construction: a
body that expands past the limit is refused at the moment it passes it. A malformed gzip body is reported
as a bad request rather than read as rubbish.

Only gzip, because that is the only encoding Git sends.

## Verified against real Git

A clone, sixty commits on the client, forty more pushed from a second clone, then a fetch with
`GIT_TRACE_CURL=1`:

```text
=> Send header: Content-Encoding: gzip
```

One of the three `git-upload-pack` requests in that fetch was compressed, and the fetch completed. That is
the reported failure reproduced and then fixed, rather than inferred.

The same scenario is now in the suite: a clone, sixty commits on each side, and a fetch that has to
negotiate. Before this change that fetch could not succeed, because Git demonstrably sends the header the
server demonstrably refused.

## In the suite

The Git suite went from 645 to 653.

| Step | Result |
| --- | --- |
| An uncompressed negotiation request | answered |
| The same request gzipped | answered, and byte for byte the same answer |
| Twenty mebibytes of zeros, gzipped to a few kilobytes | `400`, naming `request-limit` |
| A body declaring `Content-Encoding: br` | `400`, naming the encoding |
| A body declaring gzip that is not gzip | `400` |
| A real fetch across a diverged history | completes |

The second row is the assertion worth keeping. It does not check that gzip is accepted but that it changes
nothing: the compressed and uncompressed forms of one request produce identical responses.

The third row is why the header could be accepted at all, and would fail if the bound were ever moved back
to the compressed size.

## Boundaries worth stating

**Only gzip.** Brotli and deflate are refused. Git does not send them, and an encoding nobody sends is one
more decompressor to bound for no gain.

**The bound is on the decompressed size.** For a fetch that is 16 MiB, unchanged by this work. A request
that compresses well and expands past it is refused while expanding, not after.

**Pushes were never affected.** Git does not compress a `git-receive-pack` body, because a packfile is
already compressed.

## Also reported, and separate

The same deployment reported an earlier push failing with `repository-budget`. That is the repository size
limit rather than anything to do with compression, and it was raised substantially on 2026-10-24 by
[ADR 0018](../decisions/0018-repository-capacity.md), which took capacity to 4 GiB. A deployment running a
build from before that date would still hit the old ceiling. Nothing in this change affects it.
