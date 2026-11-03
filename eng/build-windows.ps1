# Builds Graft's Windows release executables: the forge, its admin tool, the runner and git-remote-graft, each one
# NativeAOT executable for win-x64 with nothing beside it, SQLite included. Needs the MSVC x64 tools (Visual Studio or
# its Build Tools, found through vswhere as the NativeAOT publish finds them), a NuGet source GitHub-Wixely-Packages that
# can authenticate (see NuGet.config) and the browser client already published to artifacts\client:
#
#   powershell -File eng\build-windows.ps1 -Version 1.2.3
#
# Writes artifacts\release\*-win-x64.exe and smoke-tests each one. From a vcvars64 prompt where vswhere is missing, the
# publish uses the prompt's tools instead.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$VcVars = ''
)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $repository
if (-not (Test-Path -LiteralPath 'artifacts\client\wwwroot\index.html')) { throw 'artifacts\client is missing: publish src\Graft.Client first.' }

$sqlite = (& (Join-Path $PSScriptRoot 'build-sqlite.ps1') -VcVars $VcVars | Select-Object -Last 1)
if (-not (Test-Path -LiteralPath $sqlite)) { throw 'The SQLite library was not built.' }

$release = Join-Path $repository 'artifacts\release'
New-Item -ItemType Directory -Force -Path $release | Out-Null
$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', ('-p:Version=' + $Version))
# Inside a vcvars prompt the tools are on PATH even where vswhere is not; tell the NativeAOT link to use them.
if ($env:VCINSTALLDIR) { $common += '-p:IlcUseEnvironmentalTools=true' }

function Publish-Executable([string]$Project, [string[]]$Properties, [string]$Published, [string]$Name) {
    $output = Join-Path $repository ('artifacts\publish\' + $Project + '-win')
    # Restore for the runtime first: a publish's own restore can leave out the win-x64 target (NETSDK1047).
    & dotnet restore ('src\' + $Project + '\' + $Project + '.csproj') -r win-x64 -p:SelfContained=true @Properties -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw ('Restoring ' + $Project + ' failed.') }
    & dotnet publish ('src\' + $Project + '\' + $Project + '.csproj') @common @Properties --no-restore -o $output -nologo
    if ($LASTEXITCODE -ne 0) { throw ('Publishing ' + $Project + ' failed.') }
    Copy-Item -Force -LiteralPath (Join-Path $output $Published) -Destination (Join-Path $release $Name)
}
Publish-Executable 'Graft.Server' @('-p:GraftServerAot=true', '-p:GraftSkipClientPublish=true', ('-p:GraftSqliteLibrary=' + $sqlite)) 'Graft.Server.exe' 'graft-server-win-x64.exe'
Publish-Executable 'Graft.Admin' @('-p:GraftAdminAot=true', ('-p:GraftSqliteLibrary=' + $sqlite)) 'Graft.Admin.exe' 'graft-admin-win-x64.exe'
Publish-Executable 'Graft.Runner' @('-p:GraftRunnerAot=true') 'graft-runner.exe' 'graft-runner-win-x64.exe'
Publish-Executable 'Graft.Remote' @('-p:GraftRemoteAot=true') 'git-remote-graft.exe' 'git-remote-graft-win-x64.exe'

# The smoke test runs the executables from a directory of their own, so one that still needs a DLL beside it fails here.
$alone = Join-Path ([System.IO.Path]::GetTempPath()) ('graft-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $alone | Out-Null
$process = $null
try {
    Copy-Item -Path (Join-Path $release '*-win-x64.exe') -Destination $alone
    $server = Join-Path $alone 'graft-server-win-x64.exe'
    $admin = Join-Path $alone 'graft-admin-win-x64.exe'
    foreach ($check in @(@($server, '--version'), @($admin, 'version'), @((Join-Path $alone 'git-remote-graft-win-x64.exe'), '--version'))) {
        $said = & $check[0] $check[1]
        if ($LASTEXITCODE -ne 0 -or -not ($said -like ('*' + $Version + '*'))) { throw ($check[0] + ' does not report ' + $Version + ': ' + $said) }
        Write-Output $said
    }
    & (Join-Path $alone 'graft-runner-win-x64.exe') version
    if ($LASTEXITCODE -ne 0) { throw 'graft-runner does not start.' }

    $data = Join-Path $alone 'data'
    $password = Join-Path $alone 'password.txt'
    [System.IO.File]::WriteAllText($password, 'smoke-test-password-0123456789')
    & $admin bootstrap --database (Join-Path $data 'graft.db') --repositories (Join-Path $data 'repositories') --username admin --password-file $password
    if ($LASTEXITCODE -ne 0) { throw 'Creating the first administrator failed.' }
    $log = Join-Path $alone 'server.log'
    $process = Start-Process -FilePath $server -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError (Join-Path $alone 'server.err') `
        -ArgumentList @('--database', (Join-Path $data 'graft.db'), '--repositories', (Join-Path $data 'repositories'), '--listen', 'https://127.0.0.1:18443')
    $ready = $false
    for ($attempt = 0; $attempt -lt 60 -and -not $ready; $attempt++) {
        Start-Sleep -Seconds 1
        $ready = (Test-Path -LiteralPath $log) -and ((Get-Content -LiteralPath $log -Raw) -match '(?m)^READY: ')
        if ($process.HasExited) { break }
    }
    if (-not $ready) { Get-Content -LiteralPath $log, (Join-Path $alone 'server.err') -ErrorAction SilentlyContinue; throw 'The forge did not start.' }
    if (-not ((Get-Content -LiteralPath $log -Raw) -match ('VERSION: ' + [regex]::Escape($Version)))) { throw 'The forge did not report its version.' }
    # Windows PowerShell 5.1 has no switch to skip the check on the forge's own loopback certificate.
    [Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $client = New-Object System.Net.WebClient
    $null = $client.DownloadString('https://127.0.0.1:18443/healthz')
    if (-not ($client.DownloadString('https://127.0.0.1:18443/') -match '<html')) { throw 'The forge served no browser client.' }
    if (-not ($client.DownloadString('https://127.0.0.1:18443/-/git-remote-graft/install.ps1') -match 'setup')) { throw 'The forge served no helper install script.' }
    Write-Output ('Smoke test passed for win-x64 ' + $Version)
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
    Remove-Item -LiteralPath $alone -Recurse -Force -ErrorAction SilentlyContinue
}

$sums = Get-ChildItem -LiteralPath $release -Filter '*-win-x64.exe' | ForEach-Object {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant() + '  ' + $_.Name
}
[System.IO.File]::WriteAllText((Join-Path $release 'SHA256SUMS-win-x64'), (($sums -join "`n") + "`n"))
$sums
