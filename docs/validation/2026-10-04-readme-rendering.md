# README rendering evidence

Date: 2026-10-04. Owner: Implementation agent. Review when the markdown pipeline, the content security policy, the diagram sandbox, or the image route changes.

## Result

A project page now renders the README at the root of the branch being viewed. This is the first place Graft turns repository content into markup rather than text, so it is the first place a stored cross-site scripting bug could live.

Three layers hold that closed, and each was proved separately by removing it and watching the suite fail:

| Layer | What it does | Removing it |
| :--- | :--- | :--- |
| `DisableHtml()` | raw HTML in a README is escaped, never parsed | the output scan rejects the document; the page degrades to plain text |
| AST URL checks | link and image destinations judged as structured values | a `javascript:` autolink reaches the output and is caught by the scan |
| Output scan | allowlist of elements and attributes, fails closed | `<script>` survives into the page and the corpus assertion fails |

The suite is `tests/Graft.Git.Acceptance`, now 221 assertions. The corpus is pushed as real repository content on a branch of its own and read back through the browser endpoint, so the assertions run against the whole path — parser configuration, rewriting, the fail-closed scan and JSON serialisation — rather than against the renderer in isolation.

Half the corpus is hostile: raw `<script>`, `<svg onload>`, `<iframe>`, `<object>`, `<embed>`, `<form>`, `<base>`, `<link>`, `<style>`, an inline handler, a comment break-out, a tag break-out, and `javascript:` in plain, mixed-case, entity-encoded, named-entity, tab-separated, null-separated, percent-encoded and leading-space forms, plus `vbscript:`, `data:`, `file:`, `blob:`, protocol-relative and server-absolute destinations, reference-style definitions, generic-attribute syntax, path traversal and angle-bracket autolinks.

The other half is ordinary markdown, and it is load-bearing: **a renderer that escaped everything would satisfy every attack assertion and still be useless.** Headings, emphasis and its extras, lists, task lists, quotes, rules, fenced code with and without a language, tables, definition lists, footnotes, relative and external links, relative and off-site images, and mermaid diagrams are each asserted present.

## The parts most likely to be wrong

**Angle-bracket autolinks are a different node type.** `<https://example.com>` parses to `AutolinkInline`, not `LinkInline`, so the first implementation walked straight past them. That left `<javascript:alert(1)>` rendering as a live href — caught only by the last layer — and left ordinary external autolinks without the attributes that stop a click leaking a referrer. Found by running the corpus through the renderer directly before writing any assertions.

**Attribute names and attribute values must be judged separately.** The first output scan searched the whole document for ` onerror=` and for `javascript:`. Both appear harmlessly in ordinary content: a code sample discussing a scheme, an image caption reading `onerror=alert(1)`, prose about an event handler. Those degraded the entire document to plain text. The scan now parses tags, checks element and attribute **names** against an allowlist, and checks for an executable scheme only in `href` and `src` **values**. Both halves are asserted.

**Inline styles are refused by the page's own policy.** Markdig writes table column alignment as `style="text-align: …"`. Under `style-src 'self'` a browser blocks every one of them, so the table rendered but lost its alignment with no visible cause — eighteen violations on the fixture README. Relaxing the policy is the one thing that must not happen on a page showing somebody else's repository content, so alignment is converted to a class before rendering and `style` is no longer an allowed attribute. A rendered run now reports **zero policy violations**.

## External images

`img-src 'self' data:` already blocks off-site images outright, which is stronger than stripping a referrer: no request to the third party is made at all, so there is nothing to leak and no tracking pixel can fire. A proxy of the kind public forges run would be *weaker* here — it would mean this server fetching attacker-chosen URLs, which on a private network is a server-side request forgery primitive aimed at your own infrastructure.

So an image resolves against the repository or it does not load, and an off-site one is downgraded to a visible link the reader can follow deliberately.

Repository images are served by `GET /api/graft/projects/{id}/image`, which resolves the path through the Git object store rather than the filesystem — traversal reaches nothing because no Git tree entry can be named `..` — and passes the bytes through the same `ImageContent.Validate` that already protects avatars. The content type served is the one the bytes prove, never one the repository claims. Asserted: a committed PNG is served as `image/png`; a text file named `.png` is refused with 415; the route cannot be used to serve a README; traversal returns 404.

## Diagrams

Mermaid 11.15.0 needs inline styles, which the page's policy forbids. It renders inside `<iframe sandbox="allow-scripts">` at `/-/mermaid-sandbox`, which has an opaque origin and therefore cannot reach Graft's DOM, cookies or session. That path is served with a policy of its own that names the origin explicitly, because `'self'` inside an opaque origin matches nothing and would block the frame's own script. It cannot be `srcdoc`, which would inherit the parent policy and hit the same wall.

The main document's policy is **unchanged**:

```
default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self' data:;
connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'
```

Mermaid is 3.2 MB and loads only inside that frame, so a page with no diagrams never pays for it. The server renders a `mermaid` fence as an ordinary code block, so without scripting a reader still sees the diagram source rather than empty space.

The frame declares no colour scheme of its own, so a browser painted it a white canvas regardless of the page's theme. The embedder now passes the surface colour it is sitting on, and re-posts when the theme changes, so a diagram follows the theme instead of staying in the one it was first drawn in.

## Rendered check

Headless Chromium against the fixture README, driven over the DevTools protocol:

- Every syntax case present in the DOM, and no `<script>` anywhere inside the rendered README.
- The committed image decoded at its real size (2172×724), fetched from the image route.
- Two diagram frames, each `sandbox="allow-scripts"`, each drawing a real SVG (720×379 and 707×974).
- The off-site image rendered as a link; every off-site link carried `rel` with `noreferrer` and `target="_blank"`.
- **Zero content security policy violations**, in both themes.
- Console output: two warnings about the fixture's untrusted loopback certificate, which the fixture generates on purpose. Nothing else.

A sandboxed frame is site-isolated into a target of its own, so its console and its violations do not reach the parent. They were read by attaching to those targets, not assumed absent. An out-of-process frame is also not painted into a `captureBeyondViewport` screenshot, which made a correctly drawn diagram photograph as an empty box; that is a capture artefact and was confirmed as one before anything was changed.

## Boundaries

- Only the root of a branch is considered. A README deeper in the tree is an ordinary file, and a subdirectory listing carries none.
- A README over 512 KiB, or one that is not valid UTF-8, is not rendered.
- No relative-link rewriting between markdown documents: a link to `docs/guide.md` resolves against the page URL, not the repository.
- No raw route for non-image files, and no HTML passthrough ever.
- Markdig is BSD-2-Clause. Its binary redistribution obligation, and those of every other bundled component including OpenSSL and ICU which are statically linked into the Linux build, are recorded in `THIRD-PARTY-NOTICES.md`, embedded in the server, and reachable through `--licences` and `/-/licences`.
- **Markdig's behaviour under NativeAOT is unverified.** This machine has no C++ Desktop workload, so ILC never runs and emits no analysis; a trimmed publish crashes ILLink identically on the commit before this work, so that crash is environmental and unrelated. The existing practice of running this harness against the published AOT executable covers it, and the new assertions are inside that harness, so they will be exercised wherever AOT can actually be built.

Recommended next action: Owner: Implementation agent — run `tests/Graft.Git.Acceptance` against a published NativeAOT server on a machine with the C++ workload, to close the one verification gap above before the renderer is treated as settled.
