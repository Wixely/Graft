# 0016: Webhooks, and what turning them on means

- Date: 2026-10-22
- Status: Accepted user request, implemented, off by default
- Source: "close these out please", where notification was the third of five gaps in the review journey
- Review trigger: Any proposal to default this on, to make a delivery durable, to let the forge follow redirects, or to send anything beyond the shape recorded here

## The problem

Nothing in Graft told anybody that anything had happened. A merge request existed only for somebody who
went looking at that project's list. For a team that is an inconvenience; for a build server it is the
difference between the forge being part of a workflow and being a place code is kept.

## Decision: the operator owns this, and it is off by default

`--webhooks true` is required before a hook can be registered at all. Without it the routes are absent
rather than refusing, because there is nothing for a hook to do on a server that will not send one.

**Turning it on lets a project Maintainer cause this server to issue an HTTP request to any address it can
reach.** That is the honest statement of what the switch does, and it is why the switch exists. On a
self-hosted forge the address a hook wants is usually an internal build server, so refusing private
addresses would refuse the main use; the control is therefore the operator's decision to allow outbound
requests at all, rather than a list of addresses the server second-guesses.

What is refused is narrower and absolute: a hook address must be an absolute `http` or `https` URL with no
credentials embedded in it. A scheme this server could be talked into treating as a file or a socket is the
one thing an address field must never accept.

The server prints its posture at startup, like every other operator switch.

## Decision: nothing a person does waits for somebody else's server

An event is queued and the request returns. Delivery happens on a worker of its own.

A push that blocked until a continuous integration box answered would make the forge as slow as the slowest
thing listening to it and as unavailable as the least reliable. So the queue is the mechanism, and its
properties are chosen to keep that true rather than to be a message broker:

- **Bounded, and in memory.** A restart loses what had not been sent.
- **Full means the oldest is dropped**, not that the queue grows. A stale push notification is worth less
  than the current one, and a receiver that went away must not be able to consume the process.
- **Three attempts, two seconds and eight seconds apart.** A receiver down for longer than that is down, and
  retrying into it for minutes would delay everything queued behind it.

**This is a notification mechanism and does not pretend to be delivery.** A receiver that must not miss an
event should reconcile against the API rather than trust that it heard. Saying so plainly is better than a
durable queue that would still lose the last event before a power cut while implying it would not.

## Decision: every delivery is signed, over the body

Each delivery carries `X-Graft-Event` and `X-Graft-Signature`. The signature is `sha256=` followed by the
lowercase hex HMAC-SHA256 of the exact bytes sent, under the hook's own secret.

Over the body rather than over selected fields, so a receiver verifies what it actually read rather than
what it believes it read. The secret is never sent, never returned by the API after the hook is created, and
never written to the audit trail.

**The secret is stored in the clear**, unlike a token's. It is not a credential for Graft: it is a shared key
the receiver must also hold, and Graft must be able to sign with it. Encrypting it would protect nothing
that the receiver's own copy does not already expose.

## Decision: four events, and a merge is also a push

`push`, `issue`, `merge_request` and `comment`. A closed set, so a hook naming an event nothing will ever
send is refused when it is created rather than silently never firing.

A merge sends both a `merge_request` event and a `push` event. The target branch moved, which is the thing
a build server is waiting for, and a receiver listening only for pushes would otherwise never hear about
work that arrived by merging.

A push reports the branch and the commits either side of the move, with a null on one side for a branch
created or deleted, so a receiver can tell the three cases apart without inferring them.

The last delivery's outcome is kept on the hook, overwritten each time, so a failing receiver is visible
without reading a log. A delivery history would be a table that grows forever to answer a question nobody
asks twice.

## What is not here

No instance-wide hooks, no hook for every project at once, no editing a hook after it is made, no manual
redelivery, no test button, and no disabling a hook short of removing it. No payload beyond the shape above:
a receiver that needs more reads the API with its own credential, which also keeps the payload from becoming
a second API nobody versioned.

## What was verified

A server without the switch has no hook routes rather than refusing ones it has. With it on: a secret
shorter than sixteen characters is refused, a `file:` address is refused, an unknown event name is refused,
and a registered hook never reports its secret back. Opening an issue delivers to a receiver the suite runs,
with the right event name, a signature the suite computes independently and compares, a payload naming what
happened and in which project, and no credential anywhere in it. The outcome is recorded on the hook.

See [the evidence](../validation/2026-10-22-review.md).
