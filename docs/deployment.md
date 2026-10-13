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

The container path uses the self-contained `linux-x64` publications at `artifacts/mvp08/server-linux-x64` and `artifacts/mvp08/mcp-linux-x64`. Both go into one image, which runs as UID/GID 1654 with a read-only root filesystem and a writable named data volume.

Copy `deploy/container/.env.example` to `deploy/container/.env` and fill it in first. It documents every variable, including the two that only matter when the MCP profile is enabled.

```powershell
dotnet publish src/Graft.Server/Graft.Server.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/mvp08/server-linux-x64
dotnet publish src/Graft.Mcp/Graft.Mcp.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/mvp08/mcp-linux-x64
podman compose --file deploy/container/compose.yaml build
podman compose --file deploy/container/compose.yaml up --detach
```

Both services name the image they run, through `GRAFT_IMAGE`, defaulting to `graft:local`. That matters more than it looks. A compose service with only a `build:` section and no `image:` makes compose invent a name from the project directory and the service, so an image you built and tagged yourself is never referenced, and `up` runs whatever compose built first regardless of what changed underneath. Naming it means `podman build --tag graft:local .` and `compose build` produce the same thing, and either is what runs. It also means one image serves both services rather than compose building the same Containerfile twice.

`up` still does not rebuild on its own. After republishing a binary, run `compose build` again, or `up --build`. Where an image looks current and the container disagrees, `compose build --no-cache` settles it.

Set `GRAFT_IMAGE` to a registry path to push a built image and pull it elsewhere.

Compose mounts the certificate and password as read-only secrets. **Those files must be readable by UID 1654.** Compose secrets outside swarm are host bind mounts, so the file's own ownership and mode are what the container sees, and a root-only `0600` key produces a startup failure reading `Access to the path '/run/secrets/graft-tls.pfx' is denied`.

The port is bound to host loopback. The image uses the .NET 10 Ubuntu runtime-deps base and is separate from the static-musl legacy-host artifact. Pinning the base by digest remains release work.

### Optional MCP endpoint

The Model Context Protocol server ships in the same image and starts only when asked for, as a compose profile:

```powershell
podman compose --file deploy/container/compose.yaml --profile mcp up --detach
```

Without the profile nothing listens on 8444 and the image is unchanged, so turning it on is a restart rather than a rebuild. With it, the endpoint is at `https://<host>:8444/mcp`, bound to host loopback like the forge.

It holds no credential. Every caller sends their own Graft personal access token as `PRIVATE-TOKEN` or `Authorization: Bearer`, so two callers get exactly the access their own tokens carry, and a request without one is refused `401` before the protocol is spoken. `GRAFT_MCP_ALLOW_WRITES` bounds what any caller can do through it and defaults to `false`.

Two overrides exist and both default to closed. `GRAFT_MCP_ALLOW_ANONYMOUS=true` serves callers who present no credential at all; they then reach Graft as strangers and see only public projects, which is a real limit but the only one. The server prints its posture on startup, so what a deployment is actually doing is visible in its logs:

```text
POSTURE: credentials=required; writes=refused; transport=TLS
```

### Plain HTTP for an internal endpoint

Where the MCP endpoint is reached only from inside a container network or a private segment, terminating TLS a second time buys little. An overlay file supplies that shape:

```powershell
podman compose --file deploy/container/compose.yaml --file deploy/container/compose.internal-http.yaml --profile mcp up --detach
```

It changes the endpoint's own transport and nothing else. The forge keeps its TLS, the MCP container still verifies the forge's certificate, credentials are still required, and the port stays bound to host loopback. The overlay must pass `--allow-plaintext true`, because the server refuses a plaintext listener off loopback without it; a deployment therefore cannot serve tokens in the clear by forgetting a flag. What it costs is that callers' tokens cross that network unencrypted, so use the reference deployment wherever anything untrusted can reach the port.

Two variables exist only for this profile. `GRAFT_INTERNAL_HOST` is the name the MCP container uses to reach the forge, and compose gives the forge that name as a network alias; it must match a subject alternative name on the certificate, because the MCP server verifies it rather than trusting the service name. `GRAFT_CA_CERTIFICATE` is the forge's certificate in PEM or DER, which is the public half of the PKCS#12 and never the private key:

```sh
openssl pkcs12 -in tls.pfx -clcerts -nokeys -out graft-ca.pem -passin file:tls-password
```

There is no option anywhere that skips certificate verification. The MCP server reaches the forge over TLS and checks it, and off loopback its own endpoint must be HTTPS with a certificate too, which the compose file supplies from the same secret.

The MCP binary is published self-contained but **not** NativeAOT, unlike the forge, because no Model Context Protocol library has been verified under AOT by anyone. See [ADR 0008](decisions/0008-mcp-layer.md). Both binaries run on the same runtime-deps base either way.

## Certificate renewal

Graft loads the certificate during process startup. To renew it, write and validate the new PKCS#12 and password files at separate temporary paths, stop Graft, atomically replace both configured files, then start Graft and verify `/healthz` plus the certificate presented to a normal validating client. Keep the previous files until verification completes. Roll back both files together if startup or trust validation fails. The acceptance harness verifies that an unchanged file keeps the same certificate across restart and that a replaced file is loaded after restart; live reload is not implemented.

Use `tests/Graft.Packaging.Acceptance` to exercise each mode. Windows Service registration requires an elevated Windows session; systemd acceptance requires root on a systemd Linux environment; container acceptance currently calls rootless Podman.
