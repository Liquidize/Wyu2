namespace Wyu2.Game;

/// <summary>
/// Timing maths for the radar sweep and the marker pings. Deliberately stateless: everything is derived
/// from an elapsed clock and the blip's own bearing, so blips can appear and disappear mid-rotation
/// without any per-blip bookkeeping to get out of step.
/// </summary>
public static class RadarSweep
{
    public const float Tau = MathF.PI * 2f;

    /// <summary>
    /// Where the sweep line is pointing, in radians, measured the same way blip bearings are
    /// (<c>atan2(y, x)</c> in screen space, so it advances clockwise on screen).
    /// </summary>
    public static float Angle(double elapsedSeconds, float periodSeconds)
    {
        if (periodSeconds <= 0f)
            return 0f;

        var turns = elapsedSeconds / periodSeconds;
        return (float)((turns - Math.Floor(turns)) * Tau);
    }

    /// <summary>
    /// How long ago the sweep last crossed <paramref name="bearing"/>, in seconds. Zero at the instant of
    /// contact, rising to a full period just before the next pass.
    /// </summary>
    public static float SecondsSincePass(float sweepAngle, float bearing, float periodSeconds)
    {
        if (periodSeconds <= 0f)
            return 0f;

        var delta = Normalize(sweepAngle - bearing);
        return delta / Tau * periodSeconds;
    }

    /// <summary>
    /// Brightness of a blip that was last swept <paramref name="secondsSincePass"/> ago: 1 at contact,
    /// easing to 0 over <paramref name="decaySeconds"/> and staying there until the next pass.
    /// </summary>
    public static float PingStrength(float secondsSincePass, float decaySeconds)
    {
        if (decaySeconds <= 0f)
            return 0f;

        var t = secondsSincePass / decaySeconds;
        if (t is < 0f or >= 1f)
            return 0f;

        // Quadratic falloff: a bright flash that trails off, rather than a linear ramp.
        var remaining = 1f - t;
        return remaining * remaining;
    }

    /// <summary>
    /// Radius of the ring that expands out of a blip when the sweep hits it, as a fraction of its final
    /// size. Returns 0 once the ring has finished, so callers can skip drawing it.
    /// </summary>
    public static float RingProgress(float secondsSincePass, float durationSeconds)
    {
        if (durationSeconds <= 0f)
            return 0f;

        var t = secondsSincePass / durationSeconds;
        return t is < 0f or >= 1f ? 0f : t;
    }

    /// <summary>
    /// Free-running 0..1 pulse used by the map markers, which have no sweep to key off. The offset
    /// staggers markers so they do not all breathe in unison.
    /// </summary>
    public static float PulsePhase(double elapsedSeconds, float periodSeconds, float offset)
    {
        if (periodSeconds <= 0f)
            return 0f;

        var phase = (elapsedSeconds / periodSeconds) + offset;
        return (float)(phase - Math.Floor(phase));
    }

    /// <summary>
    /// A stable 0..1 number derived from a string, used to give each contact its own pulse offset.
    /// Hand-rolled FNV-1a rather than <see cref="string.GetHashCode()"/>, which is randomised per process
    /// and would shuffle everyone's phase on every game restart.
    /// </summary>
    public static float StableOffset(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return 0f;

        var hash = 2166136261u;
        foreach (var c in key)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return (hash % 1000u) / 1000f;
    }

    /// <summary>Wraps an angle into [0, 2pi).</summary>
    public static float Normalize(float radians)
    {
        var value = radians % Tau;
        return value < 0f ? value + Tau : value;
    }
}
