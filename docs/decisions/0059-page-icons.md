# 0059: A page can have its own icon

- Date: 2026-11-18
- Status: Accepted, implemented
- Source: On 2026-11-18 the project owner asked for project pages to show their own favicon, without caching letting
  one page show another's, and suggested configuring it in `.graft/project.json`.
- Builds on: [ADR 0006](0006-project-pages-and-public-browsing.md) (the config may only narrow, and names nothing it
  does not grant), [ADR 0048](0048-page-looks-like-the-repository.md) (the page on the forge) and
  [ADR 0051](0051-page-artifacts.md) (the pages origin)
- Review trigger: an SVG icon found running script anywhere; a request for several sizes or an Apple touch icon; a request for the
  repository view to wear the icon too; a browser found showing a stale icon despite the versioned address

## What was missing

Every page on the forge showed Graft's icon in the browser tab, whatever the project. A site on the pages origin that
named no icon made the browser ask for the host's own `/favicon.ico`, which every site on the host shares. Anything
served or remembered there would have shown on all of them.

## Decision: `page.icon` names the file

`page.icon` is a path in the repository. It must be a file the page already serves to the same readers: one `files`
admits, or one inside `site.root`, which the pages origin serves to anybody the page is published to. Naming any other
file is a config error, as it is for `image` and `readme`, so an icon is never an implicit grant.

The formats are the ones browsers take for an icon, widened the same day at the owner's request for "all common file
types for favicons": `.ico`, `.png`, `.apng`, `.gif`, `.jpg`, `.jpeg`, `.webp`, `.avif`, `.bmp` and `.svg`. The file
is served only if its bytes prove one of them, as the type they prove:
- a Windows icon directory whose entries all lie inside the file;
- a BMP, an AVIF (an `ftyp` box naming the brand), or an extended WebP, which is how a WebP with transparency is
  written;
- an SVG, as described below;
- or an image the avatar validator accepts.

SVG is accepted here and nowhere else on the forge. Elsewhere it is refused because it is a document that can run
script. An icon is drawn as an image, and a browser runs no script in an image. The remaining risk is somebody opening
the icon's address as a page. So every icon response carries
`Content-Security-Policy: sandbox; default-src 'none'; style-src 'unsafe-inline'`, which blocks script and requests
and gives the document an origin of its own. An SVG that declares entities is refused, so it cannot expand without
bound in a reader's browser. Readme pictures still exclude SVG.

## Decision: an address per icon, and revalidation every time

Browsers keep icons by address and do not always ask again when the response says to. A shared address is how one
page's icon turns up on another, so the forge never shares one:
- **The page on the forge** is told its icon at `/api/graft/pages/<slug>/icon?v=<blob id>`. The address differs for
  every project and changes whenever the icon does. The page puts it in the tab by replacing the icon link, since
  some browsers look again only when a link is added, and restores Graft's when the reader leaves.
- **The response** is `no-cache, private` with the blob id as its entity tag, so a browser revalidates it and gets
  `304` while it is unchanged.
- **On the pages origin,** `/<slug>/-/icon` serves the same file `no-cache`, under the project's own path, for a site
  to name in `<link rel="icon">`. It sits beside `-/repo` and `-/graft.json`, resolved before any file.
- **The pages origin's `/favicon.ico`** belongs to no site and is answered `404` with `no-store`, so a browser keeps
  nothing from it to show on another site.

## Not done

- The repository view and the rest of the forge keep Graft's icon; only the page wears the project's.
- Several icon sizes, Apple touch icons and web app manifests. A site can name those itself in its HTML.
- Rewriting a site's HTML to add the icon. A site names its own, or uses `-/icon`.

## What was verified

- **Unit checks:** an icon the page does not otherwise serve is refused; one in `files` or inside the site's folder
  is accepted; every listed extension is accepted by name and another is refused. By content, an icon file, PNG,
  BMP, AVIF, transparent WebP and SVG are accepted as their types; an SVG declaring entities, HTML and plain text
  are refused.
- **The Git acceptance suite**, with an icon inside the site's folder:
  - the page response names an icon address carrying a version;
  - that address serves the icon without an account, as `image/x-icon`, `no-cache`, with an entity tag, under a
    sandboxing content security policy;
  - `/<slug>/-/icon` serves it on the pages origin;
  - the pages origin's own `/favicon.ico` is `404` and `no-store`.

The tab swap in the browser was not driven by the suites.
