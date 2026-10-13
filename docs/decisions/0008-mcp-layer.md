# 0008: The Model Context Protocol layer

- Date: 2026-10-13
- Status: Accepted user request, implemented, with one substitution the request did not specify
- Source: "can you build this using DNAX? so it remains backwards compatible with the gitlab API layer but we have our own MCP layer?"
- Review trigger: A DnaX release that makes MCPFab embeddable, a NativeAOT publish of `Graft.Mcp` actually running, or any proposal to move MCP into the forge process

## What was asked, and what was built

The request named DnaX. DnaX does ship MCP packages, and they were read before this was written. Neither
fits, and the reasons differ, so the MCP layer is built on the official Model Context Protocol C# SDK
instead. That is the substitution, stated here rather than buried.

**`DnaX.MCPFab` cannot be embedded.** It is an application template, not a library: `McpFabHost.CreateBuilder`
unilaterally creates its own `WebApplication`, binds its own Kestrel listener, replaces the static Serilog
logger, installs its own configuration chain, registers its own default authentication scheme, and owns the
process exit code. There is no way to hand it a host that already exists. Adopting it would not give Graft
an MCP layer; it would make Graft an MCPFab server. It also costs roughly twenty runtime assemblies,
including the whole Serilog stack, and taking its companion `DnaX.MCPFab.Build` package is a hard build
error unless Graft's own `Directory.Build.props` adopts MCPFab's trim policy block.

**`DnaX.RemoteAccess.Mcp` is the right shape and still not worth taking.** It is twenty-four lines, and the
two calls it makes are the official SDK's own API. What it adds on top is `DnaX.RemoteAccess`'s credential
model: a second, SQLite-backed token store beside the one Graft already has. Graft does not need a second
answer to who is calling.

So the SDK directly. This is a smaller dependency than either DnaX route, because DnaX's own MCP package
depends on the same SDK and adds to it.

What DnaX contributed anyway was the hard-won list of things that break, and those are followed:
`WithTools<T>()` and never the assembly-scanning overload, whose failure mode is a server that starts
cleanly and advertises nothing; JSON built as node trees rather than serialized from anonymous types; and
reflection-based serialization switched off so those failures surface at build time instead of at runtime
in a trimmed binary.

## Decision: a separate process, not another route

`Graft.Mcp` is its own executable. It talks to Graft over the ordinary HTTP API with a personal access
token, exactly as any other client would.

**Graft.Server publishes NativeAOT and that is a release claim.** No MCP library has been AOT-published by
anyone involved: DnaX verifies trimmed and single-file behaviour with a real smoke test and has never run
`PublishAot`, and its own build props say `IsAotCompatible` stays off until something actually publishes
native. Putting that inside the forge would put a release claim behind a library nobody has tested that
way. Keeping it out means the forge's dependency closure is unchanged, exactly.

Three further things fall out of it, and all are improvements rather than consolations.

- It can be pointed at a Graft that is already running, including one already deployed, with nothing to
  redeploy.
- An operator who wants no agent access runs nothing. There is no route to leave switched off, and no
  configuration mistake that exposes one.
- The tools can reach precisely what the token's owner can reach. The blast radius is a credential an
  administrator can see and revoke, not the care with which these tools were written.

## Decision: the GitLab layer gains a route it was missing, not a Graft invention

The MCP layer needed two things no API client could do: change an existing project's visibility, and
publish a project page. Before this, the only write route on the GitLab-compatible surface was project
creation, so a client could create a project already public and then never change it.

Those land in two different places, deliberately.

- **`PUT /api/v4/projects/:id`** takes `visibility`. GitLab has this route and this attribute, so
  implementing a bounded subset of it makes Graft more compatible, not less. It accepts that one attribute
  and refuses every other by name, the way the create route already does, so a caller is never told it
  succeeded at something that did not happen.
- **`PUT /api/graft/projects/:id/page`** publishes the curated page. GitLab has no such concept, so it stays
  under Graft's own prefix. The same reasoning keeps `POST /api/v4/projects/:id/uploads` unclaimed in
  [ADR 0006](0006-project-pages-and-public-browsing.md): a path somebody else owns should not come to mean
  something only this server understands.

Both check the token's scope **before** looking the project up. A token that could never perform the action
is told its scope is wrong rather than that the project does not exist. A credential's own scope is not a
secret about somebody else's project, and project concealment is unaffected: a caller with the right scope
and no access still gets the same 404 as for a project that is not there.

## Decision: writes are off by default

`Graft.Mcp` refuses every mutating tool unless started with `--allow-writes true`, and says which switch
would permit it. An agent handed this server by default can read a forge and cannot alter one.

This is not redundant with the token's scopes. Scopes are coarse, and the same token is often the one a
person already uses; the switch lets an operator hand an agent a capable credential and still bound what
the agent does with it to reading.

## Decision: no flag that skips certificate verification

A self-hosted forge often presents a certificate from a private authority. The answer is `--ca-certificate`,
naming the certificate to trust, rather than an option to stop checking. A tool that carries a credential to
a host it did not verify hands that credential to whoever answered.

Naming a root replaces the system store rather than adding to it, which pins the client to that one
authority. For a tool pointed at a single known forge that is the stronger position, and the hostname is
still checked either way.

The token is read from a file and never taken on the command line, matching `Graft.Admin bootstrap`. A
command line is visible to every process on the machine and lands in shell history.

## What is not settled

**NativeAOT for `Graft.Mcp` itself is unverified**, the same environmental gap as the rest of the
repository: this machine has no C++ build tools, so ILC never runs. What is now known is better than
nothing. The project builds clean with `IsAotCompatible`, the AOT, trim and single-file analyzers all on,
and reflection-based JSON off. Those analyzers caught a real hazard while this was written, a generic
`JsonArray.Add<T>` that reflects over its argument, and failed the build until it was fixed. An AOT publish
reaches the native linker and stops there on the missing toolchain, not on anything in the code.

**The transport is stdio only.** That is what a local MCP client launches directly. Streamable HTTP is the
other half of the SDK and is not wired up; a server reachable over HTTP needs an authentication story of its
own, and that is a larger decision than this one.
