# Wyu2

*What you up to?*

A Dalamud plugin for FINAL FANTASY XIV that shows you where your friends are and what they are doing —
on a radar, on the zone map, and in a live activity list. Nobody appears until they opt in, and you
decide field by field what you send back.

> Wyu2 only shares what its users deliberately choose to share, with people they have both
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
2. Each gets a share code that looks like `WY-9GQ4-K72M-XPZ1-D8V0`.
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

Wyu2 is not in the official Dalamud repository. Add it as a custom one:

```
https://github.com/Liquidize/Wyu2/releases/latest/download/repo.json
```

`/xlsettings` → **Experimental** → *Custom Plugin Repositories* → paste that URL → Save, then install
**Wyu2** from `/xlplugins`. The URL always resolves to the newest release, so it never needs changing.

<details>
<summary>Building it yourself instead</summary>

```bash
git clone https://github.com/Liquidize/Wyu2.git
cd Wyu2
dotnet build src/Wyu2/Wyu2.csproj -c Release
```

Then `/xlsettings` → Experimental → **Dev Plugin Locations** → add
`Wyu2/src/Wyu2/bin/Release/Wyu2.dll`, and enable it in `/xlplugins` → Dev Tools.

The build needs Dalamud's reference assemblies. It finds them automatically in the usual XIVLauncher
location; otherwise pass `-p:DalamudLibPath=/path/to/dalamud/dev/` or set `DALAMUD_HOME`.
</details>

## Using it

| Command | Does |
| --- | --- |
| `/wyu2` (or `/wyu`) | Open the friend list |
| `/wyu2 radar` | Toggle the radar |
| `/wyu2 map` | Toggle the zone map |
| `/wyu2 config` | Open settings |
| `/wyu2 share on\|off` | Start or stop sharing |
| `/wyu2 pause [minutes]` | Pause sharing, 15 minutes by default |
| `/wyu2 note <text>` | Set your status note |

## Running a relay

Wyu2 needs a relay to pass updates between clients. One ships with this repository: a small ASP.NET Core
service with no database, which only ever handles blobs it cannot decrypt.

```bash
cp .env.example .env      # name it, decide whether registration is open
docker compose up -d      # listens on 127.0.0.1:8080
```

That leaves TLS to you. To let the stack handle it, set `WYU2_DOMAIN` (and point its DNS at the box)
and bring up the bundled Caddy:

```bash
docker compose --profile tls up -d
```

Caddy fetches a certificate from Let's Encrypt and proxies `https://$WYU2_DOMAIN` to the relay. Either
way, paste the URL into the plugin's **Relay** tab and create an account.

Without Docker:

```bash
dotnet run --project src/Wyu2.Relay
```

See [docs/SELF_HOSTING.md](docs/SELF_HOSTING.md) for every setting, invite-only mode, retention and
backups, and [docs/PROTOCOL.md](docs/PROTOCOL.md) for the wire format.

## Repository layout

| Path | What it is |
| --- | --- |
| `src/Wyu2` | The Dalamud plugin (net10.0-windows) |
| `src/Wyu2.Core` | Sharing profiles, snapshot filtering and map maths, with no game dependencies |
| `src/Wyu2.Protocol` | Wire contracts, share codes and the sealed-box crypto, shared with the relay |
| `src/Wyu2.Relay` | The relay service (ASP.NET Core) |
| `tests/Wyu2.Tests` | Unit and end-to-end tests |
| `repo.json` | Dalamud repository manifest, regenerated by `scripts/make-repo-json.py` |
| `docker-compose.yml`, `deploy/` | Relay stack, optionally with Caddy for TLS |

```bash
dotnet test                                     # unit and end-to-end tests, no game required
dotnet build src/Wyu2/Wyu2.csproj -c Release    # needs Dalamud reference assemblies
```

### Cutting a release

Bump `<Version>` in `src/Wyu2/Wyu2.csproj`, then:

```bash
dotnet build src/Wyu2/Wyu2.csproj -c Release
python3 scripts/make-repo-json.py --tag v1.2.3.4
git commit -am "Release v1.2.3.4" && git tag v1.2.3.4 && git push --follow-tags
```

The release workflow rebuilds, refuses to publish if the tag and `repo.json` disagree with the built
assembly, and attaches `Wyu2.zip` and `repo.json` to the GitHub release.

## Limitations

- Positions are only meaningful when two people are in the same zone, the same public instance and on
  the same world. The plugin checks all three before plotting anybody.
- A friend standing next to you is read live from your own client; everyone else is as fresh as their
  last publish, which is every 10 seconds by default.
- The plugin cannot see anything the game does not tell your client. There is no server-side data source.
- Cross-data-centre travel changes your current world, so friends elsewhere show as "somewhere else"
  rather than on your radar.

## Licence

MIT, © Kaliya. See [LICENSE](LICENSE).

FINAL FANTASY XIV © SQUARE ENIX CO., LTD. This project is not affiliated with or endorsed by Square Enix.
