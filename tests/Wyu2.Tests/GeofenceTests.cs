using System.Numerics;
using Wyu2.Game;

namespace Wyu2.Tests;

public class GeofenceTests
{
    private const ushort Thanalan = 140;
    private const ushort LaNoscea = 155;
    private const string Bob = "bob";

    private static GeofenceRule ZoneRule(ushort territory = Thanalan, bool onExit = false) => new()
    {
        Id = "zone",
        Name = "Thanalan",
        Scope = GeofenceScope.Zone,
        TerritoryTypeId = territory,
        OnExit = onExit,
    };

    private static GeofenceRule RadiusRule(bool onExit = false) => new()
    {
        Id = "radius",
        Name = "The camp",
        Scope = GeofenceScope.Radius,
        TerritoryTypeId = Thanalan,
        CentreX = 100f,
        CentreZ = 100f,
        RadiusYalms = 50f,
        OnExit = onExit,
    };

    private static GeofenceRule NearMeRule() => new()
    {
        Id = "nearme",
        Name = "Near me",
        Scope = GeofenceScope.NearMe,
        RadiusYalms = 30f,
    };

    // ---------------------------------------------------------------- containment

    [Fact]
    public void AZoneRuleMatchesItsZone()
    {
        Assert.True(GeofenceWatcher.Contains(ZoneRule(), Thanalan, null));
        Assert.False(GeofenceWatcher.Contains(ZoneRule(), LaNoscea, null));
    }

    [Fact]
    public void ARadiusRuleNeedsBothTheZoneAndThePosition()
    {
        var rule = RadiusRule();

        Assert.True(GeofenceWatcher.Contains(rule, Thanalan, new Vector3(120f, 0f, 100f)));
        Assert.False(GeofenceWatcher.Contains(rule, Thanalan, new Vector3(400f, 0f, 400f)));

        // Right coordinates, wrong zone: that is a different place entirely.
        Assert.False(GeofenceWatcher.Contains(rule, LaNoscea, new Vector3(100f, 0f, 100f)));
    }

    [Fact]
    public void HeightDoesNotTakeYouOutOfARadius()
    {
        Assert.True(GeofenceWatcher.Contains(RadiusRule(), Thanalan, new Vector3(100f, 900f, 100f)));
    }

    [Fact]
    public void NearMeComparesAgainstWhereYouAre()
    {
        var rule = NearMeRule();

        Assert.True(GeofenceWatcher.Contains(rule, Thanalan, new Vector3(10f, 0f, 0f), Vector3.Zero));
        Assert.False(GeofenceWatcher.Contains(rule, Thanalan, new Vector3(200f, 0f, 0f), Vector3.Zero));
    }

    [Fact]
    public void MissingInformationIsNotTheSameAsBeingOutside()
    {
        // No zone at all.
        Assert.Null(GeofenceWatcher.Contains(ZoneRule(), 0, null));

        // A radius rule with no position to test.
        Assert.False(GeofenceWatcher.Contains(RadiusRule(), Thanalan, null) ?? false);

        // Near-me with nothing to compare against.
        Assert.Null(GeofenceWatcher.Contains(NearMeRule(), Thanalan, new Vector3(1f, 0f, 1f), reference: null));
        Assert.Null(GeofenceWatcher.Contains(NearMeRule(), Thanalan, null, Vector3.Zero));
    }

    // ---------------------------------------------------------------- transitions

    [Fact]
    public void TheFirstObservationOnlyRecords()
    {
        var watcher = new GeofenceWatcher();
        var rules = new[] { ZoneRule() };

        // Somebody already standing inside should not fire the moment the rule is created.
        Assert.Empty(watcher.Evaluate(Bob, Thanalan, null, rules));
    }

    [Fact]
    public void EnteringFires()
    {
        var watcher = new GeofenceWatcher();
        var rules = new[] { ZoneRule() };

        watcher.Evaluate(Bob, LaNoscea, null, rules);
        var events = watcher.Evaluate(Bob, Thanalan, null, rules);

        var crossing = Assert.Single(events);
        Assert.True(crossing.Entered);
        Assert.Equal("zone", crossing.RuleId);
        Assert.Equal(Bob, crossing.SubjectId);
    }

