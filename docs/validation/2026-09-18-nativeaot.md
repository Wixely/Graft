# NativeAOT compatibility acceptance

- Recorded: 2026-09-18
- Source: Local binary inspection/build/test output and user-pasted target-host report at 17:10:10 UTC
- Review trigger: SDK/compiler/native library/publish option/kernel changes
- Verification scope: Probe only

Target: Linux **3.10.108**, **x86_64**, vendor OS version **7.1.1**, build **42962**, update **7**. Personal host/account names and local paths are omitted. .NET **10.0.12**, dynamic-code support **false**, invariant globalization **0**. User-reported exit **0**.

| Check | Target result |
| --- | --- |
| Secure randomness and SHA-256 | PASS |
| Thread pool and timers | PASS |
| Non-invariant globalization | PASS |
| File write, flush, rename and reopen | PASS |
| SQLite/Dapper.AOT CRUD, rollback, WAL, two-connection contention, reopen/integrity | PASS |
| ASP.NET Core loopback HTTPS, RSA certificate and TLS handshake | PASS |

The same native build passed on Ubuntu 24.04/WSL, as an unprivileged user, and in an otherwise empty root filesystem containing only the executable, writable temp storage, basic device nodes and `/proc`. ELF inspection found no `PT_INTERP`, `DT_NEEDED` or `GLIBC_*` requirements. [Build evidence](../../experiments/compatibility/native-build-evidence.txt) records native dependencies and warnings. Full ICU data is embedded; the initial missing-data failure was fixed rather than switching to invariant globalization.

The original CoreCLR probe failed before managed startup due to the host's missing GLIBC symbol version. Moving execution out of `/tmp` resolved an earlier access failure; a `noexec` mount was suspected but not inspected. Those earlier failures do not invalidate the successful NativeAOT result.

Accepted archive SHA256, as recorded by the builder:

```text
a9765943a146b5d0aebbdbb3ceb88cd3f2650d9178dd736bb27917c27d586188
```

The user report did not independently include its hash. The accepted archive is retained locally under ignored `artifacts/reference/`; it is not committed. Its enclosed instructions predate the user's acceptance report. This document records the later result without changing the tested archive.

Not tested: full Graft application/UI, Blazor, Git HTTP/SSH/LFS, runners, service packaging, external CA trust, arbitrary OpenSSL providers, DNS, cross-process locks, power-loss durability, migration/upgrade/recovery or other target machines. No Docker, host-library upgrade, kernel upgrade or installed runtime was needed for the reported run. This does not expand Microsoft's official support policy.

Next action — Owner: Next agent: validate added application components against this baseline during GRAFT-001/002. No repeat run is needed for the unchanged probe.
