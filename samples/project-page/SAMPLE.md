# A project page and its site

A working example of Graft's project page: a curated surface over a repository that stays private, plus a
static site served from a hostname of its own.

Push this folder's contents to a project, publish the page, and the result is the two things below.

## What is here

```
.graft/project.json   the config: what the page offers and where the site lives
README.md             named by the config, rendered on the page
docs/guide.md         matched by the config's allowlist
compose.yaml          matched by the config's allowlist
pages/                the site: index.html, about.html, style.css, lantern.svg
```

The config is the whole feature:

```json
{
  "version": 1,
  "page": {
    "title": "Lanternry",
    "description": "A small library for hanging lights on things.",
    "readme": "README.md",
    "files": ["README.md", "docs/**/*.md", "compose.yaml"],
    "site": { "root": "pages", "index": "index.html" }
  }
}
```

**Nothing outside `files` is reachable on the page, and nothing outside `pages/` is reachable on the site.**
Add a `src/secret.cs` to this repository and neither surface will serve it, because a page is deny by
default: no `files` means no files, and there is no "everything except" form.

## Running it

The site needs a second hostname. Nothing is served until an operator configures one, because repository
HTML cannot share an origin with the session cookie and the API.

```powershell
dotnet src\Graft.Server\bin\Debug\net10.0\Graft.Server.dll `
  --database <path> --repositories <dir> `
  --public-browsing true `
  --pages-origin https://pages.example.test
```

Then, as the project's owner:

1. Push this folder to the project.
2. Publish the page: the project's **Settings** tab, **Publish a project page**.

The page is then at `/-/p/<project-slug>` on the forge, and the site at
`https://pages.example.test/<project-slug>/`. The slug is the project's instance-wide name, so neither
address contains an account name.

For a local try without touching DNS, send the Host header yourself:

```sh
curl -k -H "Host: pages.example.test" https://127.0.0.1:7443/lanternry/
```

![The page](../../docs/assets/project-page.png)

![The site](../../docs/assets/project-page-site.png)

## What to look at

- The page lists exactly three files, and the repository's other files are absent rather than hidden.
- `README.md` is rendered because the config named it. A project's readme is **not** shown by default.
- The site's own links and stylesheet work, because the configured index answers for a directory.
- The site links back to the project with `<a href="-/repo">`, which names no hostname. Graft resolves it to
  wherever the forge is, so moving the forge to a new domain does not make the site wrong.
- On the pages host, `/api/session`, `/healthz` and every other application route answer 404. That host
  carries the site and nothing else.
