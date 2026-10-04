# Server-authored Git object evidence

Date: 2026-10-04. Owner: Implementation agent. Review when the Git object model, the engine limits, the tree ordering rule, or the path validation rules change.

## Result

Graft can now construct Git objects on the server. Until this slice every write path ingested a pack a client had already built; committing a file from the browser writes a blob, rewrites the trees along that path, builds a commit and advances the branch.

**The objects are byte-identical to Git's own.** This was checked, not assumed: the same content was committed with Git 2.53 using the same identity, message and a fixed timestamp, and the object ids compared.

| | Graft | Git 2.53 |
| --- | --- | --- |
| Initial commit | `52ecd2c9fc239449212956723f3a8f3a434a31de` | identical |
| Initial tree | `52a54943ef50c44a3cc1ea55c32f05476d5b1983` | identical |
| Follow-up commit | `1902889d625b664eb059152944127e86e08486dd` | identical |
| Follow-up tree | `3203ecd726d169b59110df7df4d4fcd3dea96356` | identical |
| Ordering fixture tree | `989afa6f4d9fbebf156ed6ea4d5215b37d5c76b3` | identical |

Comparing object ids compares every byte of every blob, tree and commit at once. These ids are pinned in `tests/Graft.Git.Acceptance`; regenerate them with real Git if the fixture content changes.

## The part most likely to be wrong

Git sorts tree entries by raw name bytes, but a directory sorts as though its name ended in a slash. The ordering fixture straddles that rule from both sides and includes non-ASCII, which must sort as UTF-8 bytes rather than as text: `Z`, `a-`, `a.txt`, `a/` (a directory), `a0`, `ab`, `é.txt`, `é/`. Git's own entry order was confirmed with `git cat-file -p`.

Deleting the slash rule makes the suite fail on the first golden assertion. That was verified by actually removing it and re-running, rather than trusting that the assertion would catch it. An earlier attempt at that check appeared to pass because a running server held the output DLL and the build had silently failed; the result is only meaningful with nothing holding the binaries.

Untouched subtrees are re-emitted by their existing id rather than rebuilt, so a commit only rewrites the directories on the path it changes. The follow-up commit matching Git exactly is what evidences that.

## End-to-end check

Two files were uploaded through Chromium, one placed in a new nested directory and one marked executable, committed, and the repository then cloned with real Git:

- `git fsck --strict --full` reported nothing. In particular no "tree not sorted".
- `webscript.sh` came back as mode `100755`; the executable bit survived.
- `docs/notes/webcommit.txt` existed with the right content, so the new nested directories were correct.
- The seeded fixture files kept their original blob ids, so nothing unrelated was rewritten.
- The commit was parented on the seeded commit and attributed to the signed-in account.

## Boundaries

- A single object is capped at `Limits.ObjectBytes`, which is 4 MiB. A larger blob would be an object no client could push back through the pack path, leaving a repository inconsistent with its own transport. Raising that cap is a Git-engine decision with its own limit matrix, not a side effect of an upload feature.
- Adds and replaces only. There is no delete, no rename, no history rewriting, and no merge.
- Attribution uses the account name with the reserved `.invalid` domain, because Graft accounts carry no email address. Commits are not signed.
- Path validation refuses control characters and backslashes in addition to Git's own rules. Those are legal to Git on Linux but become a different path, or an invalid one, when a Windows client checks the repository out. This is a deliberate deviation from Git's acceptance set.
- The engine's repository-wide ceilings are unchanged: 4096 objects, 32 MiB expanded, 128 refs.

Recommended next action: Owner: Implementation agent — decide whether file deletion and rename belong in the same authoring surface before the commit API is treated as stable, since adding them later changes the request shape.
