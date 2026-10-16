# The MCP endpoint in the standard deployment

Recorded: 2026-10-13. Owner: Implementation agent. Decision: [ADR 0008](../decisions/0008-mcp-layer.md).

> Superseded on 2026-10-16. The compose profile, the second port and the plain-HTTP overlay described here no
> longer exist; the forge hosts the endpoint itself behind `GRAFT_MCP=true`. See
> [the later record](2026-10-16-mcp-in-the-forge.md) and [ADR 0009](../decisions/0009-mcp-in-the-forge.md).

## What changed

The MCP server ships in the standard container image beside the forge and starts only when an operator asks
for it, as a compose profile:

```
podman compose --file deploy/container/compose.yaml --profile mcp up --detach
```

Without the profile, nothing listens on 8444 and the image is identical, so enabling it is a restart rather
than a rebuild.

A profile rather than a flag inside the forge, because the MCP server is a separate process for the reasons
[ADR 0008](../decisions/0008-mcp-layer.md) records. An operator who wants no agent access then has nothing
listening, instead of a route switched off.

## It carries no credential

Every caller sends their own Graft personal access token, so the deployed endpoint is a protocol translator
rather than a credential vault. Two callers get exactly the access their own tokens carry, and a request
without one is refused before the protocol is spoken. `GRAFT_MCP_ALLOW_WRITES` bounds every caller and
defaults to `false`.

The MCP container verifies the forge's certificate rather than trusting whatever answers on the network, so
compose gives the forge a network alias matching the name on its certificate. A service name would not match
a subject alternative name, and there is no option anywhere that skips the check.

## Credentials are required, and the override is explicit

A caller presenting nothing is refused `401` before the protocol is spoken. That was already true; what is
new is that lifting it is a named switch rather than something nobody can do, and that the refusal says so:

```text
Send a Graft personal access token as PRIVATE-TOKEN or Authorization: Bearer. This server does not serve
anonymous callers; an operator can allow that with --allow-anonymous true.
```

With `--allow-anonymous true` an uncredentialed caller reaches Graft as a stranger and sees only public
projects. That is a real bound, and it is the only one, which is why it is not the default.

The server prints its posture on startup so a deployment nobody has looked at in months still says what it
is doing: `POSTURE: credentials=required; writes=refused; transport=TLS`.

## Plain HTTP for an internal endpoint

`deploy/container/compose.internal-http.yaml` is an overlay that serves the MCP endpoint over plain HTTP:

```
docker compose -f compose.yaml -f compose.internal-http.yaml --profile mcp up --detach
```

It changes the endpoint's own transport and nothing else. The forge keeps its TLS, the MCP container still
verifies the forge's certificate, credentials are still required, and the port stays on host loopback. The
overlay must pass `--allow-plaintext true` because the server refuses a plaintext listener off loopback
without it, so a deployment cannot serve tokens in the clear by forgetting a flag.

## Verified, in a real container runtime

Docker 29.8.0 inside WSL Ubuntu 24.04, building through compose and running the real files against a
self-signed certificate whose subject alternative name matches the configured hostname.

| Check | Result |
| --- | --- |
| Services selected with no profile | `graft` only |
| Services selected with `--profile mcp` | `graft` and `graft-mcp` |
| Forge health with no profile | `200` |
| Port 8444 with no profile | nothing listening |
| Reference deployment posture | `credentials=required; writes=refused; transport=TLS` |
| MCP endpoint, no credential | `401`, naming `--allow-anonymous` in the body |
| MCP endpoint, with a token | `200`, `text/event-stream`, correct server identity |
| Tool call reaching the forge | refused by Graft: `The token was not accepted` |
| Write tool, read-only deployment | refused, naming `--allow-writes` |
| With `GRAFT_MCP_ALLOW_ANONYMOUS=true` | posture `credentials=optional`, and no credential gives `200` |
| Internal HTTP overlay | posture `transport=plaintext`, still `401` with no credential, `200` with one |
| Hardening on the MCP container | `read_only=true cap_drop=[ALL] no-new-privileges pids=128 memory=256M` |
| Compose without `GRAFT_CA_CERTIFICATE` | valid, and the forge starts |

The last two rows of the override block matter together: the two switches are independent, so the plaintext
overlay still demands a credential and allowing anonymous access does nothing to the transport.

The tool-call row is the one that proves the wiring rather than the configuration. A bogus but well-formed
token gets past this server's own check, so the call travels to the forge over TLS. Getting back "the token
was not accepted" means the alias resolved, the certificate verified, and Graft answered. A broken chain
would have said "Graft could not be reached" instead.

The last row matters for operators who never enable MCP: the two variables that exist only for this profile
are not required to run the forge.

## Hardened defaults

`compose.yaml` is the reference deployment and its defaults are the ones to copy. Both services drop every
capability, refuse privilege escalation, run read-only with a small tmpfs, carry bounded logs and bounded
memory and process counts, and publish only to host loopback. A YAML anchor carries the shared block so the
two services cannot drift apart.

There is no healthcheck, deliberately: the chiseled base has no shell and no HTTP client, so there is
nothing in the image to probe with. Whatever sits in front should watch `/healthz` instead.

## Three things this found, none of them MCP's fault

**The documented container publish command did not work.** `-p:PublishSingleFile=true` applies to every
project in the graph, and it reached the nested Blazor client publish, which cannot use an application host:
`NETSDK1098`. The client publish inside `Graft.Server.csproj` already removes `RuntimeIdentifier`,
`SelfContained`, `PublishAot` and `PublishTrimmed` from the nested build, and `PublishSingleFile` was simply
missing from that list. It and `PublishReadyToRun` are removed now. This is unrelated to MCP and predates it.

**Secret files must be readable by UID 1654.** Compose secrets outside swarm are host bind mounts, so the
file's own ownership and mode are what the container sees. A root-only `0600` key fails at startup with
`Access to the path '/run/secrets/graft-tls.pfx' is denied`, which reads like a certificate fault and is
not one. The deployment runbook says so now.

**Compose was building its own image and ignoring the tagged one, and now does not.** The services carried
only a `build:` section, so compose invented a name from the project directory and the service, and a
separately built `--tag` was never referenced. Several runs of this verification reported a server rejecting
arguments it understood perfectly, because the container was an older build than the binary on disk, and the
two ways I checked the binary both bypassed compose.

Both services now name the image through `GRAFT_IMAGE`, default `graft:local`. Confirmed in Docker: compose
tags what it builds with that name, both services resolve to it and start from it, and an image built
separately under that name is then what compose runs. One image now serves both services instead of the same
Containerfile being built twice under two invented names.

`up` still does not rebuild by itself, which is the half that remains a matter of discipline: after
republishing a binary, run `compose build` or `up --build`.

The MCP server's own argument parser now reports *why* it refused rather than printing usage and leaving the
reader to bisect their command line. That was found the hard way here: a container restarting every two
seconds with nothing but a usage message is close to undiagnosable, and in a container there is no command
line to inspect.

## What this does not cover

The MCP binary is published self-contained and **not** NativeAOT, unlike the forge, because no Model Context
Protocol library has been verified under AOT by anyone. Both run on the same runtime-deps base, so the image
is unaffected either way.

`tests/Graft.Packaging.Acceptance` still exercises the container path with rootless Podman and does not know
about the MCP profile. The run above was Docker and was driven by hand rather than by that suite.

The base image is still not pinned by digest, which remains release work and is unchanged by this.

