using System.Numerics;

namespace Wyu2.Game;

/// <summary>
/// Where a moving contact will be, and where to head to meet them. Built on the trail the client
/// already keeps, so this costs nothing extra on the wire.
/// </summary>
public static class Interception
{
    /// <summary>Below this the maths is degenerate and the answer would be noise rather than a result.</summary>
    private const float Epsilon = 0.0001f;

    /// <summary>
    /// Average velocity over the most recent stretch of a trail, in yalms per second. Flat, like every
    /// other distance in the plugin: vertical movement is mostly mounts and cliffs and says nothing
    /// about where somebody is going.
    /// </summary>
    public static Vector3? EstimateVelocity(PositionTrail trail, double nowSeconds, float windowSeconds = 3f)
    {
        var points = trail.Points;
        if (points.Count < 2 || windowSeconds <= 0f)
            return null;

        var cutoff = nowSeconds - windowSeconds;
        var last = points[^1];

        // Walk back to the oldest point still inside the window.
        var firstIndex = points.Count - 1;
        while (firstIndex > 0 && points[firstIndex - 1].TimeSeconds >= cutoff)
            firstIndex--;

        var first = points[firstIndex];
        var elapsed = (float)(last.TimeSeconds - first.TimeSeconds);
        if (elapsed < 0.05f)
            return null;

        // Crossing a zone would give a meaningless velocity across the world.
        if (first.TerritoryTypeId != last.TerritoryTypeId)
            return null;

        var delta = last.Position - first.Position;
        return new Vector3(delta.X / elapsed, 0f, delta.Z / elapsed);
    }

    /// <summary>Where something travelling at a constant velocity ends up after a while.</summary>
    public static Vector3 Predict(Vector3 position, Vector3 velocity, float seconds)
        => new(position.X + (velocity.X * seconds), position.Y, position.Z + (velocity.Z * seconds));

    /// <summary>Flat speed, in yalms per second.</summary>
    public static float Speed(Vector3 velocity)
        => MathF.Sqrt((velocity.X * velocity.X) + (velocity.Z * velocity.Z));

    /// <summary>
    /// How long until a pursuer running at <paramref name="pursuerSpeed"/> can meet a target moving at a
    /// constant velocity, or null when they cannot be caught at all. Solves the usual intercept
    /// quadratic: the pursuer's reachable radius grows as speed times t while the gap changes as the
    /// length of d plus v times t, and the answer is the first time those two meet.
    /// </summary>
    public static float? TimeToIntercept(
        Vector3 pursuer,
        float pursuerSpeed,
        Vector3 target,
        Vector3 targetVelocity)
    {
        if (pursuerSpeed < 0f)
            return null;

        var dx = target.X - pursuer.X;
        var dz = target.Z - pursuer.Z;
        var vx = targetVelocity.X;
        var vz = targetVelocity.Z;

        var a = (vx * vx) + (vz * vz) - (pursuerSpeed * pursuerSpeed);
        var b = 2f * ((dx * vx) + (dz * vz));
        var c = (dx * dx) + (dz * dz);

        // Already standing on them.
        if (c < Epsilon)
            return 0f;

        // Equal speeds: the quadratic collapses to a straight line.
        if (MathF.Abs(a) < Epsilon)
        {
            if (MathF.Abs(b) < Epsilon)
                return null;

            var linear = -c / b;
            return linear > 0f ? linear : null;
        }

        var discriminant = (b * b) - (4f * a * c);
        if (discriminant < 0f)
            return null;

        var root = MathF.Sqrt(discriminant);
        var first = (-b - root) / (2f * a);
        var second = (-b + root) / (2f * a);

        // Both roots can be negative, which means the meeting point is behind us: uncatchable.
        var earliest = float.MaxValue;
        if (first > 0f)
            earliest = first;
        if (second > 0f && second < earliest)
            earliest = second;

        return earliest is float.MaxValue ? null : earliest;
    }

    /// <summary>Where to run to, or null when the target cannot be caught at that speed.</summary>
    public static Vector3? InterceptPoint(
        Vector3 pursuer,
        float pursuerSpeed,
        Vector3 target,
        Vector3 targetVelocity)
    {
        var time = TimeToIntercept(pursuer, pursuerSpeed, target, targetVelocity);
        return time is { } t ? Predict(target, targetVelocity, t) : null;
    }
}
