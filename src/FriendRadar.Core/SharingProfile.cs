namespace FriendRadar.Configuration;

/// <summary>
/// Exactly which details leave this machine. Everything here is additive: a field that is off is never
/// put into a payload, so a recipient cannot tell the difference between "not shared" and "not known".
/// </summary>
public sealed class SharingProfile
{
    /// <summary>Send the character name. When off, contacts see the alias you registered with instead.</summary>
    public bool ShareCharacterName { get; set; } = true;

    /// <summary>Send home and current world.</summary>
    public bool ShareWorld { get; set; } = true;

    /// <summary>Send the zone (and public instance number) you are in.</summary>
    public bool ShareZone { get; set; } = true;

    /// <summary>Send exact coordinates so contacts can plot you on the radar and map.</summary>
    public bool SharePosition { get; set; } = true;

    /// <summary>Send the activity bucket and its detail line (duty name, FATE name, ...).</summary>
    public bool ShareActivity { get; set; } = true;

    /// <summary>Send current job and level.</summary>
    public bool ShareJob { get; set; } = true;

    /// <summary>Send the game's own online status (AFK, busy, role playing, ...).</summary>
    public bool ShareOnlineStatus { get; set; } = true;

    /// <summary>Send how many people are in your party.</summary>
    public bool ShareParty { get; set; }

    /// <summary>Send your free company tag.</summary>
    public bool ShareFreeCompany { get; set; }

    /// <summary>Send the name of what you are fighting, as part of the activity detail.</summary>
    public bool ShareCombatTarget { get; set; }

    /// <summary>Send the status note you typed into the plugin.</summary>
    public bool ShareNote { get; set; } = true;

    public SharingProfile Clone() => (SharingProfile)MemberwiseClone();

    /// <summary>The most private profile that is still useful: "online, somewhere".</summary>
    public static SharingProfile Minimal() => new()
    {
        ShareCharacterName = false,
        ShareWorld = false,
        ShareZone = false,
        SharePosition = false,
        ShareActivity = false,
        ShareJob = false,
        ShareOnlineStatus = true,
        ShareParty = false,
        ShareFreeCompany = false,
        ShareCombatTarget = false,
        ShareNote = true,
    };

    /// <summary>Zone and activity, but no coordinates.</summary>
    public static SharingProfile ZoneOnly() => new()
    {
        SharePosition = false,
        ShareCombatTarget = false,
    };

    /// <summary>Everything the plugin can send.</summary>
    public static SharingProfile Everything() => new()
    {
        ShareParty = true,
        ShareFreeCompany = true,
        ShareCombatTarget = true,
    };
}

/// <summary>Situations where sharing is automatically suspended or trimmed.</summary>
public sealed class PrivacyRules
{
    /// <summary>Stop publishing entirely while in a PvP instance.</summary>
    public bool PauseInPvp { get; set; } = true;

    /// <summary>Stop publishing entirely while in housing areas.</summary>
    public bool PauseInHousing { get; set; }

    /// <summary>Stop publishing entirely while inside a duty.</summary>
    public bool PauseInDuty { get; set; }

    /// <summary>Stop publishing while your online status is Busy or "do not disturb".</summary>
    public bool PauseWhenBusy { get; set; }

    /// <summary>Keep publishing inside duties, but drop the coordinates.</summary>
    public bool HidePositionInDuty { get; set; } = true;

    /// <summary>Keep publishing in housing areas, but drop the coordinates.</summary>
    public bool HidePositionInHousing { get; set; } = true;

    /// <summary>TerritoryType ids that are never shared, whatever else is enabled.</summary>
    public List<ushort> BlockedTerritories { get; set; } = [];
}
