using Dalamud.Interface.ImGuiNotification;
using FFXIVClientStructs.FFXIV.Client.UI;
using Wyu2.Configuration;
using Wyu2.Game;
using Wyu2.Model;
using Wyu2.Protocol;

namespace Wyu2.Tracking;

/// <summary>
/// Watches contacts for the transitions somebody asked to hear about, and raises them once. Runs on the
/// framework thread off the back of the hub's friend list.
/// </summary>
public sealed unsafe class AlertService(Configuration.Configuration config, GameDataCache data)
{
    private readonly Dictionary<string, ContactState> previous = new(StringComparer.Ordinal);
    private readonly GeofenceWatcher fences = new();

    /// <summary>What we knew about a contact last tick, so a change can be spotted.</summary>
    private readonly record struct ContactState(bool Online, ushort Territory, uint Instance, bool InDuty);

    public void Evaluate(IReadOnlyList<TrackedFriend> friends)
    {
        if (!Service.ClientState.IsLoggedIn)
        {
            // Logging back in should not replay everything that happened while we were away.
            previous.Clear();
            fences.Clear();
            return;
        }

        var myTerritory = (ushort)Service.ClientState.TerritoryType;
        var myInstance = Service.ClientState.Instance;

        foreach (var friend in friends)
        {
            var current = Snapshot(friend);

            // The first time a contact is seen we only record them. Otherwise every login would fire a
            // "came online" for the entire list at once.
            if (!previous.TryGetValue(friend.AccountId, out var before))
            {
                previous[friend.AccountId] = current;
                continue;
            }

            previous[friend.AccountId] = current;

            if (!config.AlertsEnabled || !config.CanSee(friend.Settings))
                continue;

            var wanted = friend.Settings.Alerts;
            if (wanted == AlertTriggers.None)
                continue;

            Compare(friend, before, current, wanted, myTerritory, myInstance);
        }

        EvaluateGeofences(friends);

        // Forget contacts that have gone away entirely, so re-adding somebody starts clean.
        if (previous.Count > friends.Count)
        {
            var live = friends.Select(f => f.AccountId).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in previous.Keys.Where(k => !live.Contains(k)).ToList())
            {
                previous.Remove(stale);
                fences.Forget(stale);
            }
        }
    }

    /// <summary>
    /// Runs every visible contact past the user's areas. Crossings are reported by the watcher, which
    /// only fires on a transition, so standing inside a fence stays quiet.
    /// </summary>
    private void EvaluateGeofences(IReadOnlyList<TrackedFriend> friends)
    {
        if (!config.AlertsEnabled || !config.GeofenceAlertsEnabled || config.Geofences.Count == 0)
            return;

        var rules = config.Geofences.Select(g => g.ToRule()).ToList();
        var me = Service.Objects.LocalPlayer?.Position;

        foreach (var friend in friends)
        {
            if (!config.CanSee(friend.Settings))
                continue;

            var crossings = fences.Evaluate(
                friend.AccountId,
                friend.Payload?.TerritoryTypeId ?? 0,
                friend.Position,
                rules,
                me);

            foreach (var crossing in crossings)
            {
                var rule = config.Geofences.FirstOrDefault(g => g.Id == crossing.RuleId);
                var where = string.IsNullOrWhiteSpace(rule?.Name) ? "one of your areas" : rule!.Name;

                Raise(
                    crossing.Entered
                        ? $"{friend.Name} entered {where}."
                        : $"{friend.Name} left {where}.",
                    crossing.Entered ? NotificationType.Success : NotificationType.Info);
            }
        }
    }

    private void Compare(
        TrackedFriend friend,
        ContactState before,
        ContactState now,
        AlertTriggers wanted,
        ushort myTerritory,
        uint myInstance)
    {
        var name = friend.Name;

        if (now.Online && !before.Online)
        {
            if (wanted.HasFlag(AlertTriggers.CameOnline))
                Raise($"{name} is online.", NotificationType.Success);
        }
        else if (!now.Online && before.Online)
        {
            if (wanted.HasFlag(AlertTriggers.WentOffline))
                Raise($"{name} went offline.", NotificationType.Info);

            // Nothing else is meaningful once they are gone.
            return;
        }

        if (!now.Online)
            return;

        var movedZone = now.Territory != before.Territory || now.Instance != before.Instance;
        if (movedZone)
        {
            var zone = data.GetZoneName(now.Territory);
            var here = now.Territory == myTerritory && now.Instance == myInstance;

            if (here && wanted.HasFlag(AlertTriggers.EnteredMyZone))
                Raise($"{name} just arrived in {Describe(zone)}.", NotificationType.Success);
            else if (wanted.HasFlag(AlertTriggers.ChangedZone))
                Raise($"{name} moved to {Describe(zone)}.", NotificationType.Info);
        }

        if (now.InDuty && !before.InDuty && wanted.HasFlag(AlertTriggers.EnteredDuty))
            Raise($"{name} started {Describe(friend.ActivityText)}.", NotificationType.Info);
        else if (!now.InDuty && before.InDuty && wanted.HasFlag(AlertTriggers.LeftDuty))
            Raise($"{name} finished their duty.", NotificationType.Success);
    }

    private static ContactState Snapshot(TrackedFriend friend)
    {
        var payload = friend.Payload;
        if (payload is null)
            return new ContactState(false, 0, 0, false);

        return new ContactState(
            true,
            payload.TerritoryTypeId ?? 0,
            payload.InstanceId ?? 0,
            payload.Flags.HasFlag(PresenceFlags.Bound));
    }

    private static string Describe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "somewhere else" : value;

    private void Raise(string message, NotificationType type)
    {
        if (config.AlertInChat)
            Service.Chat.Print(message, "Wyu2");

        if (config.AlertAsNotification)
        {
            Service.Notifications.AddNotification(new Notification
            {
                Title = "Wyu2",
                Content = message,
                Type = type,
                InitialDuration = TimeSpan.FromSeconds(6),
            });
        }

        if (config.AlertSound)
            UIGlobals.PlayChatSoundEffect((uint)Math.Clamp(config.AlertSoundId, 1, 16));
    }
}
