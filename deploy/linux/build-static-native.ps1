param(
    [Parameter(Mandatory = $true)][string]$Distribution,
    [string]$BuildRoot = ('/root/graft-product-build-' + [Guid]::NewGuid().ToString('N'))
)

$ErrorActionPreference = 'Stop'
if ($BuildRoot -notmatch '^/root/graft-product-build-[a-zA-Z0-9-]+$') {
    throw 'Use a dedicated /root/graft-product-build-* directory.'
}

function Invoke-Linux {
    param([Parameter(Mandatory = $true)][string[]]$Command)
    & wsl.exe -d $Distribution -u root --exec @Command
    if ($LASTEXITCODE -ne 0) { throw ('Linux command failed: ' + $Command[0]) }
}

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$clientOutput = Join-Path $repository 'artifacts\client'
& dotnet publish (Join-Path $repository 'src\Graft.Client\Graft.Client.csproj') -c Release -o $clientOutput
if ($LASTEXITCODE -ne 0) { throw 'Windows client publication failed.' }
$source = (& wsl.exe -d $Distribution --exec wslpath -a $repository).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve repository path in WSL.' }
$rootfs = $BuildRoot + '/rootfs'

Invoke-Linux -Command @('mkdir', '-p', $rootfs)
$alpineBase = 'https://dl-cdn.alpinelinux.org/alpine/v3.21/releases/x86_64/alpine-minirootfs-3.21.3-x86_64.tar.gz'
Invoke-Linux -Command @('curl', '-fsSL', '--retry', '3', '--retry-all-errors', $alpineBase, '-o', ($BuildRoot + '/alpine.tar.gz'))
Invoke-Linux -Command @('curl', '-fsSL', ($alpineBase + '.sha256'), '-o', ($BuildRoot + '/alpine.sha256'))
$actual = ((Invoke-Linux -Command @('sha256sum', ($BuildRoot + '/alpine.tar.gz'))) -split ' ')[0]
$expected = ((Invoke-Linux -Command @('cat', ($BuildRoot + '/alpine.sha256'))) -split ' ')[0]
if ($actual -ne $expected) { throw 'Alpine checksum mismatch.' }

& wsl.exe -d $Distribution -u root --exec test -e ($rootfs + '/etc/alpine-release')
if ($LASTEXITCODE -eq 0) { throw 'Build root is already initialized; existing files were preserved.' }
Invoke-Linux -Command @('tar', '-xzf', ($BuildRoot + '/alpine.tar.gz'), '-C', $rootfs)
Invoke-Linux -Command @('cp', '/etc/resolv.conf', ($rootfs + '/etc/resolv.conf'))
Invoke-Linux -Command @('cp', '/etc/ssl/certs/ca-certificates.crt', ($rootfs + '/etc/ssl/certs/ca-certificates.crt'))
foreach ($device in @(@('null','1','3'), @('zero','1','5'), @('random','1','8'), @('urandom','1','9'))) {
    Invoke-Linux -Command @('mknod', '-m', '666', ($rootfs + '/dev/' + $device[0]), 'c', $device[1], $device[2])
}
Invoke-Linux -Command @('/usr/sbin/chroot', $rootfs, '/sbin/apk', 'add', '--no-cache', 'clang', 'build-base', 'zlib-dev',
    'openssl-dev', 'openssl-libs-static', 'icu-dev', 'icu-static', 'icu-data-full', 'sqlite-dev', 'sqlite-static', 'cmake',
    'bash', 'linux-headers', 'lttng-ust', 'libunwind', 'ca-certificates')

$sdkUrl = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-linux-musl-x64.tar.gz'
$sdkHash = '7f51b39f92066b6955fdf63764c799bfa6f7d63683893af8972706cbd8bcff5db95b784c06f0bd4a4cd439e22d17c75fd8f51da99246c46a316b3e9cc081c7a1'
Invoke-Linux -Command @('curl', '-fsSL', '--retry', '3', '--retry-all-errors', $sdkUrl, '-o', ($BuildRoot + '/sdk.tar.gz'))
$actual = ((Invoke-Linux -Command @('sha512sum', ($BuildRoot + '/sdk.tar.gz'))) -split ' ')[0]
if ($actual -ne $sdkHash) { throw 'SDK checksum mismatch.' }
Invoke-Linux -Command @('mkdir', '-p', ($rootfs + '/dotnet'), ($rootfs + '/src'))
Invoke-Linux -Command @('tar', '-xzf', ($BuildRoot + '/sdk.tar.gz'), '-C', ($rootfs + '/dotnet'))

