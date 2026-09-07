# FriendRadar

A Dalamud plugin for FINAL FANTASY XIV that shows you where your friends are and what they are doing —
on a radar, on the zone map, and in a live activity list. Nobody appears until they opt in, and you
decide field by field what you send back.

> FriendRadar only shares what its users deliberately choose to share, with people they have both
> agreed to link with. It is not a tool for tracking players who have not opted in.

---

## What it does

**A radar.** A top-down view centred on you, rotating with your camera, with distance rings and blips
for every linked friend in your zone and instance. Friends past the edge of the range become arrows
pointing at them.

**A map.** The real zone map sheet from the game files, pannable and zoomable, with a marker for each
friend in that zone. Click a marker to open the in-game map at their coordinates.

**A list.** Who is online, which job and level they are playing, which zone and public instance they are
in, and one line describing what they are actually doing:

| What they are doing | What you see |
| --- | --- |
| In a dungeon or raid | `[DUTY] The Aery (in progress)` |
| Fighting something | `[FGHT] Fighting Ixali Windtalker` |
| In a FATE | `[FGHT] FATE: Breaking Dawn` |
| Gathering, crafting, fishing | `[GATH] Gathering as BTN`, `[CRFT] Crafting as CUL`, `[FISH] Fishing in Middle La Noscea` |
| Queued | `[WAIT] In the duty queue` |
| Cutscene, trading, performing | `[CUTS] Watching a cutscene`, `[TRDE] Trading`, `[PERF] Performing` |
| Housing, Gold Saucer, island | `[HOME] Inside a house`, `[GATE] At the Gold Saucer` |
| Nothing much | `[IDLE] Idle`, `[MOVE] Travelling` |

Plus optional in-world name labels above friends you can see on screen, and a server info bar entry
showing how many contacts are online.

## How the opt-in works

1. Both people install the plugin and point it at the same relay.
2. Each gets a share code that looks like `FR-9GQ4-K72M-XPZ1-D8V0`.
3. One pastes the other's code and sends an invite. Nothing flows yet.
4. The other accepts. Now you are linked — and either of you can unlink at any time, which immediately
   deletes anything the relay was holding between you.
5. Sharing is still **off** until you switch it on, and then only sends the fields you ticked.

Trading codes both ways links you immediately, since you both clearly agreed.

## Privacy

- **Nothing is published until you turn sharing on.** The master switch defaults to off.
- **Every field is a separate choice.** Character name, world, zone, exact position, activity, job,
  online status, party size, free company tag, status note. A field that is off is not written into the
  payload at all — a recipient cannot tell "off" from "unknown".
- **Per-contact profiles.** Give your static a full view and an acquaintance "online only".
- **Automatic stops.** Sharing pauses on its own in PvP (default), and optionally in housing, in duties,
  or when your status is Busy. You can hide coordinates inside duties and houses while still showing the
  duty name, and block individual zones outright.
- **End-to-end encryption.** Each update is sealed separately for each recipient with a key derived from
  an ECDH P-256 exchange, then AES-256-GCM. The relay stores a nonce and a ciphertext, and can read
  neither.
- **The relay forgets.** Presence lives in memory with a short TTL and is never written to disk. A relay
  restart drops every update.
- **You can leave.** "Delete account" removes your account, your links, and everything parked for you.

Full detail in [docs/PRIVACY.md](docs/PRIVACY.md).

There is a switch for plotting in-game friends who never opted in, using only what your client already
renders on screen. It is **off by default and should stay off** unless everyone involved knows about it.

## Installing

The plugin is not in the official Dalamud repository. Build it and load it as a dev plugin:

```bash
git clone https://github.com/Liquidize/Wyu2.git
cd Wyu2
dotnet build src/FriendRadar/FriendRadar.csproj -c Release
```

Then in-game: `/xlsettings` → Experimental → **Dev Plugin Locations** → add
`Wyu2/src/FriendRadar/bin/Release/FriendRadar.dll`, and enable it in `/xlplugins` → Dev Tools.

The build needs Dalamud's reference assemblies. It finds them automatically in the usual XIVLauncher
location; otherwise pass `-p:DalamudLibPath=/path/to/dalamud/dev/` or set `DALAMUD_HOME`.

## Using it

| Command | Does |
| --- | --- |
| `/friendradar` (or `/frad`) | Open the friend list |
| `/friendradar radar` | Toggle the radar |
| `/friendradar map` | Toggle the zone map |
| `/friendradar config` | Open settings |
| `/friendradar share on\|off` | Start or stop sharing |
| `/friendradar pause [minutes]` | Pause sharing, 15 minutes by default |
| `/friendradar note <text>` | Set your status note |

## Running a relay

The relay is a small ASP.NET Core service in this repository. It needs no database and holds no
readable location data.

```bash
docker compose up -d          # http://localhost:8080, put TLS in front of it
# or
dotnet run --project src/FriendRadar.Relay
```

Point the plugin at its URL on the **Relay** tab. See [docs/SELF_HOSTING.md](docs/SELF_HOSTING.md) for
configuration, invite-only mode, retention and reverse proxy notes, and
[docs/PROTOCOL.md](docs/PROTOCOL.md) for the wire format.

## Repository layout

| Path | What it is |
| --- | --- |
| `src/FriendRadar` | The Dalamud plugin (net10.0-windows) |
| `src/FriendRadar.Core` | Sharing profiles, snapshot filtering and map maths, with no game dependencies |
| `src/FriendRadar.Protocol` | Wire contracts, share codes and the sealed-box crypto, shared with the relay |
| `src/FriendRadar.Relay` | The relay service (ASP.NET Core) |
| `tests/FriendRadar.Tests` | Unit and end-to-end tests |

```bash
dotnet test                                                   # unit and end-to-end tests, no game required
dotnet build src/FriendRadar/FriendRadar.csproj -c Release    # needs Dalamud reference assemblies
```

## Limitations

- Positions are only meaningful when two people are in the same zone, the same public instance and on
  the same world. The plugin checks all three before plotting anybody.
- A friend standing next to you is read live from your own client; everyone else is as fresh as their
  last publish, which is every 10 seconds by default.
- The plugin cannot see anything the game does not tell your client. There is no server-side data source.
- Cross-data-centre travel changes your current world, so friends elsewhere show as "somewhere else"
  rather than on your radar.

## Licence

MIT. See [LICENSE](LICENSE).

FINAL FANTASY XIV © SQUARE ENIX CO., LTD. This project is not affiliated with or endorsed by Square Enix.
