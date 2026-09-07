namespace Wyu2.Game;

/// <summary>
/// Turning a world-space offset into the "142y NE" that goes next to a friend's name. Kept away from the
/// game assemblies so the compass arithmetic can be tested.
/// </summary>
public static class Bearing
{
    private static readonly string[] Compass =
    [
        "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
        "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW",
    ];

    /// <summary>
    /// Bearing from one point to another in radians, clockwise from north. The game's north is -Z and its
    /// east is +X, which is what makes this an <c>atan2(dx, -dz)</c> rather than the usual form.
    /// </summary>
    public static float FromWorldDelta(float deltaX, float deltaZ)
    {
        var radians = MathF.Atan2(deltaX, -deltaZ);
        return radians < 0f ? radians + RadarSweep.Tau : radians;
    }

    /// <summary>Nearest of the sixteen compass points.</summary>
    public static string ToCompass(float radians)
    {
        var normalized = RadarSweep.Normalize(radians);
        var index = (int)MathF.Round(normalized / RadarSweep.Tau * Compass.Length) % Compass.Length;
        return Compass[index];
    }

    /// <summary>
    /// The whole readout. Below a few yalms the direction is meaningless and just jitters, so close
    /// contacts get "here" instead of a compass point that changes every step.
    /// </summary>
    public static string Describe(float distanceYalms, float radians)
    {
        if (distanceYalms < 3f)
            return "here";

        return $"{distanceYalms:0}y {ToCompass(radians)}";
    }
}
