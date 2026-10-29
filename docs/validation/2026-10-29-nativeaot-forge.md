# The forge at 2026-10-29, published and run NativeAOT

Recorded: 2026-10-29. Owner: Implementation agent. Follows [the 2026-10-16 evidence](2026-10-16-mcp-in-the-forge.md), which first ran the forge native and found the one package property that stood in the way.

## Why

Everything built on 2026-10-29 was verified on the managed runtime: the collaboration depth list
(ADRs 0019 to 0023), search, forks, LFS, packages and the registry (0024 to 0027), the Git engine rewrite
(0028), file deletion (0029) and token rotation (0030). The handoff's standing next action was a NativeAOT
publish with the Git suite against it, because the native build is what the deployment runs and a route or
a serializer that only works through runtime reflection is invisible until then.

## How

`win-x64`, Release, `GraftServerAot=true`, `IlcUseEnvironmentalTools=true`, under `vcvars64.bat` with
`Platform` cleared, as [build-and-test.md](../build-and-test.md) records. Two things about the run:

- `DnaX.Uploads` is still pinned at the published `10.0.0-alpha.7`, which cannot start native
  ([Wixely/DnaX#17](https://github.com/Wixely/DnaX/issues/17)). The fix exists on the DnaX branch
  `fix/issue-17-aot-generators`, unmerged and unreleased at the time of writing. For this run the package
  was packed from that branch as `10.0.0-alpha.12-local` into a scratch feed, the pin was pointed at it, and
  the pin and the lock files were reverted afterwards. Nothing in this repository changed to make the native
  build work; the local package is not kept.
- The publish's implicit restore produced an assets file without the `net10.0/win-x64` target
  (NETSDK1047). An explicit `dotnet restore -r win-x64` with the same properties, followed by the publish
  with `--no-restore`, went through. The cause was not run down; the workaround is in build-and-test.md.

## What the publish found

One defect, in work from the same day. The GitLab-shaped token rotation route was mapped through a delegate
variable rather than a lambda. The Request Delegate Generator can only see a lambda or a method group, so
it skipped the endpoint (RDG002) and the route would have been built by the runtime factory, which the
native build does not carry and which the publish therefore refuses (IL2026, IL3050 as errors). The route
is mapped through a lambda now. The managed suite could not have found this, because the managed runtime
has the factory.

## Result

The publish completes with **zero ILC warnings** and produces a 65 MB `Graft.Server.exe`, which starts
reporting `MODE: NativeAOT`, 177 embedded assets, and `READY:`.

| Suite | Against the native binary |
| --- | --- |
| `Graft.Git.Acceptance` | **947 of 947** pass |
| `Graft.Mcp.Acceptance` | **104 of 104** pass |

Those are the same counts the managed server passes. The Git suite drove the native binary through real
Git clients for every workflow it covers, including the repository format conversion, delta packs,
housekeeping, protocol version 2, LFS, forks, packages, the registry, file deletion and token rotation.

## Boundaries

Windows x64 only. The static-musl Linux build, the required Linux 3.10.108 host, the service modes and the
container were not exercised here; the entries in [build-and-test.md](../build-and-test.md) for those
stand as last recorded. The release claim still waits on a DnaX release that carries the generator
property: until then a native forge from this repository as pinned does not start, and the deployment
runs the managed server.
