# 0038: Runner transfers are chunked above a threshold, and jobs keep artifacts

- Date: 2026-10-31
- Status: Accepted, implemented
- Source: The user, on 2026-10-31: "all transfers (including artifacts) should use a chunked approach like DNAX. single file transfers above 80mb shoiuldbe chunked", then "optional, configurable"
- Review trigger: a file between forge and runner larger than 4 GiB; a runner that cannot hold a 256 MiB whole transfer in memory; a request for artifact expiry, `artifacts:when`, `reports`, `name` or `exclude`, or for passing artifacts to later stages; GitLab's runner API, whose artifacts upload is a single HTTP request; CupriNet adding resumable relics or acknowledged conduits

## What was missing

[ADR 0037](0037-runners-over-cuprinet.md) sent a job's source to the runner as one CupriNet relic built in
memory and capped at 64 MiB, and a dropped connection lost the whole transfer. Jobs could not keep artifacts at
all; a pipeline file that asked for them failed.

## Decision: whole up to a threshold, verified chunks above it, as DnaX does

Files between the forge and its runners move in one of two ways, chosen by size against the operator's threshold.

- **Whole**, at or below the threshold. The file is checked against its SHA-256 on arrival, and an interrupted
  transfer starts again from nothing. A whole transfer is at most 256 MiB, because a whole download is one relic
  held in memory and CupriNet bounds a relic there.
- **Chunked**, above it. The file is cut into chunks, 8 MiB by default. Each chunk carries its own SHA-256, and
  the whole file's SHA-256 is checked at the end. The receiver keeps what it has verified durably and says where
  it got to, and the sender resumes from there, so an interruption costs at most the chunk in flight. That is
  DnaX's chunked mode, where the server's committed offset is authoritative.

The threshold is 80 MiB unless the operator sets `--runner-chunk-threshold <MiB>`. `0` chunks everything and
`off` chunks nothing, in which case a file past the whole limit is refused by name. `--runner-chunk-size <MiB>`
sets the chunk, from 1 to 64. The forge prints the policy at startup and decides for every transfer; the runner
follows what each transfer says. Any file is limited to 4 GiB, the forge's repository bound.

Underneath, everything still travels in pieces of at most 160 KiB, under the 192 KiB a Shrine message may carry.
Pieces are the transport's business; chunks are the unit of verification and resumption.

DnaX itself is not used here. It is a browser upload library over HTTP, and the runner speaks CupriNet; what is
shared is the model, not the code. The rule that API clients upload over plain HTTP is untouched, and Git, LFS,
package and registry wire formats are unchanged, because their clients define them.

## Decision: how each direction works

**Source, forge to runner.** The forge writes the commit's tar.gz to a staging directory beside the database
and hashes it. A whole source is offered as one relic. A chunked one is offered as a relic per chunk, named
`<name>.00000` onward, each read from disk only when a runner asks for it, with at most four in memory. The job
handed to the runner carries a descriptor: name, size, SHA-256, and for chunks their size and hashes. The runner
writes each verified chunk to disk and then records how many it has, so a retry, or a runner restarted, carries
on from the first chunk it lacks. Staged sources are deleted when the job finishes and cleared at startup.

**Artifacts, runner to forge.** An upload starts with the archive's size and SHA-256. Pieces are sent at the
offset the forge expects; a retried piece already written is accepted, and a piece at the wrong offset is
refused with where the upload stands. In chunked mode a piece may not cross into the next chunk until the chunk
is committed. A commit names the chunk and its SHA-256. The forge flushes the data to disk and only then
rewrites a journal beside it recording the committed offset, so a crash never leaves the journal claiming bytes
that were not kept. A chunk that does not match is discarded and sent again. Starting the same upload again,
same job, size and hash, is how a runner resumes: from the last committed chunk when chunked, from zero when
whole. The forge checks the whole file's SHA-256 before keeping it. Journals survive a forge restart; one
untouched for a day belongs to a job that is over and is swept at startup.

The runner retries any step that failed because the connection did, on a new CupriNet visit, and gives up after
eight failures in a row without progress. Reporting a job's result is retried the same way.

## Decision: artifacts in a pipeline file

A job may now say `artifacts: paths:`, up to 50 patterns relative to the checkout. `*` and `?` match within one
name, `**` matches across directories, and a directory stands for everything below it, as on GitLab. Patterns
may not start with `/` or contain `..`, and links are not followed. A pattern that matches nothing is a warning
in the log, not a failure, as on GitLab. Artifacts are collected only from a job that succeeded, GitLab's
default `when: on_success`; any other key under `artifacts` fails the pipeline naming it.

The runner zips what matched and uploads it before reporting the result. The forge keeps it beside the project's
repository as `artifacts/<job id>.zip`, with its size and SHA-256 in the database. A failed upload fails the job.
The job's API shape gains GitLab's `artifacts_file` with `filename` and `size`. GitLab's
`GET /projects/:id/jobs/:job_id/artifacts` serves the archive to whoever may browse the repository, with the
SHA-256 as its entity tag and byte ranges honoured, so a large download can be resumed. The Pipelines page links it.

## What is not done

Artifact expiry and deletion; `artifacts:name`, `when`, `expire_in`, `exclude`, `untracked`, `public` and
`reports`; passing artifacts to later stages or `dependencies`; browsing or fetching a single file inside an
archive; the by-ref artifact routes. A deleted project's directory takes its artifacts with it, but deleting a
pipeline is not built. Resumption across a forge restart is designed in through the journal, but the suite drops
the runner's connection, not the forge.

## What was verified

The acceptance suite drives the real runner against the real forge.

- With the defaults, a small archive goes whole. It holds exactly the file the job wrote under the directory it
  named; its entity tag is its SHA-256; a stranger and a job without artifacts get 404; a failed job keeps none;
  an unmatched pattern is warned about.
- With `--runner-chunk-threshold 1 --runner-chunk-size 1`, a 3.5 MB incompressible file is committed. The source
  goes in four chunks and an archive of the file matched by `data/**/*.bin` comes back in four. The runner is
  told by `GRAFT_RUNNER_FAULT_DROP_EVERY=3` to drop its connection before every third relic fetched or chunk
  committed. The log shows the download resuming at the chunk it lacked and the upload resuming at the committed
  2 MiB. The downloaded archive matches its SHA-256 and holds the file byte for byte, and a byte range comes back
  as 206 with the right bytes.
- With `off`, the forge says it transfers whole only.
