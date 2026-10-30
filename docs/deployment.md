# Deployment runbook

Recorded: 2026-09-19. Owner: Operator. Review when hosting, certificates or publish settings change.

Graft currently supports one server process and one writable data directory. The server needs absolute paths for its SQLite database and repository root. Every mode requires HTTPS and accepts an optional public origin used by browser origin checks:

```text
Graft.Server --database <path> --repositories <directory> [--listen <https URI>] [--origin <https origin>] [--certificate <PKCS#12 path>] [--certificate-password-file <UTF-8 path>]
```

Without `--listen`, interactive mode chooses a random loopback port. A configured listener must use a literal IP address, HTTPS and an explicit nonzero port. Loopback development may omit `--certificate` and use the generated short-lived certificate. A non-loopback listener is rejected unless an operator PKCS#12 certificate and explicit public `--origin` are supplied.

The certificate must contain a currently valid private key. If it has an enhanced-key-usage extension, that extension must allow TLS server authentication. Its subject alternative name must match the `--origin` hostname; common-name fallback is deliberately disabled. Store the password in a separate UTF-8 file containing one line. A final CRLF or LF is removed. Do not place the password in arguments, environment variables, repository files or service definitions. Restrict both files to the service identity, with the PKCS#12 file treated as a private key.

Before starting a fresh deployment, use the matching `Graft.Admin` publication to create the first administrator while the server is stopped:

```text
Graft.Admin bootstrap --database <path> --repositories <directory> --username <name> --password-file <protected UTF-8 path>
```

Bootstrap is one-use and holds the same maintenance locks as backup and restore. Remove the password file after verifying sign-in. See [operator operations](operations.md) for the Windows development form and security boundary.

## Windows

### Large repositories

The repository quota is 4 GiB of uncompressed object history; individual objects are
limited to 64 MiB. Git request bodies may reach 5 GiB, with a two-hour transfer deadline.
A reverse proxy must permit these bodies and durations. Raising only a proxy limit does
not change Graft's compiled limits. See [ADR 0018](decisions/0018-repository-capacity.md).

Allow temporary disk space for a 5 GiB incoming pack and up to 8 GiB of expanded pack
work per concurrent push, in addition to repository storage. Fetches and archives also
spool to disk. On Windows, use a service-account temporary directory on a suitable disk;
configure its `TEMP`/`TMP` environment before starting the process if needed. The container
image and systemd template set `TMPDIR` to a `temporary` subdirectory of the writable data
directory, avoiding a small or memory-backed `/tmp`. Graft creates staging directories as
needed. After an abrupt termination, inspect and remove abandoned `graft-transfer-*`
directories only while the server is stopped.

The container's default memory ceiling is now 2 GiB, configurable with `GRAFT_MEMORY_LIMIT`.
This is operational headroom, not a proven bound for one million objects or concurrent
large transfers. Existing `.env` files retain their old explicit limits and must be reviewed.
The revised container/systemd settings have not been exercised on Linux for this change.

### Publish and run

Publish a self-contained NativeAOT server, then run it interactively:

```powershell
dotnet publish src\Graft.Server\Graft.Server.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -o artifacts\mvp08\server-win-x64
& .\artifacts\mvp08\server-win-x64\Graft.Server.exe --database C:\ProgramData\Graft\graft.db --repositories C:\ProgramData\Graft\repositories --listen https://0.0.0.0:7443 --origin https://graft.example.internal:7443 --certificate C:\ProgramData\Graft\secrets\tls.pfx --certificate-password-file C:\ProgramData\Graft\secrets\tls-password
```

An elevated PowerShell session may register the same executable with the Service Control Manager. Quote every absolute path in `binPath`; grant the service identity read/execute access to the application directory and modify access to the data directory.

```powershell
sc.exe create Graft start= auto binPath= '"C:\Program Files\Graft\Graft.Server.exe" --database "C:\ProgramData\Graft\graft.db" --repositories "C:\ProgramData\Graft\repositories" --listen https://0.0.0.0:7443 --origin https://graft.example.internal:7443 --certificate "C:\ProgramData\Graft\secrets\tls.pfx" --certificate-password-file "C:\ProgramData\Graft\secrets\tls-password"'
sc.exe start Graft
sc.exe query Graft
```

Stop Graft before backup, restore or upgrade. Validate a new binary interactively, replace the application files while stopped, start the service, then check `https://127.0.0.1:7443/healthz`. Retain the previous executable and a verified backup for rollback.

## Linux interactive and systemd

`deploy/linux/build-static-native.ps1` builds and inspects independent static-musl server and admin executables in an isolated Alpine chroot under an existing WSL distribution. It verifies that neither ELF contains an interpreter nor a `NEEDED` library entry. It does not install WSL or deploy to another host.

```powershell
.\deploy\linux\build-static-native.ps1 -Distribution <existing-wsl-distribution>
```

Copy the executable to `/opt/graft/Graft.Server`, create a dedicated unprivileged account and `/var/lib/graft`, then replace every `@...@` placeholder in `deploy/systemd/graft.service`, including the listen URI, public origin and certificate paths readable only by that account. Install the rendered file as `/etc/systemd/system/graft.service` and run:

