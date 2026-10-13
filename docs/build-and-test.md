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

The current extension adds administrator user creation and account lifecycle, owner-managed project roles, and user-managed personal access tokens while retaining this harness. It passes 135 assertions against both managed and Windows NativeAOT servers; see [user/membership evidence](validation/2026-09-19-user-membership.md), [token evidence](validation/2026-09-19-personal-access-tokens.md), and [account credential evidence](validation/2026-09-20-account-credentials.md).

The same harness now also covers README rendering, which is the one place repository content becomes markup. Its corpus is pushed as real repository content on a branch of its own and read back through the browser endpoint, so the assertions run against the whole path rather than against the renderer in isolation. Half the corpus is hostile and must not survive; the other half is ordinary markdown and must not be flattened, because a renderer that escaped everything would satisfy every attack case and still be useless. See [README rendering evidence](validation/2026-10-04-readme-rendering.md).

The same harness also covers visibility and the public browser, in both operator states. It writes public,
unlisted and page-published rows straight through the store while the server is still refusing to publish, so
the assertions can show that the operator switch withdraws a grant the stored data still claims, then
restarts the server with publishing enabled and asserts the same rows now mean what they say. Every one of
those assertions runs with no session and no token, because that is the only vantage point from which "not
available" can be believed. See [visibility evidence](validation/2026-10-13-visibility-and-public-browsing.md).

Publishing is off unless an operator turns it on:

```powershell
dotnet src\Graft.Server\bin\Debug\net10.0\Graft.Server.dll --database <path> --repositories <dir> --public-browsing true
```

With it off, a project cannot be made public, the listing at `/api/browse` answers 404, and a stored public
row is inert. The browser fixture passes `true` so the public listing and the visibility controls can be
looked at; a fixture is not a deployment.

A second operator switch, `--push-to-create true|false`, also off by default, lets a push to a path holding
no project create one. The created project is private, owned by the pusher, and only in the pusher's own
namespace. The suite covers both states, and most of its assertions there are about the cases that must not
create. See [push-to-create evidence](validation/2026-10-13-push-to-create.md) and
[ADR 0007](decisions/0007-push-to-create.md).

Graft ships a Model Context Protocol server as a separate executable, `src/Graft.Mcp`. It speaks stdio and
Streamable HTTP, and reaches Graft over the ordinary REST API with a personal access token, so it is
deliberately not part of the forge process and the server's dependency closure is unchanged by it:

```powershell
dotnet build src\Graft.Mcp\Graft.Mcp.csproj -c Debug
dotnet src\Graft.Mcp\bin\Debug\net10.0\Graft.Mcp.dll --server <https origin> --transport stdio --token-file <path> [--allow-writes true|false] [--ca-certificate <path>]
dotnet src\Graft.Mcp\bin\Debug\net10.0\Graft.Mcp.dll --server <https origin> --transport http --listen https://0.0.0.0:8443 --certificate <PKCS#12 path>
dotnet src\Graft.Mcp\bin\Debug\net10.0\Graft.Mcp.dll --licences
```

Over stdio the credential belongs to the process, so `--token-file` is required. Over Streamable HTTP it
belongs to the caller: each request carries its own Graft token as `PRIVATE-TOKEN` or
`Authorization: Bearer`, this server stores none, `--token-file` is refused, and a request without a token
is answered `401` before the protocol is spoken. Off loopback the endpoint must be HTTPS with a certificate.

Mutating tools are refused unless `--allow-writes true`. There is no option to skip certificate
verification; `--ca-certificate` names the certificate to trust, which also covers a forge behind a private
authority.

A fourth suite drives both transports against a real Graft, launching the MCP server as a child process the
way a client would:

```powershell
dotnet build tests\Graft.Mcp.Acceptance\Graft.Mcp.Acceptance.csproj -c Debug
dotnet tests\Graft.Mcp.Acceptance\bin\Debug\net10.0\Graft.Mcp.Acceptance.dll src\Graft.Server\bin\Debug\net10.0\Graft.Server.dll src\Graft.Mcp\bin\Debug\net10.0\Graft.Mcp.dll
```

