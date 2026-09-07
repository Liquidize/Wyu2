using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Wyu2.Model;

namespace Wyu2.Tracking;

/// <summary>A player the game is currently rendering near you.</summary>
public sealed record NearbyPlayer(
    uint EntityId,
    string Name,
    uint HomeWorldId,
    Vector3 Position,
    float Rotation,
    uint JobId,
    byte Level,
    bool IsGameFriend);

/// <summary>
/// Reads the object table so contacts standing next to you are plotted from live game data instead of a
/// relay update that is a few seconds old. Runs on the framework thread.
/// </summary>
public sealed class NearbyScanner(Configuration.Configuration config)
{
    private readonly List<NearbyPlayer> nearby = [];

    /// <summary>Players the game marks as friends, for the (off by default) non-consenting mode.</summary>
    public IReadOnlyList<NearbyPlayer> NearbyGameFriends { get; private set; } = [];

    /// <summary>Refreshes the nearby list and folds live positions into matching contacts.</summary>
    public void Scan(IEnumerable<TrackedFriend> friends)
    {
        nearby.Clear();

        if (!Service.ClientState.IsLoggedIn)
        {
            NearbyGameFriends = [];
            return;
        }

        foreach (var obj in Service.Objects)
        {
            if (obj.ObjectKind != ObjectKind.Pc || obj is not IPlayerCharacter character)
                continue;

            if (character.EntityId == Service.Objects.LocalPlayer?.EntityId)
                continue;

            var name = character.Name.TextValue;
            if (string.IsNullOrEmpty(name))
                continue;

            nearby.Add(new NearbyPlayer(
                character.EntityId,
                name,
                character.HomeWorld.RowId,
                character.Position,
                character.Rotation,
                character.ClassJob.RowId,
                character.Level,
                character.StatusFlags.HasFlag(StatusFlags.Friend)));
        }

        NearbyGameFriends = config.TrackNonConsentingGameFriends
            ? nearby.Where(p => p.IsGameFriend).ToList()
            : [];

        if (!config.UseNearbyScanForContacts)
            return;

        var now = DateTime.UtcNow;
        foreach (var friend in friends)
        {
            var match = MatchContact(friend);
            if (match is null)
            {
                // Only forget the live position once it has gone stale, so a friend who steps behind a
                // wall for a moment does not flicker off the radar.
                if (friend.NearbySeenAt is { } seen && now - seen > TimeSpan.FromSeconds(10))
                {
                    friend.NearbyPosition = null;
                    friend.NearbyEntityId = null;
                    friend.NearbySeenAt = null;
                }

                continue;
            }

            friend.NearbyPosition = match.Position;
            friend.NearbyRotation = match.Rotation;
            friend.NearbyEntityId = match.EntityId;
            friend.NearbyName = match.Name;
            friend.NearbySeenAt = now;
            friend.Sources |= PresenceSource.Nearby;
        }
    }

    /// <summary>
    /// Finds the object table entry for a contact. Matching is by the character name they chose to share
    /// plus their home world; a contact who shares no name cannot be matched, which is the point.
    /// </summary>
    private NearbyPlayer? MatchContact(TrackedFriend friend)
    {
        var payload = friend.Payload;
        var name = payload?.CharacterName;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        foreach (var candidate in nearby)
        {
            if (!string.Equals(candidate.Name, name, StringComparison.Ordinal))
                continue;

            // Home world 0 means the contact did not share it, so name alone has to do.
            if (payload!.HomeWorldId != 0 && candidate.HomeWorldId != payload.HomeWorldId)
                continue;

            return candidate;
        }

        return null;
    }
}
