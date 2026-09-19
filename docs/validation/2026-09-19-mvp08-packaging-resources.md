# MVP-08 packaging and resource validation

Date: 2026-09-19. Status: conditional PASS. Owner: Implementation agent. Review after certificate, service, base-image, toolchain or required-host changes.

## Result

Graft now has Windows interactive and Windows Service hosting integration, Linux interactive and systemd integration, an optional non-root container definition, and an integrated static-musl build for both the server and administration tool. The same server artifact continues to pass the 64 MVP-06 Git/API assertions and 36 MVP-07 import/recovery assertions on Windows NativeAOT and local Linux static-musl NativeAOT.

The slice remains conditional because this non-elevated Windows session could not register a temporary service and the new integrated static-musl artifacts have not run on the mandatory Linux 3.10.108 host. Production TLS is also unresolved. These are explicit release gates rather than inferred passes.

## Exercised matrix

| Mode | Environment and artifact | Result |
| --- | --- | --- |
| Windows interactive | Windows x64 NativeAOT, .NET SDK 10.0.300 | PASS: 206 packaging/resource assertions; 64 Git/API; 36 operations |
| Windows Service | NativeAOT host compiled with Windows service lifetime | BLOCKED: live SCM test requires administrator privileges unavailable in this session |
| Linux interactive | Ubuntu 24.04.4 WSL kernel, static-musl NativeAOT server/admin | PASS: 206 packaging/resource assertions; 64 Git/API; 36 operations |
| Linux systemd | Ubuntu 24.04.4 WSL, self-contained CoreCLR server, systemd `Type=notify` | PASS: 6 assertions covering install, readiness, notify state, stop and maintenance-lock release |
| Rootless container | Podman on Ubuntu 24.04.4 WSL, Ubuntu .NET 10 runtime-deps image | PASS: 7 assertions covering build, non-root execution, read-only root, volume, health and restart |
| Required legacy host | Linux 3.10.108 x86_64 | PENDING: integrated artifact execution requires user-coordinated access |

The integrated static build used SDK 10.0.401 in the checksum-verified Alpine 3.21 chroot. `file` reported both outputs as stripped x86-64 static PIE executables. `readelf -l` found no `INTERP`; `readelf -d` found no `NEEDED`. The server was 72,369,096 bytes and admin tool 44,437,608 bytes. Native compilation emitted the previously known upstream CMake unused-variable warning and two OpenSSL PAL unused-value warnings; no managed AOT or trimming warnings were observed.

## Resource baseline

The harness waits for `/healthz`, warms it ten times, records 100 sequential loopback requests, then performs 200 requests across eight clients. These figures describe synthetic health traffic on this development machine; they do not compare Graft with GitLab and are not production capacity claims.

| Measurement | Windows NativeAOT | Linux static-musl NativeAOT | Provisional gate |
| --- | ---: | ---: | ---: |
| Startup to healthy | 619.9 ms | 4,104.3 ms | < 10 s |
| Working set after load | 53,305,344 bytes | 57,913,344 bytes | < 256 MiB |
| Executable | 30,894,592 bytes | 72,369,096 bytes | recorded only |
| Publication directory | 101,198,901 bytes | 72,369,096 bytes | recorded only |
| Health p50 | 0.09 ms | 0.35 ms | recorded only |
| Health p95 | 0.24 ms | 0.54 ms | < 250 ms |
| 200-request parallel batch | 11.5 ms | 25.2 ms | < 10 s |

The Windows publication directory includes native symbol files for local diagnosis; release packaging must omit PDBs. The Linux release copy contains only the standalone executable in each server/admin directory.

## Boundaries and next acceptance

- The health route proves process readiness only; it does not inspect database/repository health.
- The generated certificate changes on restart and is not trusted. Stable certificate loading and renewal are required before remote use.
- The systemd and container runs used a modern local Linux environment. They do not establish Linux 3.10 service-manager, container or general distribution support.
- The container is an optional CoreCLR deployment and does not replace the mandatory static-musl direct-host path.
- Service stop released the maintenance lease under systemd. The equivalent SCM check remains pending with the elevated lifecycle run.

Recommended next action: Owner: User + implementation agent — run the Windows Service harness from an elevated disposable Windows environment, then transfer the recorded static-musl server/admin pair to the required Linux 3.10.108 host and repeat the interactive, Git and operations acceptance commands. Owner: Implementation agent — add operator-managed certificate loading before any non-loopback deployment.
