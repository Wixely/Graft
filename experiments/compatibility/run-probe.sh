#!/bin/sh
# No installation, downloads, sudo, Docker changes or external network requests.
set -u
umask 077
ulimit -c 0 2>/dev/null || :
cd "$(dirname "$0")" || exit 2
report=$(mktemp ./graft-report-XXXXXX.txt) || exit 2
scratch=$(mktemp -d ./graft-run-XXXXXX) || exit 2
trap 'rm -f "$scratch/output"; rmdir "$scratch" 2>/dev/null || :' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
record() { printf '%s\n' "$*" | tee -a "$report"; }
record 'Graft .NET 10 compatibility report v1'
record "UTC: $(date -u '+%Y-%m-%dT%H:%M:%SZ')"
record "Kernel: $(uname -r)"
architecture=$(uname -m)
record "Architecture: $architecture"
if command -v getconf >/dev/null 2>&1; then
    record "Libc: $(getconf GNU_LIBC_VERSION 2>/dev/null || printf unknown)"
else
    record 'Libc: getconf unavailable'
fi
if [ -r /etc/VERSION ]; then
    record 'Selected vendor OS version fields:'
    grep -E '^(productversion|buildnumber|smallfixnumber)=' /etc/VERSION | tee -a "$report"
fi
if command -v docker >/dev/null 2>&1; then
    docker --version 2>&1 | tee -a "$report"
else
    record 'Docker CLI: not on PATH (not required for this probe)'
fi
if [ "$architecture" != x86_64 ]; then
    record 'STOP: this package is for Linux x86_64 only.'
    record "Report: $report"
    exit 2
fi
if [ ! -f ./Graft.Compatibility ]; then
    record 'STOP: extract the entire archive before running this script.'
    exit 2
fi
chmod u+x ./Graft.Compatibility || exit 2
# Keep ambient .NET configuration from injecting code or changing resolution.
unset DOTNET_STARTUP_HOOKS DOTNET_ADDITIONAL_DEPS DOTNET_SHARED_STORE DOTNET_ROOT
unset DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU
run_probe() {
    mode=$1
    record "--- Run: globalization invariant=$mode ---"
    if command -v timeout >/dev/null 2>&1; then
        DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=$mode timeout 60 ./Graft.Compatibility > "$scratch/output" 2>&1
    else
        DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=$mode ./Graft.Compatibility > "$scratch/output" 2>&1
    fi
    result=$?
    cat "$scratch/output" | tee -a "$report"
    record "Process exit code: $result"
    return "$result"
}
run_probe 0
normal_result=$?
if [ "$normal_result" -eq 126 ] || [ "$normal_result" -eq 127 ]; then
    record 'Launch failed before probe execution; runtime compatibility remains unknown.'
    record 'For Permission denied, check executable permissions and whether the extraction filesystem is mounted noexec.'
    record 'Copy the entire extracted directory to a user-writable filesystem that permits execution and rerun without sudo.'
elif [ "$normal_result" -ne 0 ] && grep -q 'GLIBC_.*not found' "$scratch/output"; then
    record 'Required GLIBC symbol version is missing. CoreCLR could not load; an ICU retry cannot fix this.'
    record 'Test a compatible container userspace next; do not replace the host system libc.'
elif [ "$normal_result" -ne 0 ]; then
    record 'Retrying with invariant globalization to distinguish ICU problems. This is diagnostic, not a full pass.'
    run_probe 1 || :
fi
record 'NativeAOT executable: NOT INCLUDED / NOT TESTED.'
record 'This is a self-contained .NET 10.0.12 runtime probe using generated Dapper.AOT queries.'
record 'No production database or existing services were accessed.'
record "Report: $report"
exit "$normal_result"
