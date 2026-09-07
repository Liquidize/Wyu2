using System.Numerics;
using FriendRadar.Configuration;
using FriendRadar.Protocol;
using FriendRadar.Tracking;

namespace FriendRadar.Tests;

/// <summary>
/// These cover the single most important rule in the plugin: a field that is switched off must never
/// appear in a payload.
/// </summary>
public class SharingProfileTests
{
    private static SelfSnapshot Snapshot(bool positionSuppressed = false) => new()
    {
        CharacterName = "Ysayle Dangoulain",
        FallbackName = "A friend",
        HomeWorldId = 73,
        CurrentWorldId = 80,
        TerritoryTypeId = 155,
        MapId = 24,
        InstanceId = 2,
        Position = new Vector3(12.5f, -3.25f, -88.75f),
        Rotation = 1.25f,
        JobId = 24,
        Level = 90,
        Activity = ActivityKind.InDuty,
        ActivityDetail = "The Aery",
        OnlineStatusId = 17,
        Flags = PresenceFlags.InCombat,
        PartySize = 4,
        FreeCompanyTag = "ISH",
        Note = "one more pull",
        ActivityDetailWithTarget = "Fighting Ixali Windtalker",
        TakenAtUnixMs = 1_700_000_000_000,
        PositionSuppressed = positionSuppressed,
    };

    [Fact]
    public void EverythingProfileSendsEverything()
    {
        var payload = Snapshot().ToPayload(SharingProfile.Everything());

        Assert.Equal("Ysayle Dangoulain", payload.CharacterName);
        Assert.Equal(73u, payload.HomeWorldId);
        Assert.Equal(80u, payload.CurrentWorldId);
        Assert.Equal((ushort)155, payload.TerritoryTypeId);
        Assert.Equal(24u, payload.MapId);
        Assert.Equal(2u, payload.InstanceId);
        Assert.Equal(12.5f, payload.X);
        Assert.Equal(-88.75f, payload.Z);
        Assert.Equal(24u, payload.JobId);
        Assert.Equal((byte)90, payload.Level);
        Assert.Equal(ActivityKind.InDuty, payload.Activity);
        Assert.Equal("Fighting Ixali Windtalker", payload.ActivityDetail);
        Assert.Equal(17u, payload.OnlineStatusId);
        Assert.Equal((byte)4, payload.PartySize);
        Assert.Equal("ISH", payload.FreeCompanyTag);
        Assert.Equal("one more pull", payload.Note);
        Assert.True(payload.HasPosition);
    }

    [Fact]
    public void MinimalProfileLeaksNothingButPresence()
    {
        var payload = Snapshot().ToPayload(SharingProfile.Minimal());

        Assert.Equal("A friend", payload.CharacterName);
        Assert.Equal(0u, payload.HomeWorldId);
        Assert.Equal(0u, payload.CurrentWorldId);
        Assert.Null(payload.TerritoryTypeId);
        Assert.Null(payload.MapId);
        Assert.Null(payload.InstanceId);
        Assert.Null(payload.X);
        Assert.Null(payload.Y);
        Assert.Null(payload.Z);
        Assert.Null(payload.Rotation);
        Assert.Null(payload.JobId);
        Assert.Null(payload.Level);
        Assert.Equal(ActivityKind.Unknown, payload.Activity);
        Assert.Null(payload.ActivityDetail);
        Assert.Equal(PresenceFlags.None, payload.Flags);
        Assert.Null(payload.PartySize);
        Assert.Null(payload.FreeCompanyTag);
        Assert.False(payload.HasPosition);

        // The status note is the one thing a minimal profile still says out loud.
        Assert.Equal("one more pull", payload.Note);
        Assert.Equal(17u, payload.OnlineStatusId);
    }

    [Fact]
    public void ZoneOnlyProfileKeepsTheZoneButDropsCoordinates()
    {
        var payload = Snapshot().ToPayload(SharingProfile.ZoneOnly());

        Assert.Equal((ushort)155, payload.TerritoryTypeId);
        Assert.Equal(2u, payload.InstanceId);
        Assert.Null(payload.X);
        Assert.Null(payload.Z);
        Assert.False(payload.HasPosition);
    }

    [Fact]
    public void PrivacyRuleSuppressionBeatsTheProfile()
    {
        var profile = SharingProfile.Everything();
        var payload = Snapshot(positionSuppressed: true).ToPayload(profile);

        Assert.True(profile.SharePosition);
        Assert.Null(payload.X);
        Assert.Null(payload.Y);
        Assert.Null(payload.Z);
        Assert.Null(payload.Rotation);

        // The zone still goes out; only the exact spot is withheld.
        Assert.Equal((ushort)155, payload.TerritoryTypeId);
    }

    [Fact]
    public void PositionIsWithheldWhenTheZoneIsNotShared()
    {
        var profile = SharingProfile.Everything();
        profile.ShareZone = false;

        var payload = Snapshot().ToPayload(profile);

        Assert.Null(payload.TerritoryTypeId);
        Assert.Null(payload.X);
        Assert.Null(payload.Z);
    }

    [Fact]
    public void HidingTheNameFallsBackToTheRelayLabel()
    {
        var profile = SharingProfile.Everything();
        profile.ShareCharacterName = false;

        Assert.Equal("A friend", Snapshot().ToPayload(profile).CharacterName);
    }

    [Fact]
    public void TurningOffActivityAlsoDropsTheFlags()
    {
        var profile = SharingProfile.Everything();
        profile.ShareActivity = false;

        var payload = Snapshot().ToPayload(profile);

        Assert.Equal(ActivityKind.Unknown, payload.Activity);
        Assert.Null(payload.ActivityDetail);
        Assert.Equal(PresenceFlags.None, payload.Flags);
    }

    [Fact]
    public void TheCombatTargetIsOnlyNamedWhenThatContactMaySeeIt()
    {
        var allowed = SharingProfile.Everything();
        var withheld = SharingProfile.Everything();
        withheld.ShareCombatTarget = false;

        Assert.Equal("Fighting Ixali Windtalker", Snapshot().ToPayload(allowed).ActivityDetail);
        Assert.Equal("The Aery", Snapshot().ToPayload(withheld).ActivityDetail);
    }

    [Fact]
    public void ANarrowerPerContactProfileCannotBeWidenedByTheDefaultOne()
    {
        // The snapshot is built once for everybody, so the target-free line has to survive in it.
        var snapshot = Snapshot();

        Assert.Equal("The Aery", snapshot.ActivityDetail);
        Assert.Equal("Fighting Ixali Windtalker", snapshot.ActivityDetailWithTarget);
    }

    [Fact]
    public void CloningAProfileDoesNotAliasIt()
    {
        var original = SharingProfile.Everything();
        var clone = original.Clone();
        clone.SharePosition = false;

        Assert.True(original.SharePosition);
        Assert.False(clone.SharePosition);
    }
}
