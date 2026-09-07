namespace Wyu2.Game;

/// <summary>A run of hours on one day when you and somebody else are both usually around.</summary>
public readonly record struct OverlapWindow(DayOfWeek Day, int StartHour, int EndHourExclusive, float Minutes);

/// <summary>
/// How much time you and one contact are online together, bucketed by day of the week and hour, so
/// "when are we both usually on" stops being guesswork.
///
/// This is local only. It is built from presence you already receive, it is never published, and the
/// relay has no equivalent - keeping any history server side would break the promise that presence is
/// ephemeral there.
/// </summary>
public sealed class OverlapHistogram
{
    public const int Buckets = 7 * 24;

    /// <summary>A single sample longer than this is a bug or a clock jump, not a play session.</summary>
    private static readonly TimeSpan LongestSample = TimeSpan.FromDays(7);

    private readonly float[] minutes;

    public OverlapHistogram() => minutes = new float[Buckets];

    private OverlapHistogram(float[] values) => minutes = values;

    /// <summary>Total overlapping time recorded, across the whole week.</summary>
    public float TotalMinutes => minutes.Sum();

    public static int IndexOf(DayOfWeek day, int hour) => ((int)day * 24) + hour;

    /// <summary>
    /// Adds a stretch of shared time. Stretches that cross an hour, a midnight or the end of the week are
    /// split across the buckets they actually cover, so a Sunday evening session does not all land on
    /// Sunday at eight.
    /// </summary>
    public void Add(DateTimeOffset start, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return;

        if (duration > LongestSample)
            duration = LongestSample;

        var cursor = start;
        var remaining = duration;

        while (remaining > TimeSpan.Zero)
        {
            var nextHour = new DateTimeOffset(
                cursor.Year, cursor.Month, cursor.Day, cursor.Hour, 0, 0, cursor.Offset).AddHours(1);

            var slice = nextHour - cursor;
            if (slice > remaining)
                slice = remaining;

            minutes[IndexOf(cursor.DayOfWeek, cursor.Hour)] += (float)slice.TotalMinutes;

            cursor += slice;
            remaining -= slice;
        }
    }

    public float MinutesAt(DayOfWeek day, int hour) => minutes[IndexOf(day, hour)];

    /// <summary>
    /// The best stretches to catch somebody, busiest first. Adjacent hours above the threshold are merged
    /// into one window, because "Tuesday between eight and eleven" is a more useful answer than three
    /// separate hours.
    /// </summary>
    public IReadOnlyList<OverlapWindow> BestWindows(int count = 3, float minimumMinutes = 30f)
    {
        var windows = new List<OverlapWindow>();

        for (var day = 0; day < 7; day++)
        {
            var start = -1;
            var total = 0f;

            for (var hour = 0; hour <= 24; hour++)
            {
                // The pass at hour 24 exists only to close a window that runs to the end of the day.
                var value = hour < 24 ? minutes[IndexOf((DayOfWeek)day, hour)] : 0f;
                var qualifies = hour < 24 && value >= minimumMinutes;

                if (qualifies)
                {
                    if (start < 0)
                    {
                        start = hour;
                        total = 0f;
                    }

                    total += value;
                    continue;
                }

                if (start >= 0)
                {
                    windows.Add(new OverlapWindow((DayOfWeek)day, start, hour, total));
                    start = -1;
                }
            }
        }

        return windows
            .OrderByDescending(w => w.Minutes)
            .ThenBy(w => w.Day)
            .ThenBy(w => w.StartHour)
            .Take(Math.Max(0, count))
            .ToList();
    }

    /// <summary>
    /// Fades older observations. Habits change: somebody who raided on Tuesdays last year should stop
    /// showing up as a Tuesday regular once they have stopped.
    /// </summary>
    public void Decay(float factor)
    {
        if (factor is <= 0f or >= 1f)
            return;

        for (var i = 0; i < minutes.Length; i++)
            minutes[i] *= factor;
    }

    /// <summary>A copy suitable for writing to the configuration file.</summary>
    public float[] Snapshot() => (float[])minutes.Clone();

    /// <summary>
    /// Restores a histogram. A snapshot of the wrong length is ignored rather than throwing, so a
    /// configuration written by a different version cannot stop the plugin loading.
    /// </summary>
    public static OverlapHistogram FromSnapshot(float[]? values)
        => values is { Length: Buckets } ? new OverlapHistogram((float[])values.Clone()) : new OverlapHistogram();
}
