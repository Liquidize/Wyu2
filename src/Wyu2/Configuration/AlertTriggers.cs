namespace Wyu2.Configuration;

/// <summary>
/// Which changes in a contact's state are worth interrupting you for. Off for everybody by default:
/// a plugin that pings on every zone change for twenty contacts is a plugin people uninstall.
/// </summary>
[Flags]
public enum AlertTriggers
{
    None = 0,

    /// <summary>They started sharing after a spell of silence.</summary>
    CameOnline = 1 << 0,

    /// <summary>They stopped publishing for longer than the forget window.</summary>
    WentOffline = 1 << 1,

    /// <summary>They arrived in the zone and instance you are standing in.</summary>
    EnteredMyZone = 1 << 2,

    /// <summary>They moved to a different zone, wherever you are.</summary>
    ChangedZone = 1 << 3,

    /// <summary>They went into a duty.</summary>
    EnteredDuty = 1 << 4,

    /// <summary>They came out of a duty, which is usually when they are free again.</summary>
    LeftDuty = 1 << 5,
}
