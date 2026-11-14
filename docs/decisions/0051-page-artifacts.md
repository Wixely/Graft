# 0051: Page artifacts, and the file types a site serves

- Date: 2026-11-13
- Status: Accepted, implemented
- Source: A request on 2026-11-13 for a project page that carries a playable demo of a WebAssembly game, where the
  build is "embedded in the page but not committed to the repo"; the project may be private, the page public, and
  "the specific artefact for demo needs to be public". Settled in the same conversation: artifacts are private by
  default and need "a special promotion to public"; whatever is promoted is "available through the proxy pages", so
  "everything is deliverable from the project pages"; no iframes; artifacts answer under `/a/` by default,
  "changeable if really required"; files can be uploaded "directly by a dev or via mcp"; the name is "page
  artifact"; more media types are served, and the list is "configurable in the graft settings"
- Builds on: [ADR 0006](0006-project-pages-and-public-browsing.md) (the page, the publish switch and the pages
  origin), [ADR 0027](0027-packages-and-registry.md) (files beside the repository),
  [ADR 0048](0048-page-looks-like-the-repository.md) (a page with a site forwards to it)
- Review trigger: a build that needs cross-origin isolation headers (threads, `SharedArrayBuffer`); a request to
  publish a page artifact from a runner job; a request for a site per branch or per version; a second tenant whose
  sites should not share browser storage with the first; a request to embed a demo inside the repository-style page

## Decision 1: a page artifact is a named folder, uploaded whole, beside the repository

A page artifact is a set of files under one name. It is uploaded as a single zip archive and replaced as a single
unit: a new upload is unpacked into a new directory and the database row is moved to it, after which the old
directory is removed. What is served is therefore always one whole build. This is the shape a bundler's output has.
The build this was designed against writes three files, two of them named with a hash that changes every time, so
uploading file by file would leave old builds accumulating and a page pointing at files that are not there yet.

The files live on disk inside the project's repository directory, under `page-artifacts/<name>/<generation>/`, as
package files do. A backup of the repositories directory carries them; a fork does not; deleting the project removes
them. The database holds the name, the promotion, the current generation and the totals (migration 30).

A name is up to 40 lower-case letters, digits, `-` and `_`. A project keeps at most 50 artifacts, and an artifact at
most 2000 files.

## Decision 2: the archive is somebody else's bytes

Every path in the archive is checked before anything is written, and a refused archive leaves nothing behind. A path
is relative, has no empty, hidden or dot segments, no backslash once Windows separators are normalised, no character
or device name that Windows reserves, and no trailing dot or space. Two paths that differ only by case are refused,
because they are one file on one file system and two on another. The unpacked size is counted as bytes are written,
not read from the archive's own header, and bounded by the same limit as the upload.

A single folder that wraps everything in the archive is dropped. Zipping a build folder produces one, and it is
packaging: the artifact's own name is already the directory its files answer under.

## Decision 3: private until promoted, and promotion is a Maintainer's

An uploaded artifact is not served anywhere public. Somebody who can read the repository can list a project's
artifacts and download a file from one, always as an attachment and never as the type its name suggests.

Promoting an artifact is a separate act, recorded in the database and in the project's audit trail. It needs the
permission to manage the project, the same as publishing the page, because both decide what a stranger can read.
Uploading needs only the permission to push: an artifact is a build of the project, like a package.

Replacing an artifact that is already promoted keeps it promoted. This means a Developer's upload can change what is
public, which is the same position a Developer's push to the default branch is in for a committed site, and is what
makes publishing a new build one step. A project that wants every public build reviewed by a Maintainer withdraws
the artifact before it is replaced, or uploads under a new name.

The repository's config cannot promote anything. `site.artifact` (decision 5) names an artifact; it does not grant.

## Decision 4: a promoted artifact is part of the site, on the pages origin and nowhere else

A promoted artifact answers on the pages origin at `<project-slug>/a/<name>/`, and only while the project's page is
published, so there is one switch that takes a project off that origin. It is never served by the forge's own
origin: an artifact is project-authored script, and the reasons a site cannot share an origin with the session
(ADR 0006) apply to it unchanged. This rules out showing a demo inside the repository-style page without a frame,
and frames were declined, so the demo lives on the site.

An artifact is looked up before the repository. Where a promoted artifact and a committed file claim the same path,
the artifact answers; where no promoted artifact has that name, the request falls through to the committed site.
Serving an artifact reads neither the repository nor its config, so it does not wait on the repository's gate.

The prefix `a` is an administrator's setting (decision 6). It is one path segment and never `-`, which is Graft's
own namespace on a site.

Files are served with the generation as their validator and `Cache-Control: no-cache`, so a returning browser
revalidates instead of downloading a multi-megabyte module again, and with range requests, which media players
need. Committed site files gained the same two things, with a digest of the content as the validator, and are now
sent after the repository's gate is released.

