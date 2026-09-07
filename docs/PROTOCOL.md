# Relay protocol v1

JSON over HTTP. The contracts live in `src/FriendRadar.Protocol` and are compiled into both the plugin
and the relay, so they cannot drift.

## Conventions

- All bodies are JSON, camel-cased.
- Authenticated calls send `Authorization: Bearer <access token>`.
- Clients send `X-FriendRadar-Protocol: 1` and `X-FriendRadar-Client: <plugin version>`.
- Errors return `{ "code": "...", "message": "..." }` with a matching HTTP status.
- Timestamps are unix milliseconds, UTC.

## Limits

| Limit | Value |
| --- | --- |
| Protocol version | 1 |
| Default presence TTL | 120 s |
| Maximum presence TTL | 900 s |
| Maximum envelope size | 8 KiB |
| Maximum recipients per publish | 200 |
| Maximum display name | 48 characters |
| Default rate limit | 240 requests/minute per token (per IP before authentication) |

## Endpoints

### `GET /health`
Liveness probe. `{ "status": "ok" }`.

### `GET /v1/info`
Public, unauthenticated. Returns `ServerInfo`: name, operator, notice, version, protocol, TTL cap,
suggested publish interval, contact cap, whether registration is open.

### `POST /v1/accounts`
Body `RegisterRequest { displayName, publicKey, clientVersion?, inviteCode? }`.
`publicKey` is a base64 SubjectPublicKeyInfo for an ECDH P-256 key.
Returns `RegisterResponse { accountId, accessToken, shareCode }`.

The access token is returned once and stored only as a SHA-256 hash.

`403 registration_closed` when the relay is invite only and the code did not match.
`503 relay_full` when the account cap is reached.

### `GET /v1/me` · `PATCH /v1/me` · `DELETE /v1/me`
`PATCH` body `UpdateAccountRequest { displayName?, rotateShareCode?, publicKey? }`. Rotating the share
code invalidates outstanding invites but keeps existing links. Changing the public key drops every
parked blob in both directions, since none of them can be read under the new key.

`DELETE` removes the account, both sides of every link, every pending invite and every blob.

### `GET /v1/contacts`
Array of `ContactDto { accountId, displayName, publicKey, linkedAtUnixMs, lastPresenceAtUnixMs? }`.

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

### `POST /v1/presence`
Body `PublishPresenceRequest { ttlSeconds, envelopes: [ { recipientAccountId, nonce, ciphertext } ] }`.

Envelopes addressed to anybody who is not a mutual contact are rejected, as are oversized ones. Each
recipient holds exactly one blob per sender: publishing again replaces the previous one.

Returns `{ accepted, rejected, serverTimeUnixMs, activeRecipients }`. `activeRecipients` lists contacts
seen in the last five minutes, so a client can skip building payloads for people who are offline.

### `GET /v1/presence`
Returns `{ entries: [ ReceivedPresence ], serverTimeUnixMs }` — everything currently addressed to you
that has not expired. Expired blobs, and blobs from accounts you have since unlinked, are dropped rather
than returned.

### `DELETE /v1/presence`
Clears every blob you published, everywhere. This is what the plugin's stop-sharing switch calls.

## Envelope format

```
nonce      = 12 random bytes,        base64
ciphertext = AES-256-GCM output || 16 byte tag, base64
key        = HKDF-SHA256(
               ikm  = ECDH-P256(self private, other public) hashed with SHA-256,
               info = "FriendRadar/v1/presence|<senderAccountId>|<recipientAccountId>",
               len  = 32)
aad        = "1|<senderAccountId>|<recipientAccountId>"
```

The info string and the associated data both bind the direction, so a blob cannot be replayed at another
recipient or reflected back at its author.

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

## Client behaviour

- Publish every 10 seconds by default, with a TTL of six intervals so a missed publish does not blink
  anybody offline.
- Fetch every 10 seconds.
- Refresh contacts every 60 seconds.
- Treat a blob that fails to decrypt as a dropped update, not an error: it usually means the sender
  rotated their key and the contact list has not caught up yet.
