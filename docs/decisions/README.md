# Decisions

Preserve this history; supersede a decision explicitly instead of silently changing it. Recorded from user instructions on 2026-09-18.

- [0001: Name and GitLab compatibility](0001-name-and-compatibility.md)
- [0002: Dapper.AOT and NativeAOT feasibility](0002-nativeaot-direction.md)
- [0003: Local repository and handoff scope](0003-local-repository.md)
- [0004: Fully C# MVP architecture proposal](0004-csharp-mvp-proposal.md) — proposed; feasibility and material architecture review remain
- [0005: Browser upload transport](0005-browser-upload-transport.md) — accepted and implemented; DnaX for the browser, plain HTTP for API clients
- [0006: Project pages and public browsing](0006-project-pages-and-public-browsing.md) — accepted and implemented
- [0007: Creating a project by pushing to it](0007-push-to-create.md) — accepted and implemented
- [0008: The Model Context Protocol layer](0008-mcp-layer.md) — accepted and implemented; partly superseded by 0009
- [0009: The Model Context Protocol endpoint inside the forge](0009-mcp-in-the-forge.md) — accepted and implemented; gated by the operator, switched by an administrator
- [0010: Instance-wide project slugs](0010-project-slugs.md) — accepted and implemented; a second address for every project, with no owner in the URL
- [0011: Bringing agent access to parity with the API](0011-agent-parity.md) — accepted and implemented; six capabilities opened to tokens, four deliberately withheld
- [0012: Recoverable token secrets](0012-recoverable-tokens.md) — accepted and implemented; off by default, and what it costs when it is on
- [0013: Deleting a project, and reading the audit trail](0013-project-deletion-and-audit.md) — accepted and implemented; the owner or an administrator, irreversible, and withheld from agents
- [0014: Issues, merge requests, and a merge that says what it cannot do](0014-collaboration.md) — accepted and implemented; reading follows the repository, writing needs membership, and conflicts are named rather than guessed at
- [0015: Closing the review journey](0015-review-journey.md) — accepted and implemented; the diff on the page, line comments that age rather than move, approvals superseded by a push, and branches managed without a Git client
- [0016: Webhooks](0016-webhooks.md) — accepted and implemented; off by default, signed over the body, and a notification rather than a guarantee
- [0017: Getting things out, without cloning](0017-getting-things-out.md) — accepted and implemented; raw files and archives always as an attachment of unknown type, and tags listed with both what they point at and what that resolves to

Open architecture choices are in [architecture.md](../architecture.md); they are not accepted decisions.

- [0018: Four GiB repositories](0018-repository-capacity.md) — implemented; disk-backed objects, pack transfers and archives replace the feasibility size budgets
- [0019: A comment becomes a conversation](0019-conversation.md) — accepted and implemented; threads one level deep, resolving needs the standing to push or to have started the thread, edits recorded, a first comment with replies kept
- [0020: Planning, and the rule that a Reporter files while a Developer organises](0020-planning.md) — accepted and implemented; labels as a project's own vocabulary, milestones that keep their work when deleted, boards with no state of their own, confidential issues for members and the author, drafts that do not merge
- [0021: Squash, rebase and code owners](0021-review-options.md) — accepted and implemented; a squash is a merge that forgets, a rebase is a forced push that replays each commit and refuses by name, code owners come from the target branch and may be required
- [0022: Releases](0022-releases.md) — accepted and implemented; a release sits on a tag and outlives it, reading follows the repository and publishing needs a push, asset bytes live in the database bounded at 32 MiB and are served as downloads
- [0023: A wiki with history, snippets, and time tracking](0023-wiki-snippets-time.md) — accepted and implemented; wiki pages in the database with every save kept and slugs that never change, snippets as comments with a file, time as an estimate and an attributed ledger
- [0024: Search without an index](0024-search.md) — accepted and implemented; a search reads what is there, bounded, and a global search sees what the caller sees
- [0025: Forks, and a proposal from outside](0025-forks.md) — accepted and implemented; a fork is a clone with an address, a proposal from a fork is mirrored into the original rather than fetched, and its author may talk about it and nothing else
- [0026: Large files](0026-large-files.md) — accepted and implemented; LFS objects content-addressed beside the repository, hashed on the way in, under the Git transport's credential and permissions
- [0027: Package feeds and a container registry](0027-packages-and-registry.md) — accepted and implemented; generic and NuGet packages and a Docker v2 registry, bytes beside the repository, Basic authentication with a token, reading follows the repository and publishing needs a push
- [0028: The Git engine keeps repositories the way Git does](0028-git-engine.md) — accepted and implemented; a bare Git repository on disk with the old layout converted on first open, packs read through their index and written with deltas, housekeeping that prunes nothing, negotiation that sends what the client lacks, protocol version 2, and SHA-1 only by decision
- [0029: Deleting a file is a commit](0029-file-deletion.md) — accepted and implemented; a deletion is authored like a write, refuses to find nothing, and takes an emptied directory with it
- [0030: A token is rotated in place](0030-token-rotation.md) — accepted and implemented; the row keeps its id, name, scopes and expiry and gets a new secret, the browser collects it through the one-time claim, and the GitLab-shaped route answers it in the body
- [0031: Git over SSH, served by the forge itself](0031-git-over-ssh.md) — accepted and implemented; a dependency-free SSH server in the binary with a small modern algorithm set, keys registered to accounts through GitLab's routes, fetches in protocol version 2 only, off until `--ssh-port` names a port