```text
systemctl daemon-reload
systemctl enable --now graft
systemctl show graft --property=ActiveState --property=SubState --property=Type
curl --insecure https://127.0.0.1:7443/healthz
```

The unit uses `Type=notify`, restarts on failure, restricts filesystem writes to the data directory and applies basic service hardening. The static NativeAOT server's managed notification path passes the full temporary-unit lifecycle recorded in [static systemd evidence](validation/2026-09-19-static-systemd.md). Stop the unit before maintenance operations.

## Optional container

The container path uses the self-contained `linux-x64` publication at `artifacts/mvp08/server-linux-x64`. It goes into one image, which runs as UID/GID 1654 with a read-only root filesystem and a writable named data volume.

Copy `deploy/container/.env.example` to `deploy/container/.env` and fill it in first. It documents every variable.

```powershell
dotnet publish src/Graft.Server/Graft.Server.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/mvp08/server-linux-x64
podman compose --file deploy/container/compose.yaml build
podman compose --file deploy/container/compose.yaml up --detach
```

The service names the image it runs, through `GRAFT_IMAGE`, defaulting to `graft:local`. That matters more than it looks. A compose service with only a `build:` section and no `image:` makes compose invent a name from the project directory and the service, so an image you built and tagged yourself is never referenced, and `up` runs whatever compose built first regardless of what changed underneath. Naming it means `podman build --tag graft:local .` and `compose build` produce the same thing, and either is what runs.

`up` still does not rebuild on its own. After republishing a binary, run `compose build` again, or `up --build`. Where an image looks current and the container disagrees, `compose build --no-cache` settles it.

Set `GRAFT_IMAGE` to a registry path to push a built image and pull it elsewhere.

Compose mounts the certificate and password as read-only secrets. **Those files must be readable by UID 1654.** Compose secrets outside swarm are host bind mounts, so the file's own ownership and mode are what the container sees, and a root-only `0600` key produces a startup failure reading `Access to the path '/run/secrets/graft-tls.pfx' is denied`.

The port is bound to host loopback. The image uses the .NET 10 Ubuntu runtime-deps base and is separate from the static-musl legacy-host artifact. Pinning the base by digest remains release work.

### Optional MCP endpoint

The forge hosts its own Model Context Protocol endpoint, at `/mcp` on the same port and certificate as everything else. It is off unless the operator asks for it: `--mcp true` on the command line, or in the container deployment

```sh
GRAFT_MCP=true
```

in `.env`, then `up --detach` again. Turning it on is a restart rather than a rebuild, and nothing else about the deployment changes: no second port, no second service, no second certificate. With it off, a request for `/mcp` is answered with a 404 that names the flag.

Once on, what agents may do is decided in the browser, by an administrator, under **Administration, Agent access**. Three switches live there and each change applies to the next request with nothing restarted:

| Switch | Default | What it does |
| --- | --- | --- |
| Serve agents | on | Pauses or resumes the endpoint. Off, callers get a 404 until it is turned back on. |
| Allow writes | off | Lets agents create projects, change visibility, and publish or withdraw pages, within what each caller's token permits. Off, those tools are refused and the refusal names this page. |
| Serve callers with no token | off | Serves requests that carry no credential, as strangers who see only public projects and published pages. Anyone who can reach the port can then talk to it. |

The endpoint holds no credential. Every caller sends their own Graft personal access token as `PRIVATE-TOKEN` or `Authorization: Bearer`, so two callers get exactly the access their own tokens carry, and a request without one is refused `401` before the protocol is spoken unless the third switch is on. Tokens are created under Profile and tokens; reading needs `read_api`, the mutating tools need `api`.

The forge prints its agent posture on startup beside its other lines, so what a deployment is actually doing is visible in its logs; the switches can change afterwards, which is their point:

```text
MCP: https://graft.example.internal:8443/mcp; enabled=true; credentials=required; writes=refused
```

For a client that launches its MCP server locally over stdio, `Graft.Mcp` remains a separate executable; see [build-and-test](build-and-test.md). It is not in the container image. See [ADR 0009](decisions/0009-mcp-in-the-forge.md) for why the endpoint moved into the forge and what it keeps from [ADR 0008](decisions/0008-mcp-layer.md).

### Optional recoverable token secrets

A personal access token's secret normally exists once: the database keeps only a digest, so a lost token
means issuing a new one. An operator can change that:

```sh
GRAFT_RECOVERABLE_TOKENS=true
```

or `--recoverable-tokens true`. Each token issued afterwards has its secret kept, encrypted with the
data-protection key ring, and its owner can read it back with their account password.

**Know what this costs before turning it on.** With it on, the database together with that key ring is
equivalent to every token on the instance. The key ring lives beside the database in the state directory,
so this defends a database that leaks on its own, a copied file or a stray backup, and not somebody who
takes the whole directory. Off, none of that applies and nothing is kept.

