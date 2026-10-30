# 0006: Project pages and public browsing

- Date: 2026-10-13
- Status: Accepted user request, design agreed, implementation in slices. **Implemented on 2026-10-20:** the `.graft/project.json` config, the file allowlist, the page surface and the static site behind `--pages-origin`. See [the evidence](../validation/2026-10-20-project-pages.md). `features` was unimplemented, and naming one an error, until 2026-10-30, when the seven surfaces that now exist became its names, offered as links only to a reader the project admits; see [project-pages.md](../project-pages.md#features)
- Source: "Repos need a project page ... it might be that they only want to share the readme.md or other .md files, docker compose files, etc Or they might want to only allow issues to be shown ... A project page could also be a custom html site created similarly to github pages. i propose this config goes into a .graft folder, its important that if the config says only xyz files are available that the rest are NOT available - similarly for the website, only files inside the designated pages folder should be publically accesssible." And: "The option to enable a public repo browser ... Repos can only appear here if they are set as Public. Or if they have a public Project Page set up (a project page can exist without showing source code). so this means not only is the overall repo got a public/private/unlisted setting that is final, the project pages has their own settings. repo settings take precidence over project page settings."
- Review trigger: Any new route that reads repository bytes, any new project surface such as issues or releases, and any proposal to serve repository-authored HTML from the main origin

## What is being decided

Graft is private-only today. There is no visibility column: `POST /api/v4/projects` validates `visibility` against the literal `"private"` and discards it, the response field is a hardcoded string, and `ProjectAuthorization.Decide` denies a null identity before looking at anything else. Two features change that, and they share one foundation.

1. A **project page**: a curated surface an owner publishes instead of, or alongside, the source tree. It can expose chosen files, chosen product features, and a custom static site.
2. A **public browser**: an anonymous front page that lists, searches and describes projects that have been published.

## Decision 1: three repository visibilities, stored, and a separate page visibility

`Projects` gains a `Visibility` column with exactly three values.

| Value | Who may read the repository | Appears in the public browser |
| --- | --- | --- |
| `private` | Owner, members, administrators | No |
| `unlisted` | Anyone holding the path | No |
| `public` | Anyone | Yes |

Every project existing before the migration becomes `private`, and the public browser is off unless an operator turns it on. An operator who upgrades must not acquire an anonymous front page by surprise.

A project page carries its own published flag, so a `private` repository may publish a page. That is the headline case the request describes: share the readme, not the code.

## Decision 2: access arrives as one grant, and the grant carries its own reach

The precedence rule is "repo settings take precedence over project page settings". Expressed as a chain of conditionals that rule becomes a pile of interacting flags, and the denial requirement cannot be verified by reading it. So it is expressed as a type instead. A caller holds exactly one grant over a project.

| Grant | Who holds it | Reach |
| --- | --- | --- |
| `Member` | Owner, member, administrator | Everything their role allows, as today |
| `Repository` | Anyone, when visibility is `public` or `unlisted` | The whole repository, read-only, clone included |
| `Page` | Anyone, when the page is published | **Only the paths the page names.** No clone, no history, no diff, no branch list |
| `None` | Everyone else | Nothing, concealed as a 404 |

Grants are resolved strictly in that order and the first that applies wins. Because `Repository` is strictly broader than `Page` and is checked first, a page can never be consulted in a way that widens access, and precedence is a property of the order rather than of any individual check.

This also fixes the meaning of a restrictive page on a public repository. The source is already readable, so the page is presentation and **not protection**. The settings UI must say so where the choice is made, because the opposite assumption is the obvious one and it is wrong.

## Decision 3: a page surface is a separate read path, not a filter over the repository view

This is the decision the denial requirement turns on, and it is the reason the feature is not simply a flag.

`RepositoryView.Tree` and `RepositoryView.File` are not the only ways repository bytes reach a response. `RepositoryDiff.ForCommit` reads `repository.Objects` directly to diff blob pairs. The commit route probes `repository.Objects.ContainsKey(sha)`, which is an existence oracle over every object in the repository. `Commits` returns commit messages, `Branches` returns branch names, and the Git transport serves whole packs computed from reachability.

A path filter threaded through those call sites would be wrong in two ways. It would have to be remembered at every future call site, so the failure mode of forgetting it is a silent leak. And for the graph walks it cannot be written correctly at all: a pack is reachability-shaped, not path-shaped, so there is no filter that makes `git fetch` honour an allowlist without rewriting history into a second object graph.

Therefore a `Page` grant resolves named blobs and nothing else. Every surface that walks the object graph requires a `Repository` or `Member` grant and refuses a `Page` grant outright, rather than filtering it. Filtering a graph walk is exactly where a leak would hide, so the design does not attempt one.

Two consequences follow and both are deliberate.

**A page cannot offer clone.** "Only `README.md` is shared" and "`git clone` works" are incompatible, because the clone would carry every object reachable from the ref. The clone menu is therefore absent for a `Page` grant. Publishing source for anonymous cloning is what `public` visibility is for.

**A page cannot show history or a diff.** A commit diff over a non-allowlisted file would print that file's contents, and a commit list leaks paths and messages. Both are repository surfaces, not page surfaces.

Defence in depth: the existing routes assert the grant they require, so a route added later that forgets the page rules fails closed instead of serving.

## Decision 4: the config narrows, the database publishes

The config lives in the repository at `.graft/project.json`, read from the repository's default branch.

It is repository content, so anyone with push access authors it, and push access is `Developer` while publishing is a `Maintainer` concern. If the config could set the published flag, a `Developer` could publish a private project by pushing a commit. So the flag that makes anything public lives in the database and requires `Maintainer`. The config can only narrow an already-published surface and describe how it looks. A push can never widen access.

`.graft/` itself is never servable content, at any visibility.

JSON rather than TOML or YAML, because `System.Text.Json` is already present and source generation keeps it NativeAOT-safe, where either alternative would add a dependency to a project whose working rule is no new runtime dependencies.

Shape, with every field optional and the whole file optional:

```json
{
  "version": 1,
  "page": {
    "title": "Graft",
    "description": "A single-binary Git forge.",
    "image": "docs/logo.png",
    "readme": "README.md",
    "files": ["README.md", "docs/**/*.md", "compose.yaml"],
    "features": ["issues"],
    "site": { "root": "pages", "index": "index.html" }
  }
}
```

Rules the parser enforces:

- **Deny by default.** No `files` means no files. An empty list means no files. There is no "everything except" form, because a negation is a leak whenever a new path appears that nobody thought about.
- **Unknown fields and unknown feature names are errors**, not warnings. A config that claims to expose a feature which does not exist must fail loudly rather than appear to work. `issues` is not implemented yet, so naming it is currently an error.
- **An invalid config unpublishes the page.** A parse failure means no page, never a page with default settings. The owner sees why; an anonymous caller sees a 404.
- **A single bounded matcher**, used by every page decision, with `*` matching within a path segment and `**` across segments. No regular expressions, no backtracking, and it runs on the normalised path the object store already produced. Paths are compared whole; a match is never a prefix test.
- `image` and `readme` must themselves match `files`. A path named in one field and absent from the allowlist is a config error, not an implicit grant.

## Decision 5: a custom site is served only from a separate origin

A project page may be a static site, like GitHub Pages. That means serving repository-authored HTML, which the README work deliberately refuses to do.

The README pipeline disables raw HTML, passes output through an element and attribute allowlist, drops executable schemes, resolves images inside the repository or not at all, and pushes mermaid into a sandboxed frame with an opaque origin because `style-src 'self'` would otherwise have to be loosened on the one page showing somebody else's content. Serving arbitrary HTML from the same origin discards all of it at once: same-origin script reaches the session cookie's privileges, the CSRF token and every authenticated API route.

So a site is served **only from a separate configured pages origin**, a different hostname, and the feature is off until an operator configures one. The session cookie is `__Host-` prefixed and therefore cannot travel to that host. The pages origin serves no API, no session and no upload route, and carries its own restrictive policy.

Within the pages origin, only files under the configured `site.root` are reachable, through the same deny-by-default matcher, with content types taken from a fixed extension allowlist rather than from the file, and `X-Content-Type-Options: nosniff`.

The documented fallback for an operator with one hostname is a sandboxed frame with an opaque origin, as the mermaid sandbox already does. It is a worse product, because links and navigation inside the site break, so it is not the default and may not be implemented at all.

## Decision 6: the public browser is an operator switch, and concealment is unchanged

Anonymous listing and search cover projects that are `public`, or that have a published page. `unlisted` never appears; that is what distinguishes it from `public`.

The existing concealment convention holds without change: a project a caller may not see returns the same 404 as one that does not exist, and never appears in a listing or in the `X-Total` count. Search is bounded and rate-limited, following the existing login throttle, because an unauthenticated search endpoint is the cheapest oracle in the product.

Anonymous Git transport changes shape but keeps its current property. Today an anonymous caller gets a `401` Basic challenge before project existence is consulted, so an existing and a nonexistent repository answer identically. Public repositories now have to clone without credentials, so visibility is consulted first. A project that is absent, private or page-only still answers with the same challenge, so existence still does not leak.

## What this does not cover

Issues, releases and any other feature a page might expose do not exist. The page config can describe which features are shown, and today every feature name is rejected.

Anonymous clone links that expire, are single use, or are pinned to one ref are a separate idea and are not this. `unlisted` is a permanent property of a project, not a credential, and it is not a substitute for a link that dies.

Nested groups, GraphQL and the GitLab `/uploads/-/system/...` convention remain unclaimed, so the page and browser routes stay under Graft's own paths.
