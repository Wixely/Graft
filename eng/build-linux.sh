#!/bin/sh
# Builds Graft's Linux release executables: the forge, its admin tool, the runner and git-remote-graft, each one static
# NativeAOT executable for linux-x64 that needs nothing from the machine it runs on, the old kernel included.
#
# Run as root in an Alpine 3.21 container, from a checkout whose NuGet source GitHub-Wixely-Packages can authenticate
# (see NuGet.config) and with the browser client already published to artifacts/client:
#
#   docker run --rm -v "$PWD:/src" -w /src alpine:3.21 sh eng/build-linux.sh 1.2.3
#
# Alpine 3.21 because the forge embeds that release's ICU data (icudt74l.dat). Writes artifacts/release/*-linux-x64
# and smoke-tests each one.
set -eu
version=${1:?usage: eng/build-linux.sh <version>}
repository=$(cd "$(dirname "$0")/.." && pwd)
cd "$repository"
test -f artifacts/client/wwwroot/index.html || { echo "artifacts/client is missing: publish src/Graft.Client first" >&2; exit 1; }

apk add --no-cache bash curl unzip file binutils git ca-certificates libstdc++ libgcc icu-libs \
    clang build-base zlib-dev zlib-static openssl-dev openssl-libs-static icu-dev icu-static icu-data-full \
    cmake linux-headers lttng-ust libunwind >/dev/null

if ! command -v dotnet >/dev/null 2>&1; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --jsonfile global.json --install-dir /usr/share/dotnet >/dev/null
    ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false

sqlite=$(sh eng/build-sqlite.sh)
out=artifacts/release
mkdir -p "$out" artifacts/publish
common="-c Release -r linux-musl-x64 --self-contained true -p:Version=$version"

publish() { # project, property, published name, release name
    dotnet publish "src/$1/$1.csproj" $common -p:"$2"=true $4 -o "artifacts/publish/$1-linux" >/dev/null
    cp "artifacts/publish/$1-linux/$3" "$out/$5"
}
publish Graft.Server GraftServerStatic Graft.Server "-p:GraftSkipClientPublish=true -p:GraftSqliteLibrary=$repository/$sqlite" graft-server-linux-x64
publish Graft.Admin GraftAdminStatic Graft.Admin "-p:GraftSqliteLibrary=$repository/$sqlite" graft-admin-linux-x64
publish Graft.Runner GraftRunnerStatic graft-runner "" graft-runner-linux-x64
publish Graft.Remote GraftRemoteStatic git-remote-graft "" git-remote-graft-linux-x64

# Static means no program interpreter and no shared library: anything else would not run on every x64 Linux.
for binary in "$out"/*-linux-x64; do
    if readelf -l "$binary" | grep -q INTERP || readelf -d "$binary" | grep -q NEEDED; then
        echo "$binary still needs a dynamic loader or a shared library" >&2
        exit 1
    fi
    file "$binary"
done

sh eng/smoke.sh "$out" linux-x64 "$version"
(cd "$out" && sha256sum *-linux-x64 > SHA256SUMS-linux-x64)
cat "$out/SHA256SUMS-linux-x64"