Invoke-Linux -Command @('tar', '-C', $source, '--exclude=.git', '--exclude=artifacts', '--exclude=*/bin', '--exclude=*/obj',
    '-cf', ($BuildRoot + '/source.tar'), '.')
Invoke-Linux -Command @('tar', '-xf', ($BuildRoot + '/source.tar'), '-C', ($rootfs + '/src'))
Invoke-Linux -Command @('mkdir', '-p', ($rootfs + '/src/artifacts/storage/dnax-feed'))
Invoke-Linux -Command @('cp', ($source + '/artifacts/storage/dnax-feed/DnaX.Data.Migrations.10.0.0-alpha.7.nupkg'),
    ($source + '/artifacts/storage/dnax-feed/DnaX.Data.Migrations.Sqlite.10.0.0-alpha.7.nupkg'),
    ($rootfs + '/src/artifacts/storage/dnax-feed/'))
Invoke-Linux -Command @('mkdir', '-p', ($rootfs + '/src/artifacts/client'))
Invoke-Linux -Command @('cp', '-a', ($source + '/artifacts/client/.'), ($rootfs + '/src/artifacts/client/'))

Invoke-Linux -Command @('mount', '-t', 'proc', 'proc', ($rootfs + '/proc'))
try {
    $environment = @('/usr/sbin/chroot', $rootfs, '/usr/bin/env', 'DOTNET_ROOT=/dotnet', 'DOTNET_CLI_TELEMETRY_OPTOUT=1',
        'DOTNET_GENERATE_ASPNET_CERTIFICATE=false')
    Invoke-Linux -Command ($environment + @('/dotnet/dotnet', 'publish', '/src/src/Graft.Server/Graft.Server.csproj', '-c', 'Release',
        '-r', 'linux-musl-x64', '--self-contained', 'true', '-p:GraftServerStatic=true', '-p:GraftSkipClientPublish=true', '-o', '/out/server'))
    Invoke-Linux -Command ($environment + @('/dotnet/dotnet', 'publish', '/src/src/Graft.Admin/Graft.Admin.csproj', '-c', 'Release',
        '-r', 'linux-musl-x64', '--self-contained', 'true', '-p:GraftAdminStatic=true', '-o', '/out/admin'))
    foreach ($binary in @('/out/server/Graft.Server', '/out/admin/Graft.Admin')) {
        Invoke-Linux -Command @('/usr/sbin/chroot', $rootfs, '/usr/bin/file', $binary)
        $headers = Invoke-Linux -Command @('/usr/sbin/chroot', $rootfs, '/usr/bin/readelf', '-l', $binary)
        $dynamic = Invoke-Linux -Command @('/usr/sbin/chroot', $rootfs, '/usr/bin/readelf', '-d', $binary)
        if (($headers -match 'INTERP') -or ($dynamic -match 'NEEDED')) {
            throw ('Output still requires a dynamic loader or shared library: ' + $binary)
        }
    }
    $destination = $source + '/artifacts/mvp08/static-linux-x64-release'
    & wsl.exe -d $Distribution -u root --exec test -e $destination
    if ($LASTEXITCODE -eq 0) { throw 'Release destination already exists; existing files were preserved.' }
    Invoke-Linux -Command @('mkdir', '-p', ($destination + '/server'), ($destination + '/admin'))
    Invoke-Linux -Command @('cp', ($rootfs + '/out/server/Graft.Server'), ($destination + '/server/'))
    Invoke-Linux -Command @('cp', ($rootfs + '/out/admin/Graft.Admin'), ($destination + '/admin/'))
} finally {
    Invoke-Linux -Command @('umount', ($rootfs + '/proc'))
}

Write-Output ('Static NativeAOT server and admin built and inspected. Build root retained: ' + $BuildRoot)
