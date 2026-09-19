# MVP preparation evidence

Recorded: 2026-09-18. Review: 2026-09-25 or on dependency/architecture changes.
Owner: Implementation agent. Scope: repository inspection, Windows baseline and source/documentation research; no application feasibility or legacy-host rerun claimed.

## Verified locally

- Working tree was clean before preparation. Only planning documents are changed.
- `dotnet build Graft.slnx --no-restore`: exit 0, zero warnings/errors.
- `dotnet run --project experiments/compatibility/Graft.Compatibility.csproj --no-build`: exit 0, all six checks passed on Windows x64, runtime .NET 10.0.8, dynamic code supported.
- Inspected the generated Dapper interceptor source under the probe's ignored `obj` directory: generated execute/scalar/query methods and interception locations are present. This remains probe evidence only.
- Installed test-client candidate: Git 2.53.0.windows.2. No request to a Graft Git server has been tested.
- `dotnet workload list` does not list `wasm-tools`. No workload, browser service or integration was installed/configured.
- Existing static-musl artifact and source were not modified or re-published. [Earlier legacy-host acceptance](2026-09-18-nativeaot.md) retains its original scope.

## DnaX assessment

Read the clean sibling DnaX checkout at commit `73826d731cef903083f2a4ae15ba6ac284e83ce3`, whose build properties declare `10.0.0-alpha.7`. This is a source snapshot, not confirmation of package-feed availability. Reviewed `docs/database-migrations.md`, migration/SQLite project files, manifest/builder/migration source and SQLite transaction acquisition. Public project: [DnaX](https://github.com/Wixely/DnaX).

The manifest is an explicit ordered collection. SQL migrations use `DbCommand`; they do not depend on Dapper runtime code generation. The SQLite adapter references `Microsoft.Data.Sqlite` and acquires a non-deferred transaction. The documentation's broad statement that adapters have no driver dependency is inconsistent with this SQLite project/source; use the actual dependency graph when locking packages. Named embedded resources are optional; explicit C# SQL literals are the smallest first experiment.

Assessment: good fit for a bounded AOT trial; prefer it over a new migration runner if the trial passes. No source-level need to dynamically discover migration types was found in the reviewed paths. That is not an AOT or trimming guarantee. Graft still owns schema, backup policy and filesystem consistency. Verify the complete transitive package graph, native SQLite linkage and migration recovery in MVP-03. MIT is declared in the reviewed project metadata; preserve package notices.

## Git backend assessment

| Candidate | Source evidence | Recommendation / unresolved evidence |
| --- | --- | --- |
| Git executable | Official [HTTP protocol](https://git-scm.com/docs/http-protocol) and [pack format](https://git-scm.com/docs/pack-format) describe interoperability target | Keep as independent test oracle only under strict C# interpretation. Bundling creates native binary, licensing/update and old-kernel validation work. No footprint/host result measured. |
| LibGit2Sharp | [Project README](https://github.com/libgit2/libgit2sharp) identifies native libgit2 underneath its .NET API and MIT for the wrapper | A C# API is not a managed Git engine. Separate libgit2 notices, static linking, transport server behavior and target compatibility would need review. Not selected. |
| NGit | [Upstream README](https://github.com/mono/ngit) describes a generated C# JGit port with repository/transport support and SharpZipLib, Mono.Security and optional Mono.Posix dependencies | Candidate only. README targets an older build ecosystem. License audit of every component, dependency modernization, pack/protocol tests, footprint and .NET 10 NativeAOT are unresolved; do not assume install-and-use suitability. No source copied. |
| New managed subset | Official Git wire/storage specifications are available | Highest ownership cost; restrict to bare repository + HTTPS workflows and prove them first. Fuzz/malformed-input, locking and crash recovery gates precede exposure. No performance/size claim yet. |

No examined candidate has demonstrated the combination of managed Git, this MVP's interoperability and the required legacy-host NativeAOT deployment. The architecture therefore has a named feasibility gate instead of an unverified dependency selection.

## UI and tooling assessment

Microsoft's [NativeAOT support matrix](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0) excludes Blazor Server and lists static files as supported. This supports investigating a static standalone client; it does not prove embedding, auth, routing or every middleware choice. The table also limits authentication support, so browser authentication must be independently published and exercised.

Microsoft documents [WebAssembly AOT](https://learn.microsoft.com/en-us/aspnet/core/blazor/webassembly-build-tools-and-aot?view=aspnetcore-10.0) separately. Do not equate it with server NativeAOT. Begin without browser AOT/native relinking; audit actual SDK build dependencies before introducing them. If they require Python/Node, the user's tooling restriction applies even when invoked transitively.

## Remaining verification

All MVP-01 through MVP-08 gates in [the plan](../mvp-plan.md) remain unexecuted. No UI, Git server, application auth, migration integration, service or restore test is claimed. Recommended next action: Implementation agent performs managed Git feasibility, records actual package/source/license selection and published-binary interoperability before implementing broader functionality.
