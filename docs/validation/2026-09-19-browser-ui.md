# Rendered production browser evidence

Date: 2026-09-19. Owner: Implementation agent. Review when the Blazor forms, repository browser, focus styles, client asset publication or supported Chromium baseline changes.

## Result

The current production Blazor WebAssembly client passes a rendered workflow in MCPHub Playwright Chromium 1217 against the current `Graft.Server` on Windows. The browser loaded the embedded client, authenticated through the secure session flow, created a private project, browsed a seeded Git repository and completed the final run with no console messages.

The first run found one product defect: the project-path `pattern` attribute was invalid under Chromium's Unicode `v` regular-expression rules because its character-class hyphen was unescaped. The form now uses a valid pattern that mirrors the server's 2-39 character boundary, alphanumeric endpoints and single punctuation separators. A clean rebuilt run confirmed `bad--path` has `patternMismatch=true`, focuses the invalid field, makes no request and writes no console error.

## Rendered checks

- Failed login produced the visible `status` message and cleared the password field; successful login exposed only the fixture user's private projects.
- Project creation produced a project card and `Project created.` status. A duplicate path produced the bounded visible error.
- The seeded repository rendered its `main` branch, commit, and three files.
- The hostile file displayed `<script>throw new Error('must remain text')</script>` as text. No inline script element was created and no script error ran.
- The binary file rendered `Binary file · 5 bytes` rather than decoding arbitrary bytes as text.
- At a 390 × 844 viewport, document width and scroll width were both 390 CSS pixels.
- Keyboard Tab moved between file buttons in document order. The focused button had a solid 3 px visible outline.
- The final rebuilt successful workflow had zero browser console entries. The deliberate failed-login and duplicate-path checks in the discovery run produced only their expected 401 and 400 resource entries.

This is rendered behavior and a focused accessibility smoke test. It is not a screen-reader audit, a full WCAG conformance claim or a cross-browser matrix.

## Reproduction and isolation

`tests/Graft.Browser.Fixture` creates a random temporary database and repository root, a `fixture` administrator, a short-lived write token, and a seeded repository containing text, hostile HTML and binary data. It launches the current server on a random loopback HTTPS port and removes its complete state when stopped. Git configuration is isolated; TLS verification is disabled only for the fixture's real Git push to the generated loopback certificate.

```powershell
dotnet build tests\Graft.Browser.Fixture\Graft.Browser.Fixture.csproj -c Debug
$env:GRAFT_BROWSER_FIXTURE_PASSWORD = '<temporary-16-to-256-character-password>'
dotnet tests\Graft.Browser.Fixture\bin\Debug\net10.0\Graft.Browser.Fixture.dll src\Graft.Server\bin\Debug\net10.0\Graft.Server.dll
```

For the recorded automation, the active ephemeral localhost certificate was temporarily added to the current user's Windows root store because this MCP browser does not expose an ignore-certificate-errors context option. The exact certificate was removed immediately after the browser instance closed. The fixture itself never changes a trust store. This validates the loopback UI through HTTPS but does not validate a deployment CA, DNS name, client rollout or renewal.

## Next action

Owner: Operator + implementation agent — validate the intended CA-issued certificate, DNS/SAN, client trust and renewal procedure in the deployment environment. The durable DnaX package source and deferred Linux 3.10.108 product-artifact run remain separate release gates.
