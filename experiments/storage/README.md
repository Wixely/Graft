# DnaX migration and Dapper.AOT feasibility

MVP-03 is an isolated SQLite experiment. It uses an application-owned two-version schema, `DnaX.Data.Migrations.Sqlite` 10.0.0-alpha.7 for ordered checksummed migrations, and Dapper.AOT for application queries. It is not the production Graft schema.

## Run and publish

From the repository root with .NET 10:

```powershell
.\experiments\storage\prepare-dnax-packages.ps1 -DnaXCheckout <clean-v10.0.0-alpha.7-checkout>
dotnet build experiments/storage/Graft.StorageProbe.csproj -c Debug
dotnet experiments/storage/bin/Debug/net10.0/Graft.StorageProbe.dll
dotnet publish experiments/storage/Graft.StorageProbe.csproj -c Release -r win-x64 --self-contained true -p:StorageProbeAot=true -o artifacts/storage/win-x64
.\artifacts\storage\win-x64\Graft.StorageProbe.exe
```

VS Code provides **Debug storage migration probe**. The executable creates unique temporary databases, reports PASS/FAIL, clears SQLite pools and removes only its own temporary directory.

The six checks cover fresh migration, an idempotent second run, restart, v1-to-v2 data preservation, checksum drift refusal, future-version refusal, bounded write-lock timeout, atomic rollback and successful restart after a failed chain. Dapper.AOT generated interceptors for all eight application query call sites in both Debug and published Release builds.

Static-musl publishing uses the same isolated Alpine technique as the compatibility probe:

```powershell
.\experiments\storage\build-static-native.ps1 -Distribution <existing-wsl-distribution>
```

The script creates a dedicated root filesystem, verifies the Alpine and SDK downloads, builds a static `linux-musl-x64` executable, rejects a dynamic loader or shared-library dependency, and runs the probe. It does not modify the WSL distribution's installed packages. The retained build root is outside this repository; generated binaries are ignored.

## Package availability boundary

The selected DnaX packages are built from public tag `v10.0.0-alpha.7` (commit `73826d731cef903083f2a4ae15ba6ac284e83ce3`) and locked by `packages.lock.json`. This tag includes platform-independent migration checksum normalization added after alpha.3. On 2026-09-19 the packages were not available from NuGet.org, and the corresponding GitHub release exposed no package assets. `prepare-dnax-packages.ps1` refuses any other or modified checkout and packs the tag into the ignored local feed at `artifacts/storage/dnax-feed`; normal and static restores consume those exact packages.

This proves the package implementation works in the tested environments, but a clean checkout cannot restore it. Do not adopt DnaX in `src/` until the exact tested packages are published to an approved durable feed or an explicit vendoring/source-build policy is selected. The delivered application must not depend on the sibling DnaX checkout.

The DnaX packages are MIT licensed. The application continues to own SQL, migration order, data-preservation fixtures, backups and recovery policy. DnaX does not replace Dapper and its migration adapters use provider commands directly.

See [recorded evidence](../../docs/validation/2026-09-19-storage-migrations.md). Required-host execution, backup/restore, crash/power-loss durability and a production schema remain open.
