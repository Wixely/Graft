# Stores the GitHub token that NuGet uses for Wixely's package feed (source GitHub-Wixely-Packages, see NuGet.config)
# in your user-level NuGet configuration, encrypted for your Windows account. Prompts for the token without echoing it,
# checks GitHub Packages accepts it, and only then stores it. Press Enter at the prompt to use the GitHub credential
# Git already has.
#
#   powershell -ExecutionPolicy Bypass -File eng\set-packages-token.ps1
param(
    [string]$Username = ''
)
$ErrorActionPreference = 'Stop'
$feed = 'https://nuget.pkg.github.com/Wixely/index.json'

if ($Username -eq '') { $Username = Read-Host 'GitHub username' }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# The feed answers 401 to a token without read:packages, so a bad one is caught here rather than at the next restore.
# GitHub Packages' NuGet feed takes only classic tokens; a fine-grained one (github_pat_...) is refused.
while ($true) {
    $secure = Read-Host 'Token with read:packages (hidden; Enter to use the one Git has)' -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer).Trim() }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    if ($token -eq '') {
        $filled = "protocol=https`nhost=github.com`n`n" | git credential fill
        $token = ($filled | Where-Object { $_ -like 'password=*' }) -replace '^password=', ''
        if (-not $token) { Write-Host 'Git has no GitHub credential to use; paste a token instead.'; continue }
    }
    $basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($Username + ':' + $token))
    try {
        Invoke-WebRequest -UseBasicParsing -Headers @{ Authorization = 'Basic ' + $basic } `
            -Uri 'https://nuget.pkg.github.com/Wixely/download/dnax.uploads/index.json' | Out-Null
        break
    }
    catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { $_.Exception.Message }
        $kind = if ($token -like 'github_pat_*') { 'fine-grained' } elseif ($token -like 'ghp_*') { 'classic' } else { 'unrecognised' }
        Write-Host ('GitHub Packages refused that token (' + $status + ', ' + $kind + ' token, ' + $token.Length +
            ' characters). It needs to be a classic token with read:packages. Try again.')
    }
}

$config = Join-Path $env:APPDATA 'NuGet\NuGet.Config'
$known = (& dotnet nuget list source --configfile $config) -match 'GitHub-Wixely-Packages'
if ($known) {
    & dotnet nuget update source GitHub-Wixely-Packages --username $Username --password $token --configfile $config | Out-Null
} else {
    & dotnet nuget add source $feed --name GitHub-Wixely-Packages --username $Username --password $token --configfile $config | Out-Null
}
if ($LASTEXITCODE -ne 0) { throw 'dotnet nuget could not store the credential.' }
$token = $null
Write-Host ('Stored the token for GitHub-Wixely-Packages in ' + $config + ', encrypted for ' + $env:USERNAME + '.')
