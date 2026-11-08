# 0045: A semi-public project: what a page may show beyond its files

- Date: 2026-11-08
- Status: Accepted, implemented
- Source: A request on 2026-11-08 for a way to make a repository semi-public, in levels: specific files public;
  every readme, docs or Markdown file public; specific folders public; the directory structure and file names
  visible with no content; the commit history visible, with fine-tuned options over whether contributor names
  and emails are shown; releases and binaries public; issues read-only with their status and comments. The
  stated purpose: a closed-source project with public documentation, bug reports and progress
- Refines: [ADR 0006](0006-project-pages-and-public-browsing.md), Decision 3, which said a page can never show
  history. It still cannot show a diff, a changed-file list or a branch; it may now show commit messages when
  the config asks. The reasoning there was about what a history view prints, and this record keeps that
  reasoning and narrows what "history" means on a page
- Review trigger: any new surface a page config might name; any change to what the store checks before
  reading an issue, a comment or a release; any proposal to show a page reader what a commit changed

## What is being decided

Three of the seven requested levels already existed. A page's `files` allowlist takes patterns, so one file,
every Markdown file (`**/*.md`) and a folder (`docs/**`) are the same mechanism with different patterns. This
record is about the other four, and about the one rule they all follow.

## Decision 1: the config says what the page shows, and each thing is off until it does

Four fields join `page` in `.graft/project.json`, beside `files`:

```json
{
  "version": 1,
  "page": {
    "files": ["README.md", "docs/**/*.md"],
    "tree": true,
    "history": { "commits": 100, "authors": "names" },
    "issues": true,
    "releases": true
  }
}
```

| Field | What it opens | Default |
| --- | --- | --- |
| `tree` | Every path on the default branch and its kind. Content only for what `files` admits. | off |
| `history` | The default branch's recent commits: message and date, and as much of the author as `authors` says. `commits` is 1 to 500, 100 if omitted. `authors` is `none`, `names` or `emails`, `none` if omitted. | off |
| `issues` | The project's issues, read only, with state, labels, milestone and comments. Never a confidential one. | off |
| `releases` | The project's releases: notes, links and attached files, downloadable. | off |

Each switch is a JSON boolean and nothing else. A string `"true"`, a `1` and a `"yes"` are errors, as every
other misspelling in this file is, because a config that is lenient about what means "show this" will one
day show it by accident. An error unpublishes the page, as before.

This is Decision 4 of ADR 0006 applied to four more things. The database publishes, with Maintainer; the
config narrows what the published page offers, with Developer. A push still cannot widen access, because
nothing in the config does anything until the flag in the database is set.

## Decision 2: the page's reach is a value the config produces and the store is handed

Reading an issue or a release is done by the store, and the store checks permission itself before reading.
Those checks know the database and nothing about a repository's config. Threading "but the page allowed it"
through them as a flag at each call site is the failure mode ADR 0006 named: forgotten at one site, it is a
silent refusal; added to the wrong site, it is a silent grant.

So the config's grants are one value, `PageReach`, with two booleans, and it goes to one place. The
authorization decision takes an optional reach; a page grant satisfies `ReadPage` always, `ReadCollaboration`
when the reach says issues, and the new `ReadReleases` action when it says releases, and nothing else whatever
the reach says. The store's read methods for issues, comments and releases take the same optional reach and
hand it to that decision. Every other store method, and every other route, passes none, so for them a page
grant reaches exactly what it did yesterday.

Only the four page routes ever hold a reach, because only they read the config. The property that follows is
the one worth having: a store method that was never told about pages fails closed, and a new surface stays
closed to pages until somebody adds it to `PageReach.Grants` and reads this record.

`ReadReleases` is a new action rather than a reuse of `BrowseRepository`, which is what reading a release
used to require. A page grant must never satisfy `BrowseRepository`, and a release's files live in the
database beside the repository rather than in it, so reading them walks no object graph. Members, tokens
and public visibility are granted `ReadReleases` wherever they were granted `BrowseRepository`, so nothing
changes for anybody but a page reader.

## Decision 3: the page keeps its own routes

Issues and releases on a page are served at `/api/graft/pages/<slug>/issues`, `.../issues/<iid>`,
`.../releases` and `.../releases/<tag>/assets/files/<name>`, not by letting a page reader into
`/api/v4/projects/<id>/issues`. The project's own API still answers a stranger with a 404 for a page-only
project, and the acceptance suite checks that it does.

Three reasons. The page is a separate read path by ADR 0006's Decision 3, and these are reads of the same
kind. The GitLab-shaped routes carry things a page did not open: a release's `commit` and its source
archives, an issue's `web_url` into a project view the reader cannot open. And the browser's project pages
load the project itself first, which a page reader may not, so a link there would land on "Unavailable". The
page renders the issues and releases itself, read only, in the shape the page already has.

The page's release shape has no commit and no source archive. A commit is history the config may not have
opened, and an archive is the repository. Its issue shape is the ordinary one with the comments beside it,
since a reader who cannot comment has no reason to fetch them separately.

## Decision 4: history on a page is what was said, never what changed

ADR 0006 refused history for two reasons: a commit list leaks paths and messages, and a diff prints the
contents of files the page never named. The second reason stands entirely. The first is now a choice the
project makes by writing `history`, with the paths left out: a page's commit carries its id, title, message
and date, and no parent ids, no changed files, no diff and no link to one. The commit route still refuses a
page grant, as does every other surface that walks the object graph.

Authors are a separate disclosure from messages, which is why `authors` exists and defaults to `none`.
Showing that a commit happened and showing who made it are different things to publish about a person, and
an email address is a third. The committer is never shown at any setting, because the setting names authors
and a committer is usually either the same person or a tool.

## Decision 5: the structure names everything and opens nothing

`tree` lists every path on the default branch, directories and files, with `.graft/` left out as it is from
every page surface. Each file says whether the page will open it, decided by the same `files` allowlist that
decides it at the file route, so a reader is never offered a file the page then refuses. The listing does not
widen the allowlist. It is deliberately the request's fourth level, "directory structure and filenames are
visible but content is not", and a project that wants content opens it with `files`.

## What this does not do

- **No writing.** Nobody comments or opens an issue through a page. ADR 0014 refuses anonymous writing until
  there is moderation, and a page reader is anonymous.
- **No merge requests, boards, milestones, wiki or snippets.** A merge request is a diff. The rest were not
  asked for and each would need the same thinking about what it prints. `features` still names them as links
  for a reader the project itself admits, as it did.
- **No page-granted reading over the GitLab API.** LabCoat and Comeet read a project's issues through
  `/api/v4`, and a page-only project is still a 404 there. A client wanting a page's issues reads the page's
  route.
- **No history on anything but the default branch**, and no more than 500 commits of it.
- **No authors on releases beyond the forge username** of whoever published it, which the ordinary release
  shape shows too.
