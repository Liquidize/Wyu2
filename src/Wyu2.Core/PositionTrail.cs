using System.Numerics;

namespace Wyu2.Game;

/// <summary>One remembered position, with when and where it was recorded.</summary>
public readonly record struct TrailPoint(Vector3 Position, ushort TerritoryTypeId, double TimeSeconds);

/// <summary>
/// A short breadcrumb trail behind one contact, so you can see which way they are heading rather than
/// inferring it from a single dot. Purely local: this is drawn from positions already received, and
/// nothing about it is ever published.
/// </summary>
public sealed class PositionTrail(int capacity = 64)
{
    private readonly List<TrailPoint> points = new(capacity);
    private readonly int capacity = Math.Max(2, capacity);

    public IReadOnlyList<TrailPoint> Points => points;

    public int Count => points.Count;

    /// <summary>
    /// Records a position, skipping ones too close to the last to be worth drawing. Moving to a different
    /// zone drops the trail rather than drawing a line across the world to where they used to be.
    /// </summary>
    public void Record(Vector3 position, ushort territoryTypeId, double timeSeconds, float minimumStep = 1.5f)
    {
        if (points.Count > 0)
        {
            var last = points[^1];

            if (last.TerritoryTypeId != territoryTypeId)
            {
                points.Clear();
            }
            else
            {
                // Standing still should not pack the buffer with identical points, which would age the
                // useful history out of it.
                if (MapGeometry.FlatDistance(last.Position, position) < minimumStep)
                    return;

                // Time running backwards means the clock was reset; start again rather than draw a
                // trail with points out of order.
                if (timeSeconds < last.TimeSeconds)
                    points.Clear();
            }
        }

        points.Add(new TrailPoint(position, territoryTypeId, timeSeconds));

        if (points.Count > capacity)
            points.RemoveRange(0, points.Count - capacity);
    }

    /// <summary>Drops points older than the trail length.</summary>
    public void PruneOlderThan(double timeSeconds, float maximumAgeSeconds)
    {
        if (points.Count == 0)
            return;

        var cutoff = timeSeconds - maximumAgeSeconds;
        var keepFrom = 0;
        while (keepFrom < points.Count && points[keepFrom].TimeSeconds < cutoff)
            keepFrom++;

        if (keepFrom > 0)
            points.RemoveRange(0, keepFrom);
    }

    public void Clear() => points.Clear();

    /// <summary>
    /// How faded a point should be drawn: 0 for the oldest still held, 1 for the newest. Falls back to
    /// full strength when there is nothing to interpolate over.
    /// </summary>
    public float Freshness(int index)
        => points.Count < 2 ? 1f : index / (float)(points.Count - 1);
}
