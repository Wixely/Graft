param(
    [Parameter(Mandatory = $true)][string]$Distribution,
    [string]$BuildRoot = ('/root/graft-native-build-' + [Guid]::NewGuid().ToString('N'))
)
$ErrorActionPreference = 'Stop'
# PowerShell 5.1 orchestration. All packages are installed inside this dedicated rootfs.
if ($BuildRoot -notmatch '^/root/graft-native-build-[a-zA-Z0-9-]+$') { throw 'Use a dedicated /root/graft-native-build-* directory.' }
function Invoke-Linux {
    param([Parameter(Mandatory = $true)][string[]]$Command)
    & wsl -d $Distribution -u root --exec @Command
    if ($LASTEXITCODE -ne 0) { throw ('Linux command failed: ' + $Command[0]) }
}
$rootfs = $BuildRoot + '/rootfs'
$source = (& wsl -d $Distribution --exec wslpath -a $PSScriptRoot).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source directory.' }
Invoke-Linux -Command @('mkdir', '-p', $rootfs)
$alpineBase = 'https://dl-cdn.alpinelinux.org/alpine/v3.21/releases/x86_64/alpine-minirootfs-3.21.3-x86_64.tar.gz'
Invoke-Linux -Command @('curl', '-fsSL', '--retry', '3', $alpineBase, '-o', ($BuildRoot + '/alpine.tar.gz'))
Invoke-Linux -Command @('curl', '-fsSL', ($alpineBase + '.sha256'), '-o', ($BuildRoot + '/alpine.sha256'))
$actual = ((Invoke-Linux -Command @('sha256sum', ($BuildRoot + '/alpine.tar.gz'))) -split ' ')[0]
$expected = ((Invoke-Linux -Command @('cat', ($BuildRoot + '/alpine.sha256'))) -split ' ')[0]
if ($actual -ne $expected) { throw 'Alpine checksum mismatch.' }
# Run against a fresh build directory; do not unpack over an existing toolchain.
& wsl -d $Distribution -u root --exec test -e ($rootfs + '/etc/alpine-release')
if ($LASTEXITCODE -eq 0) { throw 'Build root already initialized. Choose a fresh BuildRoot; existing files have been preserved.' }
Invoke-Linux -Command @('tar', '-xzf', ($BuildRoot + '/alpine.tar.gz'), '-C', $rootfs)
Invoke-Linux -Command @('cp', '/etc/resolv.conf', ($rootfs + '/etc/resolv.conf'))
Invoke-Linux -Command @('cp', '/etc/ssl/certs/ca-certificates.crt', ($rootfs + '/etc/ssl/certs/ca-certificates.crt'))
foreach ($device in @(@('null','1','3'), @('zero','1','5'), @('random','1','8'), @('urandom','1','9'))) {
    Invoke-Linux -Command @('mknod', '-m', '666', ($rootfs + '/dev/' + $device[0]), 'c', $device[1], $device[2])
}
Invoke-Linux -Command @('/usr/sbin/chroot', $rootfs, '/sbin/apk', 'add', '--no-cache', 'clang', 'build-base', 'zlib-dev', 'openssl-dev', 'openssl-libs-static', 'icu-dev', 'icu-static', 'icu-data-full', 'sqlite-dev', 'sqlite-static', 'cmake', 'bash', 'linux-headers', 'lttng-ust', 'libunwind', 'ca-certificates')
$sdkUrl = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-linux-musl-x64.tar.gz'
$sdkHash = '7f51b39f92066b6955fdf63764c799bfa6f7d63683893af8972706cbd8bcff5db95b784c06f0bd4a4cd439e22d17c75fd8f51da99246c46a316b3e9cc081c7a1'
Invoke-Linux -Command @('curl', '-fsSL', '--retry', '3', $sdkUrl, '-o', ($BuildRoot + '/sdk.tar.gz'))
$actual = ((Invoke-Linux -Command @('sha512sum', ($BuildRoot + '/sdk.tar.gz'))) -split ' ')[0]
if ($actual -ne $sdkHash) { throw 'SDK checksum mismatch.' }
Invoke-Linux -Command @('mkdir', '-p', ($rootfs + '/dotnet'), ($rootfs + '/src'))
Invoke-Linux -Command @('tar', '-xzf', ($BuildRoot + '/sdk.tar.gz'), '-C', ($rootfs + '/dotnet'))
foreach ($file in @('Graft.Compatibility.csproj', 'Program.cs', 'NuGet.config')) {
    Invoke-Linux -Command @('cp', ($source + '/' + $file), ($rootfs + '/src/' + $file))
}
Invoke-Linux -Command @('mount', '-t', 'proc', 'proc', ($rootfs + '/proc'))
try {
    Invoke-Linux -Command @('/usr/sbin/chroot', $rootfs, '/usr/bin/env', 'DOTNET_ROOT=/dotnet', 'DOTNET_CLI_TELEMETRY_OPTOUT=1', 'DOTNET_GENERATE_ASPNET_CERTIFICATE=false', '/dotnet/dotnet', 'publish', '/src/Graft.Compatibility.csproj', '-c', 'Release', '-r', 'linux-musl-x64', '--self-contained', 'true', '-p:StaticProbe=true', '-o', '/out')
    Invoke-Linux -Command @('/usr/sbin/chroot', $rootfs, '/usr/bin/file', '/out/Graft.Compatibility')
    $headers = Invoke-Linux -Command @('/usr/sbin/chroot', $rootfs, '/usr/bin/readelf', '-l', '/out/Graft.Compatibility')
    $dynamic = Invoke-Linux -Command @('/usr/sbin/chroot', $rootfs, '/usr/bin/readelf', '-d', '/out/Graft.Compatibility')
    if (($headers -match 'INTERP') -or ($dynamic -match 'NEEDED')) { throw 'Output still requires a dynamic loader or shared library.' }
    Invoke-Linux -Command @(($rootfs + '/out/Graft.Compatibility'))
    New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot 'artifacts/native-linux-x64') | Out-Null
    Invoke-Linux -Command @('cp', ($rootfs + '/out/Graft.Compatibility'), ($source + '/artifacts/native-linux-x64/Graft.Compatibility'))
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'run-native-probe.sh'), (Join-Path $PSScriptRoot 'NATIVE-README.md') -Destination (Join-Path $PSScriptRoot 'artifacts/native-linux-x64')
} finally {
    Invoke-Linux -Command @('umount', ($rootfs + '/proc'))
}
Write-Output 'Static NativeAOT executable built and checked. Keep the build root for provenance; package notices/evidence before distribution.'
