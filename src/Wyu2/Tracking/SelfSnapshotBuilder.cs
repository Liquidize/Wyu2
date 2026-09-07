using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Enums;
using Wyu2.Configuration;
using Wyu2.Game;
using Wyu2.Protocol;

namespace Wyu2.Tracking;

/// <summary>Why the plugin decided not to publish this tick.</summary>
public enum PublishBlockReason
{
    None,
    NotLoggedIn,
    SharingDisabled,
    Paused,
    NoRelayAccount,
    PvpZone,
    HousingZone,
    InDuty,
    Busy,
    BlockedTerritory,
}

/// <summary>
/// Reads the local character once per publish tick and applies the privacy rules that can veto or trim a
/// snapshot before any profile is even considered. Must run on the framework thread.
/// </summary>
public sealed class SelfSnapshotBuilder(Configuration.Configuration config, GameDataCache data, ActivityResolver activity)
{
    private Vector3 lastPosition;
    private DateTime lastMovedAt = DateTime.MinValue;

    /// <summary>Set when the last call to <see cref="TryBuild"/> refused to produce a snapshot.</summary>
    public PublishBlockReason LastBlockReason { get; private set; } = PublishBlockReason.NotLoggedIn;

    public bool TryBuild(out SelfSnapshot? snapshot)
    {
        snapshot = null;

        var player = Service.Objects.LocalPlayer;
        if (player is null || !Service.ClientState.IsLoggedIn)
        {
            LastBlockReason = PublishBlockReason.NotLoggedIn;
            return false;
        }

        if (!config.SharingEnabled)
        {
            LastBlockReason = PublishBlockReason.SharingDisabled;
            return false;
        }

        if (config.IsPaused)
        {
            LastBlockReason = PublishBlockReason.Paused;
            return false;
        }

        var territory = (ushort)Service.ClientState.TerritoryType;
        var use = data.GetIntendedUse(territory);
        var rules = config.Privacy;

        if (rules.BlockedTerritories.Contains(territory))
        {
            LastBlockReason = PublishBlockReason.BlockedTerritory;
            return false;
        }

        if (rules.PauseInPvp && Service.ClientState.IsPvP)
        {
            LastBlockReason = PublishBlockReason.PvpZone;
            return false;
        }

        var housing = GameDataCache.IsHousing(use);
        if (rules.PauseInHousing && housing)
        {
            LastBlockReason = PublishBlockReason.HousingZone;
            return false;
        }

        var flags = activity.ResolveFlags(player);
        var bound = flags.HasFlag(PresenceFlags.Bound);
        if (rules.PauseInDuty && bound)
        {
            LastBlockReason = PublishBlockReason.InDuty;
            return false;
        }

        if (rules.PauseWhenBusy && flags.HasFlag(PresenceFlags.Busy))
        {
            LastBlockReason = PublishBlockReason.Busy;
            return false;
        }

        var position = player.Position;
        var moving = TrackMovement(position);
        var resolved = activity.Resolve(player, moving);

        var suppressPosition =
            (rules.HidePositionInDuty && bound) ||
            (rules.HidePositionInHousing && housing);

        snapshot = new SelfSnapshot
        {
            CharacterName = player.Name.TextValue,
            FallbackName = string.IsNullOrWhiteSpace(config.DisplayName) ? "A friend" : config.DisplayName,
            HomeWorldId = player.HomeWorld.RowId,
            CurrentWorldId = player.CurrentWorld.RowId,
            TerritoryTypeId = territory,
            MapId = Service.ClientState.MapId,
            InstanceId = Service.ClientState.Instance,
            Position = position,
            Rotation = player.Rotation,
            JobId = player.ClassJob.RowId,
            Level = player.Level,
            Activity = resolved.Kind,
            ActivityDetail = resolved.Detail,
            ActivityDetailWithTarget = resolved.DetailWithTarget,
            OnlineStatusId = player.OnlineStatus.RowId,
            Flags = flags,
            PartySize = (byte)Math.Clamp(Service.Party.Length, 0, 255),
            FreeCompanyTag = NullIfBlank(player.CompanyTag.TextValue),
            Note = NullIfBlank(config.StatusNote),
            TakenAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PositionSuppressed = suppressPosition,
        };

        LastBlockReason = PublishBlockReason.None;
        return true;
    }

    /// <summary>
    /// A character counts as travelling for a few seconds after their last real movement, so standing
    /// still at a traffic light does not flip the activity line back and forth.
    /// </summary>
    private bool TrackMovement(Vector3 position)
    {
        if (MapMath.FlatDistance(position, lastPosition) > 0.75f)
        {
            lastPosition = position;
            lastMovedAt = DateTime.UtcNow;
            return true;
        }

        return DateTime.UtcNow - lastMovedAt < TimeSpan.FromSeconds(4);
    }

    /// <summary>Human readable explanation for the status line.</summary>
    public static string Explain(PublishBlockReason reason) => reason switch
    {
        PublishBlockReason.None => "Sharing",
        PublishBlockReason.NotLoggedIn => "Not logged in",
        PublishBlockReason.SharingDisabled => "Sharing is off",
        PublishBlockReason.Paused => "Sharing paused",
        PublishBlockReason.NoRelayAccount => "No relay account",
        PublishBlockReason.PvpZone => "Paused: PvP zone",
        PublishBlockReason.HousingZone => "Paused: housing",
        PublishBlockReason.InDuty => "Paused: in a duty",
        PublishBlockReason.Busy => "Paused: status is busy",
        PublishBlockReason.BlockedTerritory => "Paused: blocked zone",
        _ => "Not sharing",
    };

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
