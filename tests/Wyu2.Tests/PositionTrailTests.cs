using System.Numerics;
using Wyu2.Game;

namespace Wyu2.Tests;

public class PositionTrailTests
{
    private const ushort Zone = 155;

    [Fact]
    public void MovementIsRecorded()
    {
        var trail = new PositionTrail();

        trail.Record(new Vector3(0, 0, 0), Zone, 0d);
        trail.Record(new Vector3(10, 0, 0), Zone, 1d);
        trail.Record(new Vector3(20, 0, 0), Zone, 2d);

        Assert.Equal(3, trail.Count);
    }

    [Fact]
    public void StandingStillDoesNotFillTheBuffer()
    {
        var trail = new PositionTrail();

        trail.Record(new Vector3(0, 0, 0), Zone, 0d);
        for (var i = 1; i < 50; i++)
            trail.Record(new Vector3(0.1f, 0, 0.1f), Zone, i);

        Assert.Equal(1, trail.Count);
    }

    [Fact]
    public void HeightChangesAloneDoNotCountAsMovement()
    {
        // Distance is measured flat, the same way "yalms away" is.
        var trail = new PositionTrail();
        trail.Record(new Vector3(0, 0, 0), Zone, 0d);
        trail.Record(new Vector3(0, 500, 0), Zone, 1d);

        Assert.Equal(1, trail.Count);
    }

    [Fact]
    public void ChangingZoneDropsTheTrail()
    {
        var trail = new PositionTrail();
        trail.Record(new Vector3(0, 0, 0), Zone, 0d);
        trail.Record(new Vector3(20, 0, 0), Zone, 1d);

        trail.Record(new Vector3(500, 0, 500), 956, 2d);

        // Otherwise the map would show a line across the world to where they used to be.
        var only = Assert.Single(trail.Points);
        Assert.Equal((ushort)956, only.TerritoryTypeId);
    }

    [Fact]
    public void TheBufferIsCapped()
    {
        var trail = new PositionTrail(capacity: 8);
        for (var i = 0; i < 40; i++)
            trail.Record(new Vector3(i * 10f, 0, 0), Zone, i);

        Assert.Equal(8, trail.Count);

        // The points kept are the newest ones.
        Assert.Equal(390f, trail.Points[^1].Position.X);
    }

    [Fact]
    public void OldPointsArePruned()
    {
        var trail = new PositionTrail();
        for (var i = 0; i < 10; i++)
            trail.Record(new Vector3(i * 10f, 0, 0), Zone, i);

        trail.PruneOlderThan(9d, 4f);

        Assert.All(trail.Points, p => Assert.True(p.TimeSeconds >= 5d));
        Assert.NotEmpty(trail.Points);
    }

    [Fact]
    public void PruningEverythingLeavesAnEmptyTrail()
    {
        var trail = new PositionTrail();
        trail.Record(new Vector3(0, 0, 0), Zone, 0d);

        trail.PruneOlderThan(1000d, 5f);

        Assert.Equal(0, trail.Count);
    }

    [Fact]
    public void AClockGoingBackwardsRestartsTheTrail()
    {
        var trail = new PositionTrail();
        trail.Record(new Vector3(0, 0, 0), Zone, 100d);
        trail.Record(new Vector3(20, 0, 0), Zone, 101d);

        trail.Record(new Vector3(40, 0, 0), Zone, 1d);

        Assert.Equal(1, trail.Count);
    }

    [Fact]
    public void FreshnessRunsFromOldestToNewest()
    {
        var trail = new PositionTrail();
        for (var i = 0; i < 5; i++)
            trail.Record(new Vector3(i * 10f, 0, 0), Zone, i);

        Assert.Equal(0f, trail.Freshness(0));
        Assert.Equal(1f, trail.Freshness(trail.Count - 1));
        Assert.True(trail.Freshness(2) > trail.Freshness(1));
    }

    [Fact]
    public void ASinglePointIsDrawnAtFullStrength()
    {
        var trail = new PositionTrail();
        trail.Record(new Vector3(0, 0, 0), Zone, 0d);

        Assert.Equal(1f, trail.Freshness(0));
    }

    [Fact]
    public void ClearEmptiesIt()
    {
        var trail = new PositionTrail();
        trail.Record(new Vector3(0, 0, 0), Zone, 0d);
        trail.Clear();

        Assert.Equal(0, trail.Count);
        Assert.Empty(trail.Points);
    }
}
