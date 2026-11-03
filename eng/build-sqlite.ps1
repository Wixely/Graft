# Builds the static SQLite library Graft's Windows executables link in, from the source and options in eng/sqlite.txt.
# Writes artifacts\sqlite\win-x64\e_sqlite3.lib and prints its path. Needs the MSVC x64 tools: from a developer prompt,
# or found through vswhere, or named with -VcVars.
param(
    [string]$VcVars = ''
)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$settings = Get-Content (Join-Path $PSScriptRoot 'sqlite.txt') | Where-Object { $_ -and -not $_.StartsWith('#') }
$url = (($settings | Where-Object { $_.StartsWith('url=') }) -replace '^url=', '')
$hash = (($settings | Where-Object { $_.StartsWith('sha256=') }) -replace '^sha256=', '')
$defines = @($settings | Where-Object { $_.StartsWith('define=') } | ForEach-Object { '/D' + ($_ -replace '^define=', '') })

$work = Join-Path $repository 'artifacts\sqlite'
$output = Join-Path $work 'win-x64'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$archive = Join-Path $work ([System.IO.Path]::GetFileName($url))
if (-not (Test-Path -LiteralPath $archive)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $archive
}
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
if ($actual -ne $hash) {
    Remove-Item -LiteralPath $archive -Force
    throw ('SQLite source checksum mismatch: expected ' + $hash + ', got ' + $actual)
}
$source = Join-Path $work ([System.IO.Path]::GetFileNameWithoutExtension($url))
if (-not (Test-Path -LiteralPath (Join-Path $source 'sqlite3.c'))) { Expand-Archive -LiteralPath $archive -DestinationPath $work -Force }

if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    if ($VcVars -eq '') {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) {
            $installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
            if ($installation) { $VcVars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat' }
        }
    }
    if ($VcVars -eq '' -or -not (Test-Path -LiteralPath $VcVars)) {
        $found = Get-ChildItem -Path (Join-Path $env:ProgramFiles 'Microsoft Visual Studio') -Filter vcvars64.bat -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { $VcVars = $found.FullName }
    }
    if ($VcVars -eq '' -or -not (Test-Path -LiteralPath $VcVars)) { throw 'The MSVC x64 tools were not found; run from a developer prompt or pass -VcVars.' }
}

# /Zl leaves the C runtime to the NativeAOT link, which chooses it; /O2 as e_sqlite3 is built.
$object = Join-Path $output 'sqlite3.obj'
$library = Join-Path $output 'e_sqlite3.lib'
$compile = 'cl.exe /nologo /c /O2 /Zl /W0 ' + ($defines -join ' ') + ' /Fo"' + $object + '" "' + (Join-Path $source 'sqlite3.c') + '"'
$archiveCommand = 'lib.exe /nologo /OUT:"' + $library + '" "' + $object + '"'
if ($VcVars -ne '') { $commands = 'call "' + $VcVars + '" >nul && ' + $compile + ' && ' + $archiveCommand }
else { $commands = $compile + ' && ' + $archiveCommand }
& cmd.exe /c $commands
if ($LASTEXITCODE -ne 0) { throw 'Building SQLite failed.' }
Remove-Item -LiteralPath $object -Force
Write-Output $library
