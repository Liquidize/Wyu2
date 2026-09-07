using System.Numerics;

namespace Wyu2.Game;

/// <summary>
/// Pure map arithmetic, kept away from the game assemblies so it can be unit tested. The Lumina typed
/// wrappers live in the plugin and just forward to these.
/// </summary>
public static class MapGeometry
{
    /// <summary>Map sheets are authored at 2048x2048 regardless of the size they ship at.</summary>
    public const float MapTextureSize = 2048f;

    /// <summary>
    /// Position on the map sheet, normalised to 0..1. Values outside that range are off the sheet.
    /// </summary>
    public static Vector2 WorldToTextureUv(float worldX, float worldZ, ushort sizeFactor, short offsetX, short offsetY)
    {
        var scale = Scale(sizeFactor);
        var x = ((worldX + offsetX) * scale) + (MapTextureSize / 2f);
        var y = ((worldZ + offsetY) * scale) + (MapTextureSize / 2f);
        return new Vector2(x / MapTextureSize, y / MapTextureSize);
    }

    /// <summary>Inverse of <see cref="WorldToTextureUv"/>, for turning a click into world coordinates.</summary>
    public static Vector2 TextureUvToWorld(Vector2 uv, ushort sizeFactor, short offsetX, short offsetY)
    {
        var scale = Scale(sizeFactor);
        var x = (((uv.X * MapTextureSize) - (MapTextureSize / 2f)) / scale) - offsetX;
        var z = (((uv.Y * MapTextureSize) - (MapTextureSize / 2f)) / scale) - offsetY;
        return new Vector2(x, z);
    }

    /// <summary>
    /// The coordinate pair the game prints in chat and on the minimap. This mirrors the game's own
    /// conversion: scale the world position, normalise it against the sheet, then map it onto the 1..42
    /// range players see.
    /// </summary>
    public static Vector2 WorldToMapCoordinates(float worldX, float worldZ, ushort sizeFactor, short offsetX, short offsetY)
    {
        var scale = Scale(sizeFactor);
        return new Vector2(
            ToMapCoordinate(worldX, offsetX, scale),
            ToMapCoordinate(worldZ, offsetY, scale));
    }

    private static float ToMapCoordinate(float value, short offset, float scale)
    {
        var scaled = (value + offset) * scale;
        return (41f / scale * ((scaled + 1024f) / 2048f)) + 1f;
    }

    /// <summary>
    /// Game path of a zone's map sheet from a Map row id such as <c>s1d1/00</c>, giving
    /// <c>ui/map/s1d1/00/s1d100_m.tex</c>. Null when the id is not in that shape.
    /// </summary>
    public static string? GetMapTexturePath(string? mapId)
    {
        if (string.IsNullOrWhiteSpace(mapId) || !mapId.Contains('/'))
            return null;

        var flat = mapId.Replace("/", string.Empty);
        return $"ui/map/{mapId}/{flat}_m.tex";
    }

    /// <summary>Distance ignoring height, which is what "yalms away" means in game.</summary>
    public static float FlatDistance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>Formats a coordinate pair the way the game does.</summary>
    public static string FormatCoordinates(Vector2 mapCoordinates)
        => $"({mapCoordinates.X:0.0}, {mapCoordinates.Y:0.0})";

    private static float Scale(ushort sizeFactor)
    {
        var scale = sizeFactor / 100f;
        return scale <= 0f ? 1f : scale;
    }
}
