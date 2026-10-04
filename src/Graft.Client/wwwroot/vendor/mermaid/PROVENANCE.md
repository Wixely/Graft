# Mermaid

| | |
| --- | --- |
| File | `mermaid.min.js` |
| Upstream project | [mermaid](https://github.com/mermaid-js/mermaid) |
| Version | `11.15.0` |
| Source | `https://cdnjs.cloudflare.com/ajax/libs/mermaid/11.15.0/mermaid.min.js` |
| SHA-256 | `70137e77bb273bb2ef972b86e8b0400cca8be53cb25bfc45911a186dc98665de` |
| Licence | MIT, see `LICENSE` in this directory |
| Vendored | 2026-10-04 |

Unmodified upstream bytes. Verify with:

```powershell
Get-FileHash -Algorithm SHA256 mermaid.min.js
```

## Why it is isolated rather than loaded into the page

Mermaid creates `<style>` elements while rendering. Graft's policy is `style-src 'self'`, which blocks that,
and relaxing it for the whole application would be worst on the one page that renders repository content
written by somebody else.

So mermaid runs inside `<iframe sandbox="allow-scripts">` pointed at `/-/mermaid-sandbox`. Without
`allow-same-origin` the frame has an opaque origin, so it cannot reach Graft's DOM, cookies or session even
if a diagram triggers a bug in it. The server sends that one route its own policy, which permits inline
styles inside the frame and nothing else: no network, no framing of anything further.

The frame cannot be `srcdoc`, because a `srcdoc` document inherits the embedding page's policy and would hit
the same restriction. For the same reason its policy names this server's origin explicitly rather than using
`'self'`, which inside an opaque origin matches nothing and would block the script from loading at all.

Diagram source is passed by `postMessage` and the frame reports its rendered height back the same way.

## Checked before vendoring

The bundle contains no `eval` and no `new Function`, so it does **not** require `script-src 'unsafe-eval'`.
That was the deciding factor in vendoring it rather than rejecting it: a diagram renderer is not worth
re-enabling runtime code generation across the application.

It is 3.2 MB, so it is fetched only when a rendered README actually contains a diagram. An ordinary page
never loads it.
