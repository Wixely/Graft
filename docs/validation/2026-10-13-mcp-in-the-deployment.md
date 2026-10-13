# The MCP endpoint in the standard deployment

Recorded: 2026-10-13. Owner: Implementation agent. Decision: [ADR 0008](../decisions/0008-mcp-layer.md).

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

## Verified, in a real container runtime

Docker 29.8.0 inside WSL Ubuntu 24.04, building the real `Containerfile` and running the real `compose.yaml`
against a self-signed certificate whose subject alternative name matches the configured hostname.

| Check | Result |
| --- | --- |
| Services selected with no profile | `graft` only |
| Services selected with `--profile mcp` | `graft` and `graft-mcp` |
| Forge health with no profile | `200` |
| Port 8444 with no profile | nothing listening |
| MCP endpoint, no credential | `401`, with the header and message saying how to authenticate |
| MCP endpoint, with a token | `200`, `text/event-stream`, `"serverInfo":{"name":"graft","version":"1.0.0"}` |
| Tool call reaching the forge | refused by Graft: `The token was not accepted` |
| Write tool, read-only deployment | refused, naming `--allow-writes` |
| Compose without `GRAFT_CA_CERTIFICATE` | valid, and the forge starts |

The tool-call row is the one that proves the wiring rather than the configuration. A bogus but well-formed
token gets past this server's own check, so the call travels to the forge over TLS. Getting back "the token
was not accepted" means the alias resolved, the certificate verified, and Graft answered. A broken chain
would have said "Graft could not be reached" instead.

The last row matters for operators who never enable MCP: the two variables that exist only for this profile
are not required to run the forge.

## Two things this found

**The documented container publish command did not work.** `-p:PublishSingleFile=true` applies to every
project in the graph, and it reached the nested Blazor client publish, which cannot use an application host:
`NETSDK1098`. The client publish inside `Graft.Server.csproj` already removes `RuntimeIdentifier`,
`SelfContained`, `PublishAot` and `PublishTrimmed` from the nested build, and `PublishSingleFile` was simply
missing from that list. It and `PublishReadyToRun` are removed now. This is unrelated to MCP and predates it.

**Secret files must be readable by UID 1654.** Compose secrets outside swarm are host bind mounts, so the
file's own ownership and mode are what the container sees. A root-only `0600` key fails at startup with
`Access to the path '/run/secrets/graft-tls.pfx' is denied`, which reads like a certificate fault and is
not one. The deployment runbook says so now.

## What this does not cover

The MCP binary is published self-contained and **not** NativeAOT, unlike the forge, because no Model Context
Protocol library has been verified under AOT by anyone. Both run on the same runtime-deps base, so the image
is unaffected either way.

`tests/Graft.Packaging.Acceptance` still exercises the container path with rootless Podman and does not know
about the MCP profile. The run above was Docker and was driven by hand rather than by that suite.

The base image is still not pinned by digest, which remains release work and is unchanged by this.