Tokens issued before it was switched on can never be read back, because their secrets were never kept, so
the interface offers Copy only where there is something to copy. An administrator cannot read another
account's tokens. Every read is audited as `reveal-token`, recording the act rather than the secret.

The forge prints which it is doing at startup:

```text
TOKENS: secrets unrecoverable once issued
```

See [ADR 0012](decisions/0012-recoverable-tokens.md).

### Optional webhooks

A project can ask to be told when something happens: a push, an issue, a merge request, a comment. The
forge sends nothing until an operator allows it to:

```sh
GRAFT_WEBHOOKS=true
```

or `--webhooks true`.

**Know what this costs before turning it on.** With it on, a project Maintainer can cause this server to
issue an HTTP request to any address it can reach, including addresses inside your network that nothing
outside it could otherwise ask for. That is the point of the feature on a self-hosted forge, where the
address a hook wants is usually an internal build server, and it is why the switch belongs to the operator
rather than to the project. Graft refuses only what no address field should accept: a hook URL must be an
absolute `http` or `https` address with no credentials embedded in it.

Each delivery carries `X-Graft-Event` and `X-Graft-Signature`, the latter being `sha256=` followed by an
HMAC-SHA256 of the exact body under the hook's own secret. A receiver should check it and reject anything
that does not match; the secret itself is never sent.

Deliveries are queued and sent on a worker, so nothing a person does in Graft waits for a receiver to
answer. The queue is bounded, held in memory, lost on restart, and drops its oldest entry when full, with
three attempts a few seconds apart. **This is a notification mechanism, not a guarantee of delivery.** A
receiver that must not miss an event should reconcile against the API rather than trust that it heard.

The forge prints which it is doing at startup:

```text
WEBHOOKS: off
```

See [ADR 0016](decisions/0016-webhooks.md).

### Optional Git over SSH

Graft can serve Git over SSH itself, with no OpenSSH daemon in front of it. It is off until a port is named:

```sh
GRAFT_SSH_PORT=2222
```

or `--ssh-port 2222`. Port 22 works where the process may bind it; most deployments pick 2222 or redirect
22 to it at the firewall. The listener binds the same address as the HTTPS listener and is a second port to
open, which a reverse proxy in front of HTTPS does not carry: Cloudflare's proxy, for one, does not pass SSH,
so the SSH port is reached directly or through a TCP forwarder of your own.

Clone URLs name the host the request came by, as `ssh://git@host:2222/owner/project.git`. Where the public
name differs, say it:

```sh
GRAFT_SSH_HOST=git.example.com
```

or `--ssh-host git.example.com`.

The host key is created on first start as `ssh_host_ecdsa_key` beside the database, in the directory your
backups already cover; keep it with the database when you move the server, or every client will be told the
host has changed. The forge prints where it listens and the key's fingerprint at startup:

```text
SSH: ssh://git@graft.example.com:2222/<owner>/<project>.git; host key SHA256:…
```

Give that fingerprint to people, so a first connection has something to check against.

Users register their public keys under **Profile and SSH keys** or through `POST /api/v4/user/keys`. A key
authenticates as its owner for Git and for nothing else: there is no shell, no forwarding, and no command but
`git-upload-pack` and `git-receive-pack`. Fetches are served in Git protocol version 2, which every Git
since 2.26 asks for by default; an older client passes `-c protocol.version=2`. Repositories with LFS
objects are cloned over HTTPS, or with `lfs.url` pointing at the HTTPS address, because `git-lfs-authenticate`
is not served.

See [ADR 0031](decisions/0031-git-over-ssh.md).

### Optional project sites

A project page can serve a static site from a folder in its repository. That is repository-authored HTML,
which cannot share an origin with the session cookie and the API, so it is served only from a second
hostname and the feature is off until an operator names one:

```sh
GRAFT_PAGES_ORIGIN=https://pages.example.com
```

or `--pages-origin https://pages.example.com` on the command line. Point that hostname at the same server;
Graft selects the pages behaviour by the request's `Host`, so no second listener and no second certificate
are needed when one certificate covers both names.

On that hostname there is no API, no session, no upload route and no browser shell. A site is reachable at
`https://pages.example.com/<project-slug>/`, and nothing outside the configured folder is reachable at all.
The forge prints which it is using at startup:

```text
PAGES: https://pages.example.com/<project-slug>/
```

See [project pages](project-pages.md) for the repository-side config, and
[ADR 0006](decisions/0006-project-pages-and-public-browsing.md) for why the second hostname is not optional.

## Certificate renewal

Graft loads the certificate during process startup. To renew it, write and validate the new PKCS#12 and password files at separate temporary paths, stop Graft, atomically replace both configured files, then start Graft and verify `/healthz` plus the certificate presented to a normal validating client. Keep the previous files until verification completes. Roll back both files together if startup or trust validation fails. The acceptance harness verifies that an unchanged file keeps the same certificate across restart and that a replaced file is loaded after restart; live reload is not implemented.

Use `tests/Graft.Packaging.Acceptance` to exercise each mode. Windows Service registration requires an elevated Windows session; systemd acceptance requires root on a systemd Linux environment; container acceptance currently calls rootless Podman.
