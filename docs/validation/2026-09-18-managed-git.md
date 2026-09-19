# MVP-01 managed Git feasibility result

Date: 2026-09-18. Review: 2026-09-25 or on protocol, dependency, runtime, storage or target-host changes.
Owner: Implementation agent. Status: **PASS for bounded Windows feasibility; not production acceptance**.
Source: Local build, self-check and real Git-client execution. User authorized the recommended MVP-01 work with "do recommended".

## Result and decision

A project-owned C# Git subset can interoperate with Git **2.53.0.windows.2** and run as a Windows x64 NativeAOT executable without a server-side Git process or native Git library. Proceed with the other bounded architecture gates; do not treat this as approval to deploy the engine or a claim of GitLab parity.

The implementation is a disposable in-memory smart HTTP v0 server at one loopback-only repository URL. The initial gate intentionally isolates the protocol/pack question from persistent storage and authentication. It is not the durable repository component proposed for the MVP. Source and reproduction commands are in [the probe runbook](../../experiments/managed-git/README.md).

## Build identity and commands

- Windows x64; SDK 10.0.300; JIT runtime and NativeAOT compiler/runtime packages 10.0.8.
- `dotnet build Graft.slnx`: exit 0, zero warnings/errors after adding the probe and client harness.
- `dotnet publish experiments/managed-git/Graft.ManagedGit.csproj -c Release -r win-x64 --self-contained true -p:GitProbeAot=true -o artifacts/managed-git/win-x64`: exit 0; no emitted AOT/trimming warnings and no warning suppression added.
- Native executable: **10,497,024 bytes**. SHA-256: `3448eb66ea162b4da112406e49104bbc90407386269c91beef7fcdfac6ad31ea`.
- Native runtime/toolchain restore is recorded separately in `experiments/managed-git/packages.win-x64.native.lock.json`; ordinary builds retain their own lock file. No explicit third-party package reference was introduced.
- Copied only `Graft.ManagedGit.exe` to ignored `artifacts/managed-git/standalone/`; no PDB, runtime config or static-assets sidecar was copied. The harness started that executable with a separate temporary working directory. It reported `MODE: NativeAOT` from `RuntimeFeature.IsDynamicCodeSupported == false`.
- Source changes are uncommitted local work; this artifact hash identifies the tested binary, not a published release or source tag. Existing compatibility-probe source and the accepted Linux artifact were not modified.

Client acceptance commands, both exit 0:

```powershell
dotnet run --project experiments/managed-git-client/Graft.ManagedGit.Client.csproj --no-build -- experiments/managed-git/bin/Debug/net10.0/Graft.ManagedGit.dll
dotnet run --project experiments/managed-git-client/Graft.ManagedGit.Client.csproj --no-build -- artifacts/managed-git/standalone/Graft.ManagedGit.exe
```

## Exercised behavior

| Check | JIT | NativeAOT |
| --- | --- | --- |
| 42 direct codec/ref/graph/cancellation checks executed inside server binary | PASS | PASS |
| Empty clone with client's default protocol negotiation | PASS | PASS |
| Initial push, nested branch name, annotated tags and populated clone | PASS | PASS |
| Incremental push and fetch, tag update by adding a new tag | PASS | PASS |
| Compare all reachable object IDs and advertised branch/tag IDs | PASS | PASS |
| Client `git fsck --full --strict` after clone/fetch | PASS | PASS |
| Independent Git-generated REF and OFS delta packs sent over HTTP | PASS | PASS |
| HTTP rejection: checksum, stale ref, traversal, truncation, oversized object, invalid mixed batch | PASS | PASS |
| Rejected requests preserve advertised refs; subsequent fetch/fsck works | PASS | PASS |
| Symlink and gitlink represented as objects without server checkout | PASS | PASS |

The deterministic fixture retained 39 objects. Final server counters recorded 50 OFS and 28 REF delta entries processed across successful receives, including intentionally repeated packs. Those are cumulative decode counts, not unique objects or a benchmark. Thin REF bases already present and forward REF bases were also tested directly by the server's self-checks.

Negative self-checks include invalid pkt-line headers/lengths, bad pack version/count/checksum, trailing bytes, corrupt zlib, a missing zlib trailer with an otherwise valid pack checksum, inflated-size mismatch, missing bases, delta copy/insert/size/depth failures, aggregate expansion limits, unsafe/duplicate/conflicting refs, missing reachable objects, branch-to-blob, non-fast-forward updates and rejection without partial in-memory mutation. This is a bounded regression suite, not exhaustive fuzzing or a security certification.

Two implementation issues were found and fixed before the final run: an empty zlib writer needs an explicit complete empty member for empty trees/blobs; the reader must turn premature source EOF into an error because `ZLibStream` can otherwise finish a truncated member without an exception. The regression tests run in both deployment modes.

## Dependency and fidelity assessment

Selected for the experiment: a small project-owned C# implementation using .NET streams, compression and cryptography. No NGit, JGit, LibGit2Sharp or Git source was copied, and no external Git implementation is called by the server. The separate harness invokes installed Git to provide an independent client and pack producer. Platform-native runtime, cryptography and compression dependencies still exist; "managed Git" does not eliminate them. License/provenance links and redistribution follow-up are in the probe runbook; no project license was chosen.

The server deliberately sends full reachable packs, retains the entire small repository in memory, and reads compressed input conservatively one byte at a time. The limits protect this local experiment; they do not establish production memory/throughput targets. Restricted ref names, no deletes or force updates, and incomplete Git object-semantic checks are known limitations. The interface is Git smart HTTP, not a GitLab REST endpoint. No API conformance row is marked passing by this result.

## Remaining gates

- No Linux/static-musl publish or required Linux 3.10.108 host execution for this new component. Compression is new relative to the earlier probe and needs its own static dependency/linking validation. Preserve the earlier accepted static-musl configuration when adding that test; no CoreCLR fallback is proposed.
- No TLS/authentication, persistent storage, process restart recovery, multi-process locking, crash/disk-full handling, service integration or production backup/restore.
- No browser UI, database migration integration, GitLab APIs, importer, CI or runner.
- No broad client/version matrix, large-repository/performance result, full malformed-object validation or independent security review. Slow-client deadlines and network cancellation are implemented but have not been acceptance-tested; only direct parser cancellation was exercised.

**Recommended next action — Owner: Implementation agent:** execute MVP-02, the standalone Blazor client/NativeAOT API and authentication feasibility gate. Keep this Git prototype isolated until durable/authenticated storage and the required-host tests are complete. MVP-03 and MVP-04 through MVP-08 remain pending.
