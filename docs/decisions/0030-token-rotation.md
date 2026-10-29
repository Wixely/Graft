# 0030: A token is rotated in place

- Date: 2026-10-29
- Status: Accepted, implemented
- Source: The backlog's "Near, and small" list, which recorded token rotation on 2026-10-22 as the thing that helps on a server that keeps no secrets, after [ADR 0012](0012-recoverable-tokens.md) chose recoverable secrets instead and said both could exist; taken on without a new user decision because the backlog is the user's list
- Review trigger: A request for reuse detection, for an administrator to rotate another account's token, for `last_used_at`, or for the rest of GitLab's `personal_access_tokens` family

## What was missing

A token's secret, once lost, could be produced again only where the operator had asked for recoverable
secrets before it was issued, which is off by default. Everywhere else the holder revoked the token and
created another, and everything configured with the old token's name had to be found and changed to the
new one, because a new token is a new id and a new row even when it has the same name.

## Decision: the row keeps its identity and gets a new secret

Rotation replaces the secret's digest, and its cipher where one is kept, on the existing row. The id,
name, scopes, creation time and expiry stay. Whatever is configured with the token's name still names the
same token afterwards, the list shows one token where there was one, and the audit trail records
`rotate-token` against the id it has always had. The old secret stops resolving the moment the
transaction commits, because there is one digest per row.

GitLab revokes the old token and creates a new one with a new id and, by default, an expiry one week
away. Graft does not, because the point of the feature here is that nothing but the secret changes. A
caller who wants a different expiry says so with `expires_at`, within the year a new token may have.

The cipher follows the same rule as creation: it is kept only where the operator asks for recoverable
secrets today. A token issued while they were off becomes recoverable if they are on at rotation, and one
issued while they were on stops being recoverable if they have since been turned off, because the secret
being stored is the new one and the setting that governs it is today's. Rotation is therefore also how an
operator who has just turned recoverable secrets on makes an existing token recoverable.

## Decision: the browser collects the new secret as it collects a new token's

The browser route answers the one-time claim [ADR 0012](0012-recoverable-tokens.md) introduced for
creation, so the secret still never enters the page and is still copied straight to the clipboard. The
button needs a second click, because the old secret stops working at once and whatever uses the token
breaks until the new secret reaches it.

## Decision: the GitLab-shaped route puts the secret in the response

`POST /api/v4/personal_access_tokens/self/rotate` and `.../:id/rotate` answer GitLab's token shape with
the new secret in `token`. This is the one route where a secret travels in a response body, and it does so
to a caller who already held the previous one and has no clipboard to fetch it into. The `api` scope is
required, as GitLab requires it; a `read_api` token cannot rotate anything, including itself. Only the owner
may rotate. An administrator rotating another account's token would be holding a credential for that
account, which is a different feature with its own record.

## What is not done

Reuse detection: GitLab revokes the whole family when a rotated-out secret is presented again, which needs
the old digests kept. Graft forgets the old secret. `last_used_at` is null because Graft does not record
use. There is no `GET` or `DELETE` on the `personal_access_tokens` family; the browser family has those.
The agent surface does not rotate tokens, because [ADR 0011](0011-agent-parity.md) withholds token issue
from it and a rotation issues a secret.

## What was verified

Through the suite. From a browser session: wrong CSRF refused; a token that is not the caller's not found;
rotation keeps the id, name and scopes and returns a claim, not a secret; the claimed secret is new; the
previous secret fails `git ls-remote` and the new one passes; the list shows one token of that name. Over
the GitLab-shaped route: a `read_repository` token is refused with `403` and no token with `401`; a token
rotates itself, keeps its identity, and the response carries the new secret and the kept expiry; the old
secret is refused by the API at once and the new one accepted; a non-date and a date beyond a year are
refused and leave the current secret working; an `api` token rotates another of its owner's tokens by id
and moves its expiry; a token that is not its owner's is not found. The browser button compiles against the
same route; it was not driven by a browser.
