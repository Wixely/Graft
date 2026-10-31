# 0037: Graft's own runner, over CupriNet, installed from a script the forge writes

- Date: 2026-10-31
- Status: Accepted, implemented in its first slice
- Source: The user, on 2026-10-31: "lets try to make a runner now"; then "graft runner should use cuprimark and cuprinet"; then "graft should give us install scripts to install runners, the script includes the intonation needed to connect to graft". The requirement "simplify runner setup and networking; investigate CupriNet integration" dates from the first requirements, and continuous integration had been a long-term goal since [ADR 0001](0001-name-and-compatibility.md)
- Review trigger: CupriNet or CupriMark reaching 1.0, or an external audit of CupriNet's Noise usage; a request for GitLab's runner API so the official `gitlab-runner` connects; a request for containers, caches, artifacts, services or `rules`; a runner that cannot reach the forge's runner port directly; the Linux 3.10.108 acceptance run; approval of the authenticated GitHub Packages feed

## What was missing

Graft could not run anything. A pushed `.gitlab-ci.yml` was a file. GitLab's runner is a separate Go program
that polls the forge over HTTPS, so it needs the forge reachable at a trusted certificate from every build
machine, and a registration ritual the user named as one of the reasons to leave GitLab.

## Decision: Graft has its own runner, in this repository

`src/Graft.Runner` builds `graft-runner`, a NativeAOT executable, and `src/Graft.RunnerProtocol` holds what it
and the forge share. They live here, not in a repository of their own, because the protocol changes on both
sides together while it is young and one commit should change both; the acceptance suite starts a forge and a
runner in one run; and the runner reuses the forge's contracts. It still ships as a separate binary. A separate
repository is the right move once the protocol is stable or the runner needs its own release cadence.

GitLab's runner API, so that `gitlab-runner` itself connects, is not built and remains the compatibility layer
to add later. GitLab pipeline files are read, within the subset below.

## Decision: the transport is a CupriNet Shrine; versions are negotiated with CupriMark

The forge serves a CupriNet Shrine on a port of its own, `--runner-port`. The Shrine's `cupri1…` address is a key
created on first start and kept beside the database, protected by the forge's key ring, so it survives restarts
and moves. A runner pins that key in the Noise handshake, so only the forge can answer it; no certificate
authority, DNS name or trusted certificate is involved, which is the networking simplification the requirement
asked for. Requests are CupriNet's request/response rite; a job's source travels as a relic, chunked and
hash-verified, over the same session, so a runner never needs to reach the forge's HTTPS at all.

The CupriNet node behind the Shrine does one thing. Its overlay listener is bound to loopback and never
advertised; gossip, decoy traffic, peer exchange and address discovery are off; the runner port is Graft's own
listener, whose connections are handed to CupriNet's documented host-side accept. The runner uses CupriNet's
visiting side only, with no node of its own.

Every runner request carries the range of protocol versions the runner speaks; the forge negotiates with
CupriMark against its catalogue and refuses by name when the two share nothing at or above its floor. A runner
and a forge on different releases therefore keep working through a migration window, which is the "simple
upgrades" requirement applied to runners.

## Decision: CupriNet authenticates the forge; Graft authenticates the runner

A CupriNet visit is anonymous by design and grants nothing. The runner's identity is a credential Graft issued,
sent on every request and checked against a digest; a job's token lets its runner append to that job's log and
finish that job, and stops working when the job ends. CupriNet is pre-1.0 and not yet externally audited, and
the runner channel carries the authority to execute code, so what a runner may do never rests on the transport
alone. [The architecture note](../architecture.md) already said transport identity is not job authorization.

## Decision: the install script is the registration

An administrator creates a runner, on the Runners page or with `POST /api/v4/user/runners`, and is handed, once,
two install scripts: Windows PowerShell and POSIX shell. Each carries the forge's Intonation, a signed link
naming the Shrine and the address to reach it, and a one-time registration token. Both expire after a day. A
script obtains the runner (downloaded from the forge's `/-/runner/download/` when the operator filled
`--runner-downloads`, or given with `-Binary`/`--binary`), runs `graft-runner register`, which validates the
Intonation against the `graft-runners` network, visits the Shrine and trades the token for a credential, and then
installs a Windows service or a systemd unit unless told `-NoService`/`--no-service`. The runner keeps the Shrine
key and addresses it pinned, never the spent token.

The Intonation advertises the host the operator names with `--runner-host`, or the host in `--origin`. Runners
reach it directly; a forge behind a NAT that runners cannot reach needs CupriNet's traversal or relay, which this
slice does not turn on.

## Decision: what a pipeline file may ask for, in this slice

Graft reads `.gitlab-ci.yml` with a reader of its own, because it takes no YAML dependency and the common .NET
ones reflect at run time, which NativeAOT refuses. Supported: `stages`, top-level `variables`, and jobs with
`stage`, `script`, `before_script`, `variables` and `allow_failure`; hidden jobs are not run, as on GitLab.
Anything else, `image`, `rules`, `include`, `artifacts` and the rest, fails the pipeline with the key named,
rather than running the file as though it had asked for less. Anchors, aliases, merge keys and flow mappings are
refused by name too.

A push that moves a branch to a commit with a pipeline file creates a pipeline, when the forge runs runners. Its
jobs run stage by stage: a job is leased only once every earlier stage succeeded or failed where that was
allowed, and a failure that was not allowed skips every later stage. A runner asks for work and the forge holds
the request up to twenty seconds for a job to appear. The runner runs the script as one shell session,
PowerShell on Windows and `sh` elsewhere, echoing each line and stopping at the first that fails, in a fresh
directory holding the commit's source, with the job's variables and GitLab's basic `CI_*` variables. Output is
streamed back about once a second; a log is kept up to 4 MiB; a job is stopped after an hour.

Pipelines are read by whoever may browse the repository, through GitLab's `pipelines`, `jobs` and `trace` routes
and a Pipelines page; a pipeline is started on request through GitLab's `POST .../pipeline`. Runners are instance
runners, managed by administrators.

## Decision: packages

`CupriNet.Hosting` 0.6.2 in the forge, `CupriNet.Shrine` 0.6.2 in the runner, and `CupriMark` 0.2.0 in the
shared protocol. Both projects publish only to the authenticated GitHub Packages feed under Wixely, the same open
decision as DnaX; until it is approved the packages are restored from the same local, ignored feed, packed from
the release tags `v0.6.2` and `v0.2.0` exactly as each project's release workflow packs them.

## What is not done

Containers and images; services; caches and artifacts; `rules`, `only`, `except`, `needs`, `when`, `parallel`,
`extends`, `include`, `after_script`, `timeout`; manual jobs; cancelling or retrying a job; job timeouts other
than the hour; group and project runners and runner tags; GitLab's runner API; a runner that drops mid-job is
not detected until it returns, so its job stays running; pipelines from commits made in the browser or by a
merge; webhooks and notifications for pipelines; CupriNet traversal, relay or Tor for a forge runners cannot
reach directly. The Linux build of the runner has not been published; the systemd path of the shell script and
the Windows service path of the PowerShell script have not been run by a test.

## What was verified

Before any of it was built: a spike in which a host minted an Intonation for a Shrine on its own listener and a
visitor reached it from the link alone, made a request, fetched a 300 KB relic, and had a link for the wrong
network refused; then the same program published NativeAOT with zero warnings and did the same. Then the forge
and the runner each published NativeAOT with zero trimming or AOT warnings. Then a smoke run on a live forge, and
the acceptance suite, as recorded in the handoff entry of 2026-10-31.
