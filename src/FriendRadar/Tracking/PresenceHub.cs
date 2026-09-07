using System.Collections.Concurrent;
using FriendRadar.Game;
using FriendRadar.Model;
using FriendRadar.Net;
using FriendRadar.Protocol;

namespace FriendRadar.Tracking;

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
    }

    /// <summary>Friends with any data at all, ordered for display.</summary>
    public IReadOnlyList<TrackedFriend> Friends { get; private set; } = [];

    /// <summary>Raised when a friend moves to a different zone, so the plugin can shout about it.</summary>
    public event Action<TrackedFriend, string>? FriendChangedZone;

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
        ForgetStaleFriends(now);
        Friends = Order(friends.Values);

        if (!session.HasAccount)
            return;

        if (IsIdle(contactTask) && now - lastContactSync > TimeSpan.FromSeconds(60))
        {
            lastContactSync = now;
            contactTask = Task.Run(() => session.RefreshContactsAsync(cancellation.Token), cancellation.Token);
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
            contactTask = Task.Run(() => session.RefreshContactsAsync(cancellation.Token), cancellation.Token);
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
            .Where(c => c.ShareWithThem && !string.IsNullOrEmpty(c.PublicKey))
            .Select(c => (c.AccountId, c.PublicKey, Profile: config.ProfileFor(c)))
            .ToList();

        PublishedRecipients = recipients.Count;
        if (recipients.Count == 0)
            return;

        publishTask = Task.Run(async () =>
        {
            var envelopes = new List<PresenceEnvelope>(recipients.Count);
            foreach (var (accountId, publicKey, profile) in recipients)
            {
                var key = session.GetOutboundKey(accountId, publicKey);
                if (key is null)
                    continue;

                try
                {
                    envelopes.Add(PresenceCrypto.Seal(key, snapshot.ToPayload(profile), config.AccountId, accountId));
                }
                catch (Exception ex)
                {
                    Service.Log.Warning(ex, "Could not encrypt presence for {Contact}", accountId);
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
            if (contact is null || !contact.ShowThem || string.IsNullOrEmpty(contact.PublicKey))
                continue;

            var key = session.GetInboundKey(entry.SenderAccountId, contact.PublicKey);
            if (key is null)
                continue;

            var payload = PresenceCrypto.Open(
                key, entry.Nonce, entry.Ciphertext, entry.SenderAccountId, config.AccountId);

            if (payload is null)
            {
                Service.Log.Debug("Dropped an unreadable presence blob from {Contact}", entry.SenderAccountId);
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

            var previousZone = friend.Payload?.TerritoryTypeId;
            var previousInstance = friend.Payload?.InstanceId;

            friend.Settings = settings;
            friend.RelayDisplayName = displayName;
            friend.Payload = payload;
            friend.RelayUpdatedAt = DateTime.UtcNow;
            friend.Sources |= PresenceSource.Relay;

            if (payload.TerritoryTypeId is { } zone &&
                (previousZone != zone || previousInstance != payload.InstanceId) &&
                previousZone is not null)
            {
                FriendChangedZone?.Invoke(friend, data.GetZoneName(zone));
            }
        }
    }

    // ---------------------------------------------------------------- derived data

    private void RefreshDerivedData()
    {
        foreach (var friend in friends.Values)
        {
            var payload = friend.Payload;
            if (payload is null)
                continue;

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

            if (friend.LastUpdate is { } last && now - last > forgetAfter)
            {
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
        cancellation.Cancel();
        cancellation.Dispose();
    }
}
