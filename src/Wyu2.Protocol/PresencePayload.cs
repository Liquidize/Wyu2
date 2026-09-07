using System.Text.Json.Serialization;

namespace Wyu2.Protocol;

/// <summary>
/// What a character is doing right now. Coarse buckets; <see cref="PresencePayload.ActivityDetail"/>
/// carries the specific name (duty, FATE, recipe, ...).
/// </summary>
public enum ActivityKind
{
    Unknown = 0,
    Idle = 1,
    Travelling = 2,
    InCombat = 3,
    InDuty = 4,
    InPvp = 5,
    Crafting = 6,
    Gathering = 7,
    Fishing = 8,
    Cutscene = 9,
    Waiting = 10,
    Housing = 11,
    GoldSaucer = 12,
    Performing = 13,
    Trading = 14,
    Offline = 15,
}

/// <summary>
/// Cheap booleans that ride along with a presence update.
/// </summary>
[Flags]
public enum PresenceFlags
{
    None = 0,
    InCombat = 1 << 0,
    Mounted = 1 << 1,
    Flying = 1 << 2,
    InCutscene = 1 << 3,
    InParty = 1 << 4,
    InQueue = 1 << 5,
    Bound = 1 << 6,
    Afk = 1 << 7,
    Busy = 1 << 8,
    Dead = 1 << 9,
    Sanctuary = 1 << 10,
}

/// <summary>
/// The plaintext that goes inside an encrypted envelope. Every optional member is omitted when the
/// publishing user's sharing profile says not to share that detail, so a recipient can only ever see
/// what was deliberately sent.
/// </summary>
public sealed record PresencePayload
{
    /// <summary>Payload schema version.</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = ProtocolConstants.Version;

    /// <summary>When the sender built this snapshot (unix milliseconds, UTC).</summary>
    [JsonPropertyName("t")]
    public long SentAtUnixMs { get; init; }

    /// <summary>Character name of the sender, as they chose to present it.</summary>
    [JsonPropertyName("name")]
    public string CharacterName { get; init; } = string.Empty;

    /// <summary>Home world row id, or 0 when not shared.</summary>
    [JsonPropertyName("hw")]
    public uint HomeWorldId { get; init; }

    /// <summary>World the character is currently on (differs from home when travelling).</summary>
    [JsonPropertyName("cw")]
    public uint CurrentWorldId { get; init; }

    /// <summary>TerritoryType row id of the current zone.</summary>
    [JsonPropertyName("tt")]
    public ushort? TerritoryTypeId { get; init; }

    /// <summary>Map row id used to render the zone map.</summary>
    [JsonPropertyName("map")]
    public uint? MapId { get; init; }

    /// <summary>Public instance number (1, 2, 3 ...), 0 or null when the zone is not instanced.</summary>
    [JsonPropertyName("inst")]
    public uint? InstanceId { get; init; }

    /// <summary>World space X, only present when position sharing is on.</summary>
    [JsonPropertyName("x")]
    public float? X { get; init; }

    /// <summary>World space Y (height).</summary>
    [JsonPropertyName("y")]
    public float? Y { get; init; }

    /// <summary>World space Z.</summary>
    [JsonPropertyName("z")]
    public float? Z { get; init; }

    /// <summary>Facing, in radians.</summary>
    [JsonPropertyName("r")]
    public float? Rotation { get; init; }

    /// <summary>ClassJob row id.</summary>
    [JsonPropertyName("job")]
    public uint? JobId { get; init; }

    /// <summary>Level of the current class/job.</summary>
    [JsonPropertyName("lvl")]
    public byte? Level { get; init; }

    /// <summary>Coarse activity bucket.</summary>
    [JsonPropertyName("act")]
    public ActivityKind Activity { get; init; } = ActivityKind.Unknown;

    /// <summary>Human readable specifics, e.g. the duty or FATE name.</summary>
    [JsonPropertyName("detail")]
    public string? ActivityDetail { get; init; }

    /// <summary>OnlineStatus row id (AFK, busy, role playing, ...).</summary>
    [JsonPropertyName("os")]
    public uint? OnlineStatusId { get; init; }

    /// <summary>Boolean state bundle.</summary>
    [JsonPropertyName("f")]
    public PresenceFlags Flags { get; init; }

    /// <summary>Number of players in the sender's party, when shared.</summary>
    [JsonPropertyName("ps")]
    public byte? PartySize { get; init; }

    /// <summary>Free company tag, when shared.</summary>
    [JsonPropertyName("fc")]
    public string? FreeCompanyTag { get; init; }

    /// <summary>Free-form status note the sender typed into the plugin.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; init; }

    /// <summary>True when the sender has a position that can be plotted.</summary>
    [JsonIgnore]
    public bool HasPosition => X.HasValue && Z.HasValue;
}
