# 0043: Pushes, fetches and large files in parts, through git-remote-graft

- Date: 2026-11-03
- Status: Accepted, implemented
- Source: The live deployment behind Cloudflare's free plan, which limits a request to 100 MB and recommends splitting
  large files into smaller parts. On 2026-11-03 the helper was asked to stand in for Git transparently when the remote
  is a Graft forge, and then to cover LFS and fetching as well.
- Review trigger: a request to carry SSH or Git LFS locking through the helper; a proxy limit below one MiB; a Git
  release that stops sending protocol version 2 over a `connect` helper; a macOS or ARM client; a deployment that wants
  the transfer state to survive a restart

## What was wrong

A proxy in front of the forge decides what one request may be. Cloudflare's free plan refuses a request body over
100 MB and gives up on a response that has not started within about 100 seconds. Git sends a whole push as one request,
so a push of more than 100 MB through such a proxy fails with 413 and cannot be made to succeed by any Git setting:
`http.postBuffer` changes how the body is framed, not how big it is. Git LFS sends each object as one request, so one
file over 100 MB fails the same way. A fetch is not limited by size, but the forge builds a pack before answering, and a
large one takes longer than the proxy waits.

## Decision: a transfer API in parts, on the forge

`/{namespace}/{project}.git/graft/transfers` carries a push (`kind=git-receive-pack`), a fetch request
(`kind=git-upload-pack`, `protocol=2` for version 2) or one Git LFS object (`kind=lfs&oid=...`) of a declared `size`:

- `POST ...?kind=&size=` with the first part creates a transfer and answers its id, what has arrived and `part_bytes`.
- `PUT .../{id}?offset=` sends the next part. A part is placed by its offset, so one whose answer was lost is sent
  again: an offset short of what arrived cuts back to it, one beyond it is refused with 409 and what arrived.
- When the last byte arrives the work runs in the background, exactly as the route the transfer stands in for would
  run it: the same `Receive`, `ServeV2` or `StoreAsync`, the same refusals, and for a push the same webhooks, activity
  record and pipelines. It waits for the repository rather than answering busy.
- `GET .../{id}?wait=` answers the state (`receiving`, `running`, `done`, `failed` with the reason), waiting up to 25
  seconds for it to finish, which is under any proxy's timeout.
- `GET .../{id}/result` answers a Git transfer's result, in byte ranges when asked. `DELETE` forgets the transfer.

The same credential and permission as Git over HTTPS: a push or a large file needs push, a fetch needs read. Every
request after the first must come from the person who started it; anybody else is told there is no such transfer.
Transfers live in memory with their bytes in a temporary directory, at most 8 per person and 64 in all, and are
forgotten after an hour without a request; a restart loses them and the client reports the failure.

`--transfer-part-size <1-95 MiB>`, default 16, is the largest part. 16 MiB is well under Cloudflare's limit and IIS's
default request limit of about 28.6 MiB.

Git LFS: the batch API answers with the `graft` transfer when the client offers it, its upload action pointing at the
transfer API, and an object is read back in byte ranges. A client that offers only `basic` gets `basic`, as before.

## Decision: git-remote-graft, which Git and git-lfs run themselves

`src/Graft.Remote` builds `git-remote-graft`, a remote helper. Git runs it for a remote written
`graft::https://host/owner/project.git`, and it offers Git the `connect` capability: Git speaks its own protocol through
it, as through SSH, and the helper carries each request over HTTPS.

- A fetch is protocol version 2, whose requests are each answered on their own. `ls-refs` is a plain request; each
  `fetch` request goes as a transfer and its pack comes back in ranges, resumed where a dropped connection stopped.
- A push is version 0: the helper reads Git's commands and walks the pack Git writes to find where it ends (the engine's
  `Pack.Capture`, as the SSH server does), then sends the whole as a transfer in parts.
- Credentials come from Git's credential helpers through `git credential`, so the token Git already has is used; TLS
  is checked unless Git's `http.sslVerify` says not to, against `http.sslCAInfo` when set.
- `git-remote-graft lfs-agent` is a Git LFS custom transfer agent for `graft`: uploads as transfers, downloads in
  ranges, each checked against its oid.

`git-remote-graft setup https://forge` makes it transparent, in the user's global Git configuration:
`url."graft::https://forge/".insteadOf "https://forge/"` (or `pushInsteadOf` with `--push-only`) and
`lfs.customtransfer.graft.*`. Existing `https://` remotes, clones, editors and tools are unchanged and go through the
helper; other hosts are untouched; any other LFS server ignores the `graft` transfer. `remove` undoes it.

Replacing `git.exe` was rejected: a Git update would overwrite it, editors bundle their own Git, and Git already has the
supported mechanism.

### The Git LFS address

git-lfs works out its address from the remote's URL after the same `insteadOf` rewriting, which yields
`graft::https://.../info/lfs`, an address it cannot use. It does read a remote's `lfsurl` first, but rewrites that too, by
the longest matching rule. So the helper, whenever Git runs it in a repository, sets `remote.<name>.lfsurl` to the plain
address (marked `remote.<name>.graftlfsurl` so it keeps it in step and never overwrites one the user set) and a
repository-local rule mapping exactly that address to itself. No Git remote begins with `.../info/lfs`, so Git never
applies the rule. A repository the helper has never run in, used with `git lfs` commands alone before any fetch or push,
still sees the rewritten address; any fetch, pull or push fixes it.

Some versions of Git start the helper for a clone before they write `remote.origin.url` into the new repository. The
helper first pinned only for a remote whose URL was configured, so on those versions a fresh clone was left unpinned and
git-lfs failed with `missing protocol: "graft::https://.../info/lfs"`. Found on 2026-11-04 by the deployment's verify
stage, which the local Git (2.55) did not reproduce. The helper now pins for whatever remote Git names, and says so
with a `graft:` warning when Git refuses the write or the value does not read back.

## Distribution

The forge serves `/-/git-remote-graft/install.ps1` and `/-/git-remote-graft/install.sh`, which carry no secret, and the
binaries `git-remote-graft-win-x64.exe` and `git-remote-graft-linux-x64` from the `--runner-downloads` directory. The
scripts install for the person running them, with no administrator rights (`%LOCALAPPDATA%\Programs\git-remote-graft`
added to the user's PATH; `~/.local/bin`) and run `setup` for the forge.

## What was verified

- The Git suite drives real Git 2.55 and git-lfs 3.7 through the helper against a server with one-MiB parts: `setup`;
  a push of three MiB in parts through a plain `https://` remote; the LFS address pinned; a clone that passes
  `git fsck --strict`; a later push and an incremental pull; the push in the activity feed; an LFS upload and a fresh
  clone's LFS download, both through the `graft` adapter; and the API's refusals: no credential, no permission, unknown
  kind, a part over the size, a part beyond what arrived, parts past the declared size, somebody else's transfer, a push
  that is not one, an LFS object that does not hash to its oid; ranged reads; the install scripts; `remove`.
- The Git suite: 1306 to 1333 assertions, passing with the managed server and helper and with the NativeAOT server and
  NativeAOT helper (`GRAFT_REMOTE_HELPER`), both published with zero warnings. The runner's cancel check now waits for
  the job's first log line instead of a fixed 1.5 seconds, a race this work's runs exposed on Windows.
- By hand on Windows: the NativeAOT helper (zero warnings, about 11 MB) cloning with LFS, uploading through the agent and
  pushing; a 6 MB push in 5 seconds with one-MiB parts.

Not verified: the live deployment behind Cloudflare and IIS, the Linux build of the helper, and transfers beyond a few
megabytes.
