# Graft compatibility probe

One-shot diagnostic for the planned Graft server. Source and packages pinned on 2026-09-18. No production data, credentials, remote integrations, or application installation are needed.

This maintained copy lives in Graft under `experiments/compatibility/`. See the repository [build runbook](../../docs/build-and-test.md) and [handoff](../../docs/handoff.md). The source now includes a Windows-only certificate-key import for Schannel; all six Windows developer checks pass. This does not modify or re-certify the previously accepted Linux archive. Use the required `-Distribution` argument when invoking `build-static-native.ps1`; no particular installed WSL distribution is assumed.

The requested direct-execution NativeAOT follow-up is now built: see [NativeAOT instructions](NATIVE-README.md) and [build evidence](native-build-evidence.txt). Its static musl executable removes the host-glibc dependency and includes SQLite, OpenSSL and ICU. The CoreCLR instructions below remain for the original comparison probe; the next target-host test is the NativeAOT archive.

## Run on the target Linux x86_64 server

Copy `graft-probe-linux-x64.tar.gz` into an empty writable directory, then:

```sh
tar -xzf graft-probe-linux-x64.tar.gz
sh run-probe.sh
```

Run as an ordinary user, without sudo. The whole extracted directory is required. The executable bundles .NET 10.0.12; it does not need an installed SDK/runtime or network access. The shell launcher is POSIX/BusyBox-oriented because the target host does not need PowerShell. Build automation uses Windows PowerShell 5.1.

Send back the generated `graft-report-*.txt`. It includes kernel/CPU/libc versions, selected vendor OS version fields, optional Docker CLI version, and PASS/FAIL results. It does not deliberately collect usernames, hostnames, IP addresses, environment contents, or Docker containers. Loader-generated errors may contain the extraction path; review that before sharing. No telemetry or external HTTP calls are made.

The probe creates a uniquely named temporary directory for its own files/database and removes it on normal completion. A crash/kill can leave that directory behind. The launcher writes the report and an intermediate output file beside itself and removes the intermediate file. The HTTPS listener binds only to loopback, on a dynamically chosen port, and stops after the test. No certificate is installed. The client accepts only the test certificate; this checks TLS/crypto, not external certificate-authority trust.

## Checks and interpretation

### Container follow-up for missing host GLIBC

The target-host run on 2026-09-18 reached the loader but failed because `GLIBC_2.27` was unavailable. Direct execution of this runtime is therefore blocked by the host libraries. From the extracted directory, the next experiment is:

```sh
docker run --rm --network none \
  --user "$(id -u):$(id -g)" \
  --mount "type=bind,src=$(pwd),dst=/probe,readonly" \
  --workdir /probe \
  mcr.microsoft.com/dotnet/runtime-deps:10.0.12-noble \
  /probe/Graft.Compatibility
```

Docker downloads the image if absent; the running container has no external network, published ports, or writable host mounts. Temporary probe data stays inside the disposable container. This image tag's registry manifest was verified on 2026-09-18; execution on this NAS is not yet verified. The test retains the host kernel and supplies newer userspace libraries. Return the full output, including any Docker startup error. It does not establish NativeAOT support or authorize replacing system libc.

### Probe coverage

- Secure random bytes, SHA-256, task scheduling/timers, and ICU-backed cultures.
- File writes with flush, rename and reopen.
- SQLite native library loading, generated Dapper.AOT CRUD, Unicode mapping, WAL, transaction rollback, two-connection write contention, reopen and integrity check.
- ASP.NET Core/Kestrel, RSA certificate creation, and a local HTTPS request.

Exit 0 means all normal-mode checks passed. A missing GLIBC version or loader error means the shipped native runtime cannot start in the current userspace; it does not prove a compatible container image or a differently built NativeAOT binary cannot work. An invariant-globalization retry occurs after a failed normal run to help isolate ICU issues. Its culture check is expected to fail: invariant mode is not full support. Exit 124 from `timeout` indicates a timed-out run. Where `timeout` is unavailable, Ctrl+C can stop the test.

The original package is **self-contained CoreCLR, not NativeAOT**. Dapper.AOT source generation is enabled in both modes. Initially the WSL environment lacked the Linux native build toolchain. For the subsequently requested NativeAOT follow-up, an isolated Alpine root filesystem was prepared with a verified SDK and native build dependencies. The static build and its actual binary inspection are documented separately; no toolchain or libraries are installed on the target NAS.

Passing is not certification for Graft, Blazor, Git serving, runners, migrations/upgrades, cross-process locks, power-loss durability, external TLS trust, or all .NET-supported platforms. The complete Graft dependency stack and deployment still need testing.

## Build and verification

Windows PowerShell 5.1: `./build.ps1`. NuGet sources are scoped to nuget.org; dependency versions are locked. VS Code debugging is configured in `.vscode/`.

The first Linux x64 self-contained publish passed all six checks on Ubuntu 24.04 under WSL. It has not been run on the target DSM/Linux 3.10.108 host. Dapper-generated interceptors were inspected for all eleven query call sites. No warnings were produced by the release publish.

This is a disposable database fixture, not an application schema: it deliberately uses one table and no migration subsystem. DnaX migration adoption remains part of the Graft plan, outside this runtime/dependency probe.

Next action — Owner: User: run the archive on the target and return the report. Owner: Agent: interpret failures, select the next native/container compatibility test, and record verified deployment boundaries.

## Dependencies and provenance

- .NET/ASP.NET Core 10.0.12: Microsoft runtime files and notices included by self-contained publish.
- Dapper 2.1.79 and Dapper.AOT 1.1.0: https://github.com/DapperLib/Dapper and https://github.com/DapperLib/DapperAOT (Apache-2.0).
- Microsoft.Data.Sqlite 10.0.12 and transitive SQLitePCLRaw: NuGet dependency graph in `packages.lock.json`; upstream package notices retained in the source package cache. These use Microsoft and SQLitePCLRaw licensing; SQLite itself is public domain.

Reference setup: https://aot.dapperlib.dev/gettingstarted.html and https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile (reviewed 2026-09-18).
