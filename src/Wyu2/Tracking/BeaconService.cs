using System.Collections.Concurrent;
using Wyu2.Game;
using Wyu2.Model;
using Wyu2.Net;
using Wyu2.Protocol;

namespace Wyu2.Tracking;

/// <summary>
/// Beacons: a labelled point somebody dropped for everybody they are linked with. Follows the same
/// division of labour as the presence hub - the local character is read on the framework thread, sealing
/// and HTTP happen on the thread pool, and the results are merged back under a lock.
///
/// Your own beacons never come back from the relay, since it only hands you what other people addressed
/// to you, so they are kept locally from the moment you drop them.
/// </summary>
public sealed class BeaconService : IDisposable
{
    private readonly Configuration.Configuration config;
    private readonly RelayClient client;
    private readonly RelaySession session;
    private readonly GameDataCache data;

    private readonly Lock sync = new();
    private readonly Dictionary<string, LiveBeacon> beacons = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<List<Received>> inbox = new();
    private readonly CancellationTokenSource cancellation = new();

    private DateTime lastFetch = DateTime.MinValue;
    private Task? fetchTask;

    public BeaconService(
        Configuration.Configuration config,
        RelayClient client,
        RelaySession session,
        GameDataCache data)
    {
        this.config = config;
        this.client = client;
        this.session = session;
        this.data = data;
    }

    /// <summary>Every beacon currently alive, yours first and then newest first.</summary>
    public IReadOnlyList<LiveBeacon> Beacons { get; private set; } = [];

    /// <summary>Beacons you have out at the moment, which is what the per-sender cap counts.</summary>
    public int MineCount => Beacons.Count(b => b.IsMine);

    /// <summary>A decrypted beacon waiting to be folded in on the next tick.</summary>
    private readonly record struct Received(string BeaconId, string SenderAccountId, string SenderDisplayName,
        BeaconPayload Payload, long ExpiresAtUnixMs);

    /// <summary>Called every framework tick.</summary>
    public void Update()
    {
        var now = DateTime.UtcNow;

        DrainInbox();
        ForgetExpired();

        if (!session.HasAccount)
            return;

        // Beacons change far less often than presence does, so they are fetched at half the rate.
        var interval = TimeSpan.FromSeconds(Math.Max(10, config.FetchIntervalSeconds * 2));
        if ((fetchTask is null || fetchTask.IsCompleted) && now - lastFetch > interval)
        {
            lastFetch = now;
            fetchTask = Task.Run(FetchAsync, cancellation.Token);
        }
    }

    /// <summary>Why dropping a beacon would not work right now, or null when it would.</summary>
    public string? DropBlockedReason()
    {
        if (!session.HasAccount)
            return "Connect to a relay first.";

        if (!config.SharingEnabled)
            return "Start sharing before you drop a beacon.";

        if (config.IsPaused)
            return "Sharing is paused.";

        if (Service.Objects.LocalPlayer is null)
            return "You are not logged in.";

        // A zone on the never-share list is a standing instruction, and a beacon is still a position.
        if (config.Privacy.BlockedTerritories.Contains((ushort)Service.ClientState.TerritoryType))
            return "You told Wyu2 never to share this zone.";

        return null;
    }

    /// <summary>
    /// Drops a beacon where the local character is standing. Must be called on the framework thread,
    /// which is where the game is safe to read; the sealing and the publish are handed to the thread
    /// pool. Returns null when it went out, or a line to show the user.
    /// </summary>
    public string? Drop(BeaconKind kind, string label)
    {
        if (DropBlockedReason() is { } blocked)
            return blocked;

        var player = Service.Objects.LocalPlayer;
        if (player is null)
            return "You are not logged in.";

        var territory = (ushort)Service.ClientState.TerritoryType;
        var payload = new BeaconPayload
        {
            CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Kind = kind,
            Label = Trim(label),
            TerritoryTypeId = territory,
            MapId = Service.ClientState.MapId,
            InstanceId = Service.ClientState.Instance,
            WorldId = player.CurrentWorld.RowId,
            X = player.Position.X,
            Y = player.Position.Y,
            Z = player.Position.Z,
        };

        var recipients = config.SnapshotContacts()
            .Where(c => config.CanShareWith(c) && !string.IsNullOrEmpty(c.PublicKey))
            .Select(c => (c.AccountId, c.PublicKey))
            .ToList();

        if (recipients.Count == 0)
            return "Nobody is set to receive your beacons.";

        var beaconId = Guid.NewGuid().ToString("N");
        var ttl = Math.Clamp(config.BeaconMinutes * 60, 60, ProtocolConstants.MaxBeaconTtlSeconds);

        Remember(new LiveBeacon
        {
            BeaconId = beaconId,
            SenderAccountId = config.AccountId,
            SenderName = "You",
            Payload = payload,
            ExpiresAt = DateTime.UtcNow.AddSeconds(ttl),
            IsMine = true,
        });

        Publish(beaconId, payload, ttl, recipients);
        return null;
    }

