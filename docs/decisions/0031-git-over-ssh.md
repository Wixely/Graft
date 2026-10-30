# 0031: Git over SSH, served by the forge itself

- Date: 2026-10-30
- Status: Accepted, implemented
- Source: The user, on 2026-10-30, ordering the remaining large work: SSH first, then optional email, groups, and the project-scoped audit view. The backlog had carried SSH since 2026-10-22 as "the most requested thing on this list and the most work"
- Review trigger: A client that cannot connect because of the algorithm set; a request for `git-lfs-authenticate` so LFS works over an SSH remote; a request for deploy keys, signing keys or administrator-managed keys; evidence that a protocol version 0 fetch over SSH is needed; the Linux 3.10.108 acceptance run, which has not exercised this

## What was missing

Graft served Git over HTTPS only, where the password is a personal access token. Every other forge offers
SSH, and for a self-hosted one it is often the only transport a person's tooling is set up for: a key in an
agent, no token to paste, no credential helper to clear.

## Decision: Graft is the SSH server

The alternative was the one GitLab takes, standing behind the system's OpenSSH daemon with an
`AuthorizedKeysCommand` and a shell that forwards to the forge. That needs a daemon the operator may not
control, a second process the forge has to reach, and configuration outside Graft. It would not be a single
binary, and on the required Linux 3.10 host the daemon's version and configurability are unknown. So Graft
listens itself, in `src/Graft.Ssh`, a project with no dependencies that implements RFC 4253, 4252 and 4254 as
far as Git needs them and no further: public-key authentication, one session channel per connection, the
`env` and `exec` requests, flow control, rekeying when the client asks. No passwords, no shell, no port
forwarding, no subsystems, no agent forwarding.

The algorithm set is small by decision, and every algorithm in it is the platform's own cryptography except
one. Key exchange is `ecdh-sha2-nistp256`; the host key is `ecdsa-sha2-nistp256`, created on first start and
kept as PKCS#8 PEM as `ssh_host_ecdsa_key` beside the database, which is what an operator already backs up
and moves; ciphers are `aes256-gcm@openssh.com`, `aes128-gcm@openssh.com`, `aes256-ctr` and `aes128-ctr`,
with `hmac-sha2-256` for the latter two; client keys are `ssh-ed25519`, `ecdsa-sha2-nistp256/384/521` and
`ssh-rsa` of 2048 bits or more, signing with `rsa-sha2-256` or `rsa-sha2-512`. The server advertises
`server-sig-algs` so a client knows that. RSA with SHA-1, DSA, `curve25519-sha256` and ChaCha20-Poly1305
are not offered: the first two because OpenSSH itself has retired them, the last two because .NET 10 has no
X25519 or ChaCha20-Poly1305 primitive and this is not the place to write one.

The exception is Ed25519 signature verification, which .NET 10 does not expose and which most keys made in
the last decade are. It is implemented in `Ed25519.cs` from RFC 8032 on `BigInteger`, verification only, no
secret ever held. It is checked in the acceptance suite against signatures OpenSSH produced and against a
signature with a bit flipped, and the whole transport is driven by the real OpenSSH client with keys of all
three families. The risk of a hand-written verifier is that it accepts what it should not; the check that
answers it is a real key signing in and a flipped signature failing, which the suite does.

## Decision: a key is the account, and only for Git

A registered key authenticates as its owner with the owner's full role, as a browser session does. A key
has no scopes, because the only thing it can reach is Git: the SSH server runs `git-upload-pack` and
`git-receive-pack` and refuses any other command by name. The username in the SSH connection is ignored, as
GitLab ignores it; `git@` is what clone URLs say. A key is unique across the instance, because one that two
accounts both held would name two people; an expiry is optional; the last use is recorded only when a
signature has verified, because a query by a client that never signs is not a use.

Keys are managed by their owner and nobody else, through GitLab's routes `GET`/`POST /api/v4/user/keys`,
`GET`/`DELETE /api/v4/user/keys/:id`, and through the browser family `/api/keys` with a tab on the profile
page. Reading needs `read_api`; adding and removing need `api`, as a token's do, because a key is a credential
and a read-only token must not mint one. Every change is audited as `add-ssh-key` or `delete-ssh-key`. The
agent surface does not manage keys, for the reason [ADR 0011](0011-agent-parity.md) withholds token issue.

## Decision: fetches are protocol version 2, pushes are what they are

Over a stateful connection, Git's protocol version 0 fetch is a multi-round conversation: the client sends
haves in batches and waits for acknowledgements between them. The engine implements the stateless form of
that conversation for HTTP, where every round carries the whole state, and the two are not the same code.
Version 2 is the same conversation over either transport, one command per request, so over SSH Graft serves
fetches in version 2 and tells a client that will not speak it so by name. Every Git since 2.26 (2020) asks
for version 2 by default and sends `GIT_PROTOCOL` through the connection's environment, which the server
accepts; an older client can say `-c protocol.version=2`. A push has no version 2 and is served as it always
was: the engine reads the commands and the pack from the channel, finding the pack's end by walking its
objects, and answers the report. A push over SSH fires the same webhooks as one over HTTPS, with `via` saying
`ssh`.

The repository lock is the one the HTTP transport takes. HTTP answers a held lock with `429` and lets the
client retry; SSH waits up to a minute and then says the repository is busy, because an SSH client does not
retry.

## Decision: off until named, on its own port

`--ssh-port <n>` starts the listener; without it there is no SSH and project responses carry no
`ssh_url_to_repo`. There is no default port, because 22 needs privileges or a redirect on most hosts and
2222 is a convention, not a standard. `--ssh-host <name>` sets the host clone URLs name, for a server whose
public name is not the one requests arrive by; otherwise the request's host is used. Port zero is accepted
and means a free port, which only a test wants. The server prints where it listens and the host key's
fingerprint at startup, so a person connecting for the first time has somewhere to check it against.

## What is not done

LFS over an SSH remote: `git-lfs` asks the SSH server for `git-lfs-authenticate` to learn the HTTPS endpoint
and a credential, and Graft refuses the command, so git-lfs falls back to guessing an HTTPS address with no
credential. A repository with large files is cloned over HTTPS, or has `lfs.url` set to it. Deploy keys,
signing keys, an administrator listing or removing another account's keys, and `GET /api/v4/keys` by
fingerprint. Protocol version 0 fetches, refused by name. The Linux 3.10.108 host has not run this; the
static-musl build has not been published with it.

## What was verified

By the Git acceptance suite with OpenSSH 10.5 as the client. Registration: `read_api` cannot add, `api` can,
the fingerprint matches `ssh-keygen -lf`, a key registered to one account cannot be registered to another, a
malformed key and a DSA key are refused, RSA and ECDSA keys are accepted, the list reads with `read_api`.
The host key `ssh-keyscan` sees has the fingerprint the server printed. A push with an Ed25519 key, a clone
with an RSA key, a second push and a fetch with haves with an ECDSA key, a tag, `ls-remote`, and the key's
last use recorded afterwards. An unregistered key refused at authentication; a registered key of an account
with no role, and a missing repository, given one refusal; a client at protocol version 0 told so by name; a
command that is not Git refused by name; an interactive login greeted and given no shell; a removed key
refused at once while the account's other keys still work. Before the suite, the transport alone was driven
with the client across both cipher families, a 40 MB stream with a rekey every megabyte, standard input,
exit status and `GIT_PROTOCOL`. The browser tab and the clone menu's SSH section compile against the same
routes and were not driven by a browser.
