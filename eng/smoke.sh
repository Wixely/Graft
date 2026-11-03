#!/bin/sh
# Smoke-tests the Linux release executables in a directory: each reports the release's version, the admin tool creates
# the first administrator, and the forge starts on it and answers with its pages and the helper's install script.
# Usage: eng/smoke.sh <directory> linux-x64 <version>
set -eu
directory=$1
platform=$2
version=$3
server=$directory/graft-server-$platform
admin=$directory/graft-admin-$platform
runner=$directory/graft-runner-$platform
helper=$directory/git-remote-graft-$platform

"$server" --version | grep -F "$version"
"$admin" version | grep -F "$version"
"$helper" --version | grep -F "$version"
"$runner" version

work=$(mktemp -d)
trap 'kill "$pid" 2>/dev/null || true; rm -rf "$work"' EXIT
pid=0
printf '%s' 'smoke-test-password-0123456789' > "$work/password"
"$admin" bootstrap --database "$work/graft.db" --repositories "$work/repositories" --username admin --password-file "$work/password"
"$server" --database "$work/graft.db" --repositories "$work/repositories" --listen https://127.0.0.1:18443 > "$work/server.log" 2>&1 &
pid=$!
for _ in $(seq 1 60); do
    grep -q '^READY: ' "$work/server.log" && break
    kill -0 "$pid" 2>/dev/null || break
    sleep 1
done
if ! grep -q '^READY: ' "$work/server.log"; then
    cat "$work/server.log" >&2
    echo "the forge did not start" >&2
    exit 1
fi
grep -F "VERSION: $version" "$work/server.log"
curl -fsk https://127.0.0.1:18443/healthz
curl -fsk https://127.0.0.1:18443/ | grep -q '<html' || { echo "the forge served no browser client" >&2; exit 1; }
curl -fsk https://127.0.0.1:18443/-/git-remote-graft/install.sh | grep -q 'setup' || { echo "the forge served no helper install script" >&2; exit 1; }
echo "smoke test passed for $platform $version"