Graft does not rewrite what it serves. A page inside an artifact must use relative addresses, because it is served
under a folder; a bundler that writes root-relative addresses needs telling (for the build this was designed
against, `trunk build --public-url ./`).

## Decision 5: a site can be an artifact alone

`page.site` accepts `artifact` beside `root`, and needs at least one of them. With `"site": { "artifact": "demo" }`
the artifact is the site's front door: the page's site address points at it, the project page forwards to it as it
does to any site (ADR 0048), and the site's own root redirects to it. A site with no `root` serves no file from the
repository.

A site with both serves committed files from `root` and its front door from the artifact. A committed page can also
simply link to, or load scripts from, `a/<name>/…` without naming it in the config.

## Decision 6: served file types are a built-in list plus what an administrator adds

The built-in list gained `.wasm`, opaque program data (`.bin`, `.data`, `.dat`, `.pck`), audio, video, a few more
image, font and text types, glTF and `.zip`. The rule is unchanged: the type comes from the extension, never from
the bytes, and an extension on no list is not served at all.

An administrator can add types under Administration, Project sites: an extension and the content type to send. An
added type applies to committed sites and to artifacts alike, on the next request, and replaces a built-in type of
the same extension. The same screen holds the artifact prefix and the largest artifact (256 MiB by default, at most
2048). These are rows in the server settings table beside the agent switches, audited when changed, not start-up
switches: they are the instance's policy, not its wiring.

## Decision 7: three ways in

- A token: `PUT /api/graft/projects/<project>/page-artifacts/<name>` with the zip as the body, and
  `GET`, `PUT …/promotion`, `DELETE` and `GET …/files/<path>` beside it. Under Graft's own prefix because GitLab has
  no such thing; GitLab Pages deploys from a pipeline, which is a review trigger here.
- The browser: a panel in the project's settings, uploading through the same resumable transport as release files.
- Agents: `graft_list_page_artifacts`, `graft_upload_page_artifact` (a zip as base64, which bounds it to roughly
  10 MB), `graft_set_page_artifact_promoted`, and `graft_delete_page_artifact` behind the destructive switch.

## What is not done

- **Cross-origin isolation.** A build that uses threads needs `Cross-Origin-Opener-Policy` and
  `Cross-Origin-Embedder-Policy` on its page. Nothing sets them. The build this was designed against is
  single-threaded.
- **Isolation between projects' sites.** Every site on the pages origin shares one origin, and so one browser
  storage. A game's saved settings sit beside every other project's. That predates this decision and is harmless on
  a forge with one tenant.
- **Publishing from a runner.** A job can call the token route; nothing does it for a job.
- **Versions.** An artifact has one current build. A second build to keep is a second name.
- **A sweep for unnamed directories.** On Windows a file a reader holds open can refuse deletion when a build is
  replaced. What is left is unreachable and goes when the artifact or the project is deleted.
- **Uploading from the browser as a Developer.** The panel is in project settings, which only a Maintainer sees. A
  Developer uploads with a token.

## What was verified

`tests/Graft.Git.Acceptance` (`CheckPageArtifacts`), against a private project with a published page: refused
archives (not a zip, a path above itself, a hidden file, a device name, a case collision, empty) leave nothing on
disk; a Developer uploads and cannot promote or delete; an unpromoted artifact answers 404 on the pages origin and
downloads only as an attachment for a member; after promotion the index page, the module (as `application/wasm`,
byte for byte), a 304 for a matching validator and a 206 for a range; an unserved type stays unserved; a replacement
serves only the new files, stays promoted, changes the validator and removes the old directory; a site that is an
artifact alone; the administrator's prefix, limit and added type applying on the next request, with invalid values
and a non-administrator refused; the page's publish switch standing over a promoted artifact; withdrawal, deletion
and the audit trail. `tests/Graft.Acceptance` covers the name, path, settings and config rules, and
`tests/Graft.Mcp.Acceptance` the four tools.

By hand, in headless Chrome against the browser fixture: a real build of the game this was designed for (three
files, a 7 MB module, its page edited to relative addresses, zipped with Windows PowerShell's `Compress-Archive`
around a wrapping folder) was uploaded through the settings panel, promoted there, and ran from
`<slug>/a/<name>/` on the pages origin, with the module served as `application/wasm` and the renderer starting. The
administrator's screen saved an added file type.

Building it found that the pages origin took a request's segments after trimming its slashes and then asked whether
the result ended in one, so a folder inside a committed site never answered with its index page. That is fixed here
and covered for an artifact's sub-folder.

Not verified: the same from the live deployment behind its proxies, and the NativeAOT publish of this change.
