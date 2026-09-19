param(
    [string]$DnaXCheckout = (Join-Path $PSScriptRoot '..\..\..\DNAX')
)
$ErrorActionPreference = 'Stop'
$expectedCommit = '73826d731cef903083f2a4ae15ba6ac284e83ce3'
$checkout = (Resolve-Path -LiteralPath $DnaXCheckout).Path
$actualCommit = (& git -C $checkout rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $expectedCommit) {
    throw 'DnaX checkout must be at public tag v10.0.0-alpha.7 (commit 73826d731cef903083f2a4ae15ba6ac284e83ce3).'
}
$changes = & git -C $checkout status --porcelain --untracked-files=no
if ($LASTEXITCODE -ne 0 -or $changes) { throw 'DnaX tracked files must be clean before packaging.' }

$feed = Join-Path $PSScriptRoot '..\..\artifacts\storage\dnax-feed'
New-Item -ItemType Directory -Force -Path $feed | Out-Null
foreach ($project in @(
    'src\DnaX.Data.Migrations\DnaX.Data.Migrations.csproj',
    'src\DnaX.Data.Migrations.Sqlite\DnaX.Data.Migrations.Sqlite.csproj'
)) {
    & dotnet pack (Join-Path $checkout $project) -c Release -o $feed
    if ($LASTEXITCODE -ne 0) { throw ('DnaX package build failed: ' + $project) }
}
foreach ($package in @(
    'DnaX.Data.Migrations.10.0.0-alpha.7.nupkg',
    'DnaX.Data.Migrations.Sqlite.10.0.0-alpha.7.nupkg'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $feed $package))) { throw ('Expected package missing: ' + $package) }
}
Write-Output ('Prepared exact tagged DnaX packages in ' + (Resolve-Path -LiteralPath $feed).Path)
