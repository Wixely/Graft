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

The separate [managed Git runbook](../experiments/managed-git/README.md) covers MVP-01's C# self-checks, real Git client harness, VS Code configurations and Windows NativeAOT publish. Its [acceptance evidence](validation/2026-09-18-managed-git.md) does not extend the old-host result. The original compatibility-probe instructions below remain the accepted legacy-host baseline; the MVP-08 product build is a new artifact with its own pending host gate.

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

MVP-06 extends the ordinary Git client workflow with GitLab-style REST reads, browser-session APIs, embedded Blazor assets and repository browsing against the same disposable database/repository root. Git must be on `PATH`; the harness isolates Git configuration and disables certificate verification only for its ephemeral loopback certificate.

```powershell
dotnet build tests\Graft.Git.Acceptance\Graft.Git.Acceptance.csproj -c Debug
dotnet tests\Graft.Git.Acceptance\bin\Debug\net10.0\Graft.Git.Acceptance.dll src\Graft.Server\bin\Debug\net10.0\Graft.Server.dll
dotnet publish src\Graft.Server\Graft.Server.csproj -c Release -r win-x64 --self-contained true -p:GraftServerAot=true -o artifacts\mvp06\win-x64
dotnet tests\Graft.Git.Acceptance\bin\Debug\net10.0\Graft.Git.Acceptance.dll artifacts\mvp06\win-x64\Graft.Server.exe
```

See [MVP-06 evidence](validation/2026-09-19-mvp06-repository-api-ui.md) for covered behavior, exact compatibility boundaries and remaining operational gates. The published server embeds a trimmed Blazor WebAssembly client; WebAssembly AOT is not enabled and the optional `wasm-tools` workload is not required for this slice.

For rendered UI work, start the disposable production fixture with a temporary password. It creates a `fixture` administrator, seeds `fixture/seeded` with text, hostile HTML and binary files, prints the random loopback URL, and removes all state on Enter:

```powershell
dotnet build tests\Graft.Browser.Fixture\Graft.Browser.Fixture.csproj -c Debug
$env:GRAFT_BROWSER_FIXTURE_PASSWORD = '<temporary-16-to-256-character-password>'
dotnet tests\Graft.Browser.Fixture\bin\Debug\net10.0\Graft.Browser.Fixture.dll src\Graft.Server\bin\Debug\net10.0\Graft.Server.dll
```

The **Debug production browser fixture** VS Code option supplies the password through a masked prompt and opens `/projects`. The generated loopback certificate is untrusted; use only a temporary browser exception or test-instance trust and remove it after the run. See [rendered-browser evidence](validation/2026-09-19-browser-ui.md).

The GitLab 18 reference harness executes the same checked-in project-creation fixture against an approved, disposable GitLab instance. Supply synthetic token files for an administrator `api` token and a `read_api` token; the harness reads but never prints their values:

```powershell
dotnet build tests\Graft.GitLab.Reference\Graft.GitLab.Reference.csproj -c Release
dotnet tests\Graft.GitLab.Reference\bin\Release\net10.0\Graft.GitLab.Reference.dll `
  <GitLab-base-URL> <api-token-file> <read-api-token-file> `
  tests\Graft.Git.Acceptance\Fixtures\gitlab-ce-18.0.0-rest-v4.json
```

The recorded live run used the official GitLab CE 18.0.0 and `glab` 1.58.0 container images in a loopback-only rootless Podman network. See [the live-reference evidence](validation/2026-09-19-gitlab-18-live-reference.md) for exact digests, results, isolation, and remaining scope.

Run the same pinned `glab` API workflow against disposable Graft with an existing WSL distribution and rootless Podman. The image must already be present unless its official pull is explicitly requested with `-PullImage`:

```powershell
.\tests\Graft.Glab.Acceptance\run.ps1 -Distribution <existing-wsl-distribution>
```

The C# harness generates a private fixture CA and validates TLS normally, mounts its synthetic token read-only, verifies the created project after shutdown, and deletes all temporary state. See [pinned-client evidence](validation/2026-09-19-glab-graft.md).

