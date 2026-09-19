# MVP-02 UI and NativeAOT acceptance

Date: 2026-09-18. Review: 2026-09-25 or after runtime, UI packaging, session design or host changes.
Owner: Implementation agent. Status: **PASS for bounded Windows feasibility**.
Source: local builds, C# HTTP/session assertions and MCPHub Chromium browser execution. User authorized the next recommended gate with "do next".

## Finding

A standalone Blazor WebAssembly client can boot and perform cookie-authenticated HTTPS requests from assets embedded in a C# NativeAOT API executable. The API serves static bytes and JSON; it does not host server-side Blazor components. This gives a concrete C# UI path compatible with the proposed server boundary on Windows. Linux/static-musl and the required legacy host remain untested for this component.

The result is for the explicit opaque in-memory session implementation in `experiments/ui-server`, not a general claim that ASP.NET Core Identity, CookieAuthentication or every middleware feature supports NativeAOT. No authentication/trimming warnings were suppressed.

## Build and artifact

- Windows x64, SDK 10.0.300, .NET runtime/compiler and Blazor packages 10.0.8.
- Final `dotnet build Graft.slnx`: exit 0, zero warnings/errors; the server's build publishes the client and embeds the output. Both Debug host and Release client are kept in the normal build path.
- Native publish command: `dotnet publish experiments/ui-server/Graft.Ui.Server.csproj -c Release -r win-x64 --self-contained true -p:UiProbeAot=true -o artifacts/ui/native`. Exit 0, no emitted NativeAOT or IL trimming warnings.
- Native executable: **28,305,408 bytes**, SHA-256 `1d68d717e8334d318d395a0160f0fcce741eba4522329aabfcf7753d66239643`.
- Copied only the executable to ignored `artifacts/ui/standalone/`. No PDB, runtimeconfig, certificate, framework file or `wwwroot` accompanied it. HTTP acceptance used a separate temporary working directory. Browser execution also used this isolated copy. It reported `MODE: NativeAOT`.
- Embedded resources: **138**, totaling **15,829,975 bytes**: 46 identity assets and corresponding Brotli/gzip variants. This includes framework/runtime files and multiple globalization data assets; the browser need not load all of them.
- Source changes are local and uncommitted. The hash identifies the tested executable, not a release tag. Earlier compatibility and managed-Git experiments were preserved.

## Toolchain boundary

The browser project's evaluated `UsingBrowserRuntimeWorkload`, `WasmBuildNative` and `RunAOTCompilation` were all `false`. Standard NuGet SDK/runtime assets plus managed build/publish tasks produced the client. No workload was installed and no Emscripten, Python or Node build/test process was launched by project tooling. Browser testing used the already available MCPHub service and installed Chromium runtime, with C# automation.

During setup, explicitly disabling native symbol stripping inadvertently triggered the SDK's native-workload requirement. That unnecessary setting was removed after inspecting the SDK targets; the build then succeeded through the ordinary non-native path. Missing-workload errors were not bypassed or suppressed. The SDK's informational recommendation to install optional `wasm-tools` remains visible. Microsoft documents that its [native WebAssembly tools](https://learn.microsoft.com/en-us/aspnet/core/blazor/webassembly-build-tools-and-aot?view=aspnetcore-10.0) use Emscripten; enabling those tools remains a separate tooling decision.

## Acceptance coverage

| Behavior | JIT host | Isolated NativeAOT executable |
| --- | --- | --- |
| Session self-checks: CSRF binding, rotation, revocation, expiry, capacity, expired-entry cleanup | PASS | PASS |
| 50 HTTP assertions in the C# harness | PASS | PASS |
| Published Blazor boot at `/projects` | PASS | PASS |
| Wrong password, successful login and synthetic protected project view | PASS | PASS |
| Authenticated deep-link refresh, logout and anonymous refresh | PASS | PASS |
| Browser confirms Secure/HttpOnly/Strict cookie attributes | PASS | PASS |
| 390px form bounds, cleared password field, negative-tabindex check | Not separately recorded | PASS |
| Visual inspection at desktop viewport | PASS | PASS |

HTTP assertions cover: expected JSON and HTML types; WASM magic/MIME and JS MIME; HEAD; Brotli/gzip decoded equality; Accept-Encoding exclusions; ETag/304; deep links; no HTML on API/Git/missing asset routes; denied anonymous access; cookie flags; CSRF absence/mismatch; foreign/missing Origin; malformed JSON/null credentials/wrong content type; bad password; session and CSRF rotation; stale-cookie rejection; protected JSON; logout/replay; rejected logout preserving the session; Host validation; and the global fixture login-attempt limit.

Commands for reproduction are in [the runbook](../../experiments/ui-server/README.md). The C# browser checks are saved as [browser-checks.csx](../../experiments/ui-checks/browser-checks.csx) without credentials.

A fresh isolated Chromium browser reported no console errors during native boot. The deliberate wrong-password test then produced the expected single HTTP 401 console entry; no unexpected browser errors were observed. The test browser's certificate-error handling was relaxed only for that disposable instance because the probe generates an untrusted certificate. C# HTTP tests pinned that certificate's exact SHA-256. No machine certificate trust changes were made, and production certificate trust was not tested.

## Payload observation

On initial native-server navigation in a fresh browser context, resource timing recorded 44 subresource requests with **2,357,979 encoded bytes** and **7,315,524 decoded bytes**. The document added 211 encoded / 546 decoded bytes, for approximately **2.36 MB** of encoded response bodies. Every resource request was same-origin. This is one local payload observation, excludes transport overhead, and is not a network latency, memory or production performance benchmark.

## Engineering corrections and scope

The initial generic ASP.NET fallback did not route dotted asset names. It was replaced with an explicit catch-all plus a restricted UI-route allowlist, then tested for both asset success and API/Git error isolation. Build orchestration now gives embedded resources stable relative names, avoids solution configuration leaking into the Release client publish, and clears only its verified generated client-output directory to prevent stale fingerprinted resources from accumulating.

The browser still uses framework-provided JavaScript and a prebuilt WebAssembly runtime; application behavior is C#/Razor. Native runtime, TLS and compression libraries remain dependencies. No custom JavaScript application, public CDN, third-party CSS or font pipeline was introduced.

Remaining: real accounts/permissions, persistent and multi-process session behavior, recovery, integration with Git, production auth/security review, trusted TLS deployment, service modes, browser/client matrix and Linux/static-musl/required-host acceptance. No broader GitLab API claim follows from this gate.

**Recommended next action — Owner: Implementation agent:** execute MVP-03 using a pinned DnaX migration package and Dapper.AOT, including drift/refusal, upgrade/rollback, concurrent migration and published-binary tests. Preserve the existing Linux static-link configuration when adding native dependencies; do not infer old-host support from this Windows result.
