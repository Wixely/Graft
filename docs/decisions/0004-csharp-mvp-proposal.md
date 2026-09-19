# 0004: Fully C# MVP architecture proposal

- Date: 2026-09-18
- Status: Proposed architecture; fully C# application direction confirmed by user
- Source: Current MVP preparation request and the linked investigation below
- Owner: Implementation agent; user owns material scope/dependency choices
- Review: 2026-09-25, before implementation, or upon a failed feasibility gate

## Recommendation

Prepare a private repository-hosting MVP in C#/.NET 10: NativeAOT Minimal API server, standalone Blazor WebAssembly UI, SQLite/Dapper.AOT, explicit DnaX migration manifests if published tests pass, and a managed C# Git engine subject to feasibility. [The MVP plan](../mvp-plan.md) defines the workflow, exclusions and acceptance gates.

Blazor Server is not compatible with the required NativeAOT route according to Microsoft's [.NET 10 support table](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0). A standalone WebAssembly UI executes components in the browser and can be delivered as static assets ([hosting model](https://learn.microsoft.com/en-us/aspnet/core/blazor/hosting-models?view=aspnetcore-10.0)). This preserves C# UI logic without running a Blazor server component under AOT. The API host serves bytes only; published behavior still needs testing.

This recommendation requires an explicit exception to the inherited rule that a server hosting a Blazor client should use the normal .NET runtime: that API/static-file host must use NativeAOT to retain the required legacy-host path. No server-side Blazor rendering is proposed. Do not mark this architecture accepted merely because the framework documentation makes it plausible.

## Alternatives and consequences

| Option | Assessment |
| --- | --- |
| Blazor Server/CoreCLR | Familiar C# UI, but conflicts with the demonstrated legacy-host execution requirement; not recommended |
| Standalone Blazor WASM + AOT API | Recommended for local feasibility; browser payload, auth integration and single-file asset embedding need proof |
| JavaScript application framework | Conflicts with current C# direction and tooling defaults; not proposed |
| Native Git executable or LibGit2Sharp | Mature implementation candidates but fail the strict managed Git interpretation; no adoption without resolving that boundary |
| Existing managed Git library | Evaluate NGit as a candidate, including its old dependencies, protocol behavior and licenses; no .NET 10/AOT compatibility assumed |
| Project-owned managed Git subset | Preserves language boundary but has substantial correctness/security cost; authorize expansion only after bounded interoperability evidence |

Keep the server single-process and avoid distributed components. Embed the published UI through a build-time manifest/resource mapping if tests demonstrate correct content types, compression, cache invalidation and deep links. Loose static files are acceptable for the experiment; shipping them is a documented packaging exception, not a fulfilled single-executable goal.

Use the normal browser IL execution path initially. WebAssembly AOT and native relinking need additional tools and may introduce restricted transitive tooling; audit before enabling. No workload was installed during preparation.

## Decision boundaries

This proposal refines the open questions in ADR 0002; it does not replace the accepted probe evidence or silently override its constraints. "Fully C#" is treated as application and Git implementation language, with native platform dependencies disclosed. The exact Git engine, UI packaging, authentication implementation and DnaX package version remain gated by [investigation evidence](../validation/2026-09-18-mvp-preparation.md).

MVP-01 update on 2026-09-18: the user authorized the recommended experiment. A project-owned managed C# subset passed bounded Windows JIT/NativeAOT interoperability and malformed-input checks; see [evidence](../validation/2026-09-18-managed-git.md). This establishes local feasibility, not a production Git backend decision or acceptance of the UI hosting exception.

MVP-02 update on 2026-09-18: user authorized the next experiment. The standalone Blazor client/NativeAOT API boundary passes Windows browser and HTTP tests with assets embedded in one executable and no browser-native toolchain installation. See [evidence](../validation/2026-09-18-ui-nativeaot.md). The experiment is explicitly authorized; production architecture adoption and required-host acceptance are still separate decisions/gates. The tested custom session design does not establish built-in Identity/CookieAuthentication support.

MVP-03 update on 2026-09-19: DnaX migrations and Dapper.AOT pass Windows JIT/NativeAOT and local static-musl NativeAOT checks; see [evidence](../validation/2026-09-19-storage-migrations.md). Technical compatibility supports the proposed storage boundary. Adoption remains held until the exact package has an approved reproducible source and the new static binary passes required-host acceptance.

Recommended next action: User/DnaX maintainer selects a durable package source; Implementation agent verifies clean restore and starts MVP-04 accounts/projects/membership.
