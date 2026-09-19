[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Distribution,

    [string]$GlabImage = 'registry.gitlab.com/gitlab-org/cli:v1.58.0',

    [switch]$PullImage
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$serverOutput = Join-Path $repositoryRoot 'artifacts\glab-acceptance\server'
$harnessOutput = Join-Path $repositoryRoot 'artifacts\glab-acceptance\harness'

function ConvertTo-WslPath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if ($resolved -notmatch '^([A-Za-z]):\\(.*)$') {
        throw "Expected a drive-qualified Windows path: $resolved"
    }
    return '/mnt/' + $Matches[1].ToLowerInvariant() + '/' + $Matches[2].Replace('\', '/')
}

Push-Location $repositoryRoot
try {
    & wsl.exe -d $Distribution -- podman image exists $GlabImage
    if ($LASTEXITCODE -ne 0) {
        if (-not $PullImage) {
            throw "Pinned glab image is absent. Re-run with -PullImage after approving the official image pull."
        }
        & wsl.exe -d $Distribution -- podman pull $GlabImage
        if ($LASTEXITCODE -ne 0) { throw 'Pinned glab image pull failed.' }
    }

    & dotnet publish src\Graft.Server\Graft.Server.csproj -c Release -r linux-x64 --self-contained true -o $serverOutput
    if ($LASTEXITCODE -ne 0) { throw 'Graft.Server Linux publication failed.' }

    & dotnet publish tests\Graft.Glab.Acceptance\Graft.Glab.Acceptance.csproj -c Release -r linux-x64 `
        --self-contained true -p:PublishSingleFile=true -p:GraftSkipClientPublish=true -o $harnessOutput
    if ($LASTEXITCODE -ne 0) { throw 'Graft.Glab.Acceptance Linux publication failed.' }

    $harness = ConvertTo-WslPath (Join-Path $harnessOutput 'Graft.Glab.Acceptance')
    $server = ConvertTo-WslPath (Join-Path $serverOutput 'Graft.Server')
    & wsl.exe -d $Distribution -- $harness $server $GlabImage
    if ($LASTEXITCODE -ne 0) { throw 'Pinned glab acceptance failed.' }
}
finally {
    Pop-Location
}
