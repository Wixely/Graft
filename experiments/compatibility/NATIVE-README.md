# Graft NativeAOT compatibility probe

Direct Linux x86_64 test. This is a separate NativeAOT build of the original probe, not the CoreCLR package.

Extract into a new directory on the user's home/data volume, not the previously blocked `/tmp` location:

```sh
tar -xzf graft-probe-native-linux-x64.tar.gz
sh run-native-probe.sh
```

No sudo, Docker, runtime installation, library installation or internet connection is required. Send back `graft-native-report-*.txt`. The launcher captures the exit status and output, including crashes. Do not mix the two probe archives in one directory.

The executable is NativeAOT for `linux-musl-x64`, statically linked with musl libc, SQLite, OpenSSL and ICU. It preserves the six original checks: randomness/hash, timers/thread pool, non-invariant globalization, file flush/rename/reopen, SQLite/Dapper.AOT operations with WAL and contention, and ASP.NET Core loopback HTTPS. All eleven Dapper call sites use generated interceptors. `Dynamic code supported: False` is expected.

The app creates its own temporary database/files and an ephemeral loopback-only HTTPS listener, then cleans up. The launcher writes a report beside itself. It does not access production data or configure services. External certificate trust, DNS, other OpenSSL providers, Blazor, Git hosting, CI runners, upgrade/recovery, cross-process locking and full Graft functionality are outside these checks.

Static linking removes the need for the host's glibc and installed copies of these libraries. On 2026-09-18 at 17:10:10 UTC, the user reported all six checks passing directly on the required Linux 3.10.108 x86_64 host, exit 0, with dynamic-code support false. This verifies the probe's operations on that host, not every syscall or the complete Graft application. Build environment and validation details are in `native-build-evidence.txt`.

This is an experimental probe, not a selected Graft release architecture. The static native libraries remain dependencies and require future security updates through rebuilding the executable.

Host acceptance is complete for this build. Next action — Owner: Agent: use the demonstrated configuration for the first authorized Graft application feasibility slice and test its additional dependencies/UI/workflows. Re-run the probe after runtime, native-library, publish-setting or target-kernel changes.
