# 0052: Release destinations, and release files on disk

- Date: 2026-11-14
- Status: Accepted, implemented
- Source: A request on 2026-11-14 to publish Graft releases to an application store whose code is private, against
  a written HTTP contract supplied with the request and nothing else: "Do not access [its] database, filesystem or
  MCP server." Settled in the same conversation: the store is not named in this repository; a publication is
  started by "maintainer or automated action from a runner"; and the 32 MiB bound on a release file is raised "in
  this work"
- Builds on: [ADR 0022](0022-releases.md) (releases, and who may publish one),
  [ADR 0027](0027-packages-and-registry.md) (files beside the repository),
  [ADR 0042](0042-import-by-address-and-mirrors.md) (an operator's policy for addresses the server calls),
  [ADR 0012](0012-recoverable-tokens.md) (what the key ring protects against)
- Supersedes: the decision in ADR 0022 that a release file's bytes live in the database, bounded at 32 MiB
- Review trigger: a second kind of destination; a request to publish automatically when a release is created; a
  runner job that needs a credential of its own instead of a person's token; a release file that has to pass a
  proxy that caps a request's size; a request for an agent to publish; a store whose expired upload sessions block
  a retry

## What was asked for, and what was there

The request describes a store's API: ask what it accepts, open an upload session per file, send the file in
hashed chunks, finalize, wait for the store to validate it, and then, separately, publish the accepted build on a
channel. It asked for this to be fitted to "Graft's existing release-provider abstraction", and for mismatches to
be reported before anything was built. They were:

- **There was no such abstraction.** Graft had releases, and two things that call out (webhooks and mirrors), but
  nothing that took a release anywhere. Decision 1 adds the smallest one.
- **A release has no version, platform, architecture or package name.** Decision 4.
- **A release file was at most 32 MiB**, in a database row. Decision 7.
- **Graft has no secret references.** A secret is either never stored or stored under the key ring. Decision 3.
- **ADR 0022 withholds publishing a release from agents.** Kept: decision 6.
- **Tests are acceptance programs, not a unit-test framework.** The fake store lives in them: see the end.

## Decision 1: a destination is a project's, and a publisher is an interface with one implementation

A release destination is a named place a project's releases can be published to: an endpoint, an application, a
key, a channel, and whether it may publish to that channel at all. A project has up to ten, each with its own
endpoint, application and credentials.

`IReleasePublisher` in `Graft.Core` is what takes a release to a destination, and `AppStorePublisher` is the one
implementation, of the kind `app-store-v1`. The interface is one method. A second kind of destination is where a
second implementation would go; nothing was built for one that does not exist.

The store is not named anywhere in this repository. Its contract is described by what it does.

## Decision 2: the operator decides whether the server publishes anywhere

`--release-publishing off|public|any`, off by default, as importing by address is and for the same reason: a
Maintainer names an address and the server calls it. `public` reaches only https endpoints whose every address is
on the public internet; `any` also reaches the server's own network and accepts plain http, for a store beside the
forge. The check is made on the connection, after the name is resolved, with no proxy.

With the switch off, a destination cannot be added and nothing can be published, but a destination can still be
removed: that is how a stored key is got rid of.

## Decision 3: the endpoint and the key are protected, write-only, and never said

The contract calls the store's API address opaque and asks that it never be logged, so the endpoint is treated as
the key is. Both are protected under the server's data-protection key ring, with a purpose of their own, before
they reach the database; the row also keeps the endpoint's host in the clear, so a list can say where a
destination points. Neither is returned by any route. A destination is changed without entering either again.

This protects against a copied database, not a copied machine: the key ring sits beside the database
(ADR 0012). A key ring that is replaced makes the stored values unreadable, and a publication then says to enter
them again.

Nothing Graft says about a publication contains the key, a transfer token, the endpoint's path or any upload
address the store issued. One class removes them from every message on its way out, including messages the store
wrote and messages Graft wrote itself. The server log holds a publication's number and whether it succeeded.
The audit trail holds the host, the channel and the outcome.

## Decision 4: what a release file is declared as

- **Version.** The tag, without the `v` that conventionally precedes a number: `v1.4.0` is `1.4.0`.
- **Notes.** The release's description. Longer than the store's 8,000 characters fails the publication before
  anything is sent; Graft does not truncate what somebody wrote.
- **Format.** `apk` for a file named `.apk` whose platform is Android, `file` for everything else.
- **Platform and architecture.** From the destination's file rules: one to a line, a file name pattern with `*`
  and `?`, then `skip`, or a platform, an architecture, and `main`. The first rule that matches decides. A file no
  rule names is declared from its own name (`-win-`, `linux`, `.dmg`, `x64`, `arm64` and so on), which is a guess,
  and a dry run shows the guess before anything is uploaded.
- **Main.** The file whose build the channel should report afterwards. It is published last.
- **Package name.** The destination's, required once a release carries an Android package.
- **Idempotency key.** `graft-` and a SHA-256 over the project's storage identifier, the tag, the application,
  the file's name and the file's SHA-256. The same upload asked for twice is the same upload; anything changing is
  a different one.

## Decision 5: a publication is queued, and never repeats a decision a person should make

Publishing sends files that may be large to a server that validates them at its own pace, so the request that
asks for a publication is answered at once with 202 and an address to read the outcome from, and a worker does
the rest, one publication at a time. The queue is the database: a queued publication survives a restart and is
taken up; one that was running is marked interrupted, because the store keeps what it was sent.

In order:

1. Checks that need no network: the channel against whether production promotion is allowed for the destination,
   the notes' length, file names, a package name for an Android package.
2. The store's capabilities. A file too large, or of a format or platform the store does not list, fails here,
   before any upload session exists. A dry run stops after this step.
3. One upload session per file. Chunks go in order, to the path the store issued, on the store's own origin only:
   a path that resolves to another scheme, host or port, or carries credentials, is refused before anything is
   sent. A redirect from anywhere is a failure. A chunk is retried a few times on a dropped connection, a timeout,
   a 429 or a 5xx; a 409 for a chunk stops the upload.
4. Finalize, then wait for validation. A file that is rejected, or a session that expired or was aborted, stops
   the release: later files are not attempted and nothing is published.
5. Only when every file is accepted, and only for a channel: read the application's revision immediately before
   each publication, publish the build with the confirmation the contract asks for, the main file last. A
   revision conflict stops there and is reported. Graft does not try again with a newer revision, because
   something else changed the application and a person should look.
6. Read back: every build listed, published, on the channel, for the application and the version; the channel on
   the main file's build. An Android package whose stored hash differs from the one sent is expected, because a
   store that signs a package changes it, and both hashes are recorded. Any other file whose hash differs fails
   the read-back.

Asking again is safe. A file the store already accepted is found by its key and not sent twice; an interrupted
upload carries on from what the store holds; a build already on the channel is not published again.

## Decision 6: a Maintainer, or a script with a Maintainer's token

Everything about destinations and publications needs the permission to manage the project, reading included: a
destination holds a credential and a publication spends it. A Developer who can attach a file to a release cannot
send it anywhere.

A publication is started from the release's page, or by `POST` to the token route. That route is what a runner
job calls. Graft's jobs have no credential of their own for the HTTP API and no stored secret variables, so the
job uses a token its runner's environment provides; giving jobs their own is a review trigger.

There is no MCP tool for any of this. ADR 0022 said a release speaks for the project and is for a person to
publish, and sending it to a store is the same act, further.

## Decision 7: a release file is on disk, up to 2 GiB

A release file is now written beside the repository under `release-files/`, hashed as it arrives, under a name
the server generated, and its row names it (migration 31). The bound is 2 GiB, what a package file may be. A
build worth publishing to a store is routinely larger than 32 MiB, and a row that size was already the wrong
place for one.

What ADR 0022 wanted from the database is given up knowingly: a backup of the database alone no longer carries
release files, and the repositories directory has to be backed up with it, as it already had to be for packages,
large files and page artifacts.

Files stored before this stay where they are. A row with no name on disk is read from its bytes, and nothing is
migrated, because a migration that copied every blob out of the database could fail halfway through.

## The API

Two families from one loop, as page artifacts have: `/api/projects/<id>` for the browser's session and
`/api/graft/projects/<id>` for a token. Under Graft's own prefix because GitLab has nothing like it.

| Route | What it does |
|---|---|
| `GET …/release-destinations` | The destinations, and whether this server publishes. |
| `PUT …/release-destinations/<name>` (`POST` in the browser family) | Adds or changes one. `endpoint` and `api_key` are required for a new one and optional afterwards. |
| `DELETE …/release-destinations/<name>` | Removes one, with what it stored. |
| `POST …/releases/<tag>/publications` | `{"destination": "<name>", "dry_run": false}`. Answers 202 with `Location`. |
| `GET …/releases/<tag>/publications` | The release's publications, newest first. |
| `GET …/release-publications/<id>` | One publication: `queued`, `running`, `succeeded` or `failed`, a summary, and each file's state, build and hashes. |

## What is not done

- **Publishing automatically when a release is created.** Asked for as an action somebody takes, and built as one.
- **A credential for a runner job.** See decision 6.
- **A release file above a proxy's request cap, by token.** The token route takes the file as one request body. A
  file above the cap goes through the browser's resumable upload, or from inside the network.
- **Aborting an upload.** The contract allows it. Graft leaves an unfinished session to be carried on or to expire.
- **An expired session.** If the store keeps an expired session under its key, the same file cannot be sent again
  under that key. It is reported as it is; whether the store does this is not known from the contract.
- **Cancelling a running publication.**
- **Moving old release files out of the database.**

## What was verified

`tests/Graft.Acceptance` (`ReleasePublishingChecks`), against `tests/Graft.TestSupport/FakeAppStore.cs`, a
loopback server that keeps sessions, chunks, builds, a revision and channels as the contract describes and can
be told to misbehave: the rules for names, endpoints, file rules, declarations, versions, keys and chunk
addresses; a dry run and each capability refusal opening no upload; a generic file and an Android package end to
end; a four-file release with a main file; a rejected file stopping a release with nothing published; an
interrupted upload carried on without sending a held chunk again; a dropped connection, a lost answer and a busy
store retried, with a held chunk accepted byte for byte; an expired session; redirects from the API and from a
chunk address, with a client that does and does not follow them; an upload address on another origin; an
idempotency conflict; a revision conflict stopping after one attempt; a wrong key, a missing scope, an unknown
application and an unreachable store; and every message produced across all of it searched for the key, a
transfer token, the endpoint's path and an upload address.

Writing that suite found that a client which follows a redirect rewrites the request it was given, so comparing
the answer's address with the request's own detected nothing. The address asked for is now kept apart.

`tests/Graft.Git.Acceptance` (`CheckReleasePublishing`), through the server: release files on disk, replaced
and removed there, and a 40 MiB one stored and downloaded whole; the switch off and on; a destination refused
for each malformed setting, for a Developer and for nobody; the key and the endpoint's path absent from every
response, from the server's log, from the audit trail and from the database file; a dry run; a publication to an
upload-only destination; the destination changed without its secrets and the same release published to stable
without a second upload; a rejected file; the history, the audit trail, and removal.

Not verified: any real store. The contract is all there was, and the fake is this repository's reading of it.
Also not verified: the browser panels by hand, and the NativeAOT publish of this change.
