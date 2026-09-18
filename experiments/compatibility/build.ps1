param([string]$Runtime = 'linux-x64')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $publishArgs = @('publish', '-c', 'Release', '-r', $Runtime, '--self-contained', 'true', '-o', "artifacts/$Runtime")
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath 'run-probe.sh', 'README.md' -Destination "artifacts/$Runtime"
} finally { Pop-Location }
