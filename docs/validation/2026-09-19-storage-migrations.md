# DnaX migration and Dapper.AOT acceptance

- Recorded: 2026-09-19
- Scope: MVP-03 isolated storage experiment
- Result: Technical PASS on Windows JIT, Windows NativeAOT and local static-musl NativeAOT; adoption HOLD for package distribution and required-host acceptance
- Review trigger: DnaX/package source, SDK, SQLite, Dapper or static-link configuration change

## Results

The same six checks passed in all three executions:

| Check | Observed behavior |
| --- | --- |
| Fresh/repeat/restart | Two migrations applied once, repeat applied zero, reopened status was Current and data remained readable. |
| v1-to-v2 preservation | The second migration added a non-null column and preserved the existing row. |
| Altered checksum | Status was Drifted and migration threw `DnaXMigrationValidationException`. |
| Future version | Status was UnsupportedFuture and migration was refused. |
| Concurrent lock | An immediate SQLite writer blocked migration; the configured 200 ms DnaX timeout was honored. |
| Failed chain/restart | Invalid version 2 rolled back the schema and ledger; a new provider with the valid manifest then applied both migrations. |

Windows Debug/JIT and a self-contained `win-x64` NativeAOT executable passed. The Windows executable was 4,024,832 bytes with SHA-256 `53619757900024d616956ecf99a44085fb90a2575300e60e73451d919f12572a`.

The `linux-musl-x64` NativeAOT executable passed inside the Alpine build root and again as numeric user 65534 in an otherwise empty chroot containing only the executable, writable `/tmp`, and basic random/null devices. `file` reported static PIE; ELF inspection found no `PT_INTERP` or `DT_NEEDED`. It was 44,495,144 bytes with SHA-256 `3b815469da406908dc7b2d5f2935284b3b7275ae27c617b2da4ad7e2e1323c22`.

The static build used Alpine 3.21, SDK 10.0.401, runtime/ILCompiler 10.0.12, Clang 19, SQLite 3.48.0 and the existing static ICU/OpenSSL technique. It emitted the same two upstream unused-expression C warnings and unused CMake-variable warning recorded for the base probe; no managed AOT or trimming warnings were observed.

Dapper.AOT generated interceptors for all eight application Dapper call sites in Debug and Release/NativeAOT output: inserts, typed row reads, scalar reads and ledger-fixture mutations. DnaX's migration adapter is separate and uses provider commands directly.

## Dependency decision

The tested dependency is `DnaX.Data.Migrations.Sqlite` 10.0.0-alpha.7 and its `DnaX.Data.Migrations` dependency, produced from public tag `v10.0.0-alpha.7` / commit `73826d731cef903083f2a4ae15ba6ac284e83ce3`. Alpha.7 was selected over the earlier planning candidate because its migration checksums normalize line endings consistently across platforms and report structured drift details. The project locks package content hashes.

The locally packed `.nupkg` SHA-256 values were `83f225fcfd86bfc078a5d887238114dba5ea3ec91ddb767c76e5155879345e53` for `DnaX.Data.Migrations` and `b7b677609a7215df7e25fba9471df009e5ecc20e94ff02a068ab65f2a5457cc7` for the SQLite adapter. A repeated pack from the same clean tag produced identical hashes.

A clean isolated restore from NuGet.org failed with NU1101 because these package IDs were unavailable there on 2026-09-19. The public GitHub release had no package assets. For feasibility only, the clean local checkout at the exact public tag was packed into an ignored local feed. A locked restore using a fresh empty package cache passed from that feed, and the isolated static build consumed the same exact `.nupkg` files. No sibling-project reference entered Graft.

Recommendation: the DnaX implementation is suitable for the next slice after the exact tested packages are made reproducibly available from an approved durable feed. Do not silently vendor cached binaries or depend on a developer checkout. Publishing or selecting a private feed is an external distribution decision and was not performed.

## Evidence boundary

The newly built storage binary has not run on the required Linux 3.10.108 host. Local static execution confirms the dependency can use the established static-musl technique; only a target-host run can extend target acceptance. The experiment does not test a production schema, backup/restore, power loss, filesystem/SQLite cross-resource atomicity, multi-process application access, service hosting, Git storage or authentication.

Recommended next action - Owner: User/DnaX maintainer selects and authorizes a durable package distribution route; Implementation agent then reruns a clean restore and begins MVP-04 accounts/projects/membership. Required-host execution remains an acceptance action for the new binary.
