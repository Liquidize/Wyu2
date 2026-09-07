# Blinding the social graph

Envelopes have always been unreadable to the relay. The relay still knew who they were between.

That is a real leak. A contact list is not much less revealing than the messages: it says who somebody
plays with, when they stopped, and who they started playing with instead. An operator who never
decrypts a single blob can still watch a friendship end.

This document describes what is being done about that, and — more importantly — what is not.

## The address, not the contents

Each pair of accounts derives a **mailbox address**: an opaque sixteen-byte identifier that both of them
can compute from the secret they already share, and that nobody else can. The sender posts to that
address. The recipient polls it. No account id appears on the envelope at all.

```
address = HKDF(ECDH(sender, recipient),
               info = "Wyu2/v1/mailbox|v2|<purpose>|<sender>|<recipient>|<window>")
```

Three properties are worth spelling out, because each of them is doing a job.

**Directional.** The address for A→B is not the address for B→A. If it were, an operator would see one
bucket with exactly two writers, which names the pair immediately.

**Rotating.** The window is a day. Watching one bucket for a month otherwise builds a stable handle for
a pair of people even without knowing which people they are — and traffic patterns are enough to guess,
given long enough.

**Per purpose.** Presence and beacons use different buckets, so the two feeds cannot be stitched
together by address alone.

Rotation happens on the wall clock rather than by negotiation, because there is nowhere to negotiate:
both sides divide time into windows and arrive at the same answer independently. Recipients watch the
window either side of the current one, so an envelope posted just before a rollover, or by a sender
whose clock runs a few minutes fast, still lands somewhere that is being read.

## What this actually buys

**Against someone who obtains the database:** a great deal. Seizure, a backup that ends up somewhere it
should not, a compromised host, a subpoena served on stored records. What is on disk is a pile of
mailbox addresses and ciphertext with no evident relationship to each other or to any account.

**Against a curious or careless operator:** something. Reading the tables no longer answers "who are
this person's friends".

**Against an operator who instruments the live service: nothing.** This is the part that gets
overstated elsewhere, so it is stated plainly here. The relay authenticates every request. It sees that
account A posts to mailbox X and that account B polls mailbox X. Correlating those two facts rebuilds
the pairing exactly, and no amount of address rotation changes it, because the correlation happens at
request time and the rotation is what both parties follow.

Closing that gap needs writes that cannot be tied to the account making them — blind signatures or an
anonymous credential scheme, where the relay can verify that a writer is entitled to post without
learning which writer they are. That is a substantial piece of cryptography with its own failure modes
(revocation, rate limiting per unknown user, double-spend of tokens). It is not built here.

Traffic analysis is untouched by any of this. Timing, message sizes and IP addresses are all still
visible, and presence is periodic by nature, which is about as helpful to an analyst as a protocol can
be. Two accounts on the same home connection remain obviously related.

## Groups survive blinding

A group is a set of people who all see each other, which is a pattern that stands out no matter how the
addressing works: N accounts each writing to N-1 buckets, all rotating together on the same schedule,
all going quiet at the same time on a Tuesday. Even without a single account id, that is a static.

Blinding does not fix this and is not claimed to. It is a reason to keep group sizes honest and to keep
saying, in the privacy documentation, that a group is a weaker privacy position than a set of pairwise
links.

## Migration

The mailbox addressing sits alongside the account-addressed path rather than replacing it outright. A
client that understands mailboxes posts to them; a relay that understands mailboxes serves them. Older
clients keep working against a newer relay, and a newer client against an older relay falls back,
because the alternative is that everybody's friend list breaks on the day they update.

The fallback is a downgrade the operator could force, so it is surfaced rather than silent: the client
records whether a contact's traffic is going through a mailbox, and the connection screen says so.
