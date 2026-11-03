# Graft

<p align="center">
  <img src="docs/assets/graft-readme-blue.png" alt="Graft" width="600" />
</p>

Graft is a self-hosted Git forge written in C#/.NET 10. It speaks GitLab's REST API and URL conventions, so GitLab
clients and tools work against it, and it aims to be simpler to run than GitLab: every part is one executable with
nothing to install beside it, the data is one SQLite database and a directory of bare repositories, and nothing
else needs running.

- **Download:** [Releases](https://github.com/Wixely/Graft/releases): Windows and Linux executables, and a container
  image at `ghcr.io/wixely/graft`.
- **Install:** [the forge](#installing-the-forge), [a runner](#installing-a-runner),
  [git-remote-graft](#git-remote-graft-for-a-forge-behind-cloudflare-or-another-proxy), and
  [step by step for an agent](#for-agents-installing-graft-unattended).
- **Status:** working and used, not finished. What exists is below; what does not is in
  [the backlog](docs/backlog.md), and GitLab compatibility is tracked per route in [compatibility](docs/compatibility.md).
  The project's licence is still undecided.

## What it does

- **Git** over HTTPS and SSH, protocol versions 0 and 2, with Git LFS, served by Graft's own managed Git engine:
  no `git` executable on the server. Repositories up to 4 GiB of history, 64 MiB per object
  ([ADR 0018](docs/decisions/0018-repository-capacity.md)).
- **Projects and groups** with nested groups, roles that inherit, private, unlisted and public visibility, forks, an address with no owner in it (`/projects/<slug>`), and import from another server's address,
  kept up to date as a mirror that never deletes ([ADR 0042](docs/decisions/0042-import-by-address-and-mirrors.md)).
- **Collaboration:** issues, merge requests with diffs, line comments, approvals, code owners, squash and rebase
  merges, boards, milestones, labels, time tracking, releases, wikis, snippets and an activity feed.
- **Pipelines** from a subset of `.gitlab-ci.yml`, run by Graft's own runner, which reaches the forge over CupriNet
  without needing a certificate or an HTTPS route ([ADR 0037](docs/decisions/0037-runners-over-cuprinet.md)).
- **Packages and container images** stored beside the repository, webhooks, a notification inbox, optional email,
  an authenticator-app second factor, personal access tokens and SSH keys.
- **Agents:** a Model Context Protocol endpoint at `/mcp` and a guide at `/llms.txt`
  ([ADR 0009](docs/decisions/0009-mcp-in-the-forge.md)).
- **Behind a limiting proxy:** `git-remote-graft` sends pushes, fetches and LFS files in parts small enough for
  Cloudflare's free plan ([ADR 0043](docs/decisions/0043-chunked-transfers-git-remote-graft.md)).

## Downloads

Every [release](https://github.com/Wixely/Graft/releases) carries these, each a single executable. The Linux ones are
static and run on any x64 Linux, old kernels included. `SHA256SUMS` lists every file's hash.

| File | What it is |
|---|---|
| `graft-server-win-x64.exe`, `graft-server-linux-x64` | The forge. |
| `graft-admin-win-x64.exe`, `graft-admin-linux-x64` | Creates the first administrator; backs up, verifies and restores. |
| `graft-runner-win-x64.exe`, `graft-runner-linux-x64` | Runs pipelines. |
| `git-remote-graft-win-x64.exe`, `git-remote-graft-linux-x64` | Git's helper for a forge behind a proxy that limits uploads. |
| `ghcr.io/wixely/graft:<version>` | The forge in a container, with every runner and helper download inside. |

The latest release's files are always at `https://github.com/Wixely/Graft/releases/latest/download/<file>`.

## Installing the forge

Graft needs HTTPS. On loopback it makes itself a short-lived certificate, which is enough to try it; anywhere else it
needs a PKCS#12 certificate whose subject alternative name matches the address people use, the certificate's password
in a file of its own, and that address as `--origin`. The [deployment runbook](docs/deployment.md) covers certificates,
proxies such as IIS and Cloudflare, temporary disk space and every option.

### Try it

```powershell
# Windows; the same on Linux with graft-admin-linux-x64 and graft-server-linux-x64
New-Item -ItemType Directory -Force graft-data | Out-Null
Set-Content -NoNewline graft-data\admin-password.txt 'choose-a-long-password'
.\graft-admin-win-x64.exe bootstrap --database graft-data\graft.db --repositories graft-data\repositories --username admin --password-file graft-data\admin-password.txt
Remove-Item graft-data\admin-password.txt
.\graft-server-win-x64.exe --database graft-data\graft.db --repositories graft-data\repositories --listen https://127.0.0.1:7443
```

Open `https://127.0.0.1:7443`, accept the certificate warning, and sign in as `admin`. The server prints `READY:` with
its address when it is listening.

### First administrator

`graft-admin bootstrap` creates the first administrator, once, while the server is stopped. The password is read
from a file so it never appears in a command line or a process list; delete the file once you have signed in.
Everyone else is created by an administrator in the browser, under **Administration**, **Users**.

### Windows service

```powershell
# As an administrator. Paths are examples; the service account needs write access to the data directory.
New-Item -ItemType Directory -Force 'C:\Program Files\Graft', 'C:\ProgramData\Graft\secrets' | Out-Null
Copy-Item graft-server-win-x64.exe 'C:\Program Files\Graft\Graft.Server.exe'
sc.exe create Graft start= auto binPath= '"C:\Program Files\Graft\Graft.Server.exe" --database "C:\ProgramData\Graft\graft.db" --repositories "C:\ProgramData\Graft\repositories" --listen https://0.0.0.0:7443 --origin https://graft.example.com:7443 --certificate "C:\ProgramData\Graft\secrets\tls.pfx" --certificate-password-file "C:\ProgramData\Graft\secrets\tls-password"'
sc.exe start Graft
```

### Linux and systemd

```sh
sudo install -m 755 graft-server-linux-x64 /opt/graft/Graft.Server
sudo useradd --system --home /var/lib/graft --create-home graft
```

Then fill in the `@...@` placeholders of [deploy/systemd/graft.service](deploy/systemd/graft.service) (executable,
data directory, listen address, origin and certificate files readable only by `graft`), install it as
`/etc/systemd/system/graft.service`, and run `sudo systemctl daemon-reload && sudo systemctl enable --now graft`.
`curl --insecure https://127.0.0.1:7443/healthz` answers once it is up.

### Container

```sh
docker pull ghcr.io/wixely/graft:latest
```

[deploy/container/compose.yaml](deploy/container/compose.yaml) runs it as an unprivileged user on a read-only root
filesystem with one data volume. Copy [deploy/container/.env.example](deploy/container/.env.example) to `.env`, set
`GRAFT_IMAGE=ghcr.io/wixely/graft:<version>` and the certificate and origin it documents, then
`docker compose --file deploy/container/compose.yaml up --detach`. Create the first administrator first, with the
admin tool inside the image, before the forge's first start. The password file is mounted for that one command and
must be readable by the image's user, UID 1654:

```sh
printf '%s' 'choose-a-long-password' > admin-password && chmod 644 admin-password
docker compose --file deploy/container/compose.yaml run --rm -v "$PWD/admin-password:/run/admin-password:ro" \
  --entrypoint /app/Graft.Admin graft \
  bootstrap --database /data/graft.db --repositories /data/repositories --username admin --password-file /run/admin-password
rm admin-password
```

The image carries the runner and `git-remote-graft` for every platform in `/app/downloads`, which the forge serves to
its install scripts.

### Options worth knowing

All are off or at their default unless given. The [deployment runbook](docs/deployment.md) has every one.

| Option | Effect |
|---|---|
| `--origin https://host` | The address people reach the forge at; required off loopback. |
| `--ssh-port <port>` | Serve Git over SSH. |
| `--runner-port <port>` | Accept runners ([ADR 0037](docs/decisions/0037-runners-over-cuprinet.md)). |
| `--runner-downloads <directory>` | Serve the runner and `git-remote-graft` executables from this directory to the install scripts. Put the release's `graft-runner-*` and `git-remote-graft-*` files there under their release names. |
| `--transfer-part-size <MiB>` | Largest part `git-remote-graft` sends; 16 by default, under Cloudflare's 100 MB. |
| `--url-import public\|any` | Allow importing and mirroring repositories by address. |
| `--public-browsing true` | Let public projects be read without signing in. |
| `--mcp true` | Serve the Model Context Protocol endpoint at `/mcp`. |
| `--webhooks true`, `--mail-smtp <url>` | Outbound webhooks; email. |

## Installing a runner

A runner is installed from a script the forge writes. An administrator opens **Administration**, **Runners**, creates
a runner, and copies its install script, which carries a one-time registration token valid for a day. Run it on the
machine that will run jobs:

- Windows, as an administrator: the PowerShell script installs `graft-runner` as the `GraftRunner` service.
- Linux, as root: the shell script installs it under `/opt/graft-runner` as a systemd service.

The script downloads `graft-runner` from the forge when the forge has `--runner-downloads`, or takes one you already
have with `-Binary` or `--binary`. `-NoService` and `--no-service` register without installing a service.

## git-remote-graft: for a forge behind Cloudflare or another proxy

A proxy can refuse a request over a size: Cloudflare's free plan stops at 100 MB. Git sends a whole push, and Git LFS a
whole file, as one request, so past that size they fail with HTTP 413. `git-remote-graft` sends pushes, fetches and
LFS files in parts the proxy accepts. Each person installs it once; after that their ordinary `https://` remotes,
clones, editors and tools for that forge go through it with nothing else changed, and other hosts are untouched.

From the forge, which also sets it up for that forge:

```powershell
# Windows PowerShell, as yourself (not as an administrator)
& ([scriptblock]::Create((Invoke-RestMethod https://graft.example.com/-/git-remote-graft/install.ps1)))
```

```sh
# Linux
curl -fsSL https://graft.example.com/-/git-remote-graft/install.sh | sh
```

Or by hand: put the release's `git-remote-graft` executable on `PATH` under the name `git-remote-graft` (`.exe` on
Windows) and run `git-remote-graft setup https://graft.example.com`. That adds one rule to your global Git
configuration, `url."graft::https://graft.example.com/".insteadOf`, and registers the `graft` transfer with Git LFS.
`--push-only` routes only pushes through it. `git-remote-graft remove https://graft.example.com` undoes the rule.

It uses the same credentials as Git: when Git asks, the password is a personal access token. Large transfers show
their progress with `git push --progress`.

## For agents: installing Graft unattended

Steps an agent can follow on someone's behalf, without prompts. Each ends with a check; stop and report if a check
fails rather than continuing. Never put a password or token on a command line: write it to a file readable only by
the account that needs it, and delete it when done.

**Choose the files.** Windows x64 uses the `*-win-x64.exe` files; any x64 Linux uses `*-linux-x64`. Other
architectures are not built. Download from `https://github.com/Wixely/Graft/releases/latest/download/<file>` (or
`/releases/download/v<version>/<file>` for a fixed version) and check each against `SHA256SUMS` from the same release:

```sh
curl -fsSLO https://github.com/Wixely/Graft/releases/latest/download/SHA256SUMS
curl -fsSLO https://github.com/Wixely/Graft/releases/latest/download/graft-server-linux-x64
grep ' graft-server-linux-x64$' SHA256SUMS | sha256sum -c -
chmod 755 graft-server-linux-x64 && ./graft-server-linux-x64 --version
```

```powershell
$base = 'https://github.com/Wixely/Graft/releases/latest/download'
Invoke-WebRequest -UseBasicParsing "$base/SHA256SUMS" -OutFile SHA256SUMS
Invoke-WebRequest -UseBasicParsing "$base/graft-server-win-x64.exe" -OutFile graft-server-win-x64.exe
$expected = ((Get-Content SHA256SUMS) -match ' graft-server-win-x64.exe$' -split ' ')[0]
if ((Get-FileHash -Algorithm SHA256 graft-server-win-x64.exe).Hash.ToLower() -ne $expected) { throw 'checksum mismatch' }
.\graft-server-win-x64.exe --version
```

**Install git-remote-graft for a person** (no administrator rights; run as that person):

1. If the forge serves it (`https://<forge>/-/git-remote-graft/download/git-remote-graft-<platform>` answers), run the
   forge's install script from the section above. Otherwise download `git-remote-graft-<platform>` from the release
   and verify it.
2. Install it on `PATH` as `git-remote-graft`: on Linux `install -m 755 git-remote-graft-linux-x64 ~/.local/bin/git-remote-graft`;
   on Windows copy it to `%LOCALAPPDATA%\Programs\git-remote-graft\git-remote-graft.exe` and add that directory to the
   user's `Path` (`[Environment]::SetEnvironmentVariable('Path', ..., 'User')`). Editors and terminals opened earlier
   need restarting to see it.
3. Run `git-remote-graft setup https://<forge>`.
4. Check: `git-remote-graft --version` prints the version; `git config --global --get-regexp '^url\.graft::'` shows
   the rule; and `git ls-remote https://<forge>/<namespace>/<project>.git` lists references, which shows Git found
   the helper. A Git LFS repository's address is fixed up the first time the helper runs in it; any fetch, pull or
   push does that.

**Install the forge** on a machine you administer:

1. Download and verify `graft-server-<platform>` and `graft-admin-<platform>`.
2. Choose a data directory, and write the first administrator's password to a file there, readable only by you.
3. `graft-admin bootstrap --database <data>/graft.db --repositories <data>/repositories --username <name> --password-file <file>`,
   then delete the file.
4. Off loopback, put the PKCS#12 certificate and a file holding its password in a directory only the service account
   can read, and install the service as above: Windows `sc.exe create`, Linux the systemd unit. Pass `--origin` as the
   address people will use.
5. Check: `curl --insecure https://127.0.0.1:<port>/healthz` answers, and the log shows `READY:` and `VERSION:`.

**Install a runner** with an administrator's personal access token with the `api` scope:

1. Create the runner: `POST https://<forge>/api/v4/user/runners` with header `PRIVATE-TOKEN: <token>` and body
   `{"description": "<machine name>"}`. The answer carries `install_powershell` and `install_shell`, the same scripts
   the browser offers, with a one-time registration token valid for a day.
2. Save the script for the machine's platform to a file readable only by you and run it: on Windows as an
   administrator with `powershell -ExecutionPolicy Bypass -File <script>`, on Linux as root with `sh <script>`. If the
   forge has no downloads directory, download and verify `graft-runner-<platform>` first and pass it with `-Binary` or
   `--binary`. Delete the script afterwards.
3. Check: the service runs (`Get-Service GraftRunner`, or `systemctl is-active graft-runner`), and
   `GET https://<forge>/api/v4/runners/all` lists the runner with `"online": true`.

## Using Git with Graft

Graft serves Git over HTTPS. A public or unlisted project clones with no credentials. Anything else, and every push,
authenticates with HTTP Basic where the **password is a personal access token**, never the account password.

1. Sign in, open **Profile and tokens** and create a token. Pushing needs the `write_repository` or `api` scope;
   cloning a private project needs `read_repository` or better.
2. When Git prompts, give your username and paste the token as the password. The token is shown once; it begins
   `graft_pat_`.

If a push keeps failing with no prompt, a credential helper has cached a refused password and is replaying it. Clear it
and try again:

```text
printf "protocol=https\nhost=<your graft host>\n\n" | git credential reject
```

**Or use SSH.** Where the forge runs with `--ssh-port`, the clone menu also offers `ssh://git@host:port/owner/project.git`.
Register the public half of your key under **Profile and SSH keys** and Git authenticates with it. Check the host key
fingerprint the server prints at startup the first time you connect. See
[ADR 0031](docs/decisions/0031-git-over-ssh.md).

**Behind a proxy that limits uploads**, install [git-remote-graft](#git-remote-graft-for-a-forge-behind-cloudflare-or-another-proxy).

## Building Graft

Building needs the .NET 10 SDK, and the C++ build tools for the single-file NativeAOT executables. DnaX, CupriNet and
CupriMark come from Wixely's GitHub Packages feed, which needs a GitHub token with `read:packages` even to read. Add it
once per machine to your user-level NuGet configuration, under the source name the repository uses:

```sh
dotnet nuget add source https://nuget.pkg.github.com/Wixely/index.json --name GitHub-Wixely-Packages \
  --username <your-github-user> --password <token-with-read:packages>
```

Then `dotnet build Graft.slnx`. The release executables are built by `eng/build-windows.ps1` and `eng/build-linux.sh`,
which [the release workflow](.github/workflows/release.yml) runs on a `v*` tag. [Build and test](docs/build-and-test.md)
covers the acceptance suites.

Working on Graft as an agent: read [AGENTS.md](AGENTS.md), then [the handoff](docs/handoff.md),
[requirements](docs/requirements.md) and [decisions](docs/decisions/README.md). The README's former running status log
is in [history](docs/history.md).

## Repository map

| Path | Purpose |
| --- | --- |
| `docs/` | Requirements, decisions, architecture questions, backlog, history and evidence |
| `eng/` | Release builds: the Windows and Linux scripts, the smoke test, and the SQLite they link in |
| `src/Graft.Server/` | The forge: HTTPS host, API, Git transports, runner host, MCP endpoint |
| `src/Graft.Git/` | Managed Git engine: bare repositories, packs and deltas, smart HTTP versions 0 and 2, diffs |
| `src/Graft.Core/`, `src/Graft.Storage/` | Authorization policy and DnaX/SQLite persistence |
| `src/Graft.Ssh/` | Dependency-free SSH server for Git over SSH ([ADR 0031](docs/decisions/0031-git-over-ssh.md)) |
| `src/Graft.Runner/`, `src/Graft.RunnerProtocol/` | The runner and the protocol it shares with the forge |
| `src/Graft.Remote/` | `git-remote-graft` ([ADR 0043](docs/decisions/0043-chunked-transfers-git-remote-graft.md)) |
| `src/Graft.Client/`, `src/Graft.Contracts/` | Blazor WebAssembly client and source-generated wire contracts |
| `src/Graft.Operations/`, `src/Graft.Admin/` | Backup, restore and import inventory, and the admin tool |
| `src/Graft.Mcp/`, `src/Graft.McpTools/` | Agent tools, served by the forge and as a separate stdio launcher |
| `deploy/` | systemd unit, container image and compose file |
| `tests/` | Acceptance suites driving real Git, git-lfs, SSH and GitLab clients against a disposable forge |
| `experiments/` | The early probes that settled the platform, kept with their runbooks |
| `THIRD-PARTY-NOTICES.md` | Attribution for every redistributed component; embedded in the server and served at `/-/licences` |
