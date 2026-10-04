# Browser upload transport evidence

Date: 2026-10-04. Owner: Implementation agent. Review when the DnaX.Uploads version, the upload profiles, the authentication bridge, or any ingest consumer changes.

## Result

Graft had no web-UI uploads at all before this work; the only "upload" in the tree was `git-upload-pack`, which is Git's fetch direction. `DnaX.Uploads` 10.0.0-alpha.7 is now mounted at `/api/graft/uploads` as the browser transport, with three consumers: Git bundle inspection, repository file commits and avatars. The reasoning and its consequences are recorded in [ADR 0005](../decisions/0005-browser-upload-transport.md).

API and application clients do not use it. Each feature is one application-owned ingest with two transports in front of it.

## Transport checks

Against a live server over loopback HTTPS:

| Check | Result |
| --- | --- |
| Capabilities, session create, byte write, complete | All succeed; profiles and an antiforgery token are returned |
| Unauthenticated capabilities and session read | `401` |
| Authenticated mutation without the antiforgery header | `400`, "Refresh upload authorization" |
| Declared length above the profile limit | `413` |
| A second account reading another account's session | `404`, for both read and delete — existence is not disclosed |
| The owning account reading its own session | `200` |
| Disabling an account with an upload in flight | Its next read and write both `401`, immediately |
| GitLab's `/uploads` path | Still the single-page shell, `text/html`; the group did not squat it |
| `GET /api/v4/unknown` | Still an API error, not HTML |

Staged files on disk are named by UUID, not by any client-supplied filename, under a directory created with owner-only permissions on Unix. The data-protection key ring sits beside the database with the same treatment.

## Consumer checks

**Git bundle inspection.** An administrator uploads a bundle and the server returns its reference inventory and the eight explicitly excluded metadata categories. Verified in Chromium with a real bundle created by Git 2.53: `HEAD`, `refs/heads/main` and `refs/tags/v1` were read correctly with the right object ids, and the staging copy was removed afterwards.

**Repository file commits.** Covered separately in [server-authored commit evidence](2026-10-04-server-authored-commits.md).

**Avatars.** Covered separately in [avatar evidence](2026-10-04-avatars.md).

Every ingest verifies the upload was created under the profile that consumer expects, so a session created under the 20 GiB archive profile cannot be fed to the 4 MiB repository-file consumer or the 2 MiB avatar consumer.

## NativeAOT status

The trimming and AOT **analysis** stage is clean: a `GraftServerAot=true` publish produced zero `IL****` diagnostics with Data Protection, antiforgery and the authentication handler added. The publish then fails at `Platform linker not found`, because the Desktop Development for C++ workload is not installed on this machine, so **no native binary exists and runtime behaviour under NativeAOT is unverified**. Static analysis being clean is not the same as antiforgery working in a published artifact. This is the one gate this slice could not close locally.

## Boundaries

- `DnaX.Uploads` 10.0.0-alpha.7 is an experimental prerelease; its API and journal format are explicitly not stable, and Graft is its first consumer.
- **The Android case that motivated this work is untested.** It needs a physical device: the browser being backgrounded while the native picker is open cannot be reproduced on desktop Chromium. Resumption was exercised only through ordinary interrupted transfers.
- Large transfers were not run. The archive profile permits 20 GiB per file, but nothing above a few hundred kilobytes has been moved through it here.
- Antiforgery keys are not included in backups, so a restore onto a fresh host invalidates outstanding tokens. Browsers recover by re-fetching capabilities.
- `MaxConcurrentConnections` moved from 16 to 64. The MVP-08 resource measurements predate that change and were not repeated.

Recommended next action: Owner: User + implementation agent — run the Android picker and backgrounding case on a physical device, since it is the reason this transport exists. Owner: Implementation agent — publish on a machine with the C++ build tools and confirm antiforgery works in a NativeAOT artifact.
