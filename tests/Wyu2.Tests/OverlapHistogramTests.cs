using Wyu2.Game;

namespace Wyu2.Tests;

public class OverlapHistogramTests
{
    // 2026-09-08 is a Tuesday.
    private static DateTimeOffset Tuesday(int hour, int minute = 0)
        => new(2026, 9, 8, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void BucketsAreLaidOutByDayThenHour()
    {
        Assert.Equal(0, OverlapHistogram.IndexOf(DayOfWeek.Sunday, 0));
        Assert.Equal(23, OverlapHistogram.IndexOf(DayOfWeek.Sunday, 23));
        Assert.Equal(24, OverlapHistogram.IndexOf(DayOfWeek.Monday, 0));
        Assert.Equal(OverlapHistogram.Buckets - 1, OverlapHistogram.IndexOf(DayOfWeek.Saturday, 23));
    }

    [Fact]
    public void TimeWithinOneHourLandsInOneBucket()
    {
        var histogram = new OverlapHistogram();
        histogram.Add(Tuesday(20, 10), TimeSpan.FromMinutes(30));

        Assert.Equal(30f, histogram.MinutesAt(DayOfWeek.Tuesday, 20), 2);
        Assert.Equal(30f, histogram.TotalMinutes, 2);
    }

    [Fact]
    public void TimeCrossingAnHourIsSplitAcrossBuckets()
    {
        var histogram = new OverlapHistogram();

        // 19:40 for ninety minutes covers twenty minutes of 19, all of 20, ten minutes of 21.
        histogram.Add(Tuesday(19, 40), TimeSpan.FromMinutes(90));

        Assert.Equal(20f, histogram.MinutesAt(DayOfWeek.Tuesday, 19), 2);
        Assert.Equal(60f, histogram.MinutesAt(DayOfWeek.Tuesday, 20), 2);
        Assert.Equal(10f, histogram.MinutesAt(DayOfWeek.Tuesday, 21), 2);
        Assert.Equal(90f, histogram.TotalMinutes, 2);
    }

    [Fact]
    public void TimeCrossingMidnightLandsOnTheNextDay()
    {
        var histogram = new OverlapHistogram();
        histogram.Add(Tuesday(23, 30), TimeSpan.FromMinutes(60));

        Assert.Equal(30f, histogram.MinutesAt(DayOfWeek.Tuesday, 23), 2);
        Assert.Equal(30f, histogram.MinutesAt(DayOfWeek.Wednesday, 0), 2);
    }

    [Fact]
    public void TimeCrossingTheEndOfTheWeekWrapsToSunday()
    {
        var histogram = new OverlapHistogram();

        // 2026-09-12 is a Saturday.
        histogram.Add(new DateTimeOffset(2026, 9, 12, 23, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(120));

        Assert.Equal(60f, histogram.MinutesAt(DayOfWeek.Saturday, 23), 2);
        Assert.Equal(60f, histogram.MinutesAt(DayOfWeek.Sunday, 0), 2);
    }

    [Fact]
    public void EmptyAndBackwardsSamplesAreIgnored()
    {
        var histogram = new OverlapHistogram();
        histogram.Add(Tuesday(20), TimeSpan.Zero);
        histogram.Add(Tuesday(20), TimeSpan.FromMinutes(-30));

        Assert.Equal(0f, histogram.TotalMinutes);
    }

    [Fact]
    public void AnAbsurdSampleIsCappedRatherThanLoopingForever()
    {
        var histogram = new OverlapHistogram();

        // A clock jump should not spin through ten years of buckets.
        histogram.Add(Tuesday(20), TimeSpan.FromDays(4000));

        Assert.Equal((float)TimeSpan.FromDays(7).TotalMinutes, histogram.TotalMinutes, 1);
    }

    [Fact]
    public void AdjacentHoursMergeIntoOneWindow()
    {
        var histogram = new OverlapHistogram();
        foreach (var hour in new[] { 20, 21, 22 })
            histogram.Add(Tuesday(hour), TimeSpan.FromMinutes(60));

        var window = Assert.Single(histogram.BestWindows());

        Assert.Equal(DayOfWeek.Tuesday, window.Day);
        Assert.Equal(20, window.StartHour);
        Assert.Equal(23, window.EndHourExclusive);
        Assert.Equal(180f, window.Minutes, 2);
    }

    [Fact]
    public void AGapSplitsTheWindow()
    {
        var histogram = new OverlapHistogram();
        histogram.Add(Tuesday(9), TimeSpan.FromMinutes(60));
        histogram.Add(Tuesday(20), TimeSpan.FromMinutes(60));

        var windows = histogram.BestWindows();

        Assert.Equal(2, windows.Count);
        Assert.Contains(windows, w => w.StartHour == 9);
        Assert.Contains(windows, w => w.StartHour == 20);
    }

    [Fact]
    public void QuietHoursAreLeftOut()
    {
        var histogram = new OverlapHistogram();
        histogram.Add(Tuesday(3), TimeSpan.FromMinutes(5));
        histogram.Add(Tuesday(20), TimeSpan.FromMinutes(60));

        var window = Assert.Single(histogram.BestWindows(minimumMinutes: 30f));

        Assert.Equal(20, window.StartHour);
    }

    [Fact]
    public void AWindowRunningToMidnightIsStillClosed()
    {
        var histogram = new OverlapHistogram();
        histogram.Add(Tuesday(22), TimeSpan.FromMinutes(120));

        var window = Assert.Single(histogram.BestWindows());

        Assert.Equal(22, window.StartHour);
        Assert.Equal(24, window.EndHourExclusive);
    }

    [Fact]
    public void TheBusiestWindowsComeFirst()
    {
        var histogram = new OverlapHistogram();
        histogram.Add(Tuesday(9), TimeSpan.FromMinutes(45));

        // 2026-09-10 is a Thursday.
        histogram.Add(new DateTimeOffset(2026, 9, 10, 20, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(180));

        var windows = histogram.BestWindows(count: 2);

        Assert.Equal(DayOfWeek.Thursday, windows[0].Day);
        Assert.Equal(DayOfWeek.Tuesday, windows[1].Day);
    }

    [Fact]
    public void AskingForNoWindowsGivesNone()
    {
        var histogram = new OverlapHistogram();
        histogram.Add(Tuesday(20), TimeSpan.FromMinutes(60));

        Assert.Empty(histogram.BestWindows(count: 0));
    }

    [Fact]
    public void DecayFadesOldHabits()
    {
        var histogram = new OverlapHistogram();
        histogram.Add(Tuesday(20), TimeSpan.FromMinutes(60));

        histogram.Decay(0.5f);

        Assert.Equal(30f, histogram.MinutesAt(DayOfWeek.Tuesday, 20), 2);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(-1f)]
    [InlineData(2f)]
    public void NonsensicalDecayIsIgnored(float factor)
    {
        var histogram = new OverlapHistogram();
        histogram.Add(Tuesday(20), TimeSpan.FromMinutes(60));

        histogram.Decay(factor);

        Assert.Equal(60f, histogram.MinutesAt(DayOfWeek.Tuesday, 20), 2);
    }

    [Fact]
    public void SnapshotsRoundTrip()
    {
        var histogram = new OverlapHistogram();
        histogram.Add(Tuesday(20), TimeSpan.FromMinutes(90));

        var restored = OverlapHistogram.FromSnapshot(histogram.Snapshot());

        Assert.Equal(histogram.TotalMinutes, restored.TotalMinutes, 2);
        Assert.Equal(60f, restored.MinutesAt(DayOfWeek.Tuesday, 20), 2);
    }

    [Fact]
    public void ASnapshotIsACopyNotAWindow()
    {
        var histogram = new OverlapHistogram();
        histogram.Add(Tuesday(20), TimeSpan.FromMinutes(60));

        var snapshot = histogram.Snapshot();
        snapshot[OverlapHistogram.IndexOf(DayOfWeek.Tuesday, 20)] = 9999f;

        Assert.Equal(60f, histogram.MinutesAt(DayOfWeek.Tuesday, 20), 2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(5)]
    public void AMalformedSnapshotStartsEmptyRatherThanThrowing(int? length)
    {
        // A configuration written by a different version must not stop the plugin loading.
        var restored = OverlapHistogram.FromSnapshot(length is null ? null : new float[length.Value]);

        Assert.Equal(0f, restored.TotalMinutes);
    }
}