    [Fact]
    public void StayingInsideDoesNotFireAgain()
    {
        var watcher = new GeofenceWatcher();
        var rules = new[] { ZoneRule() };

        watcher.Evaluate(Bob, LaNoscea, null, rules);
        Assert.Single(watcher.Evaluate(Bob, Thanalan, null, rules));

        for (var i = 0; i < 5; i++)
            Assert.Empty(watcher.Evaluate(Bob, Thanalan, null, rules));
    }

    [Fact]
    public void LeavingOnlyFiresWhenAskedFor()
    {
        var watcher = new GeofenceWatcher();
        var quiet = new[] { ZoneRule() };

        watcher.Evaluate(Bob, Thanalan, null, quiet);
        Assert.Empty(watcher.Evaluate(Bob, LaNoscea, null, quiet));

        var loud = new GeofenceWatcher();
        var rules = new[] { ZoneRule(onExit: true) };
        loud.Evaluate(Bob, Thanalan, null, rules);

        var crossing = Assert.Single(loud.Evaluate(Bob, LaNoscea, null, rules));
        Assert.False(crossing.Entered);
    }

    [Fact]
    public void LosingTrackOfSomebodyIsNotAnExit()
    {
        var watcher = new GeofenceWatcher();
        var rules = new[] { RadiusRule(onExit: true) };

        watcher.Evaluate(Bob, Thanalan, new Vector3(100f, 0f, 100f), rules);

        // They stopped sharing their position. That is not the same as walking out.
        Assert.Empty(watcher.Evaluate(Bob, 0, null, rules));

        // And when they reappear inside, nothing spurious fires either.
        Assert.Empty(watcher.Evaluate(Bob, Thanalan, new Vector3(105f, 0f, 100f), rules));
    }

    [Fact]
    public void SubjectsAreTrackedIndependently()
    {
        var watcher = new GeofenceWatcher();
        var rules = new[] { ZoneRule() };

        watcher.Evaluate("alice", LaNoscea, null, rules);
        watcher.Evaluate("bob", LaNoscea, null, rules);

        var events = watcher.Evaluate("alice", Thanalan, null, rules);

        Assert.Equal("alice", Assert.Single(events).SubjectId);
        Assert.Empty(watcher.Evaluate("bob", LaNoscea, null, rules));
    }

    [Fact]
    public void SeveralRulesAreEvaluatedTogether()
    {
        var watcher = new GeofenceWatcher();
        var rules = new[] { ZoneRule(), RadiusRule() };

        watcher.Evaluate(Bob, LaNoscea, new Vector3(0f, 0f, 0f), rules);
        var events = watcher.Evaluate(Bob, Thanalan, new Vector3(100f, 0f, 100f), rules);

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.True(e.Entered));
    }

    [Fact]
    public void ForgettingSomebodyMakesThemNewAgain()
    {
        var watcher = new GeofenceWatcher();
        var rules = new[] { ZoneRule() };

        watcher.Evaluate(Bob, LaNoscea, null, rules);
        watcher.Forget(Bob);

        // Re-observed rather than compared, so no crossing is reported.
        Assert.Empty(watcher.Evaluate(Bob, Thanalan, null, rules));
    }

    [Fact]
    public void ClearingForgetsEverybody()
    {
        var watcher = new GeofenceWatcher();
        var rules = new[] { ZoneRule() };

        watcher.Evaluate("alice", LaNoscea, null, rules);
        watcher.Evaluate("bob", LaNoscea, null, rules);
        watcher.Clear();

        Assert.Empty(watcher.Evaluate("alice", Thanalan, null, rules));
        Assert.Empty(watcher.Evaluate("bob", Thanalan, null, rules));
    }

    [Fact]
    public void ForgettingOnePersonLeavesTheRest()
    {
        var watcher = new GeofenceWatcher();
        var rules = new[] { ZoneRule() };

        watcher.Evaluate("alice", LaNoscea, null, rules);
        watcher.Evaluate("alice2", LaNoscea, null, rules);
        watcher.Forget("alice");

        // A prefix match would wrongly drop "alice2" as well.
        Assert.Single(watcher.Evaluate("alice2", Thanalan, null, rules));
    }
}
