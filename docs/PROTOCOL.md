# Relay protocol v2

JSON over HTTP. The contracts live in `src/Wyu2.Protocol` and are compiled into both the plugin
and the relay, so they cannot drift.

Version 2 added forward secrecy: a long-term signing key per account and a rotating epoch key vouched
for by it. The URL namespace stays `/v1`, because version 2 only added fields to existing shapes. A
version 1 client keeps working against a version 2 relay, and vice versa, without either end pretending
to a secrecy property it is not getting.

## Conventions

- All bodies are JSON, camel-cased.
- Authenticated calls send `Authorization: Bearer <access token>`.
- Clients send `X-Wyu2-Protocol: 2` and `X-Wyu2-Client: <plugin version>`.
- Errors return `{ "code": "...", "message": "..." }` with a matching HTTP status.
- Timestamps are unix milliseconds, UTC.

## Limits

| Limit | Value |
| --- | --- |
| Protocol version | 2 |
| Epoch key lifetime | 24 h |
| Epoch private keys retained | 2 (current and one predecessor) |
| Default presence TTL | 120 s |
| Maximum presence TTL | 900 s |
| Maximum envelope size | 8 KiB |
| Maximum recipients per publish | 200 |
| Maximum display name | 48 characters |
| Default beacon TTL | 900 s |
| Maximum beacon TTL | 3 h |
| Beacons per sender, per recipient | 8 |
| Maximum beacon label | 60 characters |
| Maximum group name | 40 characters |
| Members per group | 64 |
| Groups per account | 16 |
| Default rate limit | 240 requests/minute per token (per IP before authentication) |

## Endpoints

### `GET /health`
Liveness probe. `{ "status": "ok" }`.

### `GET /v1/info`
Public, unauthenticated. Returns `ServerInfo`: name, operator, notice, version, protocol, TTL cap,
suggested publish interval, contact cap, whether registration is open.

### `POST /v1/accounts`
Body `RegisterRequest { displayName, publicKey, signingPublicKey?, clientVersion?, inviteCode? }`.
`publicKey` is a base64 SubjectPublicKeyInfo for an ECDH P-256 key; `signingPublicKey` is the same
encoding for the account's long-term ECDSA P-256 identity key.
Returns `RegisterResponse { accountId, accessToken, shareCode }`.

`signingPublicKey` may be omitted or empty: a client from before forward secrecy has none to send, and
registration succeeds without it. Such an account cannot publish an epoch key until it sets one.

The access token is returned once and stored only as a SHA-256 hash.

`403 registration_closed` when the relay is invite only and the code did not match.
`503 relay_full` when the account cap is reached.

### `GET /v1/me` · `PATCH /v1/me` · `DELETE /v1/me`
`GET` returns `AccountInfo { accountId, displayName, shareCode, publicKey, signingPublicKey,
createdAtUnixMs, contactCount, pendingRequestCount }`.

`PATCH` body `UpdateAccountRequest { displayName?, rotateShareCode?, publicKey?, signingPublicKey? }`.
Rotating the share code invalidates outstanding invites but keeps existing links. Changing the public
key drops every parked blob in both directions, since none of them can be read under the new key.

Setting `signingPublicKey` is how an account created before forward secrecy is upgraded in place rather
than abandoned and re-created. It drops nothing: a signing key seals no traffic, it only vouches for
epoch keys, so everything already parked stays exactly as readable as it was.

`DELETE` removes the account, both sides of every link, every pending invite and every blob.

### `POST /v1/me/prekey`
Body `PublishPrekeyRequest { bundle: { epoch, epochPublicKey, createdAtUnixMs, expiresAtUnixMs,
signature } }`. Files the epoch key contacts should encrypt to. Returns `204`.

- `409 no_signing_key` when the account has no signing key on file to check the bundle against. Set one
  with `PATCH /v1/me` first.
- `400 invalid_bundle` when the bundle is incomplete or its signature does not check out.
- `409 stale_epoch` when the epoch is not greater than the one already published. An old bundle must
  never be able to displace a newer one, or a captured bundle could be replayed to drag a contact back
  onto a key whose private half may already be known.

The relay's signature check is a cheap filter against garbage, not a security control. It proves nothing
to anybody who matters, because the relay is not trusted: every client verifies each bundle against the
contact's own identity key before encrypting to it.

### `GET /v1/contacts`
Array of `ContactDto { accountId, displayName, publicKey, signingPublicKey, prekey?, linkedAtUnixMs,
lastPresenceAtUnixMs? }`.

`prekey` is that contact's current bundle, or absent when they have published none — which means they
are still on a client from before forward secrecy, and traffic with them falls back to long-term keys.

### `DELETE /v1/contacts/{accountId}`
Unlinks both directions and drops any blob parked between the two accounts.

### `GET /v1/contacts/requests`
Array of `ContactRequestDto`, incoming and outgoing, newest first.

