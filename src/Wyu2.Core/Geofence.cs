using System.Numerics;

namespace Wyu2.Game;

/// <summary>What a geofence is measured against.</summary>
public enum GeofenceScope
{
    /// <summary>The whole of one zone.</summary>
    Zone = 0,

    /// <summary>A circle at fixed coordinates within one zone.</summary>
    Radius = 1,

    /// <summary>A circle that follows you around, wherever you are.</summary>
    NearMe = 2,
}

/// <summary>One spatial rule: an area, and which crossings of it are worth hearing about.</summary>
public sealed record GeofenceRule
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public GeofenceScope Scope { get; init; } = GeofenceScope.Zone;

    /// <summary>Zone the rule applies to. Ignored by <see cref="GeofenceScope.NearMe"/>.</summary>
    public ushort TerritoryTypeId { get; init; }

    public float CentreX { get; init; }

    public float CentreZ { get; init; }

    public float RadiusYalms { get; init; } = 50f;

    public bool OnEnter { get; init; } = true;

    public bool OnExit { get; init; }
}

/// <summary>A crossing that actually happened.</summary>
public readonly record struct GeofenceEvent(string SubjectId, string RuleId, bool Entered);

/// <summary>
/// Tracks who is inside which fence and reports the crossings. Stateful on purpose: a rule fires on the
/// transition, not on every tick somebody happens to be standing inside it.
/// </summary>
public sealed class GeofenceWatcher
{
    private readonly Dictionary<string, bool> inside = new(StringComparer.Ordinal);
    private readonly List<GeofenceEvent> events = [];

    /// <summary>
    /// Whether a point falls inside a rule. Returns null when there is not enough information to say,
    /// which is a different answer from being outside.
    /// </summary>
    public static bool? Contains(
        GeofenceRule rule,
        ushort territoryTypeId,
        Vector3? position,
        Vector3? reference = null)
    {
        if (territoryTypeId == 0)
            return null;

        switch (rule.Scope)
        {
            case GeofenceScope.Zone:
                return territoryTypeId == rule.TerritoryTypeId;

            case GeofenceScope.Radius:
                if (territoryTypeId != rule.TerritoryTypeId)
                    return false;

                return position is { } point &&
                       MapGeometry.FlatDistance(point, new Vector3(rule.CentreX, 0f, rule.CentreZ))
                           <= rule.RadiusYalms;

            case GeofenceScope.NearMe:
                if (position is not { } them || reference is not { } me)
                    return null;

                return MapGeometry.FlatDistance(them, me) <= rule.RadiusYalms;

            default:
                return null;
        }
    }

    /// <summary>
    /// Folds one observation of one subject through every rule and returns the crossings. The first
    /// observation only records where they are, so adding a rule does not immediately fire for everybody
    /// already standing inside it.
    /// </summary>
    public IReadOnlyList<GeofenceEvent> Evaluate(
        string subjectId,
        ushort territoryTypeId,
        Vector3? position,
        IReadOnlyList<GeofenceRule> rules,
        Vector3? reference = null)
    {
        events.Clear();

        foreach (var rule in rules)
        {
            var current = Contains(rule, territoryTypeId, position, reference);

            // No information: hold the previous state rather than inventing an exit. Somebody who stops
            // sharing their position has not left anywhere.
            if (current is not { } isInside)
                continue;

            var key = Key(subjectId, rule.Id);
            if (!inside.TryGetValue(key, out var was))
            {
                inside[key] = isInside;
                continue;
            }

            if (was == isInside)
                continue;

            inside[key] = isInside;

            if (isInside && rule.OnEnter)
                events.Add(new GeofenceEvent(subjectId, rule.Id, true));
            else if (!isInside && rule.OnExit)
                events.Add(new GeofenceEvent(subjectId, rule.Id, false));
        }

        return events;
    }

    /// <summary>Drops a subject's remembered state, so they are re-observed rather than compared.</summary>
    public void Forget(string subjectId)
    {
        var prefix = subjectId + " ";
        foreach (var key in inside.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            inside.Remove(key);
    }

    /// <summary>Drops everything, for a logout or a change of relay.</summary>
    public void Clear() => inside.Clear();

    private static string Key(string subjectId, string ruleId) => subjectId + " " + ruleId;
}
