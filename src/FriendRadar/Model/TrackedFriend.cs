using System.Numerics;
using FriendRadar.Configuration;
using FriendRadar.Protocol;

namespace FriendRadar.Model;

/// <summary>Where a piece of information came from.</summary>
[Flags]
public enum PresenceSource
{
    None = 0,
    /// <summary>Decrypted from a relay blob the contact published.</summary>
    Relay = 1,
    /// <summary>Read from the game's object table because they are standing near you.</summary>
    Nearby = 2,
}

/// <summary>
/// One contact plus everything currently known about them. Fields are refreshed in place by the hub;
/// the UI reads copies of the list rather than holding on to instances.
/// </summary>
public sealed class TrackedFriend
{
    public required string AccountId { get; init; }

    /// <summary>Relay label, used when the contact does not share a character name.</summary>
    public string RelayDisplayName { get; set; } = string.Empty;

    /// <summary>Local settings for this contact.</summary>
    public ContactSettings Settings { get; set; } = new();

    /// <summary>Latest decrypted payload, or null when they have not published anything yet.</summary>
    public PresencePayload? Payload { get; set; }

    /// <summary>When the relay payload arrived, local clock.</summary>
    public DateTime? RelayUpdatedAt { get; set; }

    /// <summary>Live position from the object table, when they are rendered near you.</summary>
    public Vector3? NearbyPosition { get; set; }

    public float? NearbyRotation { get; set; }

    public DateTime? NearbySeenAt { get; set; }

    /// <summary>Entity id of their character object while it is loaded.</summary>
    public uint? NearbyEntityId { get; set; }

    /// <summary>Character name as observed nearby; helps when they do not share their name.</summary>
    public string? NearbyName { get; set; }

    // ------------------------------------------------------------------ derived

    public string ZoneName { get; set; } = string.Empty;
    public string RegionName { get; set; } = string.Empty;
    public string WorldName { get; set; } = string.Empty;
    public string JobName { get; set; } = string.Empty;
    public string JobAbbreviation { get; set; } = string.Empty;
    public string ActivityText { get; set; } = string.Empty;
    public string OnlineStatusName { get; set; } = string.Empty;
    public uint MapId { get; set; }

    /// <summary>Map coordinates (the numbers the game shows), when a position is known.</summary>
    public Vector2? MapCoordinates { get; set; }

    public PresenceSource Sources { get; set; }

    /// <summary>Best available display name: alias, then shared character name, then relay label.</summary>
    public string Name
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Settings.Alias))
                return Settings.Alias!;
            if (!string.IsNullOrWhiteSpace(Payload?.CharacterName))
                return Payload!.CharacterName;
            if (!string.IsNullOrWhiteSpace(NearbyName))
                return NearbyName!;
            return string.IsNullOrWhiteSpace(RelayDisplayName) ? "(unknown)" : RelayDisplayName;
        }
    }

    /// <summary>Most recent evidence of any kind.</summary>
    public DateTime? LastUpdate
    {
        get
        {
            if (RelayUpdatedAt is null)
                return NearbySeenAt;
            if (NearbySeenAt is null)
                return RelayUpdatedAt;
            return RelayUpdatedAt > NearbySeenAt ? RelayUpdatedAt : NearbySeenAt;
        }
    }

    public TimeSpan? Age => LastUpdate is null ? null : DateTime.UtcNow - LastUpdate.Value;

    /// <summary>Position to draw: the live one if we can see them, otherwise the shared one.</summary>
    public Vector3? Position
    {
        get
        {
            if (NearbyPosition is not null && NearbySeenAt is not null &&
                DateTime.UtcNow - NearbySeenAt.Value < TimeSpan.FromSeconds(5))
            {
                return NearbyPosition;
            }

            var payload = Payload;
            if (payload is { X: { } x, Z: { } z })
                return new Vector3(x, payload.Y ?? 0f, z);

            return null;
        }
    }

    public ushort? TerritoryTypeId => Payload?.TerritoryTypeId;

    public uint InstanceId => Payload?.InstanceId ?? 0;

    public bool IsOnline => Payload is not null && Payload.Activity != ActivityKind.Offline;
}
