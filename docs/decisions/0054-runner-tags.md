# 0054: Runner tags route a job to the machines that can run it

- Date: 2026-11-16
- Status: Accepted, implemented
- Source: The project owner asked on 2026-11-16 whether building and testing for Windows, Linux and Android needs a
  runner per environment. The answer was one runner per operating system a job must run on, and that Graft could not
  yet send a job to a particular one, because any runner took any job. They asked for that gap to be closed, and
  whether a Windows machine could run the Linux jobs through WSL.
- Builds on: [ADR 0037](0037-runners-over-cuprinet.md), whose "not done" list named runner tags, and
  [ADR 0041](0041-labcoat-routes-retry-cancel-directory.md) (retry, which copies a job's tags with it)
- Review trigger: a request for variables in tags, for group or project runners, for paused runners, or for
  containers and images; a runner that should tag itself; a client that sends `tag_list` as a comma-separated string

## What was missing

The runner runs a job's script in the shell of the machine it is installed on. A pipeline that builds on Windows and
tests on Linux needs a runner on each, and a job had no way to say which. The first runner to ask took the next job,
so a PowerShell script could land on a Linux runner and fail for a reason that had nothing to do with the change.

## Decision: GitLab's tags, with GitLab's matching rule

A job lists `tags:` in the pipeline file. A runner has a list of tags and a `run_untagged` setting. A job with tags
goes only to a runner that has every one of them; a job with no tags goes only to a runner that runs untagged jobs.
This is GitLab's rule, so a pipeline file written for GitLab routes the same way here.

- Tags are trimmed and lower-cased, because GitLab compares them without regard to case. A tag is at most 64
  characters with no comma or control character, and a list holds at most 20. The rule is `RunnerTags` in
  `Graft.Core/Ci.cs`, and the pipeline reader and the runner settings both use it.
- A tag that names a variable (`$RUNNER`) fails the pipeline, saying why. GitLab expands variables in tags. Graft
  does not yet, and matching the literal text would route the job somewhere nobody meant.
- The match is made when a runner asks for work, in the same statement that leases the job. A change to a runner's
  tags therefore routes every job leased after it, and a job already running keeps its runner.
- A runner created before this has no tags and runs untagged jobs, which is what every runner did before.

## Decision: an administrator sets the tags, not the runner

Tags are given when a runner is created, on the Runners page or with `tag_list` and `run_untagged` on
`POST /api/v4/user/runners`. They are changed afterwards with `PUT /api/v4/runners/:id` or the page's "Edit tags".
This follows GitLab's current flow, where tags belong to the runner the administrator created, not to the
registration command. A runner's credential therefore cannot widen which jobs it receives. Within that route,
`description` can also be changed. Asking to pause a runner is refused, because Graft has no paused runners.
`GET /api/v4/runners/:id` is added, and `GET /api/v4/runners/all` takes GitLab's `tag_list` filter.

## Decision: a job no runner can take says so

A pending job is *stuck* when no registered runner could take it. A runner counts once its install script has
claimed the registration, even while it is offline, because it takes the job when it returns. The job's
`tag_list` and Graft's `stuck` are in the job's JSON, and the Pipelines page says the job is waiting and why. A
misspelt tag therefore shows up as a message rather than a pipeline that never moves.

## WSL: a Linux runner on a Windows machine

A Windows machine runs Linux jobs by having a second runner installed inside WSL 2 with the Linux install script,
tagged for Linux. No new runner code is involved: inside WSL the runner is an ordinary Linux process running
`sh`. Two things about WSL are handled in the script:

- The script installs a systemd unit. WSL runs systemd only when `/etc/wsl.conf` asks for it. The script now checks
  for systemd *before* registering, because registering spends the one-time token. Under WSL it then prints the two
  lines that turn systemd on.
- WSL stops a distribution once nothing on Windows is using it, and a systemd service does not count. After
  installing under WSL, the script prints a command for Windows to run at startup that keeps the distribution up.

WSL runs Microsoft's kernel, not 3.10. A job that passes in WSL says nothing about the legacy host in
[the requirements](../requirements.md), which still needs a runner on that host.

## Not done

- **Variables in tags.** Refused, as above.
- **Group and project runners.** Every runner is still an instance runner that any project's jobs may use.
- **Paused runners and `locked`.**
- **A runner reporting its own platform as a tag.** It reports its platform, which the page shows, but routing
  uses only the tags an administrator gave it.
- **Containers.** A job still runs in the runner's own shell. A Linux job on a Windows machine goes through a
  runner in WSL, not through an image.
- **Android emulators.** An emulator, or a phone attached over `adb`, belongs to whichever runner's machine it is
  on. Tag that runner (for example `android`).

## What was verified

Through the acceptance suite (`RESULT: PASS; assertions: 1665`, Debug build on Windows), against a forge and the
real runner over CupriNet:
- the job's tags read lower-cased;
- an untagged runner ran the untagged job and left the tagged one pending and marked stuck;
- `paused`, a tag containing a comma, and a non-administrator were refused;
- an administrator gave the runner two tags and turned off untagged jobs, leaving the description as it was, and
  the runner read back with them;
- the list filter matched and failed to match;
- the tagged job stopped being stuck, and the runner then ran it;
- a later untagged job waited, stuck, until `run_untagged` was turned back on alone, keeping the tags, and then ran;
- a runner was created with tags;
- a tag naming a variable failed its pipeline.

Not verified: the WSL path of the install script on a real WSL distribution, with and without systemd; the keep-alive
command; a forge on the same Windows machine reached from WSL's network. Under WSL's default NAT networking,
Windows's `localhost` is not WSL's, so a runner in WSL reaches a forge on the same machine through the host's
address or with mirrored networking.
