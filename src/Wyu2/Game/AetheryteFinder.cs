using System.Numerics;
using Lumina.Excel.Sheets;

namespace Wyu2.Game;

/// <summary>An aetheryte you are attuned to, and how far it is from wherever you were looking.</summary>
public sealed record NearestAetheryte(
    uint AetheryteId,
    byte SubIndex,
    string Name,
    float DistanceYalms,
    uint GilCost);

/// <summary>
/// Works out which of your attuned aetherytes is closest to a contact. Attuned specifically: an
/// aetheryte you cannot teleport to is not an answer to "how do I get there".
/// </summary>
public sealed class AetheryteFinder
{
    /// <summary>
    /// The best aetheryte for reaching a point, or null when the zone has none you are attuned to.
    /// Falls back to the only aetheryte in the zone when positions cannot be resolved, since with one
    /// candidate the distance does not matter.
    /// </summary>
    public NearestAetheryte? Find(ushort territoryTypeId, Vector3? destination)
    {
        if (territoryTypeId == 0 || !Service.ClientState.IsLoggedIn)
            return null;

        NearestAetheryte? best = null;
        var candidates = 0;

        foreach (var entry in Service.Aetherytes)
        {
            if (entry.TerritoryId != territoryTypeId)
                continue;

            // Housing plots and apartments share the zone but are not general purpose destinations.
            if (entry.IsSharedHouse || entry.IsApartment)
                continue;

            if (entry.AetheryteData.ValueNullable is not { } row)
                continue;

            candidates++;
            var name = row.PlaceName.ValueNullable?.Name.ExtractText() ?? "Aetheryte";
            var position = PositionOf(row);

            var distance = position is { } p && destination is { } target
                ? MapGeometry.FlatDistance(p, target)
                : float.MaxValue;

            if (best is null || distance < best.DistanceYalms)
                best = new NearestAetheryte(entry.AetheryteId, entry.SubIndex, name, distance, entry.GilCost);
        }

        // With several candidates and no usable positions every distance is MaxValue, so the "nearest"
        // would be whichever happened to come first. Say nothing rather than send somebody the wrong way.
        if (best is { DistanceYalms: float.MaxValue } && candidates > 1)
            return null;

        return best;
    }

    /// <summary>World position of an aetheryte, taken from the level it sits on.</summary>
    private static Vector3? PositionOf(Aetheryte row)
    {
        for (var i = 0; i < row.Level.Count; i++)
        {
            if (row.Level[i].ValueNullable is not { } level)
                continue;

            if (level.X == 0f && level.Y == 0f && level.Z == 0f)
                continue;

            return new Vector3(level.X, level.Y, level.Z);
        }

        return null;
    }
}
