# Instructions for agents working on Graft

Read README.md, docs/handoff.md, docs/requirements.md, docs/architecture.md, docs/build-and-test.md, docs/validation/2026-09-18-nativeaot.md and the decision records before changing code. Inspect Git status first; preserve unrelated work. Graft is a working forge with a live deployment and published releases; the early probes are kept under `experiments/`.

## Confirmed project constraints

- Name: Graft. Compatibility target: **GitLab**, not GitHub. Initial GitHub wording was explicitly corrected.
- Use C#/.NET 10 with top-level statements. Data access uses Dapper.AOT; keep the source generator enabled and confirm interceptions rather than assuming package installation is enough.
- Carry the verified static-musl NativeAOT configuration forward for the required Linux x64 host. Full Graft dependency/UI compatibility still requires validation. Do not silently revert to CoreCLR, require a kernel upgrade, replace system libc or make Docker mandatory to bypass a failure.
- The Linux 3.10.108 x86_64 host is mandatory. The six probe checks passed there on 2026-09-18. That is not proof of full application, service packaging, Git, runner or upgrade support.
- The browser UI is a standalone Blazor WebAssembly client embedded in the NativeAOT host (ADR 0004, MVP-02); server-side Blazor is not used. Ask only for a material product/architecture choice that cannot be resolved within existing requirements.
- Most GitLab functionality and all API/URL operations are long-term goals. Track actual conformance by version/client/workflow. Unsupported behavior must be explicit; never claim parity from a handful of endpoints.
- GitLab import, understandable permissions, simple upgrades, low resource use and simpler runner networking are central. Graft's own runner talks to the forge over CupriNet (ADR 0037).

## Shared working defaults

These summarize the relevant confirmed PLAN preferences as of 2026-09-18. If the sibling PLAN checkout is present, also read its AGENTS.md, README.md, preferences/development.md, preferences/planning.md and relevant knowledge before substantial work. Current explicit user instructions take precedence. Do not promote assumptions to preferences.

- Windows is the primary development platform; Linux is secondary and the legacy target is a required acceptance environment. Treat .NET 10 support as the general OS baseline; report actual tested coverage rather than implying every platform works.
- Use installed Windows PowerShell 5.1 for orchestration. POSIX launchers inside Linux are permitted for the existing probe. Do not introduce Python, Node.js, Tailwind or transitive tooling requiring them without explicit permission. Inspect exposed MCP tools and local MCPHub before proposing Python/Node or additional integrations.
- For browser work, vendor CSS/JS/fonts/icons with pinned versions and license/provenance. Avoid `tabindex="-1"` on non-input elements unless a documented accessibility requirement needs it.
- Keep VS Code build/debug tasks aligned with working commands.
- Persistence is SQLite with DnaX migrations (restored from the GitHub Packages feed, ADR 0044). Applications own schemas, SQL and recovery policy.
- Planned services should support interactive and Windows Service modes on Windows, interactive/systemd on applicable Linux systems, and an optional Docker deployment. Verify each; a successful probe does not verify these modes. The NAS may need its own service integration.
- Validate in proportion to risk. For NativeAOT/trimming changes preserve the baseline and exercise real published functionality; do not suppress linker warnings to manufacture success. No need to retest an unchanged artifact repeatedly.
- Consult existing tools and MCPHub before looking for a new MCP server. Installing/enabling/connecting/configuring an external integration requires explicit approval. If MCPHub is unavailable for required work, report what was checked and ask how to proceed.
- The repository's remote is GitHub (Wixely/Graft), and releases are built from `v*` tags (ADR 0044). Push, tag or publish only when the owner asks. The licence is still undecided; do not choose one. GitHub is for intentional open source; private work belongs on the local GitLab. New private GitLab projects must grant the `agents` group Developer access, verified after setup.
- Never commit secrets, raw host reports, production databases, private infrastructure addresses or machine-specific build paths. Before a push, review every outgoing commit/object and metadata, including binaries, under PLAN's security-and-privacy rules. Apply the Wixely identity rule if GitHub is selected. No remote credentials are included here.
- Record decisions with ISO dates, sources, status and review triggers; supersede rather than erase history. Keep requirements, proposals, facts and unknowns distinct. End handoffs with remaining work and a concrete next action with an owner.

## Scope for the next agent

Start from the newest entry in docs/handoff.md and from docs/backlog.md. The Git engine (ADR 0028), the UI (standalone Blazor WebAssembly) and the runner transport (ADR 0037) are decided. Use local reversible experiments to resolve engineering questions. Nothing here authorizes changing the live deployment, migrating production data, rewriting existing repositories, opening network access or publishing; each needs the owner's request.
