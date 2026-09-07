using System.Numerics;
using Lumina.Excel.Sheets;

namespace FriendRadar.Game;

/// <summary>
/// Lumina-typed wrappers over <see cref="MapGeometry"/>. The arithmetic itself lives in FriendRadar.Core
/// so it can be tested without the game.
/// </summary>
public static class MapMath
{
    /// <summary>The X/Y pair the game shows in the chat log and on the minimap.</summary>
    public static Vector2 WorldToMapCoordinates(Vector3 world, Map map)
        => MapGeometry.WorldToMapCoordinates(world.X, world.Z, map.SizeFactor, map.OffsetX, map.OffsetY);

    /// <summary>Position on the zone map sheet, normalised to 0..1.</summary>
    public static Vector2 WorldToTextureUv(Vector3 world, Map map)
        => MapGeometry.WorldToTextureUv(world.X, world.Z, map.SizeFactor, map.OffsetX, map.OffsetY);

    /// <summary>Inverse of <see cref="WorldToTextureUv"/>, for click-to-flag on a drawn map.</summary>
    public static Vector2 TextureUvToWorld(Vector2 uv, Map map)
        => MapGeometry.TextureUvToWorld(uv, map.SizeFactor, map.OffsetX, map.OffsetY);

    /// <summary>Game path of a zone's map sheet, e.g. <c>ui/map/s1d1/00/s1d100_m.tex</c>.</summary>
    public static string? GetMapTexturePath(Map map)
        => MapGeometry.GetMapTexturePath(map.Id.ExtractText());

    /// <summary>Distance between two points ignoring height.</summary>
    public static float FlatDistance(Vector3 a, Vector3 b) => MapGeometry.FlatDistance(a, b);

    /// <summary>Formats a coordinate pair the way the game does.</summary>
    public static string FormatCoordinates(Vector2 mapCoordinates) => MapGeometry.FormatCoordinates(mapCoordinates);
}
