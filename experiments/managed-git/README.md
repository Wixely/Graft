# Managed Git feasibility probe

MVP-01 only: a disposable, bounded, single-repository smart HTTP server in C#/.NET 10. The reusable Git parsing, object hashing, delta application, refs and protocol engine now lives in `src/Graft.Git`; this experiment remains its in-memory regression host. No Git executable, libgit2 or other Git library is used by the server. The separate client harness invokes installed Git as an independent oracle.

The server binds only IPv4 loopback on an OS-selected port, prints `READY: http://127.0.0.1:<port>` and serves `/fixture.git`. It accepts no configuration arguments other than `--self-test`. Storage is in memory and lost at exit. It has no authentication, TLS, durable refs, hooks, checkout, UI or database. Use synthetic data only. Do not expose this experiment as a forge.

## Run and debug

From the repository root, using Windows PowerShell 5.1 and the pinned .NET SDK:

```powershell
dotnet build Graft.slnx
dotnet run --project experiments/managed-git/Graft.ManagedGit.csproj --no-build -- --self-test
dotnet run --project experiments/managed-git-client/Graft.ManagedGit.Client.csproj --no-build -- experiments/managed-git/bin/Debug/net10.0/Graft.ManagedGit.dll
```

The acceptance harness starts/stops the server, isolates Git configuration, creates synthetic repositories in a unique temporary directory and removes only that directory. It does not modify this checkout's Git configuration or refs. It compares every reachable object ID, checks advertised branch/tag IDs and runs `git fsck --full --strict`. Git-generated REF/OFS packs are sent to the server independently of its pack writer. Fixed test identities use `example.invalid`; never substitute real repository data or credentials.

VS Code has **Debug managed Git probe (loopback, disposable)** and **Debug managed Git acceptance client** configurations. Focused build tasks build the required projects. See [VS Code debugging](../../docs/vscode-debugging.md) for self-check and attach options. The client can also target a separately published native executable:

```powershell
dotnet publish experiments/managed-git/Graft.ManagedGit.csproj -c Release -r win-x64 --self-contained true -p:GitProbeAot=true -o artifacts/managed-git/win-x64
dotnet run --project experiments/managed-git-client/Graft.ManagedGit.Client.csproj --no-build -- artifacts/managed-git/win-x64/Graft.ManagedGit.exe
```

Windows NativeAOT requires the C++ native build tools. No new tooling was installed for the recorded run. The probe's `MODE: NativeAOT` output checks `RuntimeFeature.IsDynamicCodeSupported`. `dotnet run` alone remains a JIT test. Debug symbols under ignored publish output are development artifacts, not a release package.

## Deliberate protocol subset

- Git SHA-1 objects, pack version 2, smart HTTP v0; the tested Git client falls back from its default protocol negotiation without client-specific protocol settings.
- Regular commit/tree/blob/tag objects; OFS and REF deltas, forward REF bases and thin-pack bases already in the repository. The sender emits complete reachable packs without delta compression or `have` exclusion. This is inefficient but interoperable in the tested workflows.
- Receive advertises `report-status ofs-delta`; upload advertises `ofs-delta symref=HEAD:refs/heads/main`. No shallow/partial clone, protocol v2 implementation, SHA-256 objects, side-band, multi-ack, delete-refs or atomic capability is advertised. Unsupported requests are rejected.
- Upload only accepts wanted object IDs advertised as refs. Commit/tree/tag links are checked for existence and expected types. Gitlinks are external references and are not followed; symlinks remain blob data.
- Refs are restricted to safe ASCII branch/tag names. Branch updates must be fast-forward; existing tags cannot be replaced. This is a deliberate deviation from general Git server capabilities.
- Parse/validate the entire receive into temporary dictionaries, then replace the visible dictionaries under one operation gate. A failed receive does not mutate visible refs/objects. This is an in-memory property, not crash-safe storage or protocol atomic-push support.

## Budgets

| Resource | Fixed experiment limit |
| --- | --- |
| HTTP request body | 16 MiB |
| Inflated object or delta result | 4 MiB |
| Cumulative inflated input plus reconstructed delta bytes per pack | 32 MiB |
| Retained repository object bytes | 32 MiB |
| Object count per pack / retained repository | 4,096 |
| Refs and receive commands | 128 |
| Delta depth | 32 |
| Concurrent repository operations | 1; competing request gets HTTP 429 |
| Kestrel concurrent connections | 4 |
| Request header timeout / operation deadline | 5 / 20 seconds |

These are payload/work limits, not an RSS guarantee; arrays/dictionaries and HTTP buffering add overhead. Pack decompression reads input one byte at a time to prevent read-ahead across entries. Premature underlying EOF is an error rather than successful zlib completion. The reader is intentionally conservative, not optimized for production throughput. Deadlines are cooperative, not hard real-time CPU isolation.

## Dependencies, provenance and next work

No third-party package or source was added. The server uses .NET/ASP.NET Core shared framework APIs, including SHA-1 and zlib compression; native runtime/OS compression and cryptographic components remain platform dependencies. .NET's [license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) and [third-party notices](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT) must accompany any later redistribution as applicable. Git is a test-only external dependency, not bundled; its [license](https://github.com/git/git/blob/master/COPYING) remains applicable to Git itself. The Graft project license is still undecided.

Implementation was written against the official Git [HTTP](https://git-scm.com/docs/http-protocol), [pack protocol](https://git-scm.com/docs/pack-protocol), [pack format](https://git-scm.com/docs/pack-format) and [pkt-line](https://git-scm.com/docs/protocol-common) specifications, reviewed 2026-09-18. No NGit/JGit/libgit2 code was copied.

See [acceptance evidence](../../docs/validation/2026-09-18-managed-git.md) for tested coverage. Before promoting code to a product: independent security review/fuzzing, more clients and larger/deeper histories, durable storage and crash recovery, authorization/TLS, streaming/backpressure and resource profiling, cancellation/slow-client acceptance, full Git object validation and static-musl execution on the required host. Windows results do not establish Linux compatibility.
