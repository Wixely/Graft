#!/bin/sh
# Builds the static SQLite library Graft's Linux executables link in, from the source and options in eng/sqlite.txt.
# Writes artifacts/sqlite/linux-musl-x64/libe_sqlite3.a and prints its path relative to the repository.
set -eu
repository=$(cd "$(dirname "$0")/.." && pwd)
cd "$repository"
setting() { grep "^$1=" eng/sqlite.txt | head -n 1 | cut -d= -f2-; }
url=$(setting url)
hash=$(setting sha256)
defines=$(grep '^define=' eng/sqlite.txt | sed 's/^define=/-D/')

work=artifacts/sqlite
output=$work/linux-musl-x64
mkdir -p "$output"
archive=$work/$(basename "$url")
[ -f "$archive" ] || curl -fsSL --retry 3 -o "$archive" "$url"
if ! echo "$hash  $archive" | sha256sum -c - >/dev/null 2>&1; then
    rm -f "$archive"
    echo "SQLite source checksum mismatch for $url" >&2
    exit 1
fi
source=$work/$(basename "$url" .zip)
[ -f "$source/sqlite3.c" ] || unzip -qo "$archive" -d "$work"
# shellcheck disable=SC2086
cc -c -O2 -fPIC -w $defines "$source/sqlite3.c" -o "$output/sqlite3.o"
ar rcs "$output/libe_sqlite3.a" "$output/sqlite3.o"
rm -f "$output/sqlite3.o"
echo "$output/libe_sqlite3.a"
