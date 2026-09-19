# Operator administrator-bootstrap evidence

Date: 2026-09-19. Owner: Implementation agent. Review when account bootstrap, password hashing, maintenance locking, storage migrations or administration packaging changes.

## Result

`Graft.Admin bootstrap` provides the first deployable account-creation path. It reads a 16-256 character password from a bounded UTF-8 file, creates the first administrator through `GraftStore`, and prints only the created username. It never accepts a password on the command line.

The current operations acceptance executable passes **41 assertions** with the managed administration DLL and again with the self-contained `win-x64` NativeAOT administration executable. The added checks prove:

- a running server's database/repository maintenance leases block bootstrap;
- bootstrap succeeds after the server stops;
- the resulting account can log in and resolves as an administrator;
- the raw password is absent from password, session and token storage;
- a second bootstrap against the populated user database is refused.

The Windows NativeAOT publication completed without build, trim or AOT warnings.

## Verification

```powershell
dotnet build tests\Graft.Operations.Acceptance\Graft.Operations.Acceptance.csproj -c Debug
dotnet build src\Graft.Admin\Graft.Admin.csproj -c Debug
dotnet tests\Graft.Operations.Acceptance\bin\Debug\net10.0\Graft.Operations.Acceptance.dll `
  src\Graft.Server\bin\Debug\net10.0\Graft.Server.dll `
  src\Graft.Admin\bin\Debug\net10.0\Graft.Admin.dll

dotnet publish src\Graft.Admin\Graft.Admin.csproj -c Release -r win-x64 --self-contained true `
  -p:GraftAdminAot=true -o artifacts\bootstrap-admin-win-x64
dotnet tests\Graft.Operations.Acceptance\bin\Debug\net10.0\Graft.Operations.Acceptance.dll `
  src\Graft.Server\bin\Debug\net10.0\Graft.Server.dll `
  artifacts\bootstrap-admin-win-x64\Graft.Admin.exe
```

This run does not revalidate the changed administration binary under static-musl NativeAOT or Linux 3.10.108. It also does not expose creation of later users: administrator user creation and owner-managed project memberships remain core/storage operations used by acceptance fixtures rather than product HTTP/UI workflows.

Recommended next action: Owner: Implementation agent — expose administrator user creation and project-owner membership grant/change/revoke through the authenticated server and Blazor UI.
