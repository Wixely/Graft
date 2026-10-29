# 0028: The Git engine keeps repositories the way Git does

- Date: 2026-10-29
- Status: Accepted user request, implemented
- Source: "build out these", quoting the engine's own description back: "a bounded subset: SHA-1 only, protocol v0, no delta compression when writing packs, no gc or repack, custom on-disk format", and the October note that this was a stopping point rather than the end state
- Review trigger: Any proposal to add SHA-256 repositories, shallow or filtered fetches, thin packs, pruning of unreachable objects, a second pack index format, reading repositories written by something other than Graft, or SSH

## What was missing

The engine stored each object as a type byte in front of raw content and the references in a file of its
own; Git could not open the directory. Every fetch sent every reachable object whatever the client already
had, the packs it wrote were never delta-compressed, nothing ever consolidated loose objects, and only
protocol version 0 was spoken although Git has asked for version 2 by default since 2.26.

## Decision: a repository directory is a bare Git repository

Loose objects are zlib streams of `type size\0content` under `objects/xx/`, packs and their version 2
indexes live under `objects/pack/`, references are in `packed-refs` with peeled lines, `HEAD` names the
default branch and `config` says the repository is bare. Git opens the directory, which is the test of the
format and the point of it: an operator can run `git fsck`, `git log` or `git bundle` against what Graft
keeps, and a repository can leave Graft by being copied.

Graft is the only writer. A loose reference under `refs/` written by something else is honoured when the
repository loads, as Git would honour it, and removed when Graft next writes the references, because two
places for one reference is how a reference gets lost. The `staging/` directory beside the Git layout is
Graft's, and Git ignores it.

Publication keeps its two visible steps: objects first, then the reference file replaced as a whole. The
interrupted-publication checks from before still pass against the new layout.

## Decision: the previous layout converts itself on first open

A directory in the old format is recognised by its files, not by a version marker: an old object begins
with a type byte of 1 to 4, which no zlib stream does, and the old reference file has a name of its own.
Each object is rewritten in place and the reference snapshot becomes `packed-refs`; an interrupted
conversion resumes, because what was converted no longer looks old. There is no flag to set and nothing an
operator does. The deployment's repositories convert the first time each is opened after the upgrade.

## Decision: packs are read through their index and written with deltas

A pack's index is read into memory and its objects are read on demand with positional reads, so one open
pack serves concurrent readers and nothing is inflated until asked for. Both checksums are verified when
the pack is opened; after that the index is trusted the way Git trusts it. Resolved delta bases are kept
in a cache of 64 MiB per pack, because a tree walk otherwise re-applies the same chains.

Every pack Graft writes, for a fetch or for housekeeping, is written the way Git writes one: objects sorted
by type, then by a hash of their path so versions of one file sit together, then larger first; each tried
as a delta against a window of ten neighbours; stored as the smaller of itself and the best delta, up to a
chain depth of twenty. Deltas name their base by offset when the client said it could read that, otherwise
by id. Objects over 16 MiB are stored whole, so the window never holds more than that times ten. The
delta format and the index format are Git's; `git index-pack` over a pack Graft wrote produces the same
index byte for byte, which the suite checks.

## Decision: housekeeping packs everything and prunes nothing

`POST /api/v4/projects/:id/housekeeping` writes every object into one pack with its index and then removes
the loose files and the packs it replaced; the new pack is complete and indexed before anything old is
deleted. It needs the `api` scope and Maintainer-equivalent access, the same standing the other project
settings need. GitLab schedules it and answers `201` with nothing; Graft does it before answering and says
what it did. It also runs by itself at the end of a push that leaves more than a thousand loose objects,
as Git runs `gc --auto` at the end of a receive; Git's own threshold is 6,700, and Graft's is lower because
every loose object is hashed when a repository opens and a pack is not.

Nothing is pruned. An object nothing points at is kept, because a line comment or a proposal may still
name the commit it belongs to, and because deciding when an object is safe to lose is a decision of its
own with its own record.

## Decision: a fetch sends what the client lacks

The version 0 advertisement now carries `multi_ack_detailed`, `include-tag` and an agent. Each have the
repository recognises is acknowledged as common, the client is told the pack is ready once every want
can reach a common commit, and the pack carries what the wants reach and the common commits do not. The
exclusion is computed as a full walk from the common commits, which is simple and correct and costs a walk
of the history on every fetch; a reachability bitmap would make it cheap and is not built.

## Decision: protocol version 2, without shallow or filter

A client that sends `Git-Protocol: version=2` is answered in version 2: the capability advertisement,
`ls-refs` with prefixes, peeling, symrefs and unborn `HEAD`, and `fetch` with wants, haves, `want-ref`,
`done`, `ofs-delta`, `include-tag` and `thin-pack` accepted, the acknowledgments section with `ready`,
the wanted-refs section, and the pack on sideband one. A push has no version 2 and is answered in version 0
whatever the header says. `deepen`, `shallow` and `filter` are refused by name rather than answered with a
full history, so a client that asked for a shallow clone is told rather than surprised.

## What stays bounded, deliberately

- **SHA-1 only.** A SHA-256 repository is a different object format throughout: ids, tree entries, pack
  trailers and indexes. GitLab 18 does not host SHA-256 repositories either, and no client or workflow in
  the compatibility target needs one. Adding it is a review trigger, not a gap.
- **No shallow or partial clones.** Refused by name.
- **No thin packs sent.** The client may say it accepts them; Graft sends complete packs.
- **No pruning.** Above.
- **No bitmaps, commit-graph or multi-pack index.** Reachability is walked each time. The ceilings in
  [ADR 0018](0018-repository-capacity.md) stand.
- **Graft's own packs.** Packs Graft reads are the packs Graft wrote. A pack copied in from elsewhere with
  a version 2 index would load, because the format is the same, and nothing here promises that.
- **SSH.** Smart HTTP only, as before.

## What was verified

Through the suite with Git 2.55.0.windows.5. A directory in the previous layout opens, converts, passes
`git fsck --full --strict`, answers `git rev-parse` and `git cat-file`, and opens again unchanged. A
repository pushed by Git is a bare repository whose `HEAD`, `packed-refs` and loose objects Git reads.
A hand-written negotiation with one common commit is acknowledged as common, finishes with the final
acknowledgment, and receives a delta-compressed pack holding exactly the objects `git rev-list --objects
first..tip` names; a round without `done` answers common, ready and NAK; an unknown have earns a bare NAK.
The version 2 advertisement, `ls-refs` with `HEAD` and its target, a `fetch` with `done` answering the
packfile section on sideband one with the same objects, a `fetch` without `done` answering acknowledgments,
`ready` and the pack together, and a `deepen` refused by name. Git clones and pulls over version 2 with its
packets traced, naming both commands and its haves. Housekeeping is refused without the `api` scope, packs
every object, leaves one pack and no loose files, passes `git fsck` and `git verify-pack` with blobs stored
as deltas, and writes an index byte-identical to the one `git index-pack --strict` writes for the same pack.
The API, a clone and a fetch read from the pack; a push afterwards leaves exactly its new objects loose
beside it. A push of 1,100 files is packed before it is answered and the repository opens again from the
pack alone. Every check that existed before, including the interrupted-publication checks, the four-gibibyte
transport check and the LFS and fork checks, still passes against the new layout.

Not verified: the conversion of the live deployment's repositories, which happens when it is upgraded; a
NativeAOT publish of this build; performance of a repack or a negotiation walk on a repository near the
4 GiB ceiling on the legacy host.
