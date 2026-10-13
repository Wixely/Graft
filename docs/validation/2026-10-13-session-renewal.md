# Sessions that survive being used

Recorded: 2026-10-13. Owner: Implementation agent. Reported by the user as being logged out constantly
after very little time or idling.

## What was wrong

A browser session expired **fifteen minutes after sign-in**, and nothing ever moved that expiry. It was not
an idle timeout. Using Graft continuously made no difference: fifteen minutes after signing in, the session
was gone mid-sentence.

Measured on a local server before the change:

```
stored window: 15.0 minutes
Set-Cookie: __Host-graft-session=<redacted>; expires=...; path=/; secure; samesite=strict; httponly
```

Then ten authenticated requests over ten seconds, and the stored expiry afterwards: **unchanged**, to the
tick. `ResolveSessionAsync` only ever asked whether the expiry was in the future; it never pushed it out.

Fifteen minutes was a bare magic number in `LoginAsync` with no comment, in a codebase where every other
decision carries its reasoning. No document stated it and no assertion pinned it, so it reads as an
unexamined default rather than a considered one.

## What it is now

A session is an **idle window**, renewed by use, with an absolute ceiling.

| | |
| --- | --- |
| Idle window | 12 hours, set by `--session-minutes` (5 to 43200) |
| Renewed when | more than half the window has elapsed |
| Absolute ceiling | 30 days from sign-in, fixed |

Renewal happens only past the halfway point because resolving a session happens on nearly every request.
Extending on each one would mean a database write per request against a store with a single writer. This
bounds it to one write per half window per session, and nothing a user could notice changes.

The ceiling means a cookie that has been copied cannot be kept alive indefinitely by using it. Eventually
its holder has to present a password again.

## The mistake worth recording

The first version of this fix renewed the stored expiry and **never sent the browser a refreshed cookie**,
so it did not work at all. The cookie carries the same expiry the server stores, and the browser discards it
at that moment, so a stored expiry that creeps forward while the cookie's stays put just produces an
anonymous request against a session the server still considers good.

The cause was subtler than a missing line. `app.UseAuthentication()` runs the session handler on **every**
request, long before any route, and it resolved the session through the same method. It therefore consumed
the renewal, and by the time the route's own `AuthenticateBrowser` ran, the session no longer looked due for
renewal, so no cookie was ever written. The database expiry moved and the browser never heard about it.

`ResolveSessionAsync` is therefore now a pure read again, which is what the authentication handler needs,
and renewal lives only in `ResolveSessionDetailedAsync`, used by the one caller that can also refresh the
cookie. Reintroducing the one-word change makes the acceptance suite fail on
`a renewed session arrives with a refreshed cookie`, which is how that assertion was confirmed to test
something.

## Evidence

| Suite | Before | After |
| --- | --- | --- |
| Git, API and browser | 365 | 372 |
| Accounts and permissions | 113 | 129 |
| Operations | 41 | 41 |

The store suite covers the timing rules by ageing the row rather than waiting: a fresh session is not
renewed and keeps its expiry; a session past halfway is renewed to a full window and the renewal is
persisted rather than merely reported; the read-only resolve authenticates without renewing; renewal never
reaches past the absolute ceiling; and an expired session cannot be renewed back to life.

The HTTP suite covers the half the store cannot see: a fresh session is not rewritten on every request, an
aged one comes back with a refreshed `Set-Cookie` that keeps `Secure` and `HttpOnly`, the browser's own copy
of the cookie then outlives the minute it had left, and the session still works afterwards.

## What this does not change

Nothing about how sessions are revoked. Logout, a password change, a reset and disabling an account all
still revoke immediately, and renewal cannot resurrect a revoked session because the renewal query carries
the same `RevokedAtUtc IS NULL` condition as the lookup.

Nothing about personal access tokens, which have their own expiry chosen at creation and are not renewed by
use.

NativeAOT is unverified here for the usual environmental reason: no C++ build tools, so ILC never runs.
