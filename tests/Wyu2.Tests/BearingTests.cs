using Wyu2.Game;

namespace Wyu2.Tests;

public class BearingTests
{
    // The game's north is -Z, east is +X.
    [Theory]
    [InlineData(0f, -10f, "N")]
    [InlineData(10f, 0f, "E")]
    [InlineData(0f, 10f, "S")]
    [InlineData(-10f, 0f, "W")]
    [InlineData(10f, -10f, "NE")]
    [InlineData(10f, 10f, "SE")]
    [InlineData(-10f, 10f, "SW")]
    [InlineData(-10f, -10f, "NW")]
    public void CardinalAndIntercardinalDirectionsComeOutRight(float dx, float dz, string expected)
    {
        Assert.Equal(expected, Bearing.ToCompass(Bearing.FromWorldDelta(dx, dz)));
    }

    [Fact]
    public void SixteenthPointsAreReachable()
    {
        // A little east of north should read NNE, not N or NE.
        Assert.Equal("NNE", Bearing.ToCompass(Bearing.FromWorldDelta(4f, -10f)));
        Assert.Equal("WNW", Bearing.ToCompass(Bearing.FromWorldDelta(-10f, -4f)));
    }

    [Fact]
    public void BearingsAlwaysLandInASingleTurn()
    {
        for (var angle = -20f; angle < 20f; angle += 0.13f)
        {
            var dx = MathF.Sin(angle) * 10f;
            var dz = -MathF.Cos(angle) * 10f;
            Assert.InRange(Bearing.FromWorldDelta(dx, dz), 0f, RadarSweep.Tau);
        }
    }

    [Fact]
    public void EveryCompassPointIsProducedSomewhere()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 720; i++)
        {
            var angle = i / 720f * RadarSweep.Tau;
            seen.Add(Bearing.ToCompass(angle));
        }

        Assert.Equal(16, seen.Count);
    }

    [Fact]
    public void CloseContactsGetHereRatherThanAJitteringDirection()
    {
        Assert.Equal("here", Bearing.Describe(0f, 0f));
        Assert.Equal("here", Bearing.Describe(2.9f, 1.2f));
    }

    [Fact]
    public void TheReadoutRoundsDistanceAndNamesTheDirection()
    {
        Assert.Equal("142y NE", Bearing.Describe(141.6f, Bearing.FromWorldDelta(10f, -10f)));
        Assert.Equal("12y S", Bearing.Describe(12.4f, Bearing.FromWorldDelta(0f, 10f)));
    }

    [Fact]
    public void WrappingAroundNorthDoesNotProduceAGap()
    {
        // Just west of due north must still read N, not NNW or a wrapped nonsense value.
        Assert.Equal("N", Bearing.ToCompass(Bearing.FromWorldDelta(-0.2f, -10f)));
        Assert.Equal("N", Bearing.ToCompass(Bearing.FromWorldDelta(0.2f, -10f)));
    }
}