    /// <summary>Takes one of your beacons back from everybody who was sent it.</summary>
    public void Withdraw(string beaconId)
    {
        lock (sync)
            beacons.Remove(MyKey(beaconId));

        Republish();
        _ = Task.Run(() => client.WithdrawBeaconAsync(beaconId, cancellation.Token), cancellation.Token);
    }

    /// <summary>
    /// Takes every beacon of yours back at once. Called when sharing stops, since a marker left standing
    /// after you switched off would still be pointing at where you were.
    /// </summary>
    public void WithdrawAll()
    {
        List<string> mine;
        lock (sync)
        {
            mine = beacons.Values.Where(b => b.IsMine).Select(b => b.BeaconId).ToList();
            foreach (var beaconId in mine)
                beacons.Remove(MyKey(beaconId));
        }

        if (mine.Count == 0)
            return;

        Republish();
        _ = Task.Run(
            async () =>
            {
                foreach (var beaconId in mine)
                    await client.WithdrawBeaconAsync(beaconId, cancellation.Token).ConfigureAwait(false);
            },
            cancellation.Token);
    }

    // ---------------------------------------------------------------- publish

    private void Publish(
        string beaconId,
        BeaconPayload payload,
        int ttlSeconds,
        List<(string AccountId, string PublicKey)> recipients)
    {
        _ = Task.Run(async () =>
        {
            var envelopes = new List<BeaconEnvelope>(recipients.Count);
            foreach (var (accountId, publicKey) in recipients)
            {
                var key = session.GetOutboundBeaconKey(accountId, publicKey);
                if (key is null)
                    continue;

                try
                {
                    var envelope = PresenceCrypto.Seal(key, payload, config.AccountId, accountId);
                    envelopes.Add(new BeaconEnvelope
                    {
                        RecipientAccountId = envelope.RecipientAccountId,
                        Nonce = envelope.Nonce,
                        Ciphertext = envelope.Ciphertext,
                    });
                }
                catch (Exception ex)
                {
                    Service.Log.Warning(ex, "Could not encrypt a beacon for {Contact}", accountId);
                }
            }

            if (envelopes.Count == 0)
                return;

            await client.PublishBeaconAsync(new PublishBeaconRequest
            {
                BeaconId = beaconId,
                TtlSeconds = ttlSeconds,
                Envelopes = envelopes,
            }, cancellation.Token).ConfigureAwait(false);
        }, cancellation.Token);
    }

    // ---------------------------------------------------------------- fetch

    private async Task FetchAsync()
    {
        var response = await client.FetchBeaconsAsync(cancellation.Token).ConfigureAwait(false);
        if (response is null)
            return;

        var received = new List<Received>(response.Entries.Count);
        foreach (var entry in response.Entries)
        {
            var contact = config.FindContact(entry.SenderAccountId);
            if (contact is null || !config.CanSee(contact) || string.IsNullOrEmpty(contact.PublicKey))
                continue;

            var key = session.GetInboundBeaconKey(entry.SenderAccountId, contact.PublicKey);
            if (key is null)
                continue;

            var payload = PresenceCrypto.Open<BeaconPayload>(
                key, entry.Nonce, entry.Ciphertext, entry.SenderAccountId, config.AccountId);

            if (payload is null)
            {
                Service.Log.Debug("Dropped an unreadable beacon from {Contact}", entry.SenderAccountId);
                continue;
            }

            received.Add(new Received(
                entry.BeaconId,
                entry.SenderAccountId,
                string.IsNullOrWhiteSpace(contact.Alias) ? entry.SenderDisplayName : contact.Alias!,
                payload,
                entry.ExpiresAtUnixMs));
        }

        inbox.Enqueue(received);
    }

