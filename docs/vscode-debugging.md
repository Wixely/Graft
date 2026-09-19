# VS Code debugging

Updated: 2026-09-19. Owner: Implementation agent. Review when projects or debug output paths change.

Open the repository folder in VS Code with the Microsoft C# extension (`ms-dotnettools.csharp`). The workspace recommends it; no extension is installed automatically. Use the .NET SDK selected by `global.json`.

In **Run and Debug**, select **Debug UI and authentication probe**, then press **F5**. Enter a temporary 16-256 character password at the masked prompt. The host builds the embedded Blazor client, starts on a random loopback HTTPS port and opens `/projects` in your default browser. Sign in as `fixture` using that password. The disposable probe uses an untrusted localhost certificate; the browser may require a local certificate exception. No trust root is installed. Stop debugging to discard sessions.

Set server breakpoints in `experiments/ui-server`. The browser client is published in Release mode and embedded; browser-side Razor/C# debugging and hot reload are not configured. Restart debugging after client changes to rebuild embedded assets.

| Launch option | Behavior |
| --- | --- |
| Debug MVP-04 accounts and permissions | Runs the disposable SQLite acceptance flow for accounts, projects, sessions, tokens, roles and revocation. |
| Debug MVP-06 repository UI and API | Runs the real Git client, GitLab-style API and browser-session/repository checks against a disposable HTTPS server, SQLite database and durable object/ref directory. |
| Debug MVP-07 import and recovery | Runs synthetic bundle inventory, repeatable Git HTTPS import, quiescence, backup/tamper/restore and rollback checks through the admin CLI. |
| Debug MVP-08 interactive packaging checks | Starts the Debug server through the packaging harness and measures startup, working set and loopback health latency against provisional gates. |
| Debug operator TLS lifecycle | Generates disposable PKCS#12/password files, pins the certificate, tests restart and replacement, and verifies non-loopback refusal without a certificate. |
| Debug UI and authentication probe | Interactive UI with server C# breakpoints and browser opening. |
| Debug compatibility probe | Runs the six compatibility checks and exits. |
| Debug storage migration probe | Runs the disposable DnaX/SQLite migration checks and exits. |
| Debug managed Git probe (loopback, disposable) | Starts the in-memory Git server; use the printed URL plus `/fixture.git`. |
| Debug managed Git acceptance client | Runs the real Git workflow harness; requires Git on PATH. |
| Debug UI HTTP acceptance checks | Runs the HTTP harness with its own temporary password. |
| Debug managed Git self-checks | Runs the Git server's direct checks and exits. |
| Debug UI session self-checks | Runs the host's direct session checks and exits without a password prompt. |
| Attach to .NET process | Select an already running managed process. |

Each launch builds its required projects in Debug mode. **Ctrl+Shift+B** still builds the complete solution. The UI acceptance task builds the host and checks sequentially. Avoid concurrent UI builds or debug launches in this checkout because they share generated client assets.

Acceptance harnesses start child server processes. Their launch options debug the harness; server breakpoints require attaching to the child process, which may exit quickly. For ordinary server debugging, select the interactive server launch instead.

These `coreclr` options debug managed DLLs, not published NativeAOT executables. NativeAOT acceptance remains a separate runbook step. Automatic browser opening follows the [official C# debugger configuration](https://code.visualstudio.com/docs/csharp/debugger-settings), using the host's `READY` output in the Debug Console.

The service, systemd, container and published NativeAOT checks remain command-line acceptance tasks because they require elevation, a Linux service manager or a container engine. Remaining verification: Owner: Developer — run the UI option with F5 and confirm a server breakpoint binds in the local VS Code installation. Configuration/build checks do not establish an interactive debugger session.
