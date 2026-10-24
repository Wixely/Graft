# Repository capacity validation

- Date: 2026-10-24
- Source: Local Windows .NET build and executable acceptance runs
- Scope: Managed runtime; managed Git engine, durable storage, HTTP transport and archives
- Review trigger: Changes to limits, pack framing, storage, runtime or deployment resources

The Git/API/browser acceptance build passes with zero warnings/errors. All **645**
assertions pass, including four new checks: a push containing 20 MiB of random data
and a 40 MiB file, a real Git clone with byte comparison and `fsck --strict`, and a
ZIP download whose large entry matches the original SHA-256. This crosses the old
16 MiB HTTP, 4 MiB object and 32 MiB repository limits. Existing permissions,
interrupted commits, browser, merge, tag and archive checks remain in the run.

`Graft.Git.Scale` runs the 42 malformed-input/protocol self-checks, tests stream offsets
above 2 GiB, and exercises receive, persistence, restart, fetch and real Git
`index-pack --strict` / `fsck --full --strict`. The default fixture is 96 MiB.
With `--full`, its distinct object payloads total exactly **4,294,967,296 bytes**,
including trees and commits. A further one-byte object is rejected with
`repository-budget`, and reloading confirms no extra durable object was published.

The final full run passed in approximately 65 seconds for receive through Git verification,
with a peak test-process working set of 488,714,240 bytes (about 466 MiB). Fixture generation
and the separate Git process are outside those measurements. The fixture is deliberately
compressible: this demonstrates the 4 GiB expanded boundary, not a 4 GiB wire transfer,
one million objects, or sustained concurrent throughput. The offset check is synthetic.

Commands:

```powershell
dotnet run --project tests/Graft.Git.Scale/Graft.Git.Scale.csproj
dotnet run --project tests/Graft.Git.Scale/Graft.Git.Scale.csproj -- --full
dotnet build tests/Graft.Git.Acceptance/Graft.Git.Acceptance.csproj
dotnet tests/Graft.Git.Acceptance/bin/Debug/net10.0/Graft.Git.Acceptance.dll src/Graft.Server/bin/Debug/net10.0/Graft.Server.dll
```

The local feed has the already-documented DnaX repack/hash mismatch. Validation restored
with `-p:NuGetLockFilePath=obj/scale.packages.lock.json --force-evaluate`, and built
using the same lockfile override. Subsequent server builds used
`-p:GraftSkipClientPublish=true` after the first successful client publication.
Tracked dependency locks are not part of this capacity change; pre-existing user edits
to the Core and Operations lockfiles are preserved.
Generated `Graft.Storage.generated.cs` still contains Dapper `InterceptsLocationAttribute`
entries for the storage calls; the generator remains active.

Container and systemd templates now place temporary work on the writable data volume;
the container memory default is 2 GiB. These template changes were inspected, not deployed.
No new dependencies, NativeAOT flags or linker suppressions were introduced. NativeAOT,
static-musl Linux, the required legacy host, reverse proxies and a full-size HTTP transfer
remain unverified for this change. No deployment or publication was performed.

Next action — Operator + implementation agent: test a representative large repository
against the intended published server, with sufficient service-account temporary disk
space and matching reverse-proxy request-size/time limits.
