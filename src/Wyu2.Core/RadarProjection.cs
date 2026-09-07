using System.Numerics;

namespace Wyu2.Game;

/// <summary>
/// Turns a world-space offset into radar-space pixels.
///
/// Three frames meet here and it is easy to get wrong, so they are written down. The game uses +X east,
/// +Z south, +Y up. Screen space uses +X right and +Y <em>down</em>. Camera yaw is measured as
/// <c>atan2(forward.X, forward.Z)</c>, which makes zero point south rather than north.
/// </summary>
public static class RadarProjection
{
    /// <summary>
    /// The yaw that puts north at the top. Not zero: yaw is measured from the +Z axis, which is south,
    /// so a north-up radar is half a turn away from the identity.
    /// </summary>
    public static readonly float NorthUpYaw = MathF.PI;

    /// <summary>
    /// Projects a world offset into radar space, with the given yaw pointing up the screen.
    ///
    /// The right-hand column is the part worth checking: looking down at a map with +X east and +Z
    /// south, the direction to the right of a forward vector <c>f</c> is <c>(-f.Z, f.X)</c>. Getting that
    /// backwards mirrors the radar east to west while leaving forward and back correct, which is
    /// remarkably hard to notice by eye.
    /// </summary>
    public static Vector2 WorldOffsetToRadar(Vector3 offset, float yaw)
    {
        var sin = MathF.Sin(yaw);
        var cos = MathF.Cos(yaw);

        return new Vector2(
            (offset.Z * sin) - (offset.X * cos),
            -((offset.X * sin) + (offset.Z * cos)));
    }
}
