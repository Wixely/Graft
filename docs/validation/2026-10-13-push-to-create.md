# Push to create, and the chunked push bug it uncovered

Recorded: 2026-10-13. Owner: Implementation agent. Decision: [ADR 0007](../decisions/0007-push-to-create.md).

## What changed

`--push-to-create true|false`, default false. With it on, a push to a path holding no project creates one,
private, owned by the pusher, in the pusher's own namespace only.

Separately, and more importantly: **a bug that made every push over one mebibyte fail has been fixed.** It
was found while testing the above and is not caused by it.

## The chunked push bug

Git cannot replay a chunked request body after a `401`, so before streaming one it settles authentication
with a probe request whose body is a single flush packet. Graft answered that probe with
`400 Rejected: empty-update`, because a receive-pack request with no update commands is not a valid push,
and the client stopped there.

Only large pushes were affected. A push that fits in the client's `http.postBuffer`, one mebibyte by
default, is sent with a content length and is never probed. That is why small pushes worked and the whole
of this repository did not.

Measured directly before the fix, against a local server:

| Attempt | Result |
| --- | --- |
| `git push` of this repository at default settings | `error: RPC failed; HTTP 400` |
| The same push with `http.postBuffer` raised above the pack size | succeeds |
| A receive-pack request whose body is a lone flush packet | `400 Rejected: empty-update` |
| The same pack posted with a content length rather than chunked | `200`, `unpack ok` |

A lone flush packet is now answered with an empty result, which is what Git's own server does.

After the fix, this repository pushes at default settings to a path that did not exist, and the clone back
matches: the same head commit, `git fsck --full --strict` clean, 921 objects.

## Why the suite missed it

Every repository the acceptance suite pushed was a few kilobytes, so every push fitted in one buffer and no
probe was ever sent. The suite now pushes an incompressible two-mebibyte repository at the client's default
settings, clones it back, checks the head commit and runs `fsck`. Removing the fix and rerunning fails that
assertion, which is how the test was confirmed to test something.

One further correction to the suite itself. Two of its newer sections restart the server, which moves it to
a new ephemeral port, and they were passing the old address back to the caller. A later assertion that
expected a refusal was therefore partly satisfied by nothing listening. Both now return the live address.

## Evidence

The Git/API/browser suite is **365 assertions**, up from 340. Accounts is 113 and operations is 41, both
unchanged and both passing.

```
dotnet tests\Graft.Git.Acceptance\bin\Debug\net10.0\Graft.Git.Acceptance.dll src\Graft.Server\bin\Debug\net10.0\Graft.Server.dll
RESULT: PASS; assertions: 365
```

Most of the push-to-create assertions are about the cases that must **not** create, because a feature whose
job is to make things appear is only safe if the ways to make it appear are few and checked:

- Disabled: a push to a missing path is refused and creates nothing.
- Enabled: a push to a missing path creates the project; it is private, owned by the pusher, publishes no
  page, and is not readable by a stranger.
- A second push to the same path is an ordinary push, not a second creation.
- Refused, creating nothing, for each of: another account's namespace, a token holding only
  `read_repository`, an anonymous caller, and a path the REST route would reject.
- A fetch of a missing project stays a miss and creates nothing, so a mistyped clone URL cannot litter a
  namespace.

The audit trail now records how a project came into being, so one that appeared from a push is
distinguishable afterwards from one somebody deliberately created.

## What this does not cover

NativeAOT, for the usual environmental reason: the C++ build tools are absent here, so ILC never runs. The
new assertions sit inside the harness that is already run against published AOT binaries.

The engine's ceilings are untouched and still apply to a created project: 4096 objects, 32 MiB expanded,
4 MiB per object, 16 MiB per request, 128 refs. This repository fits inside them with room to spare, at
1109 objects and 14.4 MiB expanded, but a large repository will not, and push-to-create does nothing to
change that.
