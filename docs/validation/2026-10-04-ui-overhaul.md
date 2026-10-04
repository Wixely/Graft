# Browser UI overhaul evidence

Date: 2026-10-04. Owner: Implementation agent. Review when the theme tokens, the Content-Security-Policy, the routed page set, or the supported Chromium baseline changes.

## Result

The browser client was rebuilt from a single-column prototype into a GitLab-shaped interface with a light and dark theme. It passes a rendered workflow in Chromium 153 against the disposable production fixture, and the Git/API/browser acceptance harness is unaffected.

Light mode is grey surfaces with a blue accent; dark mode is dark grey with a neon blue accent. Every colour is a custom property defined on bare `:root`, with the dark palette repeated under a guarded `prefers-color-scheme` block and again under `[data-theme="dark"]` so an explicit choice wins in either direction. The accent is used for links, controls and focus only, never body text, so it never carries reading contrast.

`Home.razor` previously mixed sign-in, the project list, user administration, tokens and password changes into one 415-line page. It is now split across `/-/login`, `/` , `/-/profile`, `/-/admin/users` and `/-/admin/import`, under GitLab's reserved `/-/` prefix, which cannot collide with a user namespace. The session cookie and `X-Graft-CSRF` pairing that was copy-pasted roughly a dozen times now lives in one place.

## Rendered checks

Chromium 153, against `tests/Graft.Browser.Fixture`:

- An operating-system dark preference with no stored choice rendered dark (`#16181d`, accent `#4cc2ff`). An explicit light choice overrode it (`#f6f7f9`, accent `#1f6feb`), which is the case a naive media-query-only implementation gets wrong.
- The toggle moved between themes, persisted across a reload, and relabelled itself and its icon each way.
- Sign-in, the project list, the repository browser, the profile and the administration pages all rendered with the expected content.
- The hostile fixture file still displayed `<script>throw new Error('must remain text')</script>` as text. No script element was created and no global was defined.
- The binary fixture file still reported as binary rather than being decoded.
- At a 390 × 844 viewport, document width and scroll width were both 390 CSS pixels, the sidebar collapsed to a horizontal strip and the body stacked.
- A keyboard Tab produced a solid 3 px focus outline at 2 px offset and matched `:focus-visible`; focus moved in document order.
- Zero console entries across the whole workflow.

## Theme selection without weakening the policy

The Content-Security-Policy is `script-src 'self'`, so the usual inline anti-flash snippet is not available. `theme.js` is an external blocking script in `<head>` that applies the stored preference before first paint. **No CSP change was required**, and none was made. Theme preference is per-browser `localStorage`; it is not an account setting and does not follow a user between devices.

Icons are hand-authored inline SVG. There is no icon font, no external request and no third-party licence to track.

## Boundaries

- This is rendered behaviour and a focused accessibility smoke test on one browser. It is not a screen-reader audit, a WCAG conformance claim or a cross-browser matrix.
- Contrast was chosen deliberately but was not measured with a contrast tool; the neon accent is held away from body text rather than certified at a ratio.
- The fixture certificate is an ephemeral loopback certificate. Rather than modifying a trust store, this run drove a throwaway Chrome profile launched with certificate errors ignored, which was removed afterwards. That differs from the 2026-09-19 run, which temporarily trusted the certificate instead.
- `/-/admin/backup` is not implemented and is not linked; see the handoff for why backup remains an offline command for now.

Recommended next action: Owner: Implementation agent — measure the light and dark palettes against WCAG contrast ratios and record the numbers, so the "accent never carries reading contrast" rule is evidenced rather than asserted.
