# 0005: Browser upload transport

- Date: 2026-10-04
- Status: Accepted user request, implemented
- Source: "move over all http uploads (that are done via webui) to use DNAX library for uploads (specially for blazor edge cases)" and, on the split, "blazor crcuit easily breaks on mobile uploads as android munimises the browser. for app based api calls wee do not need or want dna."
- Review trigger: A DnaX.Uploads version change, a second non-browser upload client, or any proposal to route API uploads through DnaX

## Decision

`DnaX.Uploads` is the **browser** upload transport. API and application clients post ordinary HTTP request bodies and do not use it.

Each upload feature is therefore one application-owned ingest with two transports in front of it. DnaX moves bytes and authorizes nothing: an upload id is a transport receipt, and the ingest re-authenticates the caller, re-authorizes the action, confirms the caller owns that upload, and confirms the upload was created under the profile that consumer expects.

## Why the browser needs a different transport

When a mobile browser opens the native file picker, Android can background and discard the page along with any upload state .NET was holding. A queue owned by the .NET runtime does not survive that. The DnaX module owns the queue in JavaScript, persists job metadata in IndexedDB, and resumes from the offset the server has already committed.

Graft's client is standalone Blazor WebAssembly rather than Blazor Server, so there is no circuit in the sense the request described. The failure is the same one and the remedy is the same; only the mechanism differs.

API clients have no equivalent failure mode, so routing them through a session-based transport would add machinery and a second authorization surface for nothing.

## Consequences and the choices they forced

**Route prefix.** Mounted at `/api/graft/uploads`, not the library default `/uploads`. GitLab owns `/uploads/...` and `/api/v4/projects/:id/uploads`, so the default would squat a path the compatibility work needs later. Unmatched paths under `/api/` already return an API error rather than the single-page shell, so nothing leaks HTML.

**An authentication scheme Graft otherwise does not have.** `MapDnaXUploads` applies `RequireAuthorization` and identifies an owner through the `ClaimTypes.NameIdentifier` claim. Graft resolves an opaque session cookie by hand everywhere else. `GraftSessionAuthenticationHandler` bridges the two, and only the upload route group opts in; every pre-existing route keeps its own origin and CSRF checks. The owner is the numeric account id, not the username, so ids survive a rename and a reused username can never inherit staged uploads. A disabled account resolves to no identity, which strands its in-flight uploads immediately.

**Data protection key material.** Antiforgery requires it, and Graft had none. Keys are persisted beside the database with restrictive permissions. They are **not** currently included in `BackupManager`, so a restore onto a fresh host invalidates outstanding antiforgery tokens; browsers recover by re-fetching capabilities. Including them is open work.

**Request bounds.** The Kestrel-wide body cap stays as the bound for every pre-existing endpoint. The upload prefix raises it per request instead, and DnaX narrows it again per route. The global 30-second deadline would kill any real transfer, so the upload prefix has its own longer, still-bounded deadline. `MaxConcurrentConnections` moved from 16 to 64 because an upload holds its connection for the whole transfer.

**The library component is not used.** `UploadPicker` requires `@rendermode="InteractiveServer"`, which Graft does not have and which is not validated under NativeAOT. The module is mounted from JavaScript instead, which is also what keeps the queue independent of the .NET runtime being alive. The client vendors `uploads.js` because the package is a Razor Class Library carrying an ASP.NET Core framework reference that cannot be referenced from a `browser-wasm` target; the server references the package for its endpoints.

**Completion is a hint, not proof.** `complete` means the bytes arrived durably. It does not mean the content was validated or that a record exists. Consumers reconcile against the authenticated session record and must be idempotent by upload id.

## Status of the dependency

`DnaX.Uploads` 10.0.0-alpha.7 is an experimental prerelease whose API and journal format are explicitly not stable. Graft is its first consumer. The prototype's own documentation records that 50–100 GiB transfers have not been run, that it is a single-process local-filesystem store, and that tus interoperability was evaluated but not implemented. Treat an upgrade as a breaking change until that stabilizes.

## What this decision does not cover

The API-side counterparts are not implemented. GitLab's avatar upload is multipart, and `POST /api/v4/projects/:id/uploads` does not exist here. Those remain explicit deviations in [compatibility.md](../compatibility.md) rather than claimed support.
