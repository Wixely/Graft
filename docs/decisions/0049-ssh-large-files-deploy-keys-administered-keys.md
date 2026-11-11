# 0049: Large files over SSH, deploy keys, and keys an administrator manages

- Date: 2026-11-11
- Status: Accepted, implemented
- Source: A request on 2026-11-11 to build what [ADR 0031](0031-git-over-ssh.md) listed as not done: "LFS over an SSH
  remote, deploy keys, and keys managed by an administrator"
- Builds on: [ADR 0031](0031-git-over-ssh.md) (Git over SSH), [ADR 0026](0026-large-files.md) (large files),
  [ADR 0041](0041-labcoat-routes-retry-cancel-directory.md) (the user directory is administrators' only)
- Supersedes: the first three items of ADR 0031's "What is not done"
- Review trigger: a client that needs `git-lfs-transfer` (the pure SSH large-file protocol) rather than falling back to
  `git-lfs-authenticate`; a request for a deploy key shared across projects as one record, or for deploy tokens; a
  request to let anybody read another account's public keys, as GitLab does; signing keys

## Decision 1: large files over SSH are moved over HTTPS, with a credential the SSH server issues

git-lfs, given an SSH remote, asks the SSH server where the HTTPS large-file API is and for a credential to use there.
It first tries `git-lfs-transfer`, the newer protocol that moves the objects over SSH itself; Graft refuses that by
name, and git-lfs falls back to `git-lfs-authenticate`, which Graft answers. The answer is the repository's own
`…/<project>.git/info/lfs` address on the forge's HTTPS origin and an `Authorization` header carrying a ticket, valid
for 30 minutes.

The ticket names the SSH key that asked, one project and one operation, and is signed with HMAC-SHA256 under a key that
lives only in the forge process. Nothing is stored, and a restart retires every outstanding ticket; git-lfs then asks
again. It is accepted on the large-file routes only (batch, object upload, object download): it opens neither the
repository's Git transport nor any API. A download ticket cannot upload, and a ticket for one project opens no other.

The ticket is not trusted as a decision. Each request resolves the key again and authorises it again, exactly as an
SSH command would be, so a removed key, a disabled account or a role taken away stops the ticket at once.

`git-lfs-transfer` itself is not implemented. It would move objects over the SSH channel, which matters only where
HTTPS is unreachable while SSH is not; behind Cloudflare it is the other way round, and git-remote-graft (ADR 0043) is
the answer to the size limit there.

## Decision 2: a deploy key belongs to a project, not to an account

A deploy key is an SSH key a project admits so that a machine can read its repository, and push to it only if the
project says so. It belongs to no account: it authenticates as itself, reaches only the projects that admit it, and
only Git (fetch, push and `git-lfs-authenticate`). A Maintainer manages a project's deploy keys, because a deploy key
is a door into the repository that no membership list shows. Adding, changing whether it may push, and removing are
audited against the project, so the project's own audit view shows them.

A key is exactly one thing. An account's key cannot be added as a deploy key, and a deploy key cannot be registered to
an account; the same deploy key may be admitted by several projects, one record each. GitLab keeps one key record
shared by projects; Graft keeps one per project, so `POST …/deploy_keys/:id/enable` copies the key into the second
project, read only, and the caller must be able to manage both projects or be an administrator. The ids therefore
differ between projects, which is a recorded deviation.

A push by a deploy key is attributed to whoever added the key, as GitLab does, or to the project's owner once that
account is gone or disabled, so webhooks, pipelines and the activity feed always name a person.

Routes: GitLab's `GET`/`POST /projects/:id/deploy_keys`, `GET`/`PUT`/`DELETE /projects/:id/deploy_keys/:key_id`,
`POST …/:key_id/enable`, and `GET /deploy_keys` for administrators; the browser family under
`/api/projects/:id/deploy-keys` and a panel on the project's settings tab, shown only when SSH is on. A token needs
`api` to read them as well as to change them, as for a project's webhooks: they are project settings, and Graft never
lets a read-only token act as a Maintainer. GitLab lets `read_api` read them; that is a recorded deviation. Migration 28.

## Decision 3: an administrator may manage another account's keys, and only an administrator

For a person who cannot add their own key, or a service account nobody signs in as, an administrator adds a key to
the account; for a lost laptop, an administrator removes one. The audit entry names the administrator. Routes:
GitLab's `GET`/`POST /users/:id/keys`, `GET`/`DELETE /users/:id/keys/:key_id`, and `GET /keys/:id` and
`GET /keys?fingerprint=` to answer "whose key is this" from a log line; the browser's `/api/users/:id/keys` and a panel
on the Users page.

GitLab lets anybody read `/users/:id/keys`. Graft lets the account itself and administrators read it, for the reason
ADR 0041 made the user directory administrators' only: a list of who has accounts, and their keys, is not for every
member of the server.

## Decision 4: SSH holds the repository per operation, not per connection

ADR 0031 had an SSH command hold the repository's lock for its whole connection. Testing large files over SSH found the
cost: `git push` keeps its connection open while it runs the pre-push hook, and git-lfs's hook opens a second connection
to list the remote's branches, which then waited a minute for the lock the first one held, and the push stalled. Any
push from a repository using git-lfs would have done this.

The lock is now held as an HTTP request holds it: for the advertisement, for each fetch request, and for applying a push,
and not while the client is thinking or running hooks. Releasing it between a push's advertisement and its update is
safe because the update checks each reference's old value itself, so a change made in between is refused, not
overwritten. A fetch's answer is staged whole under the lock and sent without it. Supersedes ADR 0031's paragraph on
the repository lock; a client still waits up to a minute for an operation's turn.

## What is not done

`git-lfs-transfer`. Deploy tokens (an HTTPS credential for a machine). A deploy key shared as one record across
projects. Signing keys. Reading another account's keys without being an administrator.

## What was verified

By the Git acceptance suite on 2026-11-11, 1,509 assertions passing with nothing skipped, with OpenSSH for Windows 9.5 and
git-lfs 3.7.1 as the clients.

Administrator keys: another account's keys are hidden from a non-administrator and listed for the account and an
administrator; only an administrator adds one, which then authenticates for that account; `/keys?fingerprint=` names
the owner and refuses anybody else; removal stops the key at once. Deploy keys: `read_api` cannot add or list one, an
account that cannot manage the project cannot add one, an account's key is refused as a deploy key and a deploy key as
an account's key; a read-only deploy key clones, cannot push, and reaches no other project; once allowed it pushes;
it is greeted as a deploy key; its use is recorded; an administrator lists every deploy key; removal stops it.
Large files: a push to an SSH remote with git-lfs stores the object beside the repository (after the lock change in
Decision 4; before it, the push stalled), and a deploy key fetches it with `git lfs pull`. By hand, the ticket
downloads the object, cannot upload with a download ticket, opens neither the repository's Git transport nor another
project's objects, is refused when altered, and an account with no role gets the same refusal as for Git. In headless Edge against the browser fixture, at phone width:
a deploy key added through the settings panel, listed read only, then allowed to push; an administrator adding and
removing another account's key on the Users page; no console errors.
