# 0034: A second factor from an authenticator app

- Date: 2026-10-30
- Status: Accepted, implemented
- Source: The third and last slice of the user's email item of 2026-10-30, whose condition was that nothing depend on email. The backlog had listed "no second factor" under email since 2026-10-22; this record is why it never belonged there
- Review trigger: A request for hardware keys (WebAuthn), for an administrator to require the factor of everyone, for codes by SMS or email, or for GitLab's `/users/:id/disable_two_factor` route

## What was missing

An account was a username and a password. A guessed or leaked password was the account.

## Decision: the factor is the app, and nothing else

The second factor is a time-based one-time code (RFC 6238 over RFC 4226: HMAC-SHA1, thirty-second steps,
six digits), which is what every authenticator app produces when a `otpauth://` address names no other
parameters. It needs no email, no phone number and no third party; the server and the app share a secret and
a clock. Codes by email or SMS are not offered: the first would make the factor depend on the thing this
item made optional, and the second on a carrier. The implementation is in `Graft.Core`, forty lines on the
platform's HMAC, with a Base32 encoder beside it, and the acceptance suite checks it against RFC 6238's own
test vectors before it types a single code.

## Decision: on in two steps, off with two proofs

Enrolment issues a secret pending and turns the factor on only when a code from the app is accepted, because
a secret the app never received would lock the account at the next sign-in. Turning it on hands out ten
recovery codes, shown once and kept only as digests, each good for one sign-in on the day the app is gone.
Turning it off needs the password and a current code or a recovery code, so a stolen session cannot do it; an
account with the factor on cannot enrol again without turning it off first, so a stolen session cannot swap
the app either.

For the person who lost both the app and the codes, an administrator removes the factor on the users page,
audited, and the password alone signs in until the person enrols again. An administrator does not remove
their own this way; their path is the password and a code, as for everyone.

## Decision: signing in is two answers

With the factor on, the password earns a challenge and not a session: `401` with `error: second-factor` and
a handle, held in memory for five minutes and spent by one attempt right or wrong, so a wrong code costs the
password step again. The handle and the code go to `/api/login/second-factor`, which answers with the session
the password route would have. A code is accepted once per thirty-second step, so one seen on the wire is
not good again in its window; the step either side of now is accepted, for a phone's clock. Both routes share
the sign-in throttle.

Personal access tokens and SSH keys are unaffected, as GitLab's are: each is a credential issued from inside a
session that already passed both factors, and the API has no password step to add a second to. The session
says whether the factor is on; `GET /api/v4/user` says so as `two_factor_enabled`; the administrator's user
list says so per account.

## What is not done

WebAuthn and hardware keys. Requiring the factor of every account or of administrators. GitLab's
`/users/:id/disable_two_factor` route; the browser family has `DELETE /api/users/:id/second-factor`. A QR
code: the enrolment page shows the key and the `otpauth://` address as text for an app that takes either,
and drawing the picture needs a QR encoder this repository does not have. The agent surface does not touch
the factor, for the reason [ADR 0011](0011-agent-parity.md) withholds account administration.

## What was verified

By the Git acceptance suite: the generator agrees with RFC 6238's vectors and the Base32 round-trips;
confirming before beginning is refused; an enrolment begins with a key and an address carrying it; a pending
enrolment changes nothing about signing in; a wrong code does not confirm, the app's code does, ten recovery
codes are shown, the session says the factor is on, and a second enrolment is refused; the password alone
earns a challenge; a wrong code is refused and spends the challenge; a fresh challenge and the current code
sign in; the same code is refused in its window; a recovery code signs in once and not twice; one typed
without its dash and in capitals is read as meant; a token is still the API's credential; turning off needs
the password and works with a recovery code; a new enrolment confirms; the administrator's list shows the
factor; an ordinary account cannot remove one; an administrator cannot remove their own this way and does
remove another's, after which the password alone signs in. The browser pages compile against these routes
and were not driven by a browser.
