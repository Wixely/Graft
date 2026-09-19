# Deployment runbook

Recorded: 2026-09-19. Owner: Operator. Review when hosting, certificates or publish settings change.

Graft currently supports one server process and one writable data directory. The server needs absolute paths for its SQLite database and repository root. Every mode requires HTTPS and accepts an optional public origin used by browser origin checks:

```text
Graft.Server --database <path> --repositories <directory> [--listen <https URI>] [--origin <https origin>]
```

Without `--listen`, interactive mode chooses a random loopback port. A configured listener must use a literal IP address, HTTPS and an explicit nonzero port. The current server creates an ephemeral self-signed certificate on every start. That is suitable for local acceptance only; a stable operator-managed certificate and trust configuration remain release gates.

## Windows

Publish a self-contained NativeAOT server, then run it interactively:

```powershell
dotnet publish src\Graft.Server\Graft.Server.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -o artifacts\mvp08\server-win-x64
& .\artifacts\mvp08\server-win-x64\Graft.Server.exe --database C:\ProgramData\Graft\graft.db --repositories C:\ProgramData\Graft\repositories --listen https://127.0.0.1:7443 --origin https://127.0.0.1:7443
```

An elevated PowerShell session may register the same executable with the Service Control Manager. Quote every absolute path in `binPath`; grant the service identity read/execute access to the application directory and modify access to the data directory.

```powershell
sc.exe create Graft start= auto binPath= '"C:\Program Files\Graft\Graft.Server.exe" --database "C:\ProgramData\Graft\graft.db" --repositories "C:\ProgramData\Graft\repositories" --listen https://127.0.0.1:7443 --origin https://127.0.0.1:7443'
sc.exe start Graft
sc.exe query Graft
```

Stop Graft before backup, restore or upgrade. Validate a new binary interactively, replace the application files while stopped, start the service, then check `https://127.0.0.1:7443/healthz`. Retain the previous executable and a verified backup for rollback.

## Linux interactive and systemd

`deploy/linux/build-static-native.ps1` builds and inspects independent static-musl server and admin executables in an isolated Alpine chroot under an existing WSL distribution. It verifies that neither ELF contains an interpreter nor a `NEEDED` library entry. It does not install WSL or deploy to another host.

```powershell
.\deploy\linux\build-static-native.ps1 -Distribution <existing-wsl-distribution>
```

Copy the executable to `/opt/graft/Graft.Server`, create a dedicated unprivileged account and `/var/lib/graft`, then replace every `@...@` placeholder in `deploy/systemd/graft.service`. Install the rendered file as `/etc/systemd/system/graft.service` and run:

```text
systemctl daemon-reload
systemctl enable --now graft
systemctl show graft --property=ActiveState --property=SubState --property=Type
curl --insecure https://127.0.0.1:7443/healthz
```

The unit uses `Type=notify`, restarts on failure, restricts filesystem writes to the data directory and applies basic service hardening. Stop the unit before maintenance operations.

## Optional container

The container path uses the self-contained `linux-x64` publication at `artifacts/mvp08/server-linux-x64`. It runs as UID/GID 1654 with a read-only root filesystem and a writable named data volume.

```powershell
dotnet publish src/Graft.Server/Graft.Server.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/mvp08/server-linux-x64
podman build --file deploy/container/Containerfile --tag graft:mvp08 .
podman compose --file deploy/container/compose.yaml up --detach
```

The compose port is bound to host loopback. The image currently uses the .NET 10 Ubuntu runtime-deps base and is separate from the static-musl legacy-host artifact. Pinning the base by digest and integrating certificate secrets remain release work.

Use `tests/Graft.Packaging.Acceptance` to exercise each mode. Windows Service registration requires an elevated Windows session; systemd acceptance requires root on a systemd Linux environment; container acceptance currently calls rootless Podman.
