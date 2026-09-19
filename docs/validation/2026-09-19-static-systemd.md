# Static NativeAOT systemd evidence

Date: 2026-09-19. Owner: Implementation agent. Review when hosting lifetime, systemd unit, NativeAOT runtime, static toolchain, or Linux target changes.

## Result

The current integrated static-musl server and admin pair passes interactive, operator-TLS, systemd, Git/API/project-creation, and recovery acceptance on Ubuntu 24.04.4 under WSL 2 kernel 6.18.33.2. This modern environment does not close the separate Linux 3.10.108 gate.

The first static systemd run exposed a real gap: Kestrel became healthy, but the NativeAOT process did not deliver `READY=1`, so the `Type=notify` unit timed out. The server now sends the systemd datagram directly from managed C# through a Unix datagram socket after successful startup and sends `STOPPING=1` during shutdown. The existing hosting integration remains responsible for Linux service lifetime behavior.

## Evidence

The fixed static artifacts were rebuilt from a fresh Alpine 3.21 root with SDK 10.0.401 by `deploy/linux/build-static-native.ps1`. Both are stripped x86-64 static PIE executables. `readelf` reports no `INTERP` or `NEEDED` entries.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| `Graft.Server` | 72,502,472 | `13b2c6786c9a896a54fdcf56fedc11cf926a25716abd7eaa54e21b7a0d30b201` |
| `Graft.Admin` | 44,437,608 | `6f7d19e8d0217c14afedee8090f5dea45aadb971e258c2c86cea8821ea3af5b2` |

| Check | Result |
| --- | --- |
| Static systemd lifecycle | PASS: 6 assertions; start reaches `active/running`, health succeeds, stop releases the maintenance lease |
| Interactive/resource | PASS: 206 assertions; 4,307.8 ms startup, 57,704,448-byte working set, 0.52 ms health p95 |
| Operator TLS | PASS: 6 assertions |
| Git/API/project creation | PASS: 76 assertions |
| Import/recovery | PASS: 36 assertions |

No managed build, trimming, or AOT warning was emitted. The static runtime build retained the known upstream unused CMake-variable warning and two OpenSSL PAL unused-expression warnings.

## Boundary

This result validates static NativeAOT service notification and the then-current product workflow on the available WSL system. A [GitLab 18.0 source-backed fixture](2026-09-19-gitlab-18-source-fixture.md) was added later and now passes a [live reference instance](2026-09-19-gitlab-18-live-reference.md); pinned `glab` API workflows also pass [against Graft](2026-09-19-glab-graft.md). Linux 3.10.108 execution is deferred, while a real deployment CA/DNS trust chain, the durable DnaX package source, and interactive browser execution remain open.
