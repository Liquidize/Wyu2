using System.Numerics;
using Wyu2.Game;

namespace Wyu2.Tests;

/// <summary>
/// These exist because the radar shipped mirrored east to west for several releases. Forward and back
/// were correct throughout, which is exactly why nobody spotted it: the blips moved plausibly, they were
/// just on the wrong side. Every orientation is pinned here so it cannot come back.
/// </summary>
public class RadarProjectionTests
{
    // Yaw is atan2(forward.X, forward.Z), so zero faces south.
    private const float FacingSouth = 0f;
    private static readonly float FacingNorth = MathF.PI;
    private static readonly float FacingEast = MathF.PI / 2f;
    private static readonly float FacingWest = -MathF.PI / 2f;

    private static readonly Vector3 East = new(10f, 0f, 0f);
    private static readonly Vector3 West = new(-10f, 0f, 0f);
    private static readonly Vector3 North = new(0f, 0f, -10f);
    private static readonly Vector3 South = new(0f, 0f, 10f);

    private static void AssertRight(Vector2 p) => Assert.True(p.X > 0.01f, $"expected right, got {p}");

    private static void AssertLeft(Vector2 p) => Assert.True(p.X < -0.01f, $"expected left, got {p}");

    private static void AssertUp(Vector2 p) => Assert.True(p.Y < -0.01f, $"expected up, got {p}");

    private static void AssertDown(Vector2 p) => Assert.True(p.Y > 0.01f, $"expected down, got {p}");

    [Fact]
    public void NorthUpPutsTheCompassWhereYouExpect()
    {
        var yaw = RadarProjection.NorthUpYaw;

        AssertUp(RadarProjection.WorldOffsetToRadar(North, yaw));
        AssertDown(RadarProjection.WorldOffsetToRadar(South, yaw));
        AssertRight(RadarProjection.WorldOffsetToRadar(East, yaw));
        AssertLeft(RadarProjection.WorldOffsetToRadar(West, yaw));
    }

    [Fact]
    public void FacingNorthPutsEastOnYourRight()
    {
        // The case that was wrong: this is the whole bug in one assertion.
        AssertRight(RadarProjection.WorldOffsetToRadar(East, FacingNorth));
        AssertLeft(RadarProjection.WorldOffsetToRadar(West, FacingNorth));
    }

    [Fact]
    public void FacingSouthPutsEastOnYourLeft()
    {
        AssertLeft(RadarProjection.WorldOffsetToRadar(East, FacingSouth));
        AssertRight(RadarProjection.WorldOffsetToRadar(West, FacingSouth));
        AssertUp(RadarProjection.WorldOffsetToRadar(South, FacingSouth));
        AssertDown(RadarProjection.WorldOffsetToRadar(North, FacingSouth));
    }

    [Fact]
    public void FacingEastPutsSouthOnYourRight()
    {
        AssertUp(RadarProjection.WorldOffsetToRadar(East, FacingEast));
        AssertDown(RadarProjection.WorldOffsetToRadar(West, FacingEast));
        AssertRight(RadarProjection.WorldOffsetToRadar(South, FacingEast));
        AssertLeft(RadarProjection.WorldOffsetToRadar(North, FacingEast));
    }

    [Fact]
    public void FacingWestPutsNorthOnYourRight()
    {
        AssertUp(RadarProjection.WorldOffsetToRadar(West, FacingWest));
        AssertDown(RadarProjection.WorldOffsetToRadar(East, FacingWest));
        AssertRight(RadarProjection.WorldOffsetToRadar(North, FacingWest));
        AssertLeft(RadarProjection.WorldOffsetToRadar(South, FacingWest));
    }

    [Fact]
    public void WhateverYouFaceIsAlwaysStraightAhead()
    {
        foreach (var yaw in new[] { FacingSouth, FacingNorth, FacingEast, FacingWest, 1.1f, -2.4f })
        {
            var forward = new Vector3(MathF.Sin(yaw) * 10f, 0f, MathF.Cos(yaw) * 10f);
            var projected = RadarProjection.WorldOffsetToRadar(forward, yaw);

            AssertUp(projected);
            Assert.Equal(0f, projected.X, 3);
        }
    }

    [Fact]
    public void TheProjectionIsNotMirrored()
    {
        // A mirrored transform flips handedness. Cross the projected east and north vectors: with the
        // screen's Y pointing down, a correctly handed result is negative here, and a mirror is positive.
        foreach (var yaw in new[] { FacingSouth, FacingNorth, FacingEast, FacingWest, 0.7f, 2.9f, -1.3f })
        {
            var e = RadarProjection.WorldOffsetToRadar(East, yaw);
            var n = RadarProjection.WorldOffsetToRadar(North, yaw);

            var cross = (e.X * n.Y) - (e.Y * n.X);
            Assert.True(cross < 0f, $"handedness flipped at yaw {yaw}");
        }
    }

    [Fact]
    public void DistanceSurvivesTheProjection()
    {
        foreach (var yaw in new[] { 0f, 1f, 2f, -3f })
        {
            var offset = new Vector3(30f, 500f, -40f);
            var projected = RadarProjection.WorldOffsetToRadar(offset, yaw);

            // Fifty yalms flat, whichever way the camera points, and height must not leak in.
            Assert.Equal(50f, projected.Length(), 2);
        }
    }

    [Fact]
    public void HeightIsIgnored()
    {
        var flat = RadarProjection.WorldOffsetToRadar(new Vector3(10f, 0f, 20f), 1.2f);
        var high = RadarProjection.WorldOffsetToRadar(new Vector3(10f, 900f, 20f), 1.2f);

        Assert.Equal(flat, high);
    }

    [Fact]
    public void StandingOnSomebodyProjectsToTheCentre()
    {
        Assert.Equal(Vector2.Zero, RadarProjection.WorldOffsetToRadar(Vector3.Zero, 1.7f));
    }

    [Fact]
    public void RotatingTheCameraRotatesTheWorldTheOtherWay()
    {
        // Turning a quarter turn to the left should sweep a fixed contact a quarter turn to the right.
        var first = RadarProjection.WorldOffsetToRadar(East, FacingNorth);
        var second = RadarProjection.WorldOffsetToRadar(East, FacingNorth + (MathF.PI / 2f));

        Assert.Equal(first.Length(), second.Length(), 3);
        Assert.NotEqual(first, second);

        // A full turn comes back to where it started.
        var full = RadarProjection.WorldOffsetToRadar(East, FacingNorth + (MathF.PI * 2f));
        Assert.Equal(first.X, full.X, 3);
        Assert.Equal(first.Y, full.Y, 3);
    }
}
