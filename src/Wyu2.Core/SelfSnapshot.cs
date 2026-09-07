using System.Numerics;
using Wyu2.Configuration;
using Wyu2.Protocol;

namespace Wyu2.Tracking;

/// <summary>
/// Everything the plugin knows about the local character at one moment. Nothing here has left the
/// machine yet: <see cref="ToPayload"/> is what decides, per recipient, which parts are allowed out.
/// </summary>
public sealed record SelfSnapshot
{
    public required string CharacterName { get; init; }
    public required string FallbackName { get; init; }
    public uint HomeWorldId { get; init; }
    public uint CurrentWorldId { get; init; }
    public ushort TerritoryTypeId { get; init; }
    public uint MapId { get; init; }
    public uint InstanceId { get; init; }
    public Vector3 Position { get; init; }
    public float Rotation { get; init; }
    public uint JobId { get; init; }
    public byte Level { get; init; }
    public ActivityKind Activity { get; init; }
    public string? ActivityDetail { get; init; }

    /// <summary>
    /// The activity line with the current enemy named. Only used for contacts whose profile allows it,
    /// so a per-contact profile can withhold it even when the default profile does not.
    /// </summary>
    public string? ActivityDetailWithTarget { get; init; }
    public uint OnlineStatusId { get; init; }
    public PresenceFlags Flags { get; init; }
    public byte PartySize { get; init; }
    public string? FreeCompanyTag { get; init; }
    public string? Note { get; init; }
    public long TakenAtUnixMs { get; init; }

    /// <summary>Set when a privacy rule says the position must not go out, whatever the profile says.</summary>
    public bool PositionSuppressed { get; init; }

    /// <summary>
    /// Projects the snapshot through one sharing profile. Fields the profile does not allow are left
    /// unset rather than blanked, so the wire format carries no trace of them at all.
    /// </summary>
    public PresencePayload ToPayload(SharingProfile profile)
    {
        var sharePosition = profile.SharePosition && !PositionSuppressed && profile.ShareZone;

        return new PresencePayload
        {
            Version = ProtocolConstants.Version,
            SentAtUnixMs = TakenAtUnixMs,
            CharacterName = profile.ShareCharacterName ? CharacterName : FallbackName,
            HomeWorldId = profile.ShareWorld ? HomeWorldId : 0,
            CurrentWorldId = profile.ShareWorld ? CurrentWorldId : 0,
            TerritoryTypeId = profile.ShareZone ? TerritoryTypeId : null,
            MapId = profile.ShareZone ? MapId : null,
            InstanceId = profile.ShareZone ? InstanceId : null,
            X = sharePosition ? Position.X : null,
            Y = sharePosition ? Position.Y : null,
            Z = sharePosition ? Position.Z : null,
            Rotation = sharePosition ? Rotation : null,
            JobId = profile.ShareJob ? JobId : null,
            Level = profile.ShareJob ? Level : null,
            Activity = profile.ShareActivity ? Activity : ActivityKind.Unknown,
            ActivityDetail = profile.ShareActivity ? DetailFor(profile) : null,
            OnlineStatusId = profile.ShareOnlineStatus ? OnlineStatusId : null,
            Flags = profile.ShareActivity ? Flags : PresenceFlags.None,
            PartySize = profile.ShareParty ? PartySize : null,
            FreeCompanyTag = profile.ShareFreeCompany ? FreeCompanyTag : null,
            Note = profile.ShareNote ? Note : null,
        };
    }

    private string? DetailFor(SharingProfile profile)
        => profile.ShareCombatTarget ? ActivityDetailWithTarget ?? ActivityDetail : ActivityDetail;
}
