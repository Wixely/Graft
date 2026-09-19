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

The container path uses the self-contained `linux-x64` publication at `artifacts/mvp08/server-linux-x64`. It runs as UID/GID 1654 with a read-only root filesystem and a writable named data volume.

```powershell
dotnet publish src/Graft.Server/Graft.Server.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/mvp08/server-linux-x64
$env:GRAFT_TLS_CERTIFICATE = '/operator/path/tls.pfx'
$env:GRAFT_TLS_PASSWORD_FILE = '/operator/path/tls-password'
$env:GRAFT_ORIGIN = 'https://graft.example.internal:8443'
podman build --file deploy/container/Containerfile --tag graft:mvp08 .
podman compose --file deploy/container/compose.yaml up --detach
```

Compose mounts the certificate and password as read-only secrets. The port is bound to host loopback. The image currently uses the .NET 10 Ubuntu runtime-deps base and is separate from the static-musl legacy-host artifact. Pinning the base by digest remains release work.

## Certificate renewal

Graft loads the certificate during process startup. To renew it, write and validate the new PKCS#12 and password files at separate temporary paths, stop Graft, atomically replace both configured files, then start Graft and verify `/healthz` plus the certificate presented to a normal validating client. Keep the previous files until verification completes. Roll back both files together if startup or trust validation fails. The acceptance harness verifies that an unchanged file keeps the same certificate across restart and that a replaced file is loaded after restart; live reload is not implemented.

Use `tests/Graft.Packaging.Acceptance` to exercise each mode. Windows Service registration requires an elevated Windows session; systemd acceptance requires root on a systemd Linux environment; container acceptance currently calls rootless Podman.
