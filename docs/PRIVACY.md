# Privacy model

Wyu2 exists to share your location with people you chose. Everything below is about making that
choice explicit, reversible, and narrow.

## The three gates

An update only reaches somebody if it passes all three:

1. **You are linked.** Both of you accepted the link on the relay. Either side can unlink at any moment.
2. **Sharing is on.** The master switch (`Start sharing`) defaults to off, and no privacy rule is
   currently suspending it.
3. **The field is enabled.** Each field is a separate tick box, in the default profile or in a
   per-contact override.

Fail any gate and the data is not sent. There is no path that skips them.

## What can be shared

| Field | Default | Notes |
| --- | --- | --- |
| Character name | on | Off means contacts see the display name you registered with instead |
| Home and current world | on | |
| Zone and public instance | on | Coordinates are meaningless without it, so turning it off also withholds them |
| Exact position | on | X/Y/Z plus facing |
| Activity | on | Duty name, FATE, gathering, crafting, cutscene, queue state |
| Name of your combat target | **off** | Adds "Fighting X" to the activity line |
| Job and level | on | |
| Online status | on | The game's own AFK / busy / role playing flags |
| Party size | **off** | A number, never names |
| Free company tag | **off** | |
| Status note | on | Only what you typed yourself |

Fields that are off are omitted from the JSON entirely rather than blanked, so a recipient cannot
distinguish "not shared" from "not known".

## What is never shared

- Your Square Enix account, e-mail, IP address as data (the relay sees the connection, like any server)
- Your inventory, currency, gear, achievements, retainers or messages
- Anything about anyone else — party members, people near you, your friend list
- Chat of any kind

## Rules that stop sharing on their own

| Rule | Default | Effect |
| --- | --- | --- |
| Stop in PvP | on | Nothing is published inside a PvP instance |
| Stop in housing | off | |
| Stop inside duties | off | |
| Stop while status is Busy | off | |
| Hide coordinates inside duties | on | Duty name still goes out, position does not |
| Hide coordinates in housing | on | |
| Blocked zones | empty | Nothing at all is published in a blocked zone |

Plus a manual **Pause** button (15 minutes by default, or `/wyu2 pause 60`), and a **Stop
sharing** switch that also asks the relay to drop anything it is still holding for you.

## Encryption

Each account generates an ECDH P-256 keypair on first registration. The private half never leaves your
machine; the public half is published so contacts can encrypt to you.

For every recipient, on every publish:

1. Derive a shared secret with ECDH against that contact's public key.
2. Run it through HKDF-SHA256 with `Wyu2/v1/presence|<sender>|<recipient>` as the info string, so
   each direction of each pair gets its own key.
3. Encrypt the payload with AES-256-GCM under a fresh random nonce, with
   `<version>|<sender>|<recipient>` as associated data.

The relay stores the recipient id, the nonce and the ciphertext. It cannot decrypt any of it, and a blob
cannot be replayed at a different recipient or back at its author, because the addressing is bound into
the associated data.

## What the relay knows

It can see, and a relay operator could log:

- Your display name, share code, public key and account id
- Who you are linked with
- That you published something, how large it was, and when
- Your IP address and the times you connect, like any HTTP service

It cannot see where you are, what you are doing, your character name, or anything else inside a payload.

## Retention

- Presence blobs are held **in memory only**, expire after their TTL (two minutes by default, capped at
  fifteen), and are gone entirely on restart. They are never written to disk.
- Accounts, links and pending invites are snapshotted to a JSON file so a restart does not unlink
  everybody.
- Pending invites expire after 14 days by default, accounts after 90 days without a connection.
- `Delete account` removes the account, both sides of every link, and every parked blob immediately.

## Local storage

The plugin's configuration file lives in your Dalamud config directory and holds your relay access token
and your private key in plain JSON. Anybody who can read that file can impersonate you on the relay and
decrypt updates sent to you. Treat it like any other credential file, and use `Delete account` if you
think it leaked.

## Tracking people who did not opt in

There is one setting, off by default, that plots in-game friends who never installed Wyu2, using
only what your own client already renders on screen. It shares nothing about them with anyone else, but
those people never agreed to be plotted. Leave it off unless everyone involved knows.
