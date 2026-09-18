#!/bin/sh
set -u
umask 077
ulimit -c 0 2>/dev/null || :
cd "$(dirname "$0")" || exit 2
report=$(mktemp ./graft-native-report-XXXXXX.txt) || exit 2
scratch=$(mktemp -d ./graft-native-run-XXXXXX) || exit 2
trap 'rm -f "$scratch/output"; rmdir "$scratch" 2>/dev/null || :' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
record() { printf '%s\n' "$*" | tee -a "$report"; }
record 'Graft NativeAOT static-musl compatibility report (2026-09-18)'
record "UTC: $(date -u '+%Y-%m-%dT%H:%M:%SZ')"
record "Kernel: $(uname -r)"
architecture=$(uname -m)
record "Architecture: $architecture"
if [ "$architecture" != x86_64 ]; then
    record 'STOP: this executable targets Linux x86_64.'
    exit 2
fi
if [ ! -x ./Graft.Compatibility ]; then
    chmod u+x ./Graft.Compatibility || exit 2
fi
unset DOTNET_STARTUP_HOOKS DOTNET_ADDITIONAL_DEPS DOTNET_SHARED_STORE DOTNET_ROOT
unset DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU
record 'NativeAOT; musl, SQLite, OpenSSL and ICU statically linked. No installed .NET runtime required.'
if command -v timeout >/dev/null 2>&1; then
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 timeout 60 ./Graft.Compatibility > "$scratch/output" 2>&1
else
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 ./Graft.Compatibility > "$scratch/output" 2>&1
fi
result=$?
cat "$scratch/output" | tee -a "$report"
record "Process exit code: $result"
if [ "$result" -eq 126 ]; then
    record 'Execution denied. Check permissions and use a filesystem that permits execution; avoid /tmp if mounted noexec.'
fi
record "Report: $report"
exit "$result"
