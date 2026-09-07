# Running a relay

The relay moves encrypted blobs between clients. It needs no database, holds no readable location data,
and is small enough to sit next to anything else you already run.

## Quick start

```bash
git clone https://github.com/Liquidize/Wyu2.git
cd Wyu2
docker compose up -d
```

That listens on `127.0.0.1:8080`. Put a TLS-terminating reverse proxy in front of it and point the plugin
at the public URL. Without Docker:

```bash
dotnet run --project src/FriendRadar.Relay
```

## Configuration

Everything lives under the `Relay` section of `appsettings.json`, or as environment variables using
`Relay__Name` style keys.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Name` | `FriendRadar relay` | Shown in the plugin before anyone registers |
| `Operator` | – | Who runs this instance |
| `Message` | – | Free-form notice shown in the relay screen |
| `RegistrationOpen` | `true` | When false, only holders of an invite code may register |
| `InviteCodes` | `[]` | Codes accepted while registration is closed |
| `DataDirectory` | `relay-data` | Where the account graph snapshot is written |
| `SnapshotIntervalSeconds` | `60` | How often the snapshot is flushed and expired data pruned |
| `AccountRetentionDays` | `90` | Accounts unseen for this long are deleted with their links |
| `ContactRequestRetentionDays` | `14` | Pending invites expire after this long |
| `MaxContacts` | `200` | Mutual contacts per account |
| `MaxPendingRequests` | `50` | Outstanding invites per account, each direction |
| `SuggestedPublishIntervalSeconds` | `10` | Advertised to clients in `/v1/info` |
| `RequestsPerMinute` | `240` | Rate limit per token, or per IP before authentication |
| `MaxAccounts` | `0` | Cap on accounts; 0 means unlimited |

Invite-only, via compose:

```yaml
environment:
  Relay__RegistrationOpen: "false"
  Relay__InviteCodes__0: "first-code"
  Relay__InviteCodes__1: "second-code"
```

## Reverse proxy

The plugin refuses anything that is not `http://` or `https://`, and you should only ever hand out
`https://`. A minimal nginx block:

```nginx
server {
    listen 443 ssl http2;
    server_name relay.example.com;

    ssl_certificate     /etc/letsencrypt/live/relay.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/relay.example.com/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

Rate limiting partitions on the access token for authenticated calls, so users behind one NAT do not
share a bucket. Unauthenticated calls (registration, `/v1/info`) partition on the remote address, which
is the proxy unless you configure forwarded headers — set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` if
you want per-client limits there too.

## What is on disk

Only `state.json` in `DataDirectory`: accounts (display name, share code, public key, hashed access
token, contact ids) and pending invites. Presence never touches disk.

Back that file up if you do not want your users to have to re-link. It contains hashed tokens, so a
leaked backup does not let anybody log in as your users, but it does reveal the social graph.

## Sizing

Each account is a few hundred bytes; each parked blob is under 8 KiB and lives for at most fifteen
minutes. A relay for a few hundred people fits comfortably in the smallest VPS you can rent.

## Operating notes

- The relay logs registrations, links and deletions by account id. It never logs payload content,
  because it cannot read it.
- Restarting drops every parked presence blob. Clients republish within a publish interval, so users see
  at most a few seconds of staleness.
- To wind an instance down, tell your users first: their plugins will simply report that the relay is
  unreachable, and their local contact settings survive.
