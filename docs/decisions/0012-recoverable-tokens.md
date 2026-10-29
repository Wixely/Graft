# 0012: Recoverable token secrets, as an operator's choice

- Date: 2026-10-21
- Status: Accepted user request, implemented, off by default
- Source: "i've lost a token and have to make new one, i want to copy an existing one" and "why dont we have recoverable secrets as a config setting. not set, then the issue is never a problem"
- Review trigger: Any proposal to default this on, to let an administrator read another account's tokens, to move the key ring away from the state directory, or to drop the password confirmation

## What was asked, and what it reverses

A personal access token's secret existed once. The database held `SHA256(secret)` and nothing else, so a
lost token meant creating a new one and updating wherever the old one was deployed. That is the standard
design and it is what makes a stolen database not a stolen set of credentials.

The request is to make the secret readable afterwards, as a setting that is off unless an operator asks.
That framing is right, and it matches the rest of Graft: public browsing, push-to-create, the agent
endpoint and project sites are all switches that are off until somebody turns them on.

So `--recoverable-tokens true` keeps each new token's secret, encrypted, and lets its owner read it back.
Unset, which is the default, nothing changes and the guarantee above still holds exactly.

## Decision: a property of each token, not of the server

The switch decides what happens to tokens issued **while it is on**. A token issued with it off can never
be read back, whatever the setting becomes later, because there is nothing to read: the secret was never
kept. Turning the switch off does not erase what was already stored either.

So recoverability is reported per token, and the browser offers Copy only where the secret was actually
kept. A single server-wide flag in the interface would promise something untrue about half the list.

## Decision: the account's password, every time

Reading an existing secret requires the account password in addition to the session.

This is the decision that makes the feature defensible. Without it, anyone who obtained a signed-in browser
could harvest every token the account holds, silently and in seconds. Today the worst a stolen session can
do is create new tokens, which appear in the owner's list and in the audit log. Recoverable secrets must
not quietly convert session theft into credential theft, so the password is asked for on every read, and
the route shares the sign-in throttle because it now takes a password and would otherwise be a quieter
place to guess one.

Every read is audited as `reveal-token`. The audit records that a secret was read, never the secret.

## Decision: encrypted by the server, with the key ring it already has

The store never sees a recoverable secret in the clear and holds no key. It takes a function from the
server, applies it to the secret it generated, and persists the result in `SecretCipher`. Decryption is the
server's too. That keeps `Graft.Storage` free of ASP.NET and means the encryption boundary is one place.

The key ring is the data-protection one that already exists for antiforgery, living beside the database in
the state directory.

**That bounds what this protects against, and the bound should be stated rather than implied.** It defends
a database that leaks on its own: a copied `.db` file, a backup that went somewhere it should not, a dump
pasted into an issue. It does **not** defend against somebody who takes the whole state directory or the
machine, because the key is there too. An operator who wants more should keep the key ring somewhere the
database's backups do not reach, which Graft does not currently arrange for them.

A cipher this server cannot decrypt, because the key ring was lost or replaced, reads as a token whose
secret was not kept. The token still works; it simply cannot be read again.

## What this costs, stated plainly

With the switch on, the database plus the key ring is equivalent to every token on the instance. That is a
real and permanent change in what a leak means, and it is why the default is off and why this record
exists. An operator turning it on should be choosing convenience over that property knowingly.

An administrator cannot read another account's tokens. The route only ever reads tokens belonging to the
caller, and the password it checks is the caller's own.

## What was verified

The Git suite went from 481 to 483, asserting that on a server without the switch a token reports itself
unrecoverable and the read route refuses. Against a server with it on: a token's secret read back after
creation is byte-for-byte the one issued, it still authenticates, a wrong password is refused with `403`,
the stored column is ciphertext rather than the token, and each read leaves an audit row.

See [the evidence](../validation/2026-10-21-recoverable-tokens.md).

## Note added 2026-10-29

Rotation, offered above as the alternative, now exists alongside this: [ADR 0030](0030-token-rotation.md). A
rotated token is recoverable afterwards exactly when the operator asks for recoverable secrets at the time of
the rotation, so rotation is also how an existing token becomes recoverable after the switch is turned on.
