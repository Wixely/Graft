# 0057: Git LFS file locking

- Date: 2026-11-18
- Status: Accepted, implemented
- Source: Every push to a Graft remote with git-lfs installed printed "Remote … does not support the Git LFS locking
  API". On 2026-11-18 the project owner asked for locking to be implemented.
- Builds on: [ADR 0026](0026-large-files.md) (the LFS batch API and basic transfer),
  [ADR 0049](0049-ssh-large-files-deploy-keys-administered-keys.md) (LFS over SSH), and the project roles of
  [the requirements](../requirements.md)
- Review trigger: a request for locks shown or managed in the browser or over `/api/v4`; for locks per branch
  (`ref`); for locking a folder or a pattern; a client that relies on GitLab's checks of locks in merge requests

## What was missing

Locking is how a team stops two people editing the same file that cannot be merged, such as a model, a scene or a
spreadsheet. git-lfs asks the server for it, checks it before every push, and warned on every push because Graft
had no locking API. A team using Graft had no way to say "I am editing this".

## Decision: the Git LFS locking API, with GitLab's permissions

The four routes git-lfs uses, beside the batch API under `/<namespace>/<project>.git/info/lfs/`:
- `POST locks` takes a lock on a path for the caller.
- `GET locks` lists locks, by `path` or `id`, with `cursor` and `limit`.
- `POST locks/verify` lists locks split into the caller's own and other people's, which git-lfs checks before a push.
- `POST locks/:id/unlock` releases one.

They accept the same credentials as the rest of LFS: a token over HTTPS, or the ticket `git-lfs-authenticate` hands
out over SSH.

- **Listing** needs what a fetch needs.
- **Taking, checking or releasing** needs what a push needs (Developer).
- **A lock belongs to a person.** A credential that names nobody, such as a deploy key, can check locks but cannot
  take or release one.
- **Somebody else's lock** is released only with `--force`, and only by someone who may manage the project
  (Maintainer). That release is written to the project's audit trail; releasing your own is routine and is not.
- **A path** is relative to the repository root, has no `.` or `..` segments, and may name a file that does not exist
  yet. A taken path answers `409` with the existing lock, so the client can say who holds it.
- **Limits:** a project holds at most 10,000 locks, and a path is at most 4,096 characters.

A lock covers the path in every branch. git-lfs sends a `ref` with its requests, and GitLab ignores it too.

## Decision: the forge refuses a push that changes somebody else's locked file

git-lfs checks locks before it pushes, but a client can skip the check with `--no-verify`, or have no git-lfs at
all. The forge therefore checks every push, over HTTPS, SSH and the chunked transfer alike.

- A branch whose new commits change a path somebody else holds is refused, alone, with "'<path>' is locked by
  <holder>"; when more than one path is locked, the reason also says how many more.
- "New commits" means the commits the push brings, each compared with its parent.
- A merge commit is not compared: what it brings in came through commits of its own, which are checked if they are
  new and were allowed when they arrived if not.
- Tags are not checked, since a tag changes no file.
- The check runs only when somebody other than the pusher holds a lock, so a project without locks walks nothing.

## Not done

- Locks in the browser: there is no list of a project's locks, and no unlock button.
- Locks over `/api/v4`.
- Any check of locks when a merge request is merged in the browser.

## What was verified

The Git acceptance suite, Debug build on Windows, with git-lfs 3.7.1:
- `git lfs lock`, `git lfs locks` and `git lfs unlock` against the forge;
- a second lock on the same path is refused;
- a stranger is asked for credentials;
- a colleague asking for a held path gets `409` naming the holder;
- a path outside the repository is refused;
- `verify` sorts the caller's locks from other people's;
- a colleague's `git push --no-verify` that changes the locked file is refused, naming the path and the holder, and
  goes through once the lock is released;
- a Developer cannot release, or force-release, somebody else's lock;
- a Maintainer must say `--force`, may then release it, and the release is in the project's audit trail.
