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
