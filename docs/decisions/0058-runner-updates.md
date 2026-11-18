# 0058: Runners update when an administrator asks

- Date: 2026-11-18
- Status: Accepted, implemented; the Windows service and systemd restarts are not yet verified on a real service
- Source: On 2026-11-18 the project owner asked whether runners could be updated from the control panel, asked for it
  to be built, and asked for a manual update script too, since their runners were on v0.1.4 and older.
- Builds on: [ADR 0037](0037-runners-over-cuprinet.md) (runners over CupriNet, protocol negotiation, install
  scripts), [ADR 0038](0038-chunked-runner-transfers-and-artifacts.md) (verified chunked transfers) and
  [ADR 0054](0054-runner-tags.md) (the systemd service and WSL)
- Review trigger: a runner built as more than one file; a runner installed under another service name; a request to
  roll back automatically after an update that starts but then misbehaves; a request for updates to be pushed without
  an administrator asking; a request for signed runner builds

## What was missing

The only way to update a runner was to run its install script again, which needs a new registration token and puts a
new runner in the list. The forge recorded a runner's release only when it registered, so the admin page could show
the wrong one after a hand update.

## Decision: a runner says which build it is, every time it asks for work

The runner protocol gains version 2. A runner that speaks it sends its release and the SHA-256 of its own executable
with every request for work, and the forge records both, with the protocol agreed. The forge compares the hash with
the file it serves for the runner's platform from `--runner-downloads`. Comparing hashes, not version numbers, means
the forge never has to run or parse a build to know what it is. Each runner in the list therefore shows one of these:
- **current:** it runs the build the forge serves;
- **available:** it runs a different build and can be told to update;
- **requested:** it has been asked and has not yet answered;
- **needs_script:** it is too old to report its build or to understand being told to update;
- **no_download:** the forge serves no runner for its platform.

Protocol 1 runners keep working unchanged; negotiation picks the highest version both sides speak.

## Decision: an administrator asks; the runner updates when it is idle

- **Asking:** an administrator presses Update on a runner, or Update all, from the Runners page. This is the
  browser API `POST /api/admin/runners/{id}/update`, or `POST /api/admin/runners/update` for all. A waiting request
  can be withdrawn. Each request goes into the audit trail.
- **Refusals:** the forge refuses by name a runner that already runs the served build, one too old to understand the
  instruction, and one whose platform it serves nothing for.
- **The instruction:** the next time the runner asks for work, which means it is idle, the forge answers with an
  instruction to update instead of a job. The instruction is sent only to a runner that agreed protocol 2. It
  describes the served build as a chunked download over the same CupriNet session the runner fetches sources on, each
  chunk and the whole checked against its SHA-256. No new port, no HTTPS from the runner, and no certificate are
  involved. The forge offers a copy of the file, so the operator replacing it mid-transfer changes nothing a runner is
  fetching.
- **The swap:** the runner writes the build beside itself as `graft-runner.new`, runs it once with `version`, and
  accepts it only if it answers as a Graft runner within 30 seconds. It then renames the running executable to
  `graft-runner.old` and the new one into its place. Both Windows and Linux allow a running executable to be renamed,
  so no helper process is needed, and the old build stays on disk for putting back by hand.
- **The restart:** the runner ends with exit code 3. How the new build starts depends on how the runner was started:
  - Under systemd, the unit's `Restart=always` restarts it.
  - Under the Windows service manager, the install and update scripts now set the service to restart when it ends
    (`sc.exe failure` and `failureflag`). The runner also starts a short PowerShell command that starts the service if
    it is still stopped ten seconds later. This covers a service installed before recovery was set and a service
    manager that does not count a clean exit as a failure.
  - A runner started by hand starts its replacement itself, with the same arguments. With `--once` it only says to
    run it again.
- **Failure:** a build that cannot be fetched, does not start, or cannot be swapped in leaves the running executable
  as it was. The runner tells the forge why, and the forge ends the request and shows the reason beside the runner.
  It is not retried until an administrator asks again.

## Decision: an update script for runners too old to be told

`/-/runner/update.ps1` and `/-/runner/update.sh` are served to anyone, since they carry no secret. Each finds the
installed runner, fetches the served build (or takes `-Binary` / `--binary`) and runs it once to see it start. It then
swaps it in, keeps the old executable beside it, and restarts the service. The registration is kept. The Windows
script also sets the service's restart on failure, so the runner can update itself from then on.

The script stops or restarts a service only when that service runs the very executable being replaced. `-InstallDir` or
`--dir` pointing somewhere else never touches the machine's installed service. A job running when the service restarts
is stopped with it, which the script says.

Running the script once on each runner from v0.1.4 or earlier is the way onto a build that updates itself.

## Not done

- Rolling back automatically when the new build starts but then fails. Checking that it starts catches a wrong
  platform or a damaged file, not a build that misbehaves later; `graft-runner.old` is there for putting it back by
  hand.
- Signing runner builds. The runner trusts the build because it came from the forge it pinned at registration, over a
  session no one else can answer, and matched the hash the forge gave. Anyone who can write to the forge's downloads
  directory can therefore put a build on every runner an administrator updates.
- A runner built as more than one file. Only the executable is replaced, which is correct for the single-file release
  builds and for nothing else.

## What was verified

The Git acceptance suite, Debug build on Windows. The forge served the suite's runner with bytes appended, which still
runs:
- a runner reports its build when it asks for work, and the forge sees it differs from the served one;
- an administrator asks for an update, withdraws the request, and asks again;
- the idle runner fetches the build over CupriNet, swaps it in, keeps the old one, and ends with exit 3;
- started again, it reports the served build and the request is done;
- a runner on the served build is not asked again;
- a served file that is not a runner is refused before anything is replaced, nothing is left behind, and the forge
  shows why;
- the update scripts are served to anyone and carry no secret; the shell script is valid POSIX shell;
- the update script swaps in a given build after seeing it start, keeps the old one, and refuses one that does not
  start.

Not verified: the restart under the Windows service manager and under systemd. Both need a real service, and the
Windows one needs an elevated session.