MVP-07 adds the NativeAOT-compatible administration tool and a separate import/recovery acceptance executable. It needs both the server and admin tool paths:

```powershell
dotnet build tests\Graft.Operations.Acceptance\Graft.Operations.Acceptance.csproj -c Debug
dotnet build src\Graft.Admin\Graft.Admin.csproj -c Debug
dotnet tests\Graft.Operations.Acceptance\bin\Debug\net10.0\Graft.Operations.Acceptance.dll src\Graft.Server\bin\Debug\net10.0\Graft.Server.dll src\Graft.Admin\bin\Debug\net10.0\Graft.Admin.dll

dotnet publish src\Graft.Server\Graft.Server.csproj -c Release -r win-x64 --self-contained true -p:GraftServerAot=true -o artifacts\mvp07\server-win-x64
dotnet publish src\Graft.Admin\Graft.Admin.csproj -c Release -r win-x64 --self-contained true -p:GraftAdminAot=true -o artifacts\mvp07\admin-win-x64
dotnet tests\Graft.Operations.Acceptance\bin\Release\net10.0\Graft.Operations.Acceptance.dll artifacts\mvp07\server-win-x64\Graft.Server.exe artifacts\mvp07\admin-win-x64\Graft.Admin.exe
```

See [the operations runbook](operations.md) for backup, verification, fresh-target restore and repository-only import inventory commands. [MVP-07 evidence](validation/2026-09-19-mvp07-import-recovery.md) records the tested boundary.

The administration tool also provides the one-use offline administrator bootstrap. Its current 41-assertion operations workflow verifies live-server exclusion, successful login, administrator status, hashed-at-rest storage and reuse refusal under both managed and `win-x64` NativeAOT administration binaries. See [bootstrap evidence](validation/2026-09-19-operator-bootstrap.md).

MVP-08 adds service lifetimes, deployment definitions, a static-musl product build and resource/lifecycle acceptance. The ordinary Windows NativeAOT baseline is:

```powershell
dotnet publish src\Graft.Server\Graft.Server.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -o artifacts\mvp08\server-win-x64
dotnet publish src\Graft.Admin\Graft.Admin.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -o artifacts\mvp08\admin-win-x64
dotnet run --project tests\Graft.Packaging.Acceptance\Graft.Packaging.Acceptance.csproj -c Release -- interactive artifacts\mvp08\server-win-x64\Graft.Server.exe
dotnet run --project tests\Graft.Packaging.Acceptance\Graft.Packaging.Acceptance.csproj -c Release -- tls artifacts\mvp08\server-win-x64\Graft.Server.exe
```

Run the Windows Service mode only from an elevated disposable session; it creates and removes a randomly named service and copies the publication beneath ProgramData:

```powershell
dotnet run --project tests\Graft.Packaging.Acceptance\Graft.Packaging.Acceptance.csproj -c Release -- windows-service artifacts\mvp08\server-win-x64\Graft.Server.exe
```

The elevated Windows NativeAOT run passed all five lifecycle assertions on 2026-09-19, including certificate-backed readiness, clean stop and maintenance-lock release.

Build the integrated static Linux server and admin tool with the existing isolated WSL toolchain:

```powershell
.\deploy\linux\build-static-native.ps1 -Distribution <existing-wsl-distribution>
```

The systemd acceptance requires root in a systemd Linux environment. It passes with the static NativeAOT server as recorded in [static systemd evidence](validation/2026-09-19-static-systemd.md). The container acceptance requires rootless Podman and the self-contained `linux-x64` publication expected by the Containerfile. `.dockerignore` and `.containerignore` restrict the build context to source/deployment files and that publication. Both tests generate disposable certificate/password files and clean their temporary service/container state. See [the deployment runbook](deployment.md), [MVP-08 evidence](validation/2026-09-19-mvp08-packaging-resources.md) and [TLS evidence](validation/2026-09-19-operator-tls.md) for commands, results and boundaries.

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