### `POST /v1/contacts/requests`
Body `CreateContactRequest { shareCode, message? }`.

- If the target already invited you, the two accounts are linked immediately: `{ "linked": true }`.
- Otherwise an invite is created: `{ "linked": false, "request": { ... } }`.
- `404 unknown_code`, `409 already_linked`, `429 limit_reached`, `400 invalid_code`.

### `POST /v1/contacts/requests/{requestId}/accept`
Links both accounts. Only the recipient of an invite may accept it.

### `POST /v1/contacts/requests/{requestId}/decline`
Declines an incoming invite, or withdraws one you sent.

### `GET /v1/groups`
Array of `GroupDto` for the groups you belong to, each with its join code and its members as
`GroupMemberDto { accountId, displayName, publicKey, signingPublicKey, prekey?, joinedAtUnixMs }`.
Bundles reach fellow group members on exactly the same terms as contacts.

### `POST /v1/groups`
Body `CreateGroupRequest { name }`. You become the owner and first member.

### `POST /v1/groups/join`
Body `JoinGroupRequest { joinCode }`. Joining a group you are already in returns the group unchanged
rather than an error.

### `PATCH /v1/groups/{groupId}`
Body `UpdateGroupRequest { name?, rotateJoinCode? }`. Owner only. Rotating the code invalidates the old
one; existing members are unaffected.

### `DELETE /v1/groups/{groupId}`
Leaves the group. When the owner leaves, the longest standing member takes it over; when the last member
leaves, the group and its join code are removed rather than lingering ownerless.

### `DELETE /v1/groups/{groupId}/members/{accountId}`
Removes somebody else. Owner only.

Leaving or being removed drops any presence parked between you and the members you can no longer see,
unless you are still linked to them another way.

### `POST /v1/presence`
Body `PublishPresenceRequest { ttlSeconds, envelopes: [ { recipientAccountId, nonce, ciphertext,
senderEpoch, recipientEpoch } ] }`.

`senderEpoch` and `recipientEpoch` say which pair of epoch keys sealed the blob. The relay stores them
and hands them back untouched; it never interprets or validates them, because it cannot read the blob
and has no business deciding how it was sealed. Zero on both means the long-term keys were used.

Envelopes addressed to anybody you are not linked with - a mutual contact or a fellow group member -
are rejected, as are oversized ones. Each
recipient holds exactly one blob per sender: publishing again replaces the previous one.

Returns `{ accepted, rejected, serverTimeUnixMs, activeRecipients }`. `activeRecipients` lists contacts
seen in the last five minutes, so a client can skip building payloads for people who are offline.

### `GET /v1/presence`
Returns `{ entries: [ ReceivedPresence ], serverTimeUnixMs }` — everything currently addressed to you
that has not expired. Expired blobs, and blobs from accounts you have since unlinked, are dropped rather
than returned.

### `DELETE /v1/presence`
Clears every blob you published, everywhere. This is what the plugin's stop-sharing switch calls.

### `POST /v1/beacons`
Body `PublishBeaconRequest { beaconId, ttlSeconds, envelopes: [ { recipientAccountId, nonce, ciphertext,
senderEpoch, recipientEpoch } ] }`. The epochs are carried and returned exactly as they are for presence.

`beaconId` is chosen by the sender and only has to be unique among that sender's own beacons; the relay
files each one under the account that sent it. Publishing an id again replaces that beacon everywhere it
was sent, which is how a beacon is moved or relabelled.

Unlike presence, a sender may have several beacons parked with one recipient at a time. Once a sender is
holding more than eight in the same inbox, their oldest is dropped to make room. Envelopes addressed to
anybody the sender is not linked with are rejected exactly as they are for presence, as are oversized
ones and blank or unusable beacon ids.

The TTL is clamped to between 30 seconds and 3 hours.

Returns `{ accepted, rejected, serverTimeUnixMs }`.

### `GET /v1/beacons`
Returns `{ entries: [ ReceivedBeacon ], serverTimeUnixMs }` — every unexpired beacon addressed to you,
each with the `beaconId` its sender gave it. Expired beacons, and beacons from accounts you have since
unlinked from, are dropped rather than returned. Your own beacons never come back here: the relay only
holds what somebody addressed to somebody else, so a client keeps its own until they expire.

### `DELETE /v1/beacons/{beaconId}`
Withdraws that beacon of yours from every recipient it reached. Only your own beacons can be withdrawn,
since the id is looked up under the calling account. Withdrawing a beacon that has already expired
everywhere succeeds; a beacon id the relay would never have stored returns `400 invalid_request`.

Beacons live in memory only, like presence: a relay restart takes every beacon with it, and none of them
are ever written to disk. Deleting an account, unlinking a contact, leaving a group and rotating a public
key all drop beacons on the same terms as presence.

