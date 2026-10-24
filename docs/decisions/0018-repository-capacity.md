# 0018: Four GiB repositories

- Date: 2026-10-24
- Status: Accepted user request; implemented with Windows validation
- Source: User request to allow 4 GB repositories and scale the implementation accordingly
- Review trigger: Larger repositories or individual files, configurable quotas, pack parser changes, or deployment to a different resource envelope

The repository quota is 4 GiB (4,294,967,296 bytes) of distinct uncompressed Git object
payloads, including history and stored unreachable objects. It is not the working-tree size
or the compressed size reported by Git. This interpretation includes decimal 4 GB.

The feasibility budgets in `Graft.Git.Limits` are superseded:

| Bound | Value |
| --- | --- |
| Repository object data | 4 GiB |
| Individual object/file version | 64 MiB |
| Git push request / generated pack | 5 GiB |
| Pack expansion work, including delta instructions and results | 8 GiB |
| Objects per repository / incoming pack | 1,000,000 |
| Branches and tags combined | 4,096 |
| Delta chain depth | 32, unchanged |
| Git transfer / archive request deadline | 2 hours |

The transport allowance accounts for incompressible data and framing. The larger delta
work budget does not increase the durable repository quota. Ordinary API bodies remain
limited to 16 MiB, as do fetch negotiation bodies; browser upload chunk policy and avatar limits remain separate.
These are compiled defaults, not new operator settings.

Whole packs cannot live in a byte array or `MemoryStream` at this size. HTTP Git bodies
and replies are spooled to private temporary files; pack offsets, lengths and accounting
are 64-bit. The existing small byte-array entry points remain for the probe and tests.
Pack checksums are verified before inflation, and graph/quota checks precede persistence.

Durable repositories retain object metadata and file locations, loading content on demand.
Incoming pack entries and delta results spill to temporary files. Publication preserves
the existing object format and ref-snapshot procedure, moving staged objects where possible
and copying across volumes when required. Existing repositories need no migration.

ZIP and tar.gz archives also build on disk, then stream to the client. This supersedes
the in-memory archive choice in ADR 0017. Archive trees are limited to 4 GiB of expanded
file content, counting each path separately; repeating a blob at many paths must not
produce an unbounded archive.

Memory still depends on the number of objects and concurrent operations, and delta
application materializes individual objects. This is not a fixed-memory or unlimited
concurrency guarantee. Temporary storage must have room for the incoming pack plus up
to 8 GiB of expanded work per push, in addition to existing repositories; separate
volumes may also require a copy during publication. Normal completion/failure removes
temporary work. Abrupt process termination may leave `graft-transfer-*` directories
in the service account's temporary directory for offline operator cleanup.

The required Linux static-musl/NativeAOT configuration is unchanged. Windows runtime
tests do not establish large-repository performance on the legacy host. See
[validation](../validation/2026-10-24-repository-capacity.md).

Remaining work: validate the deployment's disk, concurrency and reverse-proxy envelope.
Next action — Operator + implementation agent: rebuild the server, exercise a representative
repository in the intended acceptance environment, and deploy through the established process.
