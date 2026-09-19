# Blazor client, NativeAOT host and session feasibility

MVP-02 is a disposable, loopback-only HTTPS experiment. The standalone Blazor WebAssembly application executes C#/Razor in the browser; a separate C# Minimal API host serves embedded published assets and synthetic authenticated data. No server-side Razor rendering, Blazor Server circuit, database or Git server is involved.

## Build and verify

Use the repository's .NET SDK and Windows PowerShell 5.1:

```powershell
dotnet build Graft.slnx
dotnet run --project experiments/ui-checks/Graft.Ui.Checks.csproj --no-build -- experiments/ui-server/bin/Debug/net10.0/Graft.Ui.Server.dll
dotnet publish experiments/ui-server/Graft.Ui.Server.csproj -c Release -r win-x64 --self-contained true -p:UiProbeAot=true -o artifacts/ui/native
dotnet run --project experiments/ui-checks/Graft.Ui.Checks.csproj --no-build -- artifacts/ui/native/Graft.Ui.Server.exe
```

The API host's build target publishes `ui-client` in Release mode and embeds its output before compiling the server. Its cleanup target verifies the resolved output is exactly this checkout's `artifacts/ui/client/` before replacing that generated directory, preventing old fingerprinted assets from accumulating. The client is intentionally not a separate solution build entry, avoiding concurrent publication into that directory. Contracts and checks build through project references. Do not run simultaneous UI builds/publishes in the same checkout.

NativeAOT is enabled only in the server project by `UiProbeAot`; browser/server publish properties are separated. The browser uses `RunAOTCompilation=false` and `WasmBuildNative=false`. With the tested SDK, `UsingBrowserRuntimeWorkload=false`; no `wasm-tools`, Emscripten, Python or Node pipeline was installed or invoked for builds. The SDK prints an informational recommendation to install optional native optimizations. Do not follow it without reviewing the tooling restriction. Browser IL trimming still runs and is verified by the actual browser tests.

The HTTP harness creates a random temporary fixture password, starts the selected DLL/EXE from a separate working directory, pins its ephemeral certificate by SHA-256 and stops the process. It checks 50 assertions plus the server's direct session checks. It does not install trust roots or print credentials, cookies or CSRF tokens. It requires a .NET runtime for the harness; the separately tested NativeAOT server executable does not.

## Interactive run and debugging

VS Code provides **Debug UI and authentication probe** (prompts for a temporary password and opens the browser) and **Debug UI HTTP acceptance checks** (generates its own password). Focused tasks build their required projects. See [VS Code debugging](../../docs/vscode-debugging.md) for self-check and attach options. UI C# source debugging in the browser is not configured; the host and checks are debug targets.

To run manually, provide a new, temporary 16-256 character password; the only fixture username is `fixture`:

```powershell
$fixtureSecret = Read-Host 'Temporary probe password' -AsSecureString
$env:GRAFT_UI_PROBE_PASSWORD = (New-Object System.Management.Automation.PSCredential('fixture', $fixtureSecret)).GetNetworkCredential().Password
try {
    dotnet experiments/ui-server/bin/Debug/net10.0/Graft.Ui.Server.dll --fixture
} finally {
    Remove-Item Env:\GRAFT_UI_PROBE_PASSWORD -ErrorAction SilentlyContinue
}
```

Open the printed `READY` HTTPS URL at `/projects`. The probe generates an untrusted, two-hour localhost certificate in memory. For isolated automated browser tests, MCPHub's existing Chromium instance was used with certificate-error handling relaxed **only in that disposable instance**. The platform trust store was not modified. That proves HTTPS transport and secure-cookie behavior, not production certificate validation.

The reusable [C# browser script](../ui-checks/browser-checks.csx) runs through MCPHub `browser_run_code`, which supplies `page`. Supply `fixturePassword` transiently as a C# string variable matching the server's temporary password; never save its value to the script. Navigate the isolated instance to `/projects` first. The checks exercise failed login/password clearing, successful login, protected content, actual cookie flags, refresh, logout, anonymous refresh, 390px form bounds and absence of negative tabindex on non-inputs. Use the existing MCP capability; no connector/server configuration is needed or authorized by this runbook.

## Authentication boundary

This experiment evaluates a small opaque-session design, **not** ASP.NET Core Identity or CookieAuthentication compatibility. A server-held SHA-256 digest indexes each random 256-bit bearer cookie. The cookie uses the `__Host-` prefix, Secure, HttpOnly, SameSite=Strict, path `/` and no Domain. Anonymous sessions expire after five minutes and signed-in sessions after fifteen, with a capacity of 128. Expired entries are removed when accessed or issuing a session. Restart clears every session.

The fixture password is hashed at startup with a random salt and platform PBKDF2-SHA256 (600,000 iterations); the host clears its inherited password environment variable. No default credential is compiled in. Password checks are capped at ten per minute globally for this small experiment. This is not a multi-user or production rate-limiting policy.

`GET /api/session` establishes an anonymous cookie plus a synchronizer CSRF token. All implemented mutations are POSTs and require an exact same-origin Origin plus that session's CSRF token in `X-Graft-CSRF`. Login rotates the session and CSRF token; logout removes the server entry and deletes the cookie. Protected data is checked server-side; replay of a revoked cookie gets 401. The client holds the CSRF value only in component memory and does not write tokens to browser storage. APIs use source-generated JSON metadata and `Cache-Control: no-store`.

`/api/session`, `/api/login`, `/api/logout` and `/api/projects` are Graft experiment routes, not GitLab-compatible endpoints. The synthetic project card tests authenticated rendering, not project membership authorization.

## Asset delivery and dependency provenance

The native executable embeds all 46 published assets and their Brotli/gzip representations (138 resources). It streams named assembly resources without extracting a `wwwroot`. Content types include JavaScript and `application/wasm`; encoding negotiation honors explicit quality exclusions, responses vary by Accept-Encoding, and representation-specific SHA-256 ETags support revalidation. The experiment uses `no-cache` even for fingerprinted assets so clients revalidate after upgrades. UI fallbacks are restricted to `/`, `/projects` and `/projects/`; missing API, Git and asset paths never receive HTML.

The client uses project-owned Razor/CSS, system fonts and no external CDN assets. Framework JavaScript/WASM/ICU assets come from the pinned .NET/Blazor **10.0.8** NuGet/runtime packages and lock file, are published locally, and are embedded locally. They are generated dependencies, not hand-written application JavaScript. Preserve the framework's [MIT license](https://github.com/dotnet/aspnetcore/blob/main/LICENSE.txt) and [.NET third-party notices](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT) for any later distribution, matched to the version shipped. The Graft project license remains undecided.

## Remaining work

See [recorded evidence](../../docs/validation/2026-09-18-ui-nativeaot.md). This does not establish required-host/static-musl behavior, production authentication, persistent sessions, real project permissions, services, trusted TLS, GitLab APIs or integration with the Git experiment. The custom session boundary still needs a production security review and an account/recovery design. MVP-03 storage feasibility is now recorded separately; the next implementation slice is MVP-04 after the DnaX package-source gate is resolved.
