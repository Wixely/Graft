# 0032: Email is optional, and everything it enables has a path without it

- Date: 2026-10-30
- Status: Accepted, implemented in its first slice (addresses, the mail sender, password reset by link). Notifications and a second factor follow as their own records
- Source: The user, on 2026-10-30: "Email, and what depends on it. This needs to be optional. A non-email self-hosted option should exist (switch in config)." The backlog had carried email since 2026-10-22 as a dependency decision as much as a feature
- Review trigger: A request for notifications or a second factor, which this record scopes out; a mail server that needs a mechanism other than PLAIN or LOGIN, or implicit TLS on a port other than 465; a request for secondary addresses or GitLab's `/user/emails` family; evidence that the in-process SMTP client should be replaced by a package

## What was missing

Graft held no addresses and sent nothing. A forgotten password was therefore an administrator typing a
temporary password and passing it on, which means the administrator knew it; there were no notifications and
no second factor. Every other forge assumes a mail server. The user's instruction is that Graft must not: a
self-hosted forge on a home network or an air-gapped one has no mail server to give it, and must still work.

## Decision: the switch is naming a mail server, and the default is none

`--mail-smtp <url>` turns mail on; without it the forge sends nothing, says `MAIL: off` at startup, and tells
the session so, which is how the browser knows which story to tell. The URL names the transport as well as
the server: `smtp://host[:port]` sends in clear and is for a relay on the same machine or network;
`smtp+starttls://host[:port]` insists on upgrading to TLS before anything else is said; `smtps://host[:port]`
connects with TLS from the first byte. `--mail-from <address>` is required with it; `--mail-password-file
<path>` carries a password the way the certificate's does, out of the process listing, and the username rides
in the URL. There is no mode in which a password is passed on the command line.

The SMTP client is written in the server, in about a hundred lines: EHLO, STARTTLS, AUTH PLAIN or LOGIN,
one message, QUIT. The in-box client has not been recommended for years and does not do implicit TLS; the
recommended package would be a dependency for four commands. Messages are plain text, UTF-8, Base64-encoded
in the body, so no server's line-length or 8-bit rules can touch them. Delivery is queued and sent by a
worker with three attempts, as webhooks are; a message that cannot be delivered is logged and dropped. It is
a notification mechanism, not a mail server: there is no store and no bounce handling.

## Decision: an address is optional and unique

An account may have one address, set by its owner on the profile or by an administrator on the users page,
and cleared the same way. It is unique across the instance without regard to case, because the one thing the
forge does with it is identify the account that asked for a reset. The shape check is deliberately plain: one
`@` with something either side and no whitespace, control characters or the delimiters of an address list.
Anything stricter refuses real addresses, and the only proof of an address is a message that arrives.
`GET /api/v4/user` reports it as `email`, as GitLab does; the browser's session and user list carry it.

## Decision: a password reset is a link, from one of two hands

A reset is a one-time secret with an expiry, redeemed at `/-/reset/<secret>` by choosing a new password.
Redeeming it revokes every session and token the account had, as an administrator's reset does, because a
reset is what you do when you no longer know who holds what. The link works once and never says why it did
not: used, expired, unknown and disabled account are one refusal.

**Without mail**, an administrator makes the link on the users page and hands it over by whatever channel
they share. It lives for a day. This is the non-email path, and it is better than the temporary password it
replaces: the administrator never knows the password the person chooses. An administrator cannot make one
for their own account, for the reason they cannot reset their own password: that path is the current
password.

**With mail**, the sign-in page offers "Forgot your password?", which takes a username or address and
answers `202` whoever was named, whether they have an address, and whether the server can send, because any
other answer would say who exists. Where the login names an active account with an address, a link lives for
an hour and goes to that address and nowhere else. Both requests share the sign-in throttle.

The temporary-password reset stays, for an administrator who prefers it or a person with no channel at all.

## What is not done

Notifications, in any form; a second factor; secondary addresses and GitLab's `/user/emails` routes;
address verification; an administrator testing the mail settings from the browser. The agent surface does
not set addresses or make reset links, for the reason [ADR 0011](0011-agent-parity.md) withholds account
administration. The browser pages compile against the routes the suite drove and were not driven by a browser.

## What was verified

By the Git acceptance suite, against a server with no mail server and then against an SMTP server of the
suite's own. Without mail: startup says so, the session says so, an administrator cannot make a link for
themself, an ordinary account cannot make one at all, a link is made for another account and points at this
server's reset page, a password the policy refuses leaves the link intact, an unknown secret is refused, the
link sets a password, the old password is gone and the new one signs in, the link does not work twice, the
account was signed out everywhere; a non-address is refused, an account sets its own address, the same
address cannot go on a second account whatever its case, an administrator sets one, the session and the
list show it, and a request for a link is accepted and nothing more said. With mail: startup names the
server, the session says mail is available, a request naming nobody and one naming the address are answered
alike, exactly one message arrives at the address on the account, it is the reset message addressed by name,
and the link it carries sets a password the account then signs in with.
