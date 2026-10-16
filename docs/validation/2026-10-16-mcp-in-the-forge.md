# The MCP endpoint inside the forge

Recorded: 2026-10-16. Owner: Implementation agent. Decision: [ADR 0009](../decisions/0009-mcp-in-the-forge.md), partly superseding [ADR 0008](../decisions/0008-mcp-layer.md).

## What changed

`Graft.Server --mcp true` hosts a Streamable HTTP Model Context Protocol endpoint at `/mcp`, on the forge's
own port and certificate. Off by default; `GRAFT_MCP` carries it in the container deployment. Beneath that
gate, an administrator sets three switches in the browser under Administration, Agent access, stored in a
new `ServerSettings` table (migration 5) and read on every request to the endpoint: serve agents (default
on), allow writes (off), serve callers with no token (off). Changes are audited with the resulting posture.

The tools moved to `src/Graft.McpTools`, shared by the forge and the `Graft.Mcp` launcher, and still call the
HTTP API with the caller's own token. Inside the forge they do so over a connection to the forge's own
listener, addressed to the public origin and accepting only the certificate this process serves.

The compose `mcp` profile, second service, port 8444, extracted CA and plain-HTTP overlay were removed. The
Containerfile no longer carries the launcher.

## Verified on the managed runtime

`Graft.Mcp.Acceptance` grew from 36 to **82 assertions**, all passing against the Debug server and launcher:

- The forge prints `MCP: <origin>/mcp; enabled=true; credentials=required; writes=refused` on startup, and
  `MCP: off` when started without the flag.
- `/mcp` refuses a caller with no token with `401`, a `WWW-Authenticate` header, and a body that names the
  administrator page; completes the handshake over `text/event-stream` for a token; advertises all ten
  tools; answers every read tool against a seeded repository; refuses an unresolvable token with the forge's
  own refusal; and refuses writes by default with a remedy that names the page rather than a flag.
- Result links are built on the public origin, not on the loopback socket the forge used to call itself.
- The settings route refuses a token and requires a browser session; the session response carries
  `mcp_available`; the stored defaults are serving, no writes, no anonymous callers; the response names the
  endpoint URL.
- Through the real admin route with a signed-in administrator: allowing writes makes the next write
  succeed; allowing anonymous callers lets a stranger complete the handshake and read the public project
  and nothing private; turning the endpoint off makes it a `404` while the settings stay reachable; turning
  it back on restores it. None of this restarted anything.
- Four changes produced four audit rows carrying the whole posture.
- A forge started without `--mcp true` answers `/mcp` with a `404` that names the flag, not the browser
  shell, and reports `mcp_available: false`.
- The launcher's stdio and HTTP transports and both of its overrides pass unchanged against the same forge.

`Graft.Git.Acceptance` still passes its **389 assertions** against the Debug server.

## NativeAOT: what is now known

This machine has the MSVC toolchain, so ILC runs to completion for the first time in this repository. The
SDK could not find it because the Visual Studio installer directory has no `vswhere.exe`; the publish runs
inside `vcvars64.bat` with `IlcUseEnvironmentalTools=true` and `Platform` cleared. The command is in
[build-and-test](../build-and-test.md).

**`Graft.Mcp` publishes NativeAOT clean**: `Graft.Mcp.exe`, zero ILC warnings, starts, prints its
third-party notices, and refuses a bad command line with exit code 2 and the reason. Done twice, before and
after the tools moved to the shared library.

**`Graft.Server` with `ModelContextProtocol.AspNetCore` inside it produces no ILC warning from the MCP SDK
or from `Graft.McpTools`.** The question ADR 0008 left open is answered: the SDK is AOT-clean in the forge.

### The one thing that was not clean, and the fix

The first native publish stopped on `DnaX.Uploads`: eight `IL2026` and eight `IL3050`, every one on
`UploadEndpoints.MapDnaXUploads`. Relaxing warnings-as-errors for one diagnostic run produced a binary that
would not start at all, with `--mcp true` and `--mcp false` alike:

```text
System.NotSupportedException: JsonTypeInfo metadata for type 'System.Threading.Tasks.Task`1[Microsoft.AspNetCore.Http.IResult]'
was not provided by TypeInfoResolver of type '[Graft.Contracts.ApiJson]'.
   at Microsoft.AspNetCore.Http.RequestDelegateFactory.AddResponseWritingToMethodCall(...)
   at Microsoft.AspNetCore.Routing.RouteGroupBuilder.GroupEndpointDataSource.get_Endpoints()
```

The cause is the shape of the upload routes, not their serialization: they are minimal-API lambdas taking
injected services and returning `Task<IResult>`, so the runtime `RequestDelegateFactory` builds their
delegates and asks for metadata no trimmed binary has. The package's own payloads are already
source-generated through `UploadJsonContext`.

The Request Delegate Generator replaces that factory at compile time, and it rewrites the `Map` calls in the
compilation it runs in. The consuming application cannot enable it on a referenced library; the library
enables it for itself. `DnaX.Uploads` does not, while the DnaX repository's own MCPFab projects do, along
with `IsTrimmable` and the trim, AOT and single-file analyzers. The upload package is the experimental one
and carries none of that.

Measured against the DnaX source at the commit this repository pins:

| Build of `DnaX.Uploads` | IL2026 | IL3050 |
| --- | --- | --- |
| As shipped | 16 | 16 |
| With `EnableRequestDelegateGenerator=true` | 0 | 0 |

It also builds clean with `IsTrimmable` and both analyzers on, so nothing else in the package objects.

### The forge, published and run native

The package was rebuilt locally with that one property, packed as `10.0.0-alpha.7-rdgtest`, and Graft was
published NativeAOT against it with warnings-as-errors left on:

- The publish succeeds with **zero ILC warnings** and produces a 55 MB `Graft.Server.exe`.
- It starts, reporting `MODE: NativeAOT`, the `MCP:` posture line and `READY:`.
- `/healthz` answers `healthy`; `/mcp` answers `401` without a token; and the upload capabilities route,
  the one that could not previously be built at all, answers `401` rather than crashing the process.
- **`Graft.Mcp.Acceptance` passes all 82 assertions against the native forge**, and
  **`Graft.Git.Acceptance` passes all 389**.

So the forge's NativeAOT claim is reachable today and the MCP endpoint does not stand in its way. What
stands in the way is one MSBuild property in a package Graft does not own. Graft still pins the published
`10.0.0-alpha.7`; the local package and the reference to it were reverted after the run, and the DnaX
checkout was not modified.

## The page, in a browser

Driven in headless Chromium through Playwright against the Debug server started with `--mcp true`: sign in as
the bootstrapped administrator lands on the projects page; the sidebar shows the Agent access link; the page
shows the heading, the endpoint URL `https://127.0.0.1:7871/mcp`, a Serving label, and three checkboxes in
the stored default state (on, off, off); Save is disabled until a switch changes; ticking Allow writes enables
it; saving shows "Agent access updated. The change applies to the next request."; and after a reload the
switch is still on, read back from the server.