    /// <summary>
    /// Folds a fetch into the list. The relay's answer is the whole truth about other people's beacons,
    /// so anything of theirs it did not mention has been withdrawn and goes as well.
    /// </summary>
    private void DrainInbox()
    {
        var changed = false;
        var arrivals = new List<LiveBeacon>();

        while (inbox.TryDequeue(out var received))
        {
            changed = true;
            lock (sync)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var entry in received)
                {
                    var beacon = new LiveBeacon
                    {
                        BeaconId = entry.BeaconId,
                        SenderAccountId = entry.SenderAccountId,
                        SenderName = entry.SenderDisplayName,
                        Payload = entry.Payload,
                        ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(entry.ExpiresAtUnixMs).UtcDateTime,
                    };

                    seen.Add(beacon.Key);
                    if (!beacons.ContainsKey(beacon.Key))
                        arrivals.Add(beacon);

                    Describe(beacon);
                    beacons[beacon.Key] = beacon;
                }

                foreach (var stale in beacons.Values.Where(b => !b.IsMine && !seen.Contains(b.Key)).ToList())
                    beacons.Remove(stale.Key);
            }
        }

        if (!changed)
            return;

        Republish();

        foreach (var beacon in arrivals)
            Announce(beacon);
    }

    /// <summary>Fills in the names and coordinates the UI shows. Reads game sheets, so framework thread only.</summary>
    private void Describe(LiveBeacon beacon)
    {
        var territory = beacon.Payload.TerritoryTypeId;
        beacon.ZoneName = data.GetZoneName(territory);

        var map = beacon.Payload.MapId != 0
            ? data.GetMap(beacon.Payload.MapId)
            : data.GetMapForTerritory(territory);

        beacon.MapId = map?.RowId ?? 0;
        beacon.MapCoordinates = map is null ? null : MapMath.WorldToMapCoordinates(beacon.Position, map.Value);
    }

    private void Announce(LiveBeacon beacon)
    {
        if (!config.AnnounceBeaconsInChat)
            return;

        var zone = string.IsNullOrWhiteSpace(beacon.ZoneName) ? "somewhere" : beacon.ZoneName;
        var where = beacon.MapCoordinates is { } coordinates
            ? $"{zone} {MapMath.FormatCoordinates(coordinates)}"
            : zone;

        Service.Chat.Print($"{beacon.SenderName} marked \"{beacon.Label}\" in {where}.", "Wyu2");
    }

    private void ForgetExpired()
    {
        lock (sync)
        {
            var expired = beacons.Values.Where(b => b.HasExpired).ToList();
            if (expired.Count == 0)
                return;

            foreach (var beacon in expired)
                beacons.Remove(beacon.Key);
        }

        Republish();
    }

    private void Remember(LiveBeacon beacon)
    {
        Describe(beacon);
        lock (sync)
        {
            beacons[beacon.Key] = beacon;

            // The relay retires a sender's oldest beacon once they hold more than the cap, so the local
            // list drops it too rather than showing a marker nobody else can see any more.
            var mine = beacons.Values.Where(b => b.IsMine).OrderBy(b => b.Payload.CreatedAtUnixMs).ToList();
            foreach (var retired in mine.Take(mine.Count - ProtocolConstants.MaxBeaconsPerSender))
                beacons.Remove(retired.Key);
        }

        Republish();
    }

    /// <summary>Rebuilds the list the windows read, so they never see the dictionary mid-edit.</summary>
    private void Republish()
    {
        lock (sync)
        {
            Beacons = beacons.Values
                .OrderByDescending(b => b.IsMine)
                .ThenByDescending(b => b.Payload.CreatedAtUnixMs)
                .ToList();
        }
    }

    /// <summary>How one of our own beacons is filed, matching <see cref="LiveBeacon.Key"/>.</summary>
    private string MyKey(string beaconId) => config.AccountId + "|" + beaconId;

    private static string Trim(string label)
    {
        var cleaned = new string((label ?? string.Empty).Trim().Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length <= ProtocolConstants.MaxBeaconLabelLength
            ? cleaned
            : cleaned[..ProtocolConstants.MaxBeaconLabelLength];
    }

    public void Dispose()
    {
        cancellation.Cancel();
        cancellation.Dispose();
    }
}
