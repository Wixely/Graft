# 0055: A backup keeps the server's keys

- Date: 2026-11-16
- Status: Accepted, implemented
- Source: The documentation review of 2026-11-16 found that `graft-admin backup` copied only the database and the
  repositories, while the deployment guide said the SSH host key was in "the directory your backups already
  cover". The project owner asked for the backup to be fixed.
- Builds on: MVP-07's offline backup and restore ([operations](../operations.md)); [ADR 0031](0031-git-over-ssh.md)
  (the SSH host key), [ADR 0037](0037-runners-over-cuprinet.md) (the runner Shrine key) and
  [ADR 0052](0052-release-destinations.md) (release-destination secrets under the key ring)
- Review trigger: a new file or directory the server keeps beside the database that a restore needs; encrypting
  the key ring at rest; online backup

## What was missing

The server keeps three things beside its database that a restore cannot do without:
- **The key ring (`data-protection-keys/`):** recoverable tokens, release destinations' endpoints and keys, and
  the runner Shrine's key are all protected by it.
- **The runner Shrine's key (`runner-shrine/`):** runners pin it when they register.
- **The SSH host key (`ssh_host_ecdsa_key`):** clients remember it.

A backup held none of them. A restore into fresh paths, which is the only restore there is, then started a server
with a new key ring, a new Shrine and a new host key. Recoverable tokens and release destinations could not be read,
every runner had to be installed again, and every SSH client was told the host had changed.

## Decision: format 2 has a `state/` directory holding exactly those three

`BackupManager.StateItems` names them, and a backup copies whichever of them exist beside the database into
`state/`, hashed in the manifest like every other file. Nothing else beside the database is kept: uploads and
transfers in progress are transient, and keeping "everything in the directory" would also keep whatever an operator
happened to put there. Verification refuses a `state/` holding anything but those names. A backup therefore cannot
carry an arbitrary file into the database's directory on restore.

Restore puts them beside the restored database, where the server looks. It refuses before publishing anything
if any of them is already there, which keeps the rule that a restore never overwrites. On Linux the directories
are created for the owner alone, as the server creates them.

A format 1 backup has no `state/` and still verifies and restores. `graft-admin` prints which state a backup kept,
or that a format 1 backup kept none.

## The cost: a backup now holds keys

The key ring is written unencrypted: `PersistKeysToFileSystem` with no `ProtectKeysWith…`, which is what makes it
portable between machines. A backup is therefore enough to read every recoverable token and release-destination
secret, where before it held only digests. The operations guide says so beside the backup instructions. Encrypting
the key ring at rest would tie it to a machine or a certificate, and is a decision of its own.

## What was verified

The operations suite (`RESULT: PASS; assertions: 47`, Debug build on Windows):
- a backup of a server's data holds its key ring under `state/`;
- a planted file in `state/` fails verification;
- a format 1 backup made from it verifies and restores without state;
- a restore beside an existing key ring is refused and publishes nothing;
- a restore puts back the same key files with no staging left behind;
- the restored server then serves a clone that passes `git fsck --strict`.

Not verified: a backup of a server with SSH and runners on, which this suite does not start (the copy is the same
code path for a file and a directory); the Linux file modes; restoring onto another machine and reading a
recoverable token there.
