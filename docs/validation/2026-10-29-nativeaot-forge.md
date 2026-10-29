# The forge at 2026-10-29, published and run NativeAOT

Recorded: 2026-10-29, in two runs the same day. Owner: Implementation agent. Follows [the 2026-10-16 evidence](2026-10-16-mcp-in-the-forge.md), which first ran the forge native and found the one package property that stood in the way.

## Why

Everything built on 2026-10-29 was verified on the managed runtime: the collaboration depth list
(ADRs 0019 to 0023), search, forks, LFS, packages and the registry (0024 to 0027), the Git engine rewrite
(0028), file deletion (0029) and token rotation (0030). The handoff's standing next action was a NativeAOT
publish with the Git suite against it, because the native build is what the deployment is meant to run and
a route or a serializer that only works through runtime reflection is invisible until then.

## How

`win-x64`, Release, `GraftServerAot=true`, `IlcUseEnvironmentalTools=true`, under `vcvars64.bat` with
`Platform` cleared, as [build-and-test.md](../build-and-test.md) records, with one addition: the publish's
implicit restore produced an assets file without the `net10.0/win-x64` target (NETSDK1047), so the server
is restored for the runtime explicitly first and the publish runs with `--no-restore`. The cause was not
run down; the workaround is in build-and-test.md.

**First run, against the published `DnaX.Uploads` 10.0.0-alpha.7**, which cannot start native
([Wixely/DnaX#17](https://github.com/Wixely/DnaX/issues/17)). The fix was on an unmerged DnaX branch, so the
package was packed from it as a clearly local version into a scratch feed, the pin pointed at it for the
run, and the pin and lock files reverted afterwards. That run passed with the counts below.

**Second run, against `DnaX.Uploads` and `DnaX.Data.Migrations.Sqlite` 10.0.0-alpha.13**, after DnaX merged
the fix (its PR #18) and bumped. The three packages Graft uses were packed from DnaX `main` at `4791c75`,
the commit DnaX's CI built and pushed to GitHub Packages, into the local feed Graft restores from, and the
pins moved to alpha.13. Nothing was reverted: this is the state the repository is in. The managed server
was rebuilt on the new packages and both suites run against it before the publish.

## What the publish found

One defect, in work from the same day. The GitLab-shaped token rotation route was mapped through a delegate
variable rather than a lambda. The Request Delegate Generator can only see a lambda or a method group, so
it skipped the endpoint (RDG002) and the route would have been built by the runtime factory, which the
native build does not carry and which the publish therefore refuses (IL2026, IL3050 as errors). The route
is mapped through a lambda now. The managed suite could not have found this, because the managed runtime
has the factory.

## Result

Both publishes complete with **zero ILC warnings** and produce a 65 MB `Graft.Server.exe`, which starts
reporting `MODE: NativeAOT`, 177 embedded assets, and `READY:`.

| Suite | Managed, alpha.13 | Native, alpha.7 plus the fix branch | Native, alpha.13 |
| --- | --- | --- | --- |
| `Graft.Git.Acceptance` | 947 of 947 | 947 of 947 | **947 of 947** |
| `Graft.Mcp.Acceptance` | 104 of 104 | 104 of 104 | **104 of 104** |

The Git suite drove the native binary through real Git clients for every workflow it covers, including
the repository format conversion, delta packs, housekeeping, protocol version 2, LFS, forks, packages, the
registry, file deletion and token rotation. The migrations package moved six prerelease versions with the
upload package; the suite's migration, preservation and recovery checks are among the 947.

## Boundaries

Windows x64 only. The static-musl Linux build, the required Linux 3.10.108 host, the service modes and the
container were not exercised here; the entries in [build-and-test.md](../build-and-test.md) for those
stand as last recorded. The native forge is no longer blocked by a dependency; what remains for a release
is the durable package source, which exists now at GitHub Packages under Wixely but is not in this
repository's `nuget.config`, so a clean external checkout still restores nothing until that feed is adopted.
