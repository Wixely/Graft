# 0009: The Model Context Protocol endpoint inside the forge

- Date: 2026-10-16
- Status: Accepted user request, implemented. Supersedes the separate-process and compose-profile decisions of [ADR 0008](0008-mcp-layer.md); every other decision there carries forward unchanged
- Source: "can we make it part of the main web service, gated by docker config. but otherwise configurable inside the web app"
- Review trigger: An MCP SDK release that changes its trim or AOT posture, an OAuth story for the endpoint, any proposal to let tools reach the store directly rather than through the HTTP API, or a request for per-user rather than instance-wide agent switches

## What changed, and what was found on the way

ADR 0008 kept the Model Context Protocol server out of the forge for one reason that mattered and two that
followed from it. The reason that mattered was that `Graft.Server` publishes NativeAOT and that is a release
claim, and no MCP library had been AOT-published by anyone involved. The two that followed were that a
separate process can be pointed at a running forge without redeploying, and that an operator who wants no
agent access runs nothing rather than switching a route off.

The first reason is gone. This machine now has the MSVC toolchain, so ILC runs to completion, and
`Graft.Mcp` published NativeAOT on the first attempt: `Graft.Mcp.exe`, zero ILC warnings, starts, prints its
notices, refuses a bad command line with the right exit code. `Graft.Server` with `ModelContextProtocol.AspNetCore`
inside it runs ILC to the end with no warning from the SDK or the tools library. ADR 0008 named exactly this
as its review trigger, and this is the review.

The same publish found something else, recorded in full in the
[validation record](../validation/2026-10-16-mcp-in-the-forge.md): `DnaX.Uploads` maps its routes through
the runtime request-delegate factory, which has no source-generated metadata in a trimmed binary, so the
native forge would not start at all, with or without `--mcp`. That defect predates this work and was
invisible while no machine here could run ILC. Rebuilding that package with
`EnableRequestDelegateGenerator=true`, the property its sibling MCPFab projects already set, removes every
warning; the forge then publishes native with none at all, starts, and passes both the 82-assertion MCP
suite and the 389-assertion Git suite as a native binary. Graft still pins the published package, so the
release claim waits on a DnaX change rather than on anything here.

The two consequences were real and are weighed below rather than waved away.

## Decision: the forge hosts `/mcp`, gated by the operator, switched by an administrator

`Graft.Server --mcp true` mounts a Streamable HTTP endpoint at `/mcp` on the forge's own port and
certificate. Off by default, like every other operator switch, and `GRAFT_MCP` carries it in the container
deployment. That is the gate the request asked for: "gated by docker config".

Beneath the gate, three switches live in the database and are edited in the browser under Administration,
Agent access. They are read on every request to the endpoint, so a change takes effect on the next call and
nothing restarts. That is the "otherwise configurable inside the web app".

| Switch | Default | What it governs |
| --- | --- | --- |
| Serve agents | on | Whether `/mcp` answers at all. Off, it is a 404 until turned back on. |
| Allow writes | off | Whether the three mutating tools perform. Off, they are refused and the refusal names this page. |
| Serve callers with no token | off | Whether a request with no credential is served, as a stranger who sees only what is public. |

The first defaults open and the other two closed, and the asymmetry is deliberate. The operator starting the
endpoint is already the deliberate act, so making an administrator also flip a switch before anything
worked would be two answers to one question. Writes and anonymous callers are each a widening with a cost,
so each stays a thing somebody has to ask for, exactly as `--allow-writes` and `--allow-anonymous` were on
the launched server.

Every change to the switches is audited with the whole resulting posture, not as a bare "changed", and the
forge prints that posture on startup beside its other lines:

```text
MCP: https://graft.example.internal:8443/mcp; enabled=true; credentials=required; writes=refused
```

### What this gives up, honestly

**A route switched off rather than nothing listening.** With `--mcp false` the forge answers `/mcp` with a
404 that names the flag. Something is listening, because the forge is; what it says is that there is no
endpoint. The alternative, letting the request fall through to the browser shell with a 200, would be worse
for a client that asked for a protocol. The separate launcher still exists for an operator who wants agent
access to be a process they can see in a process list.

**Pointing it at an already-running forge.** Still possible, through `Graft.Mcp`, which remains the stdio
launcher for clients that start their server locally and still has its own HTTP listener. What moved is the
deployed shape.

## Decision: the tools call the API, even from inside the process

The tools inside the forge are the same `GraftTools` the launcher uses, and they still reach Graft over the
HTTP API with the caller's own token. Nothing reaches into the store.

That looks indirect and is the point. The ADR 0008 property worth keeping was that an agent can do exactly
what its token could do with curl, so the blast radius is a credential an administrator can see and revoke
rather than the care with which the tools were written. Every authorization decision an API route makes,
scope checks, the deliberate 404 that hides a private project from a stranger, the operator's public-browsing
switch making a stored visibility inert, is made once, in the route, for agents and people alike. Reaching
the store directly would mean reimplementing each of those for ten tools and keeping them in step.

So the forge connects to itself. Requests are addressed to the public origin, so SNI, the Host header and
every URL the API builds into a result are the ones a browser would see; an agent is handed links a person
could open rather than a loopback address. The socket, though, is opened straight to the listener this
process bound, never through name resolution, and the certificate that answers must be byte-for-byte the one
this process is serving. There is nothing between the two ends to impersonate, which is a stronger position
than any chain validation.

Writes are decided per request from the live setting, so the client built for a request carries the answer
and the refusal names the page where it changes.

## Decision: one image, one port, one certificate

The compose file no longer has an `mcp` profile, a second service, a second port, a CA extracted from the
PKCS#12, or a network alias so one container could verify the other. The internal plain-HTTP overlay goes
with it: there is no second hop to decide not to encrypt. `GRAFT_MCP=true` on the forge is the whole change
to a deployment, and the Containerfile no longer carries the launcher.

The launcher is still built and still tested. It is the right tool for an MCP client that launches its
server over stdio, which no in-process endpoint can serve.

## Decision: the gate is a startup flag, not a stored setting

An administrator cannot turn the endpoint on from the browser if the operator did not start it. The page
says so and names the flag. This keeps the forge's existing shape, where everything that widens what a
server does for strangers is an operator decision stated in a unit file or container command, and a
restored database cannot bring a surface with it that the new host's operator never enabled.

## What was verified

The MCP suite grew from 36 to 82 assertions. The new ones drive the forge's own endpoint as a client would
and change the switches as an administrator would, over the real admin route with a browser session, then
assert the next agent request sees the change: writes refused by default and performed once allowed, an
anonymous caller refused by default and then served as a stranger who cannot see a private project, the
endpoint paused and resumed without a restart, result links on the public origin, the posture line, the
audit rows, and a server started without the flag answering a 404 that names it. The launcher's stdio and
HTTP checks are unchanged and still pass against the same forge.

The main Git/API/browser suite still passes its 389 assertions. Both suites were also run against a
NativeAOT-published forge, which passes them in full once the upload package is rebuilt as described in the
[validation record](../validation/2026-10-16-mcp-in-the-forge.md).
