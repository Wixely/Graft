param(
    [string]$DnaXCheckout = (Join-Path $PSScriptRoot '..\..\..\DNAX')
)
$ErrorActionPreference = 'Stop'
$expectedCommit = '73826d731cef903083f2a4ae15ba6ac284e83ce3'
$checkout = (Resolve-Path -LiteralPath $DnaXCheckout).Path

# Extract the exact tagged tree into a temporary directory instead of requiring the developer's checkout to
# be parked on that commit. The source is read-only: the sibling working tree and branch are never touched.
& git -C $checkout cat-file -e ($expectedCommit + '^{commit}') 2>$null
if ($LASTEXITCODE -ne 0) {
    throw ('DnaX checkout does not contain the pinned commit ' + $expectedCommit + ' (public tag v10.0.0-alpha.7). Fetch it first.')
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ('dnax-' + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
try {
    $archive = Join-Path $staging 'source.tar'
    & git -C $checkout archive --format=tar --output=$archive $expectedCommit
    if ($LASTEXITCODE -ne 0) { throw 'Could not export the pinned DnaX commit.' }
    & tar -x -f $archive -C $staging
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the exported DnaX source.' }
    Remove-Item -LiteralPath $archive -Force

    $feed = Join-Path $PSScriptRoot '..\..\artifacts\storage\dnax-feed'
    New-Item -ItemType Directory -Force -Path $feed | Out-Null
    foreach ($project in @(
        'src\DnaX.Data.Migrations\DnaX.Data.Migrations.csproj',
        'src\DnaX.Data.Migrations.Sqlite\DnaX.Data.Migrations.Sqlite.csproj',
        'src\DnaX.Uploads\DnaX.Uploads.csproj'
    )) {
        & dotnet pack (Join-Path $staging $project) -c Release -o $feed
        if ($LASTEXITCODE -ne 0) { throw ('DnaX package build failed: ' + $project) }
    }
    foreach ($package in @(
        'DnaX.Data.Migrations.10.0.0-alpha.7.nupkg',
        'DnaX.Data.Migrations.Sqlite.10.0.0-alpha.7.nupkg',
        'DnaX.Uploads.10.0.0-alpha.7.nupkg'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $feed $package))) { throw ('Expected package missing: ' + $package) }
    }

    # Pack output is not byte-identical across SDK patch versions, so the lock files' content hashes only
    # identify packages built by one SDK. Report the SDK that produced these so a later hash change can be
    # attributed instead of investigated. See docs/validation/2026-09-19-storage-migrations.md.
    $sdk = (& dotnet --version).Trim()
    Write-Output ('Prepared exact tagged DnaX packages in ' + (Resolve-Path -LiteralPath $feed).Path)
    Write-Output ('Source commit ' + $expectedCommit + '; packed with .NET SDK ' + $sdk)
    Write-Output 'If restore now fails with NU1403, the lock files were written by a different SDK; see the storage-migration evidence before changing them.'
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}
