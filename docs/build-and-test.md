# Build and verification runbook

Recorded: 2026-09-18. Owner: Next agent. Review when dependency/toolchain or publish configuration changes.

## Developer baseline

Use .NET SDK 10.0.300 or a compatible later .NET 10 feature band (`global.json`), with PowerShell 5.1 on Windows. The probe's `NuGet.config` uses nuget.org only. NuGet versions are pinned in the project and lock file; avoid unrelated upgrades while reproducing acceptance.

```powershell
dotnet build Graft.slnx
dotnet run --project experiments/compatibility/Graft.Compatibility.csproj --no-build
```

VS Code: **Debug compatibility probe** builds its project then launches the Debug DLL. See [VS Code debugging](vscode-debugging.md) for the UI, Git, self-check and attach options. Running it on Windows validates only this developer baseline. `dotnet test` is not an application conformance suite here; the probe executable is the current functional oracle.

Checkout setup validation on 2026-09-18: solution build passed with zero warnings/errors; all six probe checks passed on Windows x64 with the installed .NET 10.0.8 runtime. The first Windows run exposed Schannel's inability to use the generated ephemeral RSA key. The maintained source now imports that test certificate with default key storage on Windows only and disposes it after the test; no certificate/trust store is installed. Linux retains the original key path. Exception reporting now includes inner exception type/HResult without paths. The accepted Linux archive was not rebuilt or altered by these developer-baseline fixes.

## Static Linux NativeAOT

For MVP-02, the [UI/auth runbook](../experiments/ui-server/README.md) covers automatic client publication/embedding, HTTP acceptance, C# browser scripts, debugging and Windows NativeAOT. The solution build includes this experiment and may restore pinned Blazor NuGet assets; it does not install a browser-native workload. [Recorded UI/auth acceptance](validation/2026-09-18-ui-nativeaot.md) is Windows-only.

The separate [managed Git runbook](../experiments/managed-git/README.md) covers MVP-01's C# self-checks, real Git client harness, VS Code configurations and Windows NativeAOT publish. Its [acceptance evidence](validation/2026-09-18-managed-git.md) does not extend the old-host result. The static Linux instructions below still apply to the original compatibility probe only.

The [storage migration runbook](../experiments/storage/README.md) covers MVP-03's JIT, Windows NativeAOT and static-musl commands. [Recorded storage evidence](validation/2026-09-19-storage-migrations.md) includes migration failure/recovery behavior and Dapper interception. Its static binary has not run on the required host, and clean external restore is blocked until the tagged DnaX packages have an approved durable source.

MVP-04 uses the reproducible ignored feed at `artifacts/storage/dnax-feed`, configured by the root `NuGet.config`. Prepare it from the pinned public DnaX tag when absent, then run the disposable acceptance executable:

```powershell
.\experiments\storage\prepare-dnax-packages.ps1
dotnet build tests\Graft.Acceptance\Graft.Acceptance.csproj -c Debug
dotnet tests\Graft.Acceptance\bin\Debug\net10.0\Graft.Acceptance.dll
dotnet publish tests\Graft.Acceptance\Graft.Acceptance.csproj -c Release -r win-x64 --self-contained true -p:AcceptanceAot=true -o artifacts\mvp04\win-x64
& .\artifacts\mvp04\win-x64\Graft.Acceptance.exe
```

The package-preparation script verifies the exact public source tag and commit before packing. This local process does not replace the durable-feed release gate. See [MVP-04 evidence](validation/2026-09-19-mvp04-accounts-permissions.md).

MVP-05 runs an ordinary Git client against the integrated HTTPS server and a disposable database/repository root. Git must be on `PATH`; the harness isolates Git configuration and disables certificate verification only for its ephemeral loopback certificate.

```powershell
dotnet build tests\Graft.Git.Acceptance\Graft.Git.Acceptance.csproj -c Debug
dotnet tests\Graft.Git.Acceptance\bin\Debug\net10.0\Graft.Git.Acceptance.dll src\Graft.Server\bin\Debug\net10.0\Graft.Server.dll
dotnet publish src\Graft.Server\Graft.Server.csproj -c Release -r win-x64 --self-contained true -p:GraftServerAot=true -o artifacts\mvp05\win-x64
dotnet tests\Graft.Git.Acceptance\bin\Debug\net10.0\Graft.Git.Acceptance.dll artifacts\mvp05\win-x64\Graft.Server.exe
```

See [MVP-05 evidence](validation/2026-09-19-mvp05-durable-git.md) for covered behavior and remaining operational gates.

`experiments/compatibility/Graft.Compatibility.csproj` has separate `NativeProbe` and `StaticProbe` switches. Plain `NativeProbe` enables AOT only; **StaticProbe** also sets static-musl executable linking, OpenSSL, ICU plus embedded data, and direct SQLite imports. Use `linux-musl-x64`, not the earlier CoreCLR `linux-x64` path, for the accepted standalone technique.

The original Linux build used an isolated Alpine 3.21 root filesystem inside existing WSL, SDK 10.0.401, Clang 19, static native libraries and the command:

```text
dotnet publish /src/Graft.Compatibility.csproj -c Release -r linux-musl-x64 --self-contained true -p:StaticProbe=true -o /out
```

The project intentionally pins ICU's data path to version 74.2. Updating ICU requires updating and retesting that choice. SQLitePCLRaw imports named `e_sqlite3` are statically mapped to `/usr/lib/libsqlite3.a`; the native SQLite build differs from the original shared-library package. Do not copy only the `PublishAot` switch and assume the result is equivalent.

A PowerShell 5.1 orchestration script is included:

```powershell
.\experiments\compatibility\build-static-native.ps1 -Distribution <existing-wsl-distribution>
```

It creates a fresh, dedicated Linux build root, downloads checksum-verified Alpine/SDK archives, installs compiler packages **inside that root**, copies source, mounts `/proc` for the build and unmounts it in cleanup, publishes, checks ELF headers and runs the executable on the local WSL kernel. It does not install/configure a new WSL distribution or touch the NAS. Native APK packages track the Alpine 3.21 repositories, so this bootstrap is not a promise of bit-identical future output. Review versions and notices when rebuilding.

The equivalent individual build steps were executed successfully during discovery. The consolidated bootstrap has been syntax-checked; its complete fresh-root execution is not separately verified. It retains its build directory for inspection and stops rather than overwriting an initialized root. On Windows, native Linux linking uses WSL; do not assume cross-OS NativeAOT publishing is supported by `dotnet publish` alone.

## Release/probe gates

1. Preserve the accepted artifact/hash and normal-runtime baseline when changing NativeAOT/trimming settings.
2. Inspect generated Dapper interceptors for every data-access call; do not fall back to runtime generation unnoticed.
3. Review managed AOT/trimming and native compiler warnings. The recorded build had two upstream unused-value C warnings plus an unused CMake-variable warning, with no observed managed AOT/trim warnings.
4. Confirm `readelf -l` has no interpreter, `readelf -d` no `NEEDED`, and version metadata no GLIBC requirements for the static build.
5. Run all six checks in the published executable, including cultures and HTTPS, outside the build root and as an ordinary user. For dependency-independence changes, repeat the empty-root test.
6. Revalidate on the required host after relevant changes. Archive the source commit, SDK/native package versions, build flags, checksum and sanitized report. A successful compile alone is insufficient.
7. Preserve relevant dependency licenses/notices. Do not ship PDBs, unrelated build directories or host reports. The project license remains undecided; do not relicense third-party code.

The local `artifacts/reference/` archive can be supplied again if needed; no download/deployment automation to the host is configured. Do not replace its contents while calling it the same accepted build.
