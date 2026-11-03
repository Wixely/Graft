# 0044: Releases: single-file executables from a tag, packages from GitHub Packages

- Date: 2026-11-03
- Status: Accepted, implemented; v0.1.0 released 2026-11-03
- Source: A request on 2026-11-03 for releases built by GitHub Actions on a version tag: the forge for Windows, Linux
  and Docker and the helper for Windows and Linux, preferably as single standalone executables; DnaX, CupriNet and
  CupriMark from the GitHub Packages feed; the image on GHCR; Graft.Admin and graft-runner as well, with install
  instructions an agent can follow on someone's behalf; and NuGet references handled the way the sibling
  repositories already handle them
- Supersedes: the local, ignored feed at `artifacts/storage/dnax-feed` as Graft's package source (the open item in the
  backlog and in ADR 0037's review triggers)
- Review trigger: an ARM or macOS build; a DnaX, CupriNet or CupriMark bump (lock hashes); a SQLitePCLRaw bump
  (`eng/sqlite.txt`); a change of container base image; signing the executables

## Decision: DnaX, CupriNet and CupriMark come from GitHub Packages

The three are published by their own CI to Wixely's GitHub Packages feed, the same feed Wixely's other repositories
restore from. `NuGet.config` names it `GitHub-Wixely-Packages`, the name those repositories use, with no credential in
the file, and maps `DnaX.*`, `CupriNet.*` and `CupriMark*` to it and everything else to nuget.org. A developer adds the
credential once in their user-level NuGet configuration under that name; the workflow authenticates it with
`dotnet nuget update source` and its own token, as the other repositories' workflows do.

The lock files keep their content hashes. CupriNet's and CupriMark's already matched the feed's packages; DnaX
10.0.0-alpha.13's had been packed locally, and packing is not byte-reproducible even on one machine, so those three
hashes were replaced with the feed's. A machine whose NuGet cache still holds the locally packed DnaX alpha.13 fails
restore with NU1403 until those three versions are removed from the cache.

## Decision: every download is one executable

NativeAOT throughout. The forge, `graft-admin`, `graft-runner` and `git-remote-graft` are each one file for win-x64 and
one static file for linux-x64, built in Alpine 3.21 for musl so it runs on any x64 Linux, the old kernel included.

The Windows forge used to need `e_sqlite3.dll` beside it. SQLite is now compiled from source and linked into the forge
and the admin tool on both platforms (`eng/build-sqlite.ps1`, `eng/build-sqlite.sh`): the same version, 3.53.3, with the
same options as the `e_sqlite3` that SQLitePCLRaw 2.1.12 loads in a managed build, listed once in `eng/sqlite.txt` with
the source archive's hash. A NativeAOT probe linked against the Windows build reported the same `compile_options` and
defaults as the DLL, apart from the compiler's version, and ran with no DLL beside it.

This also fixes a difference nobody had noticed: the earlier static Linux build linked Alpine's system `libsqlite3`,
which leaves foreign keys off by default where `e_sqlite3` turns them on, so `ON DELETE CASCADE` would not have applied
on that build. Graft sets no SQLite option itself and relies on the library's defaults.

`--version` (forge), `version` (admin tool, runner) and `--version` (helper) print the release, stamped from the tag;
the forge prints it at startup as `VERSION:`. The runner reports it to the forge when it registers.

## Decision: the release workflow

`.github/workflows/release.yml`, in the shape of CupriCut's: on a `v*` tag, or by hand with a version for a run that
builds and tests and publishes nothing.

1. The browser client is published once and embedded in both forges.
2. Windows builds on `windows-latest` with `eng/build-windows.ps1`; Linux in an `alpine:3.21` container with
   `eng/build-linux.sh`, which also checks that each Linux file has no interpreter and no shared library.
3. Each executable is smoke-tested where it was built (`eng/smoke.sh` and the end of the Windows script): it reports the
   version, the admin tool creates an administrator, and the forge starts on that database from a directory of its own
   and serves its pages and the helper's install script.
4. The image `ghcr.io/wixely/graft:<version>`, and `:latest` for a release without a hyphen, carries the static Linux
   forge and admin tool and, in `/app/downloads`, every runner and helper executable, which the compose file serves by
   default.
5. A GitHub release with the eight executables and one `SHA256SUMS`, marked prerelease for a version with a hyphen.

`--runner-downloads` no longer requires `--runner-port`, because the directory serves `git-remote-graft` too.

The README became an installation guide, with a section for agents installing Graft unattended; its running status
log moved to [history](../history.md).

## What was verified

- Locally on Windows: `eng/build-windows.ps1` from a clean package folder restoring from GitHub Packages, the four
  executables published with zero warnings (forge 78 MB, runner 11.7 MB, helper 11.4 MB, admin tool 6.5 MB), and the
  smoke test from a directory holding nothing else.
- The Git suite, 1333 assertions, against those release executables: the forge with SQLite compiled in and the feed's
  DnaX, the release runner (`GRAFT_RUNNER`) and helper (`GRAFT_REMOTE_HELPER`); MVP-04, 215, on the feed's packages.
  The helper check now installs the helper under the name Git looks for, as the README tells people to.

- In GitHub Actions: four manual runs found and fixed what a local build could not (a credential the Linux build
  container could not see, and ignore files that hid `artifacts/container` from the image build); the fourth passed
  every job. Tag `v0.1.0` then published the release with eight executables and `SHA256SUMS`, and
  `ghcr.io/wixely/graft:0.1.0` and `:latest`, which pull anonymously. The workflow's own token reads the feed.

Not verified: the released Linux executables on the 3.10 host, and an install that follows the README end to end.
