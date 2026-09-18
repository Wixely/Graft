# Product-line integration candidates

Recorded: 2026-09-18 from existing PLAN product records. Owner: Next agent. Review package/source maturity and licensing before adoption; these are fit proposals, not installed dependencies.

| Product | Intended fit | Decision boundary |
| --- | --- | --- |
| CupriNet / CupriMark | Runner connectivity and capability negotiation | User explicitly suggested evaluation. Verify traversal/relay behavior, authorization, resource use and NativeAOT. Keep optional transports optional. |
| DnaX | Host paths, diagnostics, migration manifests, uploads, optional admin/MCP support | Evaluate migrations first per shared preference. Confirm AOT; no randomized replacement of GitLab-compatible routes. |
| GitlabMCPSharp / MCPHub | Existing compatibility client and task-oriented automation | Verify configurable endpoints and actual workflow fidelity. Do not configure connectors without approval. |
| CoLibra | Worker leases/fencing/failover | Introduce only when distributed coordination is justified; avoid overlapping transport responsibilities. |
| RepoDetox | Optional import assessment and privacy/history tools | Never silently rewrite imported Git history. |
| Banter / DaggerAgent | Notifications, repository rooms, opt-in agent workflows | Later scoped API/webhook integration; core forge remains independent. |
| Lantern | Repository/build/runner status dashboard | Later integration; currently an idea in PLAN. |

Private PLAN projects are not a source of code to copy into a future public repository. Integration evaluation does not authorize moving code across the source-hosting boundary.

Canonical ecosystem context, when PLAN is present: [Cupri](../../PLAN/projects/github/cupri/README.md), [DnaX](../../PLAN/projects/github/dnax/README.md), [MCPSharp](../../PLAN/projects/github/mcpsharp/README.md), [CoLibra](../../PLAN/projects/github/colibra/README.md), [RepoDetox](../../PLAN/projects/github/repo-detox/README.md), [Banter](../../PLAN/projects/github/banter/README.md), [Lantern](../../PLAN/inbox/lantern.md).
