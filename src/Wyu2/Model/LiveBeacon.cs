using System.Numerics;
using Wyu2.Protocol;

namespace Wyu2.Model;

/// <summary>
/// A decrypted beacon: somewhere a contact pointed at, or somewhere you pointed at yourself. Held only
/// for as long as the relay says it lives, then dropped without ceremony.
/// </summary>
public sealed class LiveBeacon
{
    /// <summary>The sender's own id for this beacon, unique within their beacons.</summary>
    public required string BeaconId { get; init; }

    public required string SenderAccountId { get; init; }

    /// <summary>Whoever dropped it, by whatever name you know them under.</summary>
    public required string SenderName { get; init; }

    public required BeaconPayload Payload { get; init; }

    public DateTime ExpiresAt { get; init; }

    /// <summary>True for beacons you dropped, which are the only ones you can withdraw.</summary>
    public bool IsMine { get; init; }

    /// <summary>Zone name resolved from the payload, for the list and the tooltips.</summary>
    public string ZoneName { get; set; } = string.Empty;

    /// <summary>Map coordinates, the numbers the game itself shows.</summary>
    public Vector2? MapCoordinates { get; set; }

    /// <summary>Map the coordinates belong to, so a click can open the game's own map there.</summary>
    public uint MapId { get; set; }

    /// <summary>Unique across senders, since two people may pick the same id for their own beacons.</summary>
    public string Key => SenderAccountId + "|" + BeaconId;

    public BeaconKind Kind => Payload.Kind;

    /// <summary>What the sender typed, or a stand-in when they left the label empty.</summary>
    public string Label => string.IsNullOrWhiteSpace(Payload.Label) ? "Unlabelled beacon" : Payload.Label;

    public Vector3 Position => new(Payload.X, Payload.Y, Payload.Z);

    public TimeSpan Remaining => ExpiresAt - DateTime.UtcNow;

    public bool HasExpired => DateTime.UtcNow >= ExpiresAt;

    /// <summary>
    /// Whether this beacon marks a spot you could actually walk to from where you are standing: the same
    /// zone, the same public instance and the same world. Coordinates from another copy of the map would
    /// otherwise be drawn as if they were here.
    /// </summary>
    public bool IsInSameInstance(ushort territory, uint instance, uint world)
    {
        if (Payload.TerritoryTypeId != territory || Payload.InstanceId != instance)
            return false;

        return Payload.WorldId == 0 || world == 0 || Payload.WorldId == world;
    }
}
