using System.Collections.Concurrent;
using Wyu2.Game;
using Wyu2.Model;
using Wyu2.Net;
using Wyu2.Protocol;

namespace Wyu2.Tracking;

/// <summary>
/// The engine room. Once per frame it folds together three sources of truth - the local character, the
/// object table, and decrypted relay blobs - into the friend list the windows draw. Game reads happen on
/// the framework thread; encryption and HTTP happen on the thread pool.
/// </summary>
public sealed class PresenceHub : IDisposable
{
    private readonly Configuration.Configuration config;
    private readonly RelayClient client;
    private readonly RelaySession session;
    private readonly SelfSnapshotBuilder snapshots;
    private readonly NearbyScanner scanner;
    private readonly GameDataCache data;

    private readonly Dictionary<string, TrackedFriend> friends = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string AccountId, string DisplayName, PresencePayload Payload)> inbox = new();
    private readonly ReplayGuard replays = new();

    /// <summary>
    /// How many publish counters are claimed at a time. The counter has to survive a restart without
    /// going backwards, but writing the configuration file on every publish would mean a disk write
    /// every few seconds. Instead a block is reserved up front and only the watermark is stored, so a
    /// restart resumes above anything actually sent.
    /// </summary>
    private const long SequenceBlock = 1_000;

    private long sequence;
    private long reservedSequence;

    private readonly Dictionary<string, OverlapHistogram> overlaps = new(StringComparer.Ordinal);
    private DateTime lastOverlapSample = DateTime.MinValue;
    private DateTime lastOverlapSave = DateTime.MinValue;
    private readonly CancellationTokenSource cancellation = new();

    private DateTime lastPublish = DateTime.MinValue;
    private DateTime lastFetch = DateTime.MinValue;
    private DateTime lastContactSync = DateTime.MinValue;
    private DateTime lastScan = DateTime.MinValue;
    private Task? publishTask;
    private Task? fetchTask;
    private Task? contactTask;

    public PresenceHub(
        Configuration.Configuration config,
        RelayClient client,
        RelaySession session,
        SelfSnapshotBuilder snapshots,
        NearbyScanner scanner,
        GameDataCache data)
    {
        this.config = config;
        this.client = client;
        this.session = session;
        this.snapshots = snapshots;
        this.scanner = scanner;
        this.data = data;

        sequence = config.PresenceSequence;
        reservedSequence = config.PresenceSequence;
    }

    private long NextSequence()
    {
        sequence++;

        if (sequence > reservedSequence)
        {
            reservedSequence = sequence + SequenceBlock;
            config.PresenceSequence = reservedSequence;
            config.Save();
        }

        return sequence;
    }

    /// <summary>Friends with any data at all, ordered for display.</summary>
    public IReadOnlyList<TrackedFriend> Friends { get; private set; } = [];

    public PublishBlockReason BlockReason => snapshots.LastBlockReason;

    public DateTime? LastPublishAt { get; private set; }

    public int PublishedRecipients { get; private set; }

    /// <summary>Called every framework tick.</summary>
    public void Update()
    {
        var now = DateTime.UtcNow;

        DrainInbox();

        if (now - lastScan > TimeSpan.FromMilliseconds(250))
        {
            lastScan = now;
            scanner.Scan(friends.Values);
        }

        RefreshDerivedData();
        RecordOverlap(now);
        ForgetStaleFriends(now);
        Friends = Order(friends.Values);

        if (!session.HasAccount)
            return;

        if (IsIdle(contactTask) && now - lastContactSync > TimeSpan.FromSeconds(60))
        {
            lastContactSync = now;
            contactTask = Task.Run(SyncAccountAsync, cancellation.Token);
        }

        if (IsIdle(publishTask) && now - lastPublish > TimeSpan.FromSeconds(Math.Max(3, config.PublishIntervalSeconds)))
        {
            lastPublish = now;
            PublishTick();
        }

        if (IsIdle(fetchTask) && now - lastFetch > TimeSpan.FromSeconds(Math.Max(3, config.FetchIntervalSeconds)))
        {
            lastFetch = now;
            fetchTask = Task.Run(FetchAsync, cancellation.Token);
        }
    }

    /// <summary>Publishes immediately, e.g. after the user changes what they share.</summary>
    public void PublishNow()
    {
        lastPublish = DateTime.UtcNow;
        if (IsIdle(publishTask))
            PublishTick();
    }

    /// <summary>Refreshes contacts immediately.</summary>
    public void SyncContactsNow()
    {
        lastContactSync = DateTime.UtcNow;
        if (IsIdle(contactTask))
            contactTask = Task.Run(SyncAccountAsync, cancellation.Token);
    }

    /// <summary>
    /// Pulls the contact list and keeps the epoch keys current. Rotation rides on the contact sync
    /// because both need the relay and neither is urgent, and a failure on either is simply retried on
    /// the next pass.
    /// </summary>
    private async Task SyncAccountAsync()
    {
        await session.EnsureForwardSecrecyAsync(cancellation.Token).ConfigureAwait(false);
        await session.RefreshContactsAsync(cancellation.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops sharing and asks the relay to drop anything it is still holding for us. The local friend
    /// list is kept: this is about what others can see, not what we can.
    /// </summary>
    public void PanicStop()
    {
        config.SharingEnabled = false;
        config.Save();
        _ = Task.Run(() => client.ClearPresenceAsync(cancellation.Token), cancellation.Token);
    }

    // ---------------------------------------------------------------- publish

    private void PublishTick()
    {
        // Reading the game has to happen here, on the framework thread; everything after this is pure
        // data and runs on the thread pool.
        if (!snapshots.TryBuild(out var snapshot) || snapshot is null)
        {
            PublishedRecipients = 0;
            return;
        }

        var recipients = config.SnapshotContacts()
            .Where(c => config.CanShareWith(c) && !string.IsNullOrEmpty(c.PublicKey))
            .Select(c => (Contact: c, Profile: config.ProfileFor(c)))
            .ToList();

        // One counter per snapshot rather than per recipient, so every copy of the same moment carries
        // the same number and a recipient sees a single clean progression.
        var sequence = NextSequence();

        PublishedRecipients = recipients.Count;
        if (recipients.Count == 0)
            return;

        publishTask = Task.Run(async () =>
        {
            var envelopes = new List<PresenceEnvelope>(recipients.Count);
            foreach (var (contact, profile) in recipients)
            {
                var sealing = session.GetOutboundKey(contact, ProtocolConstants.KeyDerivationInfo);
                if (sealing is not { } seal)
                    continue;

                try
                {
                    var payload = snapshot.ToPayload(profile) with { Sequence = sequence };
                    var envelope = PresenceCrypto.Seal(
                        seal.Key, payload, config.AccountId, contact.AccountId);

                    envelopes.Add(envelope with
                    {
                        SenderEpoch = seal.SenderEpoch,
                        RecipientEpoch = seal.RecipientEpoch,
                    });
                }
                catch (Exception ex)
                {
                    Service.Log.Warning(ex, "Could not encrypt presence for {Contact}", contact.AccountId);
                }
            }

            if (envelopes.Count == 0)
                return;

            var response = await client.PublishPresenceAsync(new PublishPresenceRequest
            {
                TtlSeconds = Math.Clamp(config.PublishIntervalSeconds * 6, 60, ProtocolConstants.MaxPresenceTtlSeconds),
                Envelopes = envelopes,
            }, cancellation.Token).ConfigureAwait(false);

            if (response is not null)
                LastPublishAt = DateTime.UtcNow;
        }, cancellation.Token);
    }

    // ---------------------------------------------------------------- fetch

    private async Task FetchAsync()
    {
        var response = await client.FetchPresenceAsync(cancellation.Token).ConfigureAwait(false);
        if (response is null)
            return;

        foreach (var entry in response.Entries)
        {
            var contact = config.FindContact(entry.SenderAccountId);
            if (contact is null || !config.CanSee(contact) || string.IsNullOrEmpty(contact.PublicKey))
                continue;

            var key = session.GetInboundKey(
                contact, entry.SenderEpoch, entry.RecipientEpoch, ProtocolConstants.KeyDerivationInfo);

            if (key is null)
                continue;

            var payload = PresenceCrypto.Open<PresencePayload>(
                key, entry.Nonce, entry.Ciphertext, entry.SenderAccountId, config.AccountId);

            if (payload is null)
            {
                Service.Log.Debug("Dropped an unreadable presence blob from {Contact}", entry.SenderAccountId);
                continue;
            }

            // The counter and the timestamp both sit inside the sealed payload, so a relay re-serving an
            // old blob cannot dress it up as a new one.
            var verdict = replays.Inspect(
                entry.SenderAccountId,
                payload.Sequence,
                payload.SentAtUnixMs,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            if (!ReplayGuard.IsAccepted(verdict))
            {
                Service.Log.Warning(
                    "Discarded presence from {Contact}: {Verdict}", entry.SenderAccountId, verdict);
                continue;
            }

            inbox.Enqueue((entry.SenderAccountId, entry.SenderDisplayName, payload));
        }
    }

    private void DrainInbox()
    {
        while (inbox.TryDequeue(out var item))
        {
            var (accountId, displayName, payload) = item;
            var settings = config.FindContact(accountId);
            if (settings is null)
                continue;

            if (!friends.TryGetValue(accountId, out var friend))
            {
                friend = new TrackedFriend { AccountId = accountId };
                friends[accountId] = friend;
            }

            friend.Settings = settings;
            friend.RelayDisplayName = displayName;
            friend.Payload = payload;
            friend.RelayUpdatedAt = DateTime.UtcNow;
            friend.Sources |= PresenceSource.Relay;
        }
    }

    // ---------------------------------------------------------------- derived data

    private void RefreshDerivedData()
    {
        var player = Service.Objects.LocalPlayer;
        var myTerritory = (ushort)Service.ClientState.TerritoryType;
        var myInstance = Service.ClientState.Instance;
        var myWorld = player?.CurrentWorld.RowId ?? 0;

        foreach (var friend in friends.Values)
        {
            var payload = friend.Payload;
            if (payload is null)
            {
                friend.DistanceYalms = null;
                friend.DirectionText = null;
                friend.LastSeenZoneName = friend.Settings.LastSeenTerritoryId == 0
                    ? string.Empty
                    : data.GetZoneName(friend.Settings.LastSeenTerritoryId);
                continue;
            }

            UpdateProximity(friend, player?.Position, myTerritory, myInstance, myWorld);
            RecordTrail(friend, payload);

            var territory = payload.TerritoryTypeId ?? 0;
            friend.ZoneName = data.GetZoneName(territory);
            friend.RegionName = data.GetRegionName(territory);
            friend.WorldName = data.GetWorldName(payload.CurrentWorldId != 0 ? payload.CurrentWorldId : payload.HomeWorldId);
            friend.OnlineStatusName = data.GetOnlineStatusName(payload.OnlineStatusId ?? 0);

            var (jobName, jobAbbreviation) = data.GetJob(payload.JobId ?? 0);
            friend.JobName = jobName;
            friend.JobAbbreviation = jobAbbreviation;
            friend.ActivityText = DescribeActivity(friend);

            var map = payload.MapId is { } mapId && mapId != 0
                ? data.GetMap(mapId)
                : data.GetMapForTerritory(territory);

            friend.MapId = map?.RowId ?? 0;
            friend.MapCoordinates = map is not null && friend.Position is { } position
                ? MapMath.WorldToMapCoordinates(position, map.Value)
                : null;
        }
    }

    /// <summary>
    /// Appends to a contact's breadcrumb trail. Uses the ImGui clock so the trail ages on the same
    /// timeline the windows draw on, and only runs while something wants it so the buffers stay empty
    /// for anybody who does not. Prediction reads the same buffer to work out which way somebody is
    /// going, so it counts as a reason to keep one.
    /// </summary>
    private void RecordTrail(TrackedFriend friend, PresencePayload payload)
    {
        if (!config.Radar.ShowTrails && !config.Radar.ShowPrediction)
        {
            if (friend.Trail.Count > 0)
                friend.Trail.Clear();

            return;
        }

        if (friend.Position is not { } position || payload.TerritoryTypeId is not { } territory)
            return;

        var now = Dalamud.Bindings.ImGui.ImGui.GetTime();
        var keep = config.Radar.ShowTrails ? MathF.Max(2f, config.Radar.TrailSeconds) : 5f;

        friend.Trail.Record(position, territory, now);
        friend.Trail.PruneOlderThan(now, keep);
    }

    /// <summary>
    /// Distance and compass bearing, but only when the two of you are genuinely in the same place: same
    /// zone, same public instance and same world. Anything else and the coordinates describe a different
    /// copy of the map, so a distance would be a lie.
    /// </summary>
    private static void UpdateProximity(
        TrackedFriend friend,
        System.Numerics.Vector3? myPosition,
        ushort myTerritory,
        uint myInstance,
        uint myWorld)
    {
        friend.DistanceYalms = null;
        friend.DirectionText = null;

        if (myPosition is not { } me || friend.Payload is not { } payload)
            return;

        if (payload.TerritoryTypeId != myTerritory || friend.InstanceId != myInstance)
            return;

        var theirWorld = payload.CurrentWorldId;
        if (theirWorld != 0 && myWorld != 0 && theirWorld != myWorld)
            return;

        if (friend.Position is not { } them)
            return;

        var distance = MapMath.FlatDistance(them, me);
        friend.DistanceYalms = distance;
        friend.DirectionText = Bearing.Describe(distance, Bearing.FromWorldDelta(them.X - me.X, them.Z - me.Z));
    }

    /// <summary>
    /// Accumulates time spent online at the same moment as each contact, so "when are we both usually
    /// around" can be answered without anybody having to keep a diary. Entirely local: this is derived
    /// from presence already received and is never published.
    /// </summary>
    private void RecordOverlap(DateTime now)
    {
        if (!Service.ClientState.IsLoggedIn)
        {
            lastOverlapSample = DateTime.MinValue;
            return;
        }

        if (lastOverlapSample == DateTime.MinValue)
        {
            lastOverlapSample = now;
            return;
        }

        var span = now - lastOverlapSample;
        if (span < TimeSpan.FromSeconds(30))
            return;

        lastOverlapSample = now;

        // A long gap means the game was closed or the machine asleep, not that the two of you spent the
        // afternoon together.
        if (span > TimeSpan.FromMinutes(5))
            return;

        // Local time, because the answer people want is "Tuesday evening", not a UTC hour.
        var when = DateTimeOffset.Now;
        foreach (var friend in friends.Values)
        {
            if (friend.IsOnline)
                HistogramFor(friend.AccountId).Add(when, span);
        }

        if (now - lastOverlapSave > TimeSpan.FromMinutes(10))
        {
            SaveOverlaps();
            lastOverlapSave = now;
        }
    }

    private OverlapHistogram HistogramFor(string accountId)
    {
        if (overlaps.TryGetValue(accountId, out var existing))
            return existing;

        var restored = OverlapHistogram.FromSnapshot(config.FindContact(accountId)?.OverlapMinutes);
        overlaps[accountId] = restored;
        return restored;
    }

    /// <summary>
    /// Writes the histograms back. Done on a slow timer and again on shutdown rather than on every
    /// sample: this is a hundred and sixty eight floats per contact and nobody needs it durable to the
    /// minute.
    /// </summary>
    private void SaveOverlaps()
    {
        var changed = false;
        foreach (var (accountId, histogram) in overlaps)
        {
            if (config.FindContact(accountId) is not { } settings)
                continue;

            settings.OverlapMinutes = histogram.Snapshot();
            changed = true;
        }

        if (changed)
            config.Save();
    }

    /// <summary>The stretches when you and a contact are most often online together, busiest first.</summary>
    public IReadOnlyList<OverlapWindow> BestOverlap(string accountId, int count = 3)
        => HistogramFor(accountId).BestWindows(count);

    /// <summary>The one line the friend list and tooltips show.</summary>
    public static string DescribeActivity(TrackedFriend friend)
    {
        var payload = friend.Payload;
        if (payload is null)
            return "Unknown";

        if (!string.IsNullOrWhiteSpace(payload.ActivityDetail))
            return payload.ActivityDetail!;

        return payload.Activity switch
        {
            ActivityKind.Idle => "Idle",
            ActivityKind.Travelling => "Travelling",
            ActivityKind.InCombat => "In combat",
            ActivityKind.InDuty => "In a duty",
            ActivityKind.InPvp => "In PvP",
            ActivityKind.Crafting => "Crafting",
            ActivityKind.Gathering => "Gathering",
            ActivityKind.Fishing => "Fishing",
            ActivityKind.Cutscene => "Watching a cutscene",
            ActivityKind.Waiting => "Waiting",
            ActivityKind.Housing => "At home",
            ActivityKind.GoldSaucer => "At the Gold Saucer",
            ActivityKind.Performing => "Performing",
            ActivityKind.Trading => "Trading",
            ActivityKind.Offline => "Offline",
            _ => "Online",
        };
    }

    private void ForgetStaleFriends(DateTime now)
    {
        var forgetAfter = TimeSpan.FromSeconds(Math.Max(30, config.ForgetAfterSeconds));
        foreach (var (accountId, friend) in friends.ToList())
        {
            if (config.FindContact(accountId) is null)
            {
                friends.Remove(accountId);
                continue;
            }

            if (friend.LastUpdate is { } last && now - last > forgetAfter && friend.Payload is { } going)
            {
                // Going quiet is not the same as never having been here. Keep the last thing we knew,
                // and write it to the contact so it survives a restart.
                friend.LastKnownPayload = going;
                friend.LastKnownAt = friend.RelayUpdatedAt ?? last;

                friend.Settings.LastSeenAtUnixMs =
                    new DateTimeOffset(friend.LastKnownAt.Value, TimeSpan.Zero).ToUnixTimeMilliseconds();
                friend.Settings.LastSeenTerritoryId = going.TerritoryTypeId ?? 0;
                friend.Settings.LastSeenActivity = DescribeActivity(friend);
                config.Save();

                friend.Payload = null;
                friend.RelayUpdatedAt = null;
                friend.NearbyPosition = null;
                friend.NearbySeenAt = null;
                friend.Sources = PresenceSource.None;
            }
        }
    }

    private static List<TrackedFriend> Order(IEnumerable<TrackedFriend> source)
        => source
            .OrderByDescending(f => f.Settings.Pinned)
            .ThenByDescending(f => f.IsOnline)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsIdle(Task? task) => task is null || task.IsCompleted;

    public void Dispose()
    {
        SaveOverlaps();
        cancellation.Cancel();
        cancellation.Dispose();
    }
}
