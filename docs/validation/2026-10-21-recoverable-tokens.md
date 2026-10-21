# Recoverable token secrets

Recorded: 2026-10-21. Owner: Implementation agent. Decision: [ADR 0012](../decisions/0012-recoverable-tokens.md).

## What changed

Two separate things, in response to two different problems.

**Copying a newly created token no longer puts it in the page.** The creation response carries a single-use
handle rather than the secret; the secret waits in server memory for one read and five minutes, and the
Copy button claims it straight into the clipboard. A token nobody copies never reaches the browser.

**An existing token's secret can be read back, where an operator asked for that.** `--recoverable-tokens
true` keeps each new token's secret encrypted in `SecretCipher`, migration 7. Its owner reads it with
`POST /api/tokens/{id}/secret` and their account password. Off by default, in which case nothing is kept
and the old guarantee holds unchanged.

## Verified with the switch off, in the suite

The Git suite goes from 481 to 483. On a server started without the switch, which is every server the
suite runs:

- A created token reports `recoverable: false`.
- `POST /api/tokens/{id}/secret` answers `404`, because there is no secret to read.
- The creation response carries no `secret` field at all, only a `reveal` handle.
- That handle works once; a second claim on it answers `404`.
- The claim requires the CSRF header, like every other browser token operation.

## Verified with the switch on, against a running server

A server started with `--recoverable-tokens true` prints its posture:

```text
TOKENS: secrets kept and readable by their owner with a password
```

| Step | Result |
| --- | --- |
| Create a token, claim it at creation | a `graft_pat_` secret, 74 characters |
| List tokens | `"recoverable": true` |
| Read it back with the wrong password | `403` |
| Read it back with the right password | the identical secret |
| Use the recovered secret on `/api/v4/user` | `200` |
| Read it back a second time | `200`, repeatable unlike the creation handle |

Read directly from the database afterwards:

```text
audit: reveal-token — Token secret read by its owner.
audit: reveal-token — Token secret read by its owner.
token 1: cipher length 219, stored in clear: False
```

So the stored column is ciphertext rather than the token, and each read leaves an audit row that records
the act and not the secret.

## Verified in a browser

Created a token through the interface and inspected the document before and after copying. The panel
offers a Copy button and the secret appears nowhere in the page. With `clipboard.writeText` intercepted,
the value reaching the clipboard was 74 characters beginning `graft_pat_`, while `graft_pat_` matched
nothing in `document.documentElement.outerHTML` at any point.

## Boundaries worth stating

**The key ring sits beside the database.** Encryption defends a database that leaks on its own: a copied
file, a stray backup, a pasted dump. It does not defend against somebody taking the whole state directory,
because the key is in it. Graft does not currently help an operator put the key ring somewhere its backups
do not reach, which is the obvious next improvement if this feature is used seriously.

**Recoverability is per token, not per server.** A token issued while the switch was off can never be read
back, so the interface offers Copy only where the secret was actually kept.

**An administrator cannot read another account's tokens.** The route reads only the caller's own, and
checks the caller's own password.

## Not done

**No rotation.** Regenerating a token in place, keeping its name, scopes and expiry while issuing a new
secret, would help the case this started from on servers that keep no secrets. It was offered and the
recoverable-secrets route was chosen instead; both could exist.

**No browser check of the recovery path.** The create-and-copy flow was driven in a browser; reading an
existing secret was exercised over HTTP only. The password form itself is untested by hand.
