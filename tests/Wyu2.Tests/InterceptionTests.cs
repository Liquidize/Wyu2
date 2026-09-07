using System.Numerics;
using Wyu2.Game;

namespace Wyu2.Tests;

public class InterceptionTests
{
    private const ushort Zone = 155;

    private static PositionTrail TrailAlong(Vector3 from, Vector3 step, int points, double startTime = 0d)
    {
        var trail = new PositionTrail();
        for (var i = 0; i < points; i++)
            trail.Record(from + (step * i), Zone, startTime + i);

        return trail;
    }

    // ---------------------------------------------------------------- velocity

    [Fact]
    public void VelocityIsMeasuredOverTheRecentWindow()
    {
        // Ten yalms east every second.
        var trail = TrailAlong(Vector3.Zero, new Vector3(10f, 0f, 0f), 6);

        var velocity = Interception.EstimateVelocity(trail, 5d, windowSeconds: 3f);

        Assert.NotNull(velocity);
        Assert.Equal(10f, velocity!.Value.X, 2);
        Assert.Equal(0f, velocity.Value.Z, 3);
        Assert.Equal(10f, Interception.Speed(velocity.Value), 2);
    }

    [Fact]
    public void VelocityIgnoresVerticalMovement()
    {
        var trail = new PositionTrail();
        trail.Record(new Vector3(0f, 0f, 0f), Zone, 0d);
        trail.Record(new Vector3(10f, 300f, 0f), Zone, 1d);

        var velocity = Interception.EstimateVelocity(trail, 1d);

        Assert.Equal(0f, velocity!.Value.Y);
        Assert.Equal(10f, Interception.Speed(velocity.Value), 2);
    }

    [Fact]
    public void OneOrNoPointsGivesNoVelocity()
    {
        Assert.Null(Interception.EstimateVelocity(new PositionTrail(), 1d));
        Assert.Null(Interception.EstimateVelocity(TrailAlong(Vector3.Zero, Vector3.Zero, 1), 1d));
    }

    [Fact]
    public void ASingleInstantGivesNoVelocity()
    {
        // Two points recorded at effectively the same moment would divide by nearly zero.
        var trail = new PositionTrail();
        trail.Record(new Vector3(0f, 0f, 0f), Zone, 10d);
        trail.Record(new Vector3(50f, 0f, 0f), Zone, 10.001d);

        Assert.Null(Interception.EstimateVelocity(trail, 10.001d));
    }

    [Fact]
    public void CrossingAZoneGivesNoVelocity()
    {
        // The trail clears on a zone change, so this is really a guard against a single stale point.
        var trail = new PositionTrail();
        trail.Record(new Vector3(0f, 0f, 0f), Zone, 0d);
        trail.Record(new Vector3(10f, 0f, 0f), Zone, 1d);
        trail.Record(new Vector3(900f, 0f, 900f), 956, 2d);

        Assert.Null(Interception.EstimateVelocity(trail, 2d));
    }

    // ---------------------------------------------------------------- prediction

    [Fact]
    public void PredictionMovesAlongTheVelocity()
    {
        var predicted = Interception.Predict(new Vector3(0f, 5f, 0f), new Vector3(10f, 0f, -4f), 3f);

        Assert.Equal(30f, predicted.X, 3);
        Assert.Equal(-12f, predicted.Z, 3);

        // Height is carried through untouched rather than extrapolated.
        Assert.Equal(5f, predicted.Y, 3);
    }

    // ---------------------------------------------------------------- intercept

    [Fact]
    public void AStationaryTargetIsReachedAtWalkingPace()
    {
        var time = Interception.TimeToIntercept(
            Vector3.Zero, pursuerSpeed: 10f, new Vector3(100f, 0f, 0f), Vector3.Zero);

        Assert.Equal(10f, time!.Value, 3);
    }

    [Fact]
    public void StandingOnThemIsNoTimeAtAll()
    {
        Assert.Equal(0f, Interception.TimeToIntercept(
            Vector3.Zero, 10f, Vector3.Zero, new Vector3(5f, 0f, 0f)));
    }

    [Fact]
    public void SomebodyFleeingFasterThanYouCannotBeCaught()
    {
        var time = Interception.TimeToIntercept(
            Vector3.Zero, pursuerSpeed: 5f, new Vector3(50f, 0f, 0f), new Vector3(20f, 0f, 0f));

        Assert.Null(time);
    }

    [Fact]
    public void SomebodyFleeingAtExactlyYourSpeedCannotBeCaught()
    {
        // The degenerate case where the quadratic collapses to a line.
        var time = Interception.TimeToIntercept(
            Vector3.Zero, pursuerSpeed: 10f, new Vector3(50f, 0f, 0f), new Vector3(10f, 0f, 0f));

        Assert.Null(time);
    }

    [Fact]
    public void SomebodyRunningTowardsYouAtYourSpeedIsMetInTheMiddle()
    {
        var time = Interception.TimeToIntercept(
            Vector3.Zero, pursuerSpeed: 10f, new Vector3(100f, 0f, 0f), new Vector3(-10f, 0f, 0f));

        Assert.Equal(5f, time!.Value, 2);
    }

    [Fact]
    public void CrossingMovementIsLedRatherThanChased()
    {
        var target = new Vector3(0f, 0f, -100f);
        var velocity = new Vector3(10f, 0f, 0f);

        var point = Interception.InterceptPoint(Vector3.Zero, 20f, target, velocity);

        Assert.NotNull(point);

        // Aim ahead of them, not at where they currently are.
        Assert.True(point!.Value.X > target.X);

        // And the meeting point must be somewhere you can actually reach in that time.
        var time = Interception.TimeToIntercept(Vector3.Zero, 20f, target, velocity)!.Value;
        Assert.Equal(20f * time, MapGeometry.FlatDistance(Vector3.Zero, point.Value), 1);
    }

    [Fact]
    public void TheEarliestMeetingIsChosen()
    {
        // Running at you from the side has two mathematical solutions; the useful one is the first.
        var time = Interception.TimeToIntercept(
            Vector3.Zero, 15f, new Vector3(60f, 0f, 60f), new Vector3(-10f, 0f, -10f));

        Assert.NotNull(time);
        Assert.True(time!.Value > 0f);

        var later = Interception.TimeToIntercept(
            Vector3.Zero, 15f, new Vector3(60f, 0f, 60f), new Vector3(-10f, 0f, -10f));
        Assert.Equal(time.Value, later!.Value, 4);
    }

    [Fact]
    public void StandingStillCatchesNobodyWhoIsMoving()
    {
        Assert.Null(Interception.TimeToIntercept(
            Vector3.Zero, pursuerSpeed: 0f, new Vector3(10f, 0f, 0f), new Vector3(1f, 0f, 0f)));
    }

    [Fact]
    public void NegativeSpeedIsRejectedRatherThanSolved()
    {
        Assert.Null(Interception.TimeToIntercept(
            Vector3.Zero, -5f, new Vector3(10f, 0f, 0f), Vector3.Zero));
    }

    [Fact]
    public void AnUncatchableTargetHasNoInterceptPoint()
    {
        Assert.Null(Interception.InterceptPoint(
            Vector3.Zero, 5f, new Vector3(50f, 0f, 0f), new Vector3(30f, 0f, 0f)));
    }
}
