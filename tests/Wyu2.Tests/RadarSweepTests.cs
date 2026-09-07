using Wyu2.Game;

namespace Wyu2.Tests;

public class RadarSweepTests
{
    private const float Period = 3f;

    [Fact]
    public void TheSweepCompletesOneTurnPerPeriod()
    {
        Assert.Equal(0f, RadarSweep.Angle(0d, Period), 4);
        Assert.Equal(RadarSweep.Tau / 4f, RadarSweep.Angle(Period / 4d, Period), 4);
        Assert.Equal(RadarSweep.Tau / 2f, RadarSweep.Angle(Period / 2d, Period), 4);
    }

    [Fact]
    public void TheSweepWrapsInsteadOfRunningAway()
    {
        // Twenty minutes in, the angle is still a sane bearing and still lines up with turn boundaries.
        Assert.Equal(0f, RadarSweep.Angle(Period * 400d, Period), 3);
        Assert.InRange(RadarSweep.Angle(1234.567d, Period), 0f, RadarSweep.Tau);
    }

    [Fact]
    public void ContactIsInstantAndThenAgesOverAFullTurn()
    {
        // The sweep sits exactly on the blip.
        Assert.Equal(0f, RadarSweep.SecondsSincePass(1.2f, 1.2f, Period), 4);

        // A quarter turn past it.
        var quarter = RadarSweep.SecondsSincePass(1.2f + (RadarSweep.Tau / 4f), 1.2f, Period);
        Assert.Equal(Period / 4f, quarter, 4);

        // Just before coming back round, the age approaches a full period rather than resetting early.
        var almost = RadarSweep.SecondsSincePass(1.2f - 0.001f, 1.2f, Period);
        Assert.InRange(almost, Period * 0.99f, Period);
    }

    [Fact]
    public void BearingsOnEitherSideOfTheWrapPointAreHandled()
    {
        // A blip just past pi and a sweep just before -pi are neighbours, not a full turn apart.
        var age = RadarSweep.SecondsSincePass(-MathF.PI + 0.01f, MathF.PI - 0.01f, Period);
        Assert.InRange(age, 0f, Period * 0.01f);
    }

    [Fact]
    public void PingIsBrightestAtContactAndDiesOutAfterTheDecay()
    {
        Assert.Equal(1f, RadarSweep.PingStrength(0f, 1.5f), 4);
        Assert.True(RadarSweep.PingStrength(0.4f, 1.5f) > RadarSweep.PingStrength(0.8f, 1.5f));
        Assert.Equal(0f, RadarSweep.PingStrength(1.5f, 1.5f));
        Assert.Equal(0f, RadarSweep.PingStrength(9f, 1.5f));
    }

    [Fact]
    public void PingDecaysMonotonically()
    {
        var previous = float.MaxValue;
        for (var t = 0f; t < 1.5f; t += 0.05f)
        {
            var strength = RadarSweep.PingStrength(t, 1.5f);
            Assert.True(strength <= previous, $"ping rose again at t={t}");
            previous = strength;
        }
    }

    [Fact]
    public void TheRingExpandsOnceAndThenStops()
    {
        Assert.Equal(0f, RadarSweep.RingProgress(0f, 1f), 4);
        Assert.Equal(0.5f, RadarSweep.RingProgress(0.5f, 1f), 4);
        Assert.Equal(0f, RadarSweep.RingProgress(1f, 1f));
        Assert.Equal(0f, RadarSweep.RingProgress(2.5f, 1f));
    }

    [Fact]
    public void PulsePhaseStaysInRangeAndIsOffsetPerMarker()
    {
        for (var t = 0d; t < 10d; t += 0.37d)
            Assert.InRange(RadarSweep.PulsePhase(t, 2.5f, 0.4f), 0f, 1f);

        Assert.NotEqual(
            RadarSweep.PulsePhase(1d, 2.5f, 0f),
            RadarSweep.PulsePhase(1d, 2.5f, 0.5f));
    }

    [Fact]
    public void MarkerOffsetsAreStableAcrossRunsAndSpreadOut()
    {
        // Not string.GetHashCode: that is randomised per process, so phases would shuffle on restart.
        Assert.Equal(RadarSweep.StableOffset("abc123"), RadarSweep.StableOffset("abc123"));
        Assert.NotEqual(RadarSweep.StableOffset("abc123"), RadarSweep.StableOffset("abc124"));
        Assert.Equal(0f, RadarSweep.StableOffset(null));

        var offsets = Enumerable.Range(0, 200)
            .Select(i => RadarSweep.StableOffset(Guid.NewGuid().ToString("N")))
            .ToList();

        Assert.All(offsets, o => Assert.InRange(o, 0f, 1f));
        Assert.True(offsets.Distinct().Count() > 150, "offsets should spread across the range");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void NonPositivePeriodsDoNotDivideByZero(float period)
    {
        Assert.Equal(0f, RadarSweep.Angle(5d, period));
        Assert.Equal(0f, RadarSweep.SecondsSincePass(1f, 2f, period));
        Assert.Equal(0f, RadarSweep.PulsePhase(5d, period, 0.2f));
        Assert.Equal(0f, RadarSweep.PingStrength(0.5f, period));
        Assert.Equal(0f, RadarSweep.RingProgress(0.5f, period));
    }

    [Fact]
    public void NormalizeWrapsIntoASingleTurn()
    {
        Assert.Equal(0f, RadarSweep.Normalize(0f), 5);
        Assert.Equal(RadarSweep.Tau - 0.5f, RadarSweep.Normalize(-0.5f), 4);
        Assert.InRange(RadarSweep.Normalize(RadarSweep.Tau * 3.25f), 0f, RadarSweep.Tau);
    }
}
