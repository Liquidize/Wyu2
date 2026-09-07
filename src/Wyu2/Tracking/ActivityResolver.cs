using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Enums;
using Wyu2.Game;
using Wyu2.Protocol;
using CsOnlineStatus = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyCommonList.CharacterData.OnlineStatus;
using RecipeNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote;

namespace Wyu2.Tracking;

/// <summary>
/// The activity line, in two variants: <paramref name="Detail"/> is always safe to send, and
/// <paramref name="DetailWithTarget"/> is set only when naming the current enemy would add something.
/// Keeping both lets a per-contact profile decide, rather than the default profile deciding for everyone.
/// </summary>
public readonly record struct ActivityResult(ActivityKind Kind, string? Detail, string? DetailWithTarget = null);

/// <summary>
/// Turns the pile of condition flags the game exposes into the one line a friend actually wants to read:
/// "In Alexander - The Fist of the Father", "Fishing", "Fighting Ixali Windtalker", "Idle in Limsa".
/// </summary>
public sealed unsafe class ActivityResolver(GameDataCache data)
{
    /// <summary>Works out what the local player is up to.</summary>
    /// <param name="player">The local character.</param>
    /// <param name="isMoving">Whether they have moved recently; distinguishes idling from travelling.</param>
    public ActivityResult Resolve(IPlayerCharacter player, bool isMoving)
    {
        var condition = Service.Condition;
        var territory = Service.ClientState.TerritoryType;
        var use = data.GetIntendedUse(territory);

        if (condition[ConditionFlag.WatchingCutscene] ||
            condition[ConditionFlag.WatchingCutscene78] ||
            condition[ConditionFlag.OccupiedInCutSceneEvent])
        {
            return new ActivityResult(ActivityKind.Cutscene, "Watching a cutscene");
        }

        if (Service.ClientState.IsPvP)
            return new ActivityResult(ActivityKind.InPvp, DutyName(territory) ?? "In PvP");

        if (IsBoundByDuty(condition))
        {
            var name = DutyName(territory) ?? data.GetZoneName(territory);
            var detail = Service.DutyState.IsDutyStarted ? $"{name} (in progress)" : name;
            return new ActivityResult(ActivityKind.InDuty, detail);
        }

        if (condition[ConditionFlag.Fishing])
            return new ActivityResult(ActivityKind.Fishing, FishingDetail(territory));

        if (condition[ConditionFlag.Gathering] || condition[ConditionFlag.ExecutingGatheringAction])
        {
            // While gathering the node is your target, which is the cheapest way to name it.
            var node = GatheringNodeName(player);
            return new ActivityResult(
                ActivityKind.Gathering,
                node is null ? $"Gathering as {JobAbbreviation(player)}" : $"Gathering {node}");
        }

        if (condition[ConditionFlag.Crafting] ||
            condition[ConditionFlag.ExecutingCraftingAction] ||
            condition[ConditionFlag.PreparingToCraft])
        {
            var recipe = data.GetRecipeName(ActiveRecipeId());
            return new ActivityResult(
                ActivityKind.Crafting,
                string.IsNullOrEmpty(recipe)
                    ? $"Crafting as {JobAbbreviation(player)}"
                    : $"Crafting {recipe}");
        }

        if (condition[ConditionFlag.Performing])
            return new ActivityResult(ActivityKind.Performing, "Performing");

        if (condition[ConditionFlag.TradeOpen])
            return new ActivityResult(ActivityKind.Trading, "Trading");

        if (condition[ConditionFlag.WaitingForDutyFinder] ||
            condition[ConditionFlag.InDutyQueue] ||
            condition[ConditionFlag.WaitingForDuty])
        {
            return new ActivityResult(ActivityKind.Waiting, "In the duty queue");
        }

        if (condition[ConditionFlag.UsingPartyFinder])
            return new ActivityResult(ActivityKind.Waiting, "In Party Finder");

        var fate = FindFate(player.Position);
        if (condition[ConditionFlag.InCombat])
        {
            if (fate is not null)
                return new ActivityResult(ActivityKind.InCombat, DescribeFate(fate));

            var target = CombatTargetName(player);
            return new ActivityResult(
                ActivityKind.InCombat,
                "In combat",
                target is null ? null : $"Fighting {target}");
        }

        if (fate is not null)
            return new ActivityResult(ActivityKind.Travelling, $"At {DescribeFate(fate)}");

        if (GameDataCache.IsHousing(use))
            return new ActivityResult(ActivityKind.Housing, use == TerritoryIntendedUse.HousingIndoor ? "Inside a house" : "In a housing ward");

        if (use == TerritoryIntendedUse.GoldSaucer)
            return new ActivityResult(ActivityKind.GoldSaucer, "At the Gold Saucer");

        if (use == TerritoryIntendedUse.IslandSanctuary)
            return new ActivityResult(ActivityKind.Travelling, "On their island");

        if (condition[ConditionFlag.OccupiedSummoningBell])
            return new ActivityResult(ActivityKind.Idle, "At a summoning bell");

        if (condition[ConditionFlag.Occupied] ||
            condition[ConditionFlag.OccupiedInEvent] ||
            condition[ConditionFlag.OccupiedInQuestEvent])
        {
            return new ActivityResult(ActivityKind.Idle, "Talking to an NPC");
        }

        return isMoving
            ? new ActivityResult(ActivityKind.Travelling, null)
            : new ActivityResult(ActivityKind.Idle, null);
    }

