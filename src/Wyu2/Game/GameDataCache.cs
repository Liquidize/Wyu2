using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;
using Lumina.Excel.Sheets;

namespace Wyu2.Game;

/// <summary>
/// Name lookups against the game's Excel sheets, memoised because they are hit every frame by the
/// friend list and the radar.
/// </summary>
public sealed class GameDataCache
{
    private readonly Dictionary<uint, string> zoneNames = [];
    private readonly Dictionary<uint, string> regionNames = [];
    private readonly Dictionary<uint, string> worldNames = [];
    private readonly Dictionary<uint, (string Name, string Abbreviation)> jobNames = [];
    private readonly Dictionary<uint, string> onlineStatusNames = [];
    private readonly Dictionary<uint, string> dutyNames = [];

    /// <summary>Zone name, e.g. "Ul'dah - Steps of Nald".</summary>
    public string GetZoneName(uint territoryTypeId)
    {
        if (territoryTypeId == 0)
            return string.Empty;

        if (zoneNames.TryGetValue(territoryTypeId, out var cached))
            return cached;

        var name = string.Empty;
        if (Service.Data.GetExcelSheet<TerritoryType>().TryGetRow(territoryTypeId, out var territory))
        {
            name = territory.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                name = territory.PlaceNameZone.ValueNullable?.Name.ExtractText() ?? string.Empty;
        }

        return zoneNames[territoryTypeId] = name;
    }

    /// <summary>Region the zone sits in, e.g. "Thanalan".</summary>
    public string GetRegionName(uint territoryTypeId)
    {
        if (territoryTypeId == 0)
            return string.Empty;

        if (regionNames.TryGetValue(territoryTypeId, out var cached))
            return cached;

        var name = string.Empty;
        if (Service.Data.GetExcelSheet<TerritoryType>().TryGetRow(territoryTypeId, out var territory))
            name = territory.PlaceNameRegion.ValueNullable?.Name.ExtractText() ?? string.Empty;

        return regionNames[territoryTypeId] = name;
    }

    public string GetWorldName(uint worldId)
    {
        if (worldId is 0 or 65535)
            return string.Empty;

        if (worldNames.TryGetValue(worldId, out var cached))
            return cached;

        var name = Service.Data.GetExcelSheet<World>().GetRowOrDefault(worldId)?.Name.ExtractText() ?? string.Empty;
        return worldNames[worldId] = name;
    }

    public (string Name, string Abbreviation) GetJob(uint classJobId)
    {
        if (classJobId == 0)
            return (string.Empty, string.Empty);

        if (jobNames.TryGetValue(classJobId, out var cached))
            return cached;

        var row = Service.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(classJobId);
        var value = row is null
            ? (string.Empty, string.Empty)
            : (Capitalise(row.Value.Name.ExtractText()), row.Value.Abbreviation.ExtractText());

        return jobNames[classJobId] = value;
    }

    public string GetOnlineStatusName(uint onlineStatusId)
    {
        if (onlineStatusId == 0)
            return string.Empty;

        if (onlineStatusNames.TryGetValue(onlineStatusId, out var cached))
            return cached;

        var name = Service.Data.GetExcelSheet<OnlineStatus>().GetRowOrDefault(onlineStatusId)?.Name.ExtractText()
                   ?? string.Empty;
        return onlineStatusNames[onlineStatusId] = name;
    }

    /// <summary>Duty name for a ContentFinderCondition row.</summary>
    public string GetDutyName(uint contentFinderConditionId)
    {
        if (contentFinderConditionId == 0)
            return string.Empty;

        if (dutyNames.TryGetValue(contentFinderConditionId, out var cached))
            return cached;

        var name = Service.Data.GetExcelSheet<ContentFinderCondition>()
            .GetRowOrDefault(contentFinderConditionId)?.Name.ExtractText() ?? string.Empty;
        return dutyNames[contentFinderConditionId] = Capitalise(name);
    }

    /// <summary>Map row for a zone, used for coordinates and for drawing the map sheet.</summary>
    public Map? GetMapForTerritory(uint territoryTypeId)
    {
        if (territoryTypeId == 0)
            return null;

        return Service.Data.GetExcelSheet<TerritoryType>().TryGetRow(territoryTypeId, out var territory)
            ? territory.Map.ValueNullable
            : null;
    }

    public Map? GetMap(uint mapId)
        => mapId == 0 ? null : Service.Data.GetExcelSheet<Map>().GetRowOrDefault(mapId);

    /// <summary>What the zone is for: dungeon, housing, gold saucer and so on.</summary>
    public TerritoryIntendedUse GetIntendedUse(uint territoryTypeId)
    {
        if (territoryTypeId == 0)
            return TerritoryIntendedUse.Town;

        return Service.Data.GetExcelSheet<TerritoryType>().TryGetRow(territoryTypeId, out var territory)
            ? (TerritoryIntendedUse)territory.TerritoryIntendedUse.RowId
            : TerritoryIntendedUse.Town;
    }

    /// <summary>ContentFinderCondition attached to a zone, which is how a duty gets its display name.</summary>
    public uint GetContentFinderConditionId(uint territoryTypeId)
        => Service.Data.GetExcelSheet<TerritoryType>().TryGetRow(territoryTypeId, out var territory)
            ? territory.ContentFinderCondition.RowId
            : 0;

    public static bool IsHousing(TerritoryIntendedUse use)
        => use is TerritoryIntendedUse.HousingOutdoor or TerritoryIntendedUse.HousingIndoor;

    public static bool IsSanctuaryLike(TerritoryIntendedUse use)
        => use is TerritoryIntendedUse.Town or TerritoryIntendedUse.Inn or TerritoryIntendedUse.HousingIndoor
            or TerritoryIntendedUse.HousingOutdoor or TerritoryIntendedUse.GoldSaucer
            or TerritoryIntendedUse.IslandSanctuary;

    /// <summary>The game stores duty and job names lower case; titles read better capitalised.</summary>
    private static string Capitalise(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return char.IsLower(value[0]) ? char.ToUpperInvariant(value[0]) + value[1..] : value;
    }
}
