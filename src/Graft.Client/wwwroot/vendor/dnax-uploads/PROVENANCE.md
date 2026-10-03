# DnaX.Uploads browser module

| | |
| --- | --- |
| File | `uploads.js` |
| Upstream project | [DnaX](https://github.com/Wixely/DnaX) |
| Version | `10.0.0-alpha.7` |
| Source commit | `73826d731cef903083f2a4ae15ba6ac284e83ce3` (public tag `v10.0.0-alpha.7`) |
| Upstream path | `src/DnaX.Uploads/wwwroot/uploads.js` |
| SHA-256 | `bb2ea936fb56d6ddaa3552ed4dc50db111bded74366dd4ee22cfd3223d80f4ba` |
| Licence | MIT, Copyright (c) 2026 DNAX contributors |
| Vendored | 2026-10-03 |

Unmodified upstream bytes. Verify with:

```powershell
git -C <DnaX checkout> show 73826d731cef903083f2a4ae15ba6ac284e83ce3:src/DnaX.Uploads/wwwroot/uploads.js |
  Get-FileHash -Algorithm SHA256 -InputStream { $input }
```

## Why this file is vendored rather than referenced

`DnaX.Uploads` ships as a Razor Class Library that serves this module at `_content/DnaX.Uploads/uploads.js`.
Graft's browser client is a standalone Blazor WebAssembly application, which cannot reference that library:
it carries an ASP.NET Core framework reference that does not exist on the `browser-wasm` target. The server
project references the package for its endpoints; the client vendors the module and imports it directly.

The library's own `UploadPicker` component is deliberately **not** used. It requires
`@rendermode="InteractiveServer"`, which Graft does not have and which has not been validated under
NativeAOT. `mount()` is called from JavaScript instead, so the upload queue is owned entirely by the browser.

## What this module needs from the page

- A same-origin endpoint group; Graft mounts one at `/api/graft/uploads`.
- An authenticated session cookie. It fetches with `credentials: 'same-origin'`.
- An antiforgery token, which it obtains itself from `GET <endpoint>/capabilities` and returns in the
  `X-DnaX-Antiforgery` header on every mutation.

It is compatible with Graft's `script-src 'self'; style-src 'self'` policy: it uses no `eval`, no `innerHTML`,
no inline `style` attribute, no injected `<style>` element and no worker or `blob:` URL. It builds DOM with
`textContent`, `className`, `dataset` and `<progress>`, which Graft's stylesheet themes through the
`.dnax-uploads` and `.dnax-upload-row` classes.

## Upgrading

`DnaX.Uploads` is an experimental prerelease; its API and journal format are explicitly not stable. When the
package version in `src/Graft.Server/Graft.Server.csproj` changes, re-copy this file from the matching commit,
update this record, and re-check the `.dnax-uploads` styling and the CSP properties above.