    /// <summary>Boolean state that rides alongside the activity.</summary>
    public PresenceFlags ResolveFlags(IPlayerCharacter player)
    {
        var condition = Service.Condition;
        var flags = PresenceFlags.None;

        if (condition[ConditionFlag.InCombat])
            flags |= PresenceFlags.InCombat;
        if (condition[ConditionFlag.Mounted] || condition[ConditionFlag.RidingPillion])
            flags |= PresenceFlags.Mounted;
        if (condition[ConditionFlag.InFlight])
            flags |= PresenceFlags.Flying;
        if (condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.OccupiedInCutSceneEvent])
            flags |= PresenceFlags.InCutscene;
        if (Service.Party.Length > 0)
            flags |= PresenceFlags.InParty;
        if (condition[ConditionFlag.InDutyQueue] || condition[ConditionFlag.WaitingForDutyFinder])
            flags |= PresenceFlags.InQueue;
        if (IsBoundByDuty(condition))
            flags |= PresenceFlags.Bound;
        if (player.IsDead)
            flags |= PresenceFlags.Dead;
        if (GameDataCache.IsSanctuaryLike(data.GetIntendedUse(Service.ClientState.TerritoryType)))
            flags |= PresenceFlags.Sanctuary;

        var status = player.OnlineStatus.RowId;
        if (status == OnlineStatusIds.Afk)
            flags |= PresenceFlags.Afk;
        if (status == OnlineStatusIds.Busy)
            flags |= PresenceFlags.Busy;

        return flags;
    }

    private static bool IsBoundByDuty(Dalamud.Plugin.Services.ICondition condition)
        => condition[ConditionFlag.BoundByDuty] ||
           condition[ConditionFlag.BoundByDuty56] ||
           condition[ConditionFlag.BoundByDuty95];

    private string? DutyName(uint territoryTypeId)
    {
        var fromDutyState = Service.DutyState.ContentFinderCondition.RowId;
        if (fromDutyState != 0)
        {
            var name = data.GetDutyName(fromDutyState);
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        var fromTerritory = data.GetContentFinderConditionId(territoryTypeId);
        if (fromTerritory == 0)
            return null;

        var territoryName = data.GetDutyName(fromTerritory);
        return string.IsNullOrWhiteSpace(territoryName) ? null : territoryName;
    }

    private string FishingDetail(uint territoryTypeId)
    {
        var zone = data.GetZoneName(territoryTypeId);
        return string.IsNullOrWhiteSpace(zone) ? "Fishing" : $"Fishing in {zone}";
    }

    private string JobAbbreviation(IPlayerCharacter player)
    {
        var (_, abbreviation) = data.GetJob(player.ClassJob.RowId);
        return string.IsNullOrWhiteSpace(abbreviation) ? "an adventurer" : abbreviation;
    }

    /// <summary>FATE name with its completion, which is what decides whether it is worth joining.</summary>
    private static string DescribeFate(IFate fate)
    {
        var name = fate.Name.TextValue;
        return fate.Progress > 0
            ? $"FATE: {name} ({fate.Progress}%)"
            : $"FATE: {name}";
    }

    /// <summary>The gathering node currently targeted, or null when it cannot be read.</summary>
    private static string? GatheringNodeName(IPlayerCharacter player)
    {
        var target = player.TargetObject;
        if (target is null || target.ObjectKind != ObjectKind.GatheringPoint)
            return null;

        var name = target.Name.TextValue;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>Recipe currently open in the crafting log, 0 when nothing is being made.</summary>
    private static uint ActiveRecipeId()
    {
        var note = RecipeNote.Instance();
        return note is null ? 0u : note->ActiveCraftRecipeId;
    }

    private static string? CombatTargetName(IPlayerCharacter player)
    {
        var target = player.TargetObject;
        if (target is null || target.ObjectKind != ObjectKind.BattleNpc)
            return null;

        var name = target.Name.TextValue;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static IFate? FindFate(Vector3 position)
    {
        foreach (var fate in Service.Fates)
        {
            if (fate.State is FateState.Ended or FateState.Failed)
                continue;

            if (fate.Radius <= 0f)
                continue;

            if (MapMath.FlatDistance(position, fate.Position) <= fate.Radius)
                return fate;
        }

        return null;
    }
}

/// <summary>
/// OnlineStatus row ids. The game's own flag enum uses one bit per row, so the row id is simply the bit
/// index; deriving them keeps this in step with FFXIVClientStructs instead of hardcoding magic numbers.
/// </summary>
public static class OnlineStatusIds
{
    public const uint Offline = 0;

    public static readonly uint Busy = RowOf(CsOnlineStatus.Busy);
    public static readonly uint Afk = RowOf(CsOnlineStatus.AwayFromKeyboard);
    public static readonly uint RolePlaying = RowOf(CsOnlineStatus.RolePlaying);
    public static readonly uint LookingForParty = RowOf(CsOnlineStatus.LookingForParty);
    public static readonly uint WaitingForDutyFinder = RowOf(CsOnlineStatus.WaitingForDutyFinder);
    public static readonly uint ViewingCutscene = RowOf(CsOnlineStatus.ViewingCutscene);

    private static uint RowOf(CsOnlineStatus flag)
        => (uint)BitOperations.TrailingZeroCount((ulong)flag);
}
