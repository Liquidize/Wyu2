using Wyu2.Tracking;

namespace Wyu2.Tests;

public class ReplayGuardTests
{
    private const string Bob = "bob";
    private const long Now = 1_800_000_000_000;

    [Fact]
    public void TheFirstUpdateFromSomebodyIsTakenAtFaceValue()
    {
        var guard = new ReplayGuard();

        Assert.Equal(ReplayVerdict.Accepted, guard.Inspect(Bob, sequence: 41, Now, Now));
        Assert.Equal(41, guard.LastSequence(Bob));
    }

    [Fact]
    public void RisingCountersAreAccepted()
    {
        var guard = new ReplayGuard();

        for (var i = 1; i <= 5; i++)
            Assert.Equal(ReplayVerdict.Accepted, guard.Inspect(Bob, i, Now + (i * 1000), Now + (i * 1000)));

        Assert.Equal(5, guard.LastSequence(Bob));
    }

    [Fact]
    public void AnOldCaptureServedAgainIsRejected()
    {
        var guard = new ReplayGuard();
        guard.Inspect(Bob, 10, Now, Now);

        // The relay hands back a blob from a minute ago as if it were current.
        var verdict = guard.Inspect(Bob, 4, Now - 60_000, Now + 1_000);

        Assert.Equal(ReplayVerdict.RejectedReplay, verdict);
        Assert.False(ReplayGuard.IsAccepted(verdict));
    }

    [Fact]
    public void ReplayingTheVeryLastUpdateIsAlsoRejected()
    {
        var guard = new ReplayGuard();
        guard.Inspect(Bob, 10, Now, Now);

        // Equal is not greater: serving the same blob twice must not pass.
        Assert.Equal(ReplayVerdict.RejectedReplay, guard.Inspect(Bob, 10, Now, Now + 5_000));
    }

    [Fact]
    public void ARejectedUpdateDoesNotMoveTheMark()
    {
        var guard = new ReplayGuard();
        guard.Inspect(Bob, 10, Now, Now);
        guard.Inspect(Bob, 3, Now - 30_000, Now);

        // A replay must not be able to lower the bar for the updates that follow it.
        Assert.Equal(10, guard.LastSequence(Bob));
        Assert.Equal(ReplayVerdict.RejectedReplay, guard.Inspect(Bob, 4, Now - 20_000, Now));
        Assert.Equal(ReplayVerdict.Accepted, guard.Inspect(Bob, 11, Now + 1_000, Now + 1_000));
    }

    [Fact]
    public void AReinstalledSenderIsLetBackIn()
    {
        var guard = new ReplayGuard();
        guard.Inspect(Bob, 5_000, Now, Now);

        // They reinstalled: the counter restarts, but the update is plainly recent.
        var later = Now + (10 * 60_000);
        var verdict = guard.Inspect(Bob, 1, later, later);

        Assert.Equal(ReplayVerdict.AcceptedAfterReset, verdict);
        Assert.True(ReplayGuard.IsAccepted(verdict));
        Assert.Equal(1, guard.LastSequence(Bob));
    }

    [Fact]
    public void TheResetPathIsTooNarrowForAReplayToSlipThrough()
    {
        var guard = new ReplayGuard();
        guard.Inspect(Bob, 5_000, Now, Now);

        // Just inside the grace window: still a replay, not a reset.
        Assert.Equal(
            ReplayVerdict.RejectedReplay,
            guard.Inspect(Bob, 1, Now + ReplayGuard.ResetGraceMs - 1, Now + ReplayGuard.ResetGraceMs));
    }

    [Fact]
    public void TimestampsFromTheFutureAreRefused()
    {
        var guard = new ReplayGuard();

        var verdict = guard.Inspect(Bob, 1, Now + ReplayGuard.FutureToleranceMs + 60_000, Now);

        Assert.Equal(ReplayVerdict.RejectedFromTheFuture, verdict);

        // And nothing was recorded, so a sane update afterwards still works.
        Assert.Null(guard.LastSequence(Bob));
        Assert.Equal(ReplayVerdict.Accepted, guard.Inspect(Bob, 1, Now, Now));
    }

    [Fact]
    public void ModestClockSkewIsTolerated()
    {
        var guard = new ReplayGuard();

        // Somebody's clock is a minute fast. That is not an attack.
        Assert.Equal(ReplayVerdict.Accepted, guard.Inspect(Bob, 1, Now + 60_000, Now));
    }

    [Fact]
    public void AFutureStampedReplayCannotPoisonTheMark()
    {
        var guard = new ReplayGuard();
        guard.Inspect(Bob, 10, Now, Now);

        guard.Inspect(Bob, 99_999, Now + (60 * 60_000), Now);

        // Rejected outright, so the sender's genuine next update is still accepted.
        Assert.Equal(10, guard.LastSequence(Bob));
        Assert.Equal(ReplayVerdict.Accepted, guard.Inspect(Bob, 11, Now + 1_000, Now + 1_000));
    }

    [Fact]
    public void SendersAreTrackedSeparately()
    {
        var guard = new ReplayGuard();
        guard.Inspect("alice", 100, Now, Now);

        // Bob's low counter says nothing about Alice's.
        Assert.Equal(ReplayVerdict.Accepted, guard.Inspect("bob", 1, Now, Now));
        Assert.Equal(100, guard.LastSequence("alice"));
        Assert.Equal(1, guard.LastSequence("bob"));
    }

    [Fact]
    public void ForgettingSomebodyClearsTheirHistory()
    {
        var guard = new ReplayGuard();
        guard.Inspect(Bob, 500, Now, Now);
        guard.Forget(Bob);

        Assert.Null(guard.LastSequence(Bob));
        Assert.Equal(ReplayVerdict.Accepted, guard.Inspect(Bob, 1, Now, Now));
    }

    [Fact]
    public void ClearingForgetsEverybody()
    {
        var guard = new ReplayGuard();
        guard.Inspect("alice", 10, Now, Now);
        guard.Inspect("bob", 10, Now, Now);

        guard.Clear();

        Assert.Null(guard.LastSequence("alice"));
        Assert.Null(guard.LastSequence("bob"));
    }

    [Fact]
    public void AnUnknownSenderHasNoRecordedCounter()
    {
        Assert.Null(new ReplayGuard().LastSequence("nobody"));
    }
}