See [MCP evidence](validation/2026-10-13-mcp-layer.md) and [ADR 0008](decisions/0008-mcp-layer.md).

A browser session is an idle window, renewed by use, set by `--session-minutes` and defaulting to 12 hours
with an absolute ceiling of 30 days from sign-in. It used to be a fifteen-minute countdown from sign-in that
no amount of activity moved, which is what it looked like from a chair: being logged out mid-sentence. See
[session evidence](validation/2026-10-13-session-renewal.md).

The suite also pushes an incompressible two-mebibyte repository at the client's default settings. That is
not a size test. A push above the client's `http.postBuffer` is streamed with chunked encoding, and Git
probes authentication with an empty request first because it cannot replay a chunked body after a
challenge. Answering that probe wrongly broke every large push while leaving small ones working, so the
assertion exists to keep that path exercised.

The diff engine is measured against real Git rather than only against fixed fixtures. The fuzz harness
generates file pairs from a deliberately repetitive alphabet, because repeated and blank lines are what make
a change's placement ambiguous, and compares every resulting diff with `git diff`:

```powershell
dotnet build tests\Graft.Diff.Fuzz\Graft.Diff.Fuzz.csproj -c Debug
dotnet tests\Graft.Diff.Fuzz\bin\Debug\net10.0\Graft.Diff.Fuzz.dll <seed> <cases> [show]
```

It exits non-zero when any case disagrees and `show` prints the first one. Agreement currently sits around
91% on seeds not used while developing the sliding passes; the rest are cases where Git picks a different
script of the same length rather than a different position. This is a measuring instrument, not a gate: the
acceptance suite is what pins specific shapes. Git must be on `PATH`. See
[commit diff evidence](validation/2026-10-09-commit-diff.md) for the measurements and what they exclude.

Every build can produce the attribution it redistributes under, because several of those licences bind the binary rather than the source tree:

```powershell
.\Graft.Server.exe --licences          # prints the notices and exits
curl https://<server>/-/licences       # the same text from a running server, unauthenticated
```

For rendered UI work, start the disposable production fixture. It creates an administrator, seeds `<username>/seeded` with text, hostile HTML and binary files, a `README.md` exercising every piece of markdown syntax the renderer supports, and a real image beside it, prints the random loopback URL, and removes all state on Enter:

```powershell
dotnet build tests\Graft.Browser.Fixture\Graft.Browser.Fixture.csproj -c Debug
dotnet tests\Graft.Browser.Fixture\bin\Debug\net10.0\Graft.Browser.Fixture.dll src\Graft.Server\bin\Debug\net10.0\Graft.Server.dll
```

Against a **Debug** server this signs in as `admin` / `admin` with nothing to type, and the fixture prints those credentials. `GRAFT_BROWSER_FIXTURE_USERNAME` and `GRAFT_BROWSER_FIXTURE_PASSWORD` override them; a supplied password is never echoed.

The short default works only because Debug builds relax `Graft.Core.PasswordPolicy.MinimumLength` from 16 to 5 for exactly this purpose. A Debug server prints a `WARNING:` line on startup saying so. **Release builds keep the 16-character minimum**, every packaged and deployed artifact is published Release, and the fixture refuses its own default against a Release server:

```powershell
dotnet build tests\Graft.Browser.Fixture\Graft.Browser.Fixture.csproj -c Release
dotnet build src\Graft.Server\Graft.Server.csproj -c Release
# Refuses: "GRAFT_BROWSER_FIXTURE_PASSWORD must be between 16 and 256 characters for this build."
dotnet tests\Graft.Browser.Fixture\bin\Release\net10.0\Graft.Browser.Fixture.dll src\Graft.Server\bin\Release\net10.0\Graft.Server.dll
```

The **Debug production browser fixture** VS Code option now launches without a prompt and opens `/projects`. The generated loopback certificate is untrusted; use only a temporary browser exception or test-instance trust and remove it after the run. See [rendered-browser evidence](validation/2026-09-19-browser-ui.md).

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
