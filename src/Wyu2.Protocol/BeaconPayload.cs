using System.Text.Json.Serialization;

namespace Wyu2.Protocol;

/// <summary>What a beacon is for, which decides how it is drawn and how loudly it is announced.</summary>
public enum BeaconKind
{
    /// <summary>A plain "look here".</summary>
    Marker = 0,

    /// <summary>Meet me at this spot.</summary>
    Rally = 1,

    /// <summary>A hunt mark.</summary>
    Hunt = 2,

    /// <summary>A FATE worth joining.</summary>
    Fate = 3,

    /// <summary>A treasure map or a portal.</summary>
    Treasure = 4,

    /// <summary>Something to avoid.</summary>
    Danger = 5,

    /// <summary>A gathering node.</summary>
    Node = 6,
}

/// <summary>
/// The plaintext inside a beacon envelope: a labelled point in the world that one person drops and
/// everybody they are linked with can see. Separate from presence because presence is a single blob per
/// sender that gets replaced on every publish, and a beacon has to stick around on its own timer.
/// </summary>
public sealed record BeaconPayload
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = ProtocolConstants.Version;

    [JsonPropertyName("t")]
    public long CreatedAtUnixMs { get; init; }

    [JsonPropertyName("kind")]
    public BeaconKind Kind { get; init; } = BeaconKind.Marker;

    /// <summary>What the person typed, e.g. "Nunyunuwi up" or "portal here".</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("tt")]
    public ushort TerritoryTypeId { get; init; }

    [JsonPropertyName("map")]
    public uint MapId { get; init; }

    [JsonPropertyName("inst")]
    public uint InstanceId { get; init; }

    /// <summary>World the beacon was dropped on, so a different data centre is not plotted as local.</summary>
    [JsonPropertyName("w")]
    public uint WorldId { get; init; }

    [JsonPropertyName("x")]
    public float X { get; init; }

    [JsonPropertyName("y")]
    public float Y { get; init; }

    [JsonPropertyName("z")]
    public float Z { get; init; }
}