## Envelope format

```
nonce      = 12 random bytes,        base64
ciphertext = AES-256-GCM output || 16 byte tag, base64
key        = HKDF-SHA256(
               ikm  = ECDH-P256(self private, other public) hashed with SHA-256,
               info = "Wyu2/v1/presence|<senderAccountId>|<recipientAccountId>",
               len  = 32)
aad        = "1|<senderAccountId>|<recipientAccountId>"
```

The info string and the associated data both bind the direction, so a blob cannot be replayed at another
recipient or reflected back at its author.

When both sides have published an epoch key, the ECDH is done with those instead of the long-term keys
and both epoch numbers are appended to the info string:

```
key        = HKDF-SHA256(
               ikm  = ECDH-P256(my epoch private, their epoch public) hashed with SHA-256,
               info = "Wyu2/v1/presence|<sender>|<recipient>|<senderEpoch>|<recipientEpoch>",
               len  = 32)
```

so the same pair of people derive a different key every epoch, and the envelope's `senderEpoch` and
`recipientEpoch` tell the recipient which of the private keys it still holds to try.

Beacons use the same construction with their own info string, `"Wyu2/v1/beacon|<sender>|<recipient>"`, so
the key that opens a presence blob cannot open a beacon between the same pair, or the other way round.

## Payload

The plaintext is a `PresencePayload`. Every optional member is omitted when the sender's profile does not
allow it:

```json
{
  "v": 1,
  "t": 1767225600000,
  "name": "Ysayle Dangoulain",
  "hw": 73, "cw": 73,
  "tt": 155, "map": 24, "inst": 2,
  "x": 12.5, "y": -3.25, "z": -88.75, "r": 1.25,
  "job": 24, "lvl": 90,
  "act": 4, "detail": "The Aery (in progress)",
  "os": 17, "f": 65, "ps": 4, "fc": "ISH",
  "note": "one more pull"
}
```

`act` is `ActivityKind`; `f` is the `PresenceFlags` bit set (in combat, mounted, flying, cutscene, party,
queue, bound by duty, AFK, busy, dead, sanctuary).

A beacon's plaintext is a `BeaconPayload`: one labelled point, with nothing optional about it, since the
sender chose to send exactly this.

```json
{
  "v": 1,
  "t": 1767225600000,
  "kind": 2,
  "label": "Nunyunuwi up",
  "tt": 155, "map": 24, "inst": 2, "w": 73,
  "x": 12.5, "y": -3.25, "z": -88.75
}
```

`kind` is `BeaconKind`: marker, rally, hunt, FATE, treasure, danger, node. The world is carried so a
beacon dropped on another world is never plotted as if it were local.

## Epoch keys and forward secrecy

An account holds three keys. The ECDH key and the ECDSA signing key are long-term; the epoch key is an
ECDH keypair replaced roughly daily.

Senders encrypt to the recipient's current epoch key, and recipients destroy epoch private keys once
they age out — only the current one and its immediate predecessor are kept, which is enough to cover a
rotation without widening the window. Once an epoch has gone, anything captured from it stays unreadable
even if the long-term keys later leak. That is the whole point of the scheme, and it is the destruction
of the old private key that delivers it, not anything the relay does.

An epoch key is published as a `PrekeyBundle`, signed by the account's long-term identity key over:

```
"wyu2-prekey|v2|<accountId>|<epoch>|<epochPublicKey>|<createdAtUnixMs>|<expiresAtUnixMs>"
```

The account id is inside the signature so a bundle cannot be lifted from one account and presented as
another's, and the epoch is inside it so an old bundle cannot be passed off as current.

**Clients must verify every bundle themselves**, against the signing key they already hold for that
contact, before encrypting anything to it. The relay checks signatures too, but only to keep obvious
rubbish out of other people's contact lists — it is not trusted, and a relay that wanted to read your
presence would simply hand out an epoch key of its own. The signature is the only thing that stops that,
and it is worth nothing unless the recipient of the bundle is the one checking it.

A contact with no `prekey` has not published one. Traffic with them falls back to the long-term keys and
carries `senderEpoch` and `recipientEpoch` of zero, which is honest about getting no forward secrecy
rather than silently pretending otherwise.

## Client behaviour

- Publish every 10 seconds by default, with a TTL of six intervals so a missed publish does not blink
  anybody offline.
- Fetch every 10 seconds.
- Fetch beacons at half that rate: they change far less often than a position does.
- Refresh contacts every 60 seconds.
- Rotate the epoch key once its lifetime is up, publish the new bundle, and keep the previous private
  key until it falls out of the retention window so blobs in flight during the rotation still open.
- Treat a blob that fails to decrypt as a dropped update, not an error: it usually means the sender
  rotated their key and the contact list has not caught up yet, or the epoch it was sealed to has been
  destroyed, which is forward secrecy working rather than a fault.
