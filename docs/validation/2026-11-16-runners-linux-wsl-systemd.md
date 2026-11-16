# Runners on Linux under systemd, and routing between a Windows and a Linux runner

Recorded: 2026-11-16. Owner: Implementation agent. Decisions: [ADR 0037](../decisions/0037-runners-over-cuprinet.md),
[ADR 0054](../decisions/0054-runner-tags.md).

## Setup

All local and disposable, removed afterwards:
- A forge built from this tree, running inside WSL 2 (Ubuntu 24.04, kernel 6.18 WSL2, systemd on) on the .NET
  runtime, with `--runner-port` on loopback.
- The v0.1.3 release's static `graft-runner-linux-x64`, checked against the release's `SHA256SUMS`, and the runner
  built from this tree on the .NET runtime.
- A Windows runner on the host, registered with its PowerShell install script against the forge in WSL. WSL's
  localhost forwarding carried the CupriNet connection.

## Found: the systemd service never started

The shell install script writes a `Type=notify` unit. On a real systemd the runner it installed was killed at the
start timeout every time, and `systemctl restart` looped. There were two causes, both confirmed in isolation:

1. **The working directory.** systemd starts a unit in `/`. The runner's host took `/` as its content root and
   watched it for configuration changes, which never finished. The runner neither asked for work nor reached
   "started". The same build ran normally from a shell in another directory. With `WorkingDirectory=` set, it
   started.
2. **Readiness.** Even with a working directory, the static release binary never told systemd it was ready. The
   forge met this in MVP-08 and sends `READY=1` itself.

Fixed:
- The runner roots its host at its configuration directory and turns configuration reloading off.
- It sends `READY=1` and `STOPPING=1` itself, as the forge does (`SystemdNotify.cs`).
- The unit the script writes also sets `WorkingDirectory=`.

**The v0.1.3 static Linux runner cannot run as a systemd service.** Releases up to and including v0.1.3 are
affected; the next release carries the fix. The Windows service path was not affected by cause 1 and was not run.

## Verified

- The runner built from this tree, started by systemd as `Type=notify` from `/` with no working directory, reached
  `active` with status "Graft runner is asking for work". The forge listed it online, and it stopped cleanly.
- The install script, as the rebuilt forge wrote it:
  - registered the runner and wrote a unit with `WorkingDirectory=/opt/graft-runner`;
  - the service was `active` and online, and survived `systemctl restart`;
  - under WSL it printed the keep-alive command with the distribution's real name.
- With systemd hidden (a private mount namespace over `/run/systemd`), the script refused before registering. It
  created nothing, printed the `wsl.conf` lines, and exited 1.
- The forge's runner download route served the release's Linux runner byte for byte (200, 15,344,280 bytes).
- A pipeline with three jobs:
  - `on-linux` (`tags: [linux]`) ran on the Linux runner and printed the WSL2 kernel;
  - `on-windows` (`tags: [windows]`) ran on the Windows runner under Windows PowerShell 5.1 (`os=Windows_NT
    edition=Desktop`);
  - an untagged job stayed pending and was reported stuck, as neither runner takes untagged jobs.

## Not verified

- **The fix in a static NativeAOT build.** Building one needs the Alpine root of `eng/build-linux.sh`. Check the
  next release's Linux runner under systemd before relying on it.
- **The `GraftRunner` Windows service.** Installing it needs an elevated session, which this one was not.
- **Whether WSL really stops an idle distribution with the service running,** and whether the keep-alive command
  prevents it. A session was open throughout.
- **The full acceptance suite with the Linux runner.** WSL had no .NET SDK and no git-lfs; installing them was not
  asked for.
- **The Linux 3.10.108 host, and a runner container image.** No access to the host from here, and no Docker or
  Podman on this machine.
