using System.Numerics;

namespace Wyu2.Game;

/// <summary>
/// The transform from world coordinates onto one of the game's own map addons, expressed as an anchor
/// point whose world position is known, a uniform pixels-per-yalm scale, and a rotation.
/// </summary>
/// <remarks>
/// Anchoring on a known point rather than on the sheet's origin makes the map's own panning irrelevant:
/// only the offset from the anchor is ever measured, so the sheet offsets cancel out of the arithmetic
/// for the player-centred case entirely.
/// </remarks>
/// <param name="Anchor">Screen pixel the anchor's world position is drawn at.</param>
/// <param name="AnchorWorld">World X and Z of that anchor, with the height dropped.</param>
/// <param name="PixelsPerYalm">Screen pixels one yalm covers on this map.</param>
/// <param name="Rotation">
/// Radians the map is turned by, clockwise on screen. Zero means north is up, which is always the case
/// for the full map and is the case for the minimap only while it is locked to north.
/// </param>
public readonly record struct NativeMapProjection(
    Vector2 Anchor,
    Vector2 AnchorWorld,
    float PixelsPerYalm,
    float Rotation)
{
    /// <summary>
    /// Whether this projection can place anything. A map that reported no scale is refused rather than
    /// drawn at a guessed one, since a marker in the wrong place is worse than no marker.
    /// </summary>
    public bool IsUsable => PixelsPerYalm > 0f && float.IsFinite(PixelsPerYalm) && float.IsFinite(Rotation);

    /// <summary>Screen pixel a world position lands on.</summary>
    public Vector2 Project(float worldX, float worldZ)
    {
        var offset = new Vector2(worldX - AnchorWorld.X, worldZ - AnchorWorld.Y) * PixelsPerYalm;
        return Anchor + Rotate(offset, Rotation);
    }

    /// <inheritdoc cref="Project(float,float)"/>
    public Vector2 Project(Vector3 world) => Project(world.X, world.Z);

    /// <summary>
    /// A projection onto a map sheet whose screen rectangle is known, which is how the full map is
    /// drawn: the sheet covers a fixed rectangle and the markers sit at fixed fractions of it.
    /// </summary>
    /// <param name="sheetMin">Top left screen pixel of the whole sheet, including the part off screen.</param>
    /// <param name="sheetSize">Screen size of the whole sheet. Sheets are square, so only the width is scaled by.</param>
    public static NativeMapProjection ForSheet(
        Vector2 sheetMin, Vector2 sheetSize, ushort sizeFactor, short offsetX, short offsetY)
    {
        var pixelsPerTexel = sheetSize.X / MapGeometry.MapTextureSize;
        var scale = sizeFactor <= 0 ? 1f : sizeFactor / 100f;

        // The middle of the sheet is by definition the world point the offsets negate.
        return new NativeMapProjection(
            sheetMin + (sheetSize / 2f),
            new Vector2(-offsetX, -offsetY),
            pixelsPerTexel * scale,
            0f);
    }

    /// <summary>
    /// A projection for a map that is kept centred on the player, which is how the minimap works: the
    /// player never moves off the middle, the sheet slides underneath, and the whole thing turns with
    /// the camera.
    /// </summary>
    public static NativeMapProjection ForPlayer(
        Vector2 centre, Vector3 playerWorld, float pixelsPerYalm, float rotation)
        => new(centre, new Vector2(playerWorld.X, playerWorld.Z), pixelsPerYalm, rotation);

    /// <summary>
    /// Screen pixels per yalm for a map that positions its markers by scaling map units, where a map
    /// unit is one pixel of the 2048 pixel sheet and a yalm is <c>sizeFactor / 100</c> of those.
    /// </summary>
    /// <param name="markerScaling">The map's own map-unit to node-unit factor, which carries its zoom.</param>
    /// <param name="nodeScale">Screen pixels per node unit, i.e. the accumulated interface scale.</param>
    public static float PixelsPerYalmFromMarkerScaling(ushort sizeFactor, float markerScaling, float nodeScale)
    {
        if (markerScaling <= 0f || nodeScale <= 0f)
            return 0f;

        var scale = sizeFactor <= 0 ? 1f : sizeFactor / 100f;
        return markerScaling * nodeScale * scale;
    }

    /// <summary>
    /// Rotation that puts the direction the camera is looking at the top of the map, given the camera's
    /// forward vector in world space. Facing north returns zero, because a north-up map is the identity.
    /// </summary>
    public static float RotationForCamera(float forwardX, float forwardZ)
    {
        if (MathF.Abs(forwardX) < 0.0001f && MathF.Abs(forwardZ) < 0.0001f)
            return 0f;

        // North is -Z and appears at the top of an unrotated sheet, so the turn needed is however far
        // the camera has swung away from that.
        return (-MathF.PI / 2f) - MathF.Atan2(forwardZ, forwardX);
    }

    /// <summary>Rotates a screen offset, positive being clockwise because screen Y points down.</summary>
    public static Vector2 Rotate(Vector2 offset, float radians)
    {
        if (radians == 0f)
            return offset;

        var sin = MathF.Sin(radians);
        var cos = MathF.Cos(radians);
        return new Vector2((offset.X * cos) - (offset.Y * sin), (offset.X * sin) + (offset.Y * cos));
    }

    /// <summary>
    /// Fits a point into the round window the minimap draws through. Points already inside come back
    /// untouched; points outside are either pulled onto the rim or refused, depending on whether the
    /// caller would rather show a direction than nothing at all.
    /// </summary>
    public static bool TryFitInCircle(Vector2 centre, float radius, Vector2 point, bool clampToRim, out Vector2 fitted)
    {
        fitted = point;
        if (radius <= 0f)
            return false;

        var offset = point - centre;
        var distance = offset.Length();
        if (distance <= radius)
            return true;

        if (!clampToRim || distance <= 0f)
            return false;

        fitted = centre + (offset / distance * radius);
        return true;
    }
}
