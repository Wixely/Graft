# Commit diff evidence

Date: 2026-10-09. Owner: Implementation agent. Review when the diff engine, its normalisation passes, the commit route, or the version of Git used as the reference changes.

## Result

Graft can say what a commit changed. `RepositoryDiff` compares two trees on the object model rather than by invoking Git, so it holds wherever the rest of the engine does, and the commit page is served behind the `/-/commit/<sha>` URLs the API had been handing out with nothing behind them.

**The diff is measured against real Git rather than assumed to match it.** A diff that is merely valid is not enough: for any pair of files many correct edit scripts exist, and a reader comparing Graft against `git diff` would see them disagree. Two independent properties are asserted.

| Property | Where | What it catches |
| :--- | :--- | :--- |
| The same hunks Git produces | `tests/Graft.Git.Acceptance`, 302 assertions | Placement and numbering |
| Applying the diff to the parent rebuilds the file | same suite | Dropped, duplicated or wrong lines |

Neither alone is sufficient, which was established rather than assumed — see the mutation results below.

## Agreement with Git, measured

`tests/Graft.Diff.Fuzz` generates file pairs from a deliberately repetitive alphabet, because repeated and blank lines are what make a change's placement ambiguous, and compares every resulting diff with `git diff`. Over **1,761 cases on seeds not used while developing the engine**:

| Compared against | Agreement |
| :--- | ---: |
| `git diff -c diff.indentHeuristic=false` | **99.0%** |
| `git diff` at its default settings | **95.2%** |

The first row is the like-for-like comparison, because Graft does not implement Git's indent heuristic. The difference between the two rows is that heuristic and nothing else: running Git with it disabled reproduces Graft's output exactly on the cases that otherwise differ. It is queued as follow-on work with a concrete acceptance test.

## Why there are two normalisation passes

A minimal edit script does not by itself agree with Git. Where a change sits beside lines identical to the ones it touches, several equally minimal scripts exist. After the comparison, each group of changed lines is slid as far down its file as the content allows, and then pulled back up to sit against whatever the other file changes in the same place.

Both passes earn their place. Measured like-for-like over 586 cases on seeds 30 and 31:

| Configuration | Agreement |
| :--- | ---: |
| Neither pass | 89.4% |
| Sliding only | 90.4% |
| Aligning only | 94.2% |
| Both | **99.0%** |

Sliding alone is worth about a point and would have been easy to discard as noise. It is kept because the two compose: aligning works from the position sliding settles on, and the pair is far better than either.

## The part most likely to be wrong

**Moving a group part of the way is worse than not moving it.** The first version of the alignment pass slid a group as far up as the content allowed, however far short of its target that left it. Moving partway joins nothing and drags the group off the position sliding chose, so a deletion near the top of a file would tug an unrelated insertion several lines towards it. Making the move all-or-nothing, measured on the same seeds:

| | Like-for-like | Git default |
| :--- | ---: | ---: |
| Partial moves allowed | 95.4% | 92.9% |
| All or nothing | **99.3%** | **95.2%** |

This is the reason the fuzz harness exists rather than a fixed corpus. The partial-move version passed every pinned shape and looked like an improvement on the seeds it was written against.

**It also corrected a claim already committed.** A commit message had said the residual was Git choosing a different script of the same length. That was wrong; it is the indent heuristic. The harness is what showed the difference, by being pointed at Git with the heuristic turned off.

## Mutation results

Every assertion here was checked by breaking the thing it guards and confirming the suite fails, each with the mutation verified present in the source before the run.

| Mutation | Caught by |
| :--- | :--- |
| A line dropped from the edit script | Reconstruction |
| Empty-side hunk numbered from its first line | Git comparison |
| Context reduced from three lines to two | Git comparison |
| Newline-at-end removed from the comparison key | Git comparison |

Running each with the other check disabled showed they cover different failures: hunk numbering is invisible to reconstruction, because a hunk with no old lines rebuilds the same either way, and a dropped line is invisible to a comparison that was never run. An earlier attempt at this appeared to pass only because the mutation had never been applied — the script reported success without asserting the replacement had happened.

## Covered shapes

The pinned corpus is pushed as real repository content and read back through the browser endpoint, so the assertions run against the whole path rather than the renderer in isolation: a file added, two separate edits producing two hunks, a full rewrite, a deletion, a file added without a trailing newline, that file later gaining one, a mode-only change, a binary change, and an initial commit compared against the empty tree. A second branch carries the ambiguous-placement shape taken from a real repository, held to the same standard.

The route is asserted separately: a served commit names the same files Git reports, a binary file arrives as a flag rather than bytes, a root commit reads as every file added rather than as an error, and an unknown id, a malformed id, an object that is not a commit, and an out-of-range parent are each refused.

## Boundaries

- **Rename and copy detection are not implemented.** Git is run with `--no-renames` on both sides of every comparison, so the agreement figures say nothing about repositories where files move.
- **Merges are compared against one parent.** The caller chooses which; there is no combined diff.
- A file over 1 MiB, or with more than 50,000 lines, is reported as changed without hunks. A commit touching more than 300 files is truncated and says so.
- Binary detection follows Git's rule of a NUL byte early in the file; such a file is reported as binary and carries no hunks.
- The agreement figures are against the Git on this machine. A different version could normalise differently, which is why the harness is committed rather than only its results.
- Diff text reaches the page as text and never as markup, so this route does not reopen the question the README renderer answers.

Recommended next action: Owner: Implementation agent — implement Git's indent heuristic (`measure_split` and `score_add_split` in `xdiff/xdiffi.c`), which is the whole of the gap between the two agreement figures above, and is a self-contained scoring function rather than a rewrite.
