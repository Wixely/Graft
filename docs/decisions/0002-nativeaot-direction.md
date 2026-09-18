# 0002: Dapper.AOT and NativeAOT feasibility

- Date: 2026-09-18
- Status: User direction accepted; standalone probe verified; full application architecture open
- Source: User requested Dapper.AOT, a NativeAOT build attempt, and direct standalone execution on the required old-kernel host
- Review trigger: Dependency, runtime, publish-setting or deployment change

Use Dapper.AOT and pursue NativeAOT publishing. The inherited ordinary Dapper/server-runtime proposal is superseded for this investigation. The browser default remains Blazor, but no claim is made that server-side Blazor can be NativeAOT-published; the UI/server boundary requires an explicit design review.

The first self-contained CoreCLR build failed to load on the host because of `GLIBC_2.27`. The subsequent static-musl NativeAOT executable embeds SQLite, OpenSSL, ICU and ICU data, and has no dynamic-loader/shared-library/GLIBC requirements in its ELF metadata. The user ran it directly on Linux 3.10.108 x86_64 at 17:10:10 UTC; all six checks passed, exit 0. See [acceptance](../validation/2026-09-18-nativeaot.md).

This demonstrates a viable build/deployment technique for tested components. It does not imply .NET officially supports this vendor OS, that all old Linux hosts work, or that Graft/Blazor/Git/runners/upgrades are implemented. Keep static native libraries explicit and rebuild for their security updates. Do not make Docker or host-library replacement a prerequisite for this demonstrated direct-execution path.
