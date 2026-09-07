namespace Wyu2.Tracking;

/// <summary>What the guard decided about one incoming update.</summary>
public enum ReplayVerdict
{
    /// <summary>Genuinely newer than anything seen from this sender.</summary>
    Accepted = 0,

    /// <summary>
    /// The counter went backwards but the update is clearly recent, so the sender restarted from a
    /// fresh install rather than an attacker replaying an old capture.
    /// </summary>
    AcceptedAfterReset = 1,

    /// <summary>Old data being served as if it were current.</summary>
    RejectedReplay = 2,

    /// <summary>Stamped further ahead than any clock skew explains.</summary>
    RejectedFromTheFuture = 3,
}

/// <summary>
/// Rejects presence that has been replayed or rolled back.
///
/// The relay cannot read a payload, but it decides which one you receive, so a malicious or simply buggy
/// one could re-serve yesterday's blob and the client would draw a stale position as if it were current.
/// Every payload carries a counter that only ever rises at the sender, and both the counter and the
/// timestamp are inside the sealed envelope, so neither can be edited in transit.
/// </summary>
public sealed class ReplayGuard
{
    /// <summary>
    /// How much newer than the last accepted update something must be before a lower counter is read as
    /// a sender who reinstalled rather than as a replay. A minute is far longer than the publish
    /// interval and far shorter than any capture worth replaying.
    /// </summary>
    public const long ResetGraceMs = 60_000;

    /// <summary>Clock skew we tolerate before treating a timestamp as nonsense.</summary>
    public const long FutureToleranceMs = 5 * 60_000;

    private readonly Dictionary<string, Seen> seen = new(StringComparer.Ordinal);

    private readonly record struct Seen(long Sequence, long SentAtUnixMs);

    /// <summary>
    /// Judges one update and, when it is accepted, remembers it as the new high-water mark. A rejected
    /// update never moves the mark, so a replay cannot poison the state for the genuine updates that
    /// follow it.
    /// </summary>
    public ReplayVerdict Inspect(string senderId, long sequence, long sentAtUnixMs, long nowUnixMs)
    {
        if (sentAtUnixMs > nowUnixMs + FutureToleranceMs)
            return ReplayVerdict.RejectedFromTheFuture;

        if (!seen.TryGetValue(senderId, out var last))
        {
            seen[senderId] = new Seen(sequence, sentAtUnixMs);
            return ReplayVerdict.Accepted;
        }

        // A replayed capture can only ever carry a counter we have already passed, because the sender's
        // counter never goes down on its own.
        if (sequence > last.Sequence)
        {
            seen[senderId] = new Seen(sequence, sentAtUnixMs);
            return ReplayVerdict.Accepted;
        }

        // A fresh install starts counting again from nothing. The timestamp is what tells the two apart.
        if (sentAtUnixMs > last.SentAtUnixMs + ResetGraceMs)
        {
            seen[senderId] = new Seen(sequence, sentAtUnixMs);
            return ReplayVerdict.AcceptedAfterReset;
        }

        return ReplayVerdict.RejectedReplay;
    }

    /// <summary>True when the verdict means the payload should be used.</summary>
    public static bool IsAccepted(ReplayVerdict verdict)
        => verdict is ReplayVerdict.Accepted or ReplayVerdict.AcceptedAfterReset;

    /// <summary>
    /// Forgets a sender, so the next update from them is taken at face value. Used when a contact is
    /// removed and re-added, which gives them a new account and no shared history.
    /// </summary>
    public void Forget(string senderId) => seen.Remove(senderId);

    public void Clear() => seen.Clear();

    /// <summary>The last counter accepted from a sender, for diagnostics.</summary>
    public long? LastSequence(string senderId)
        => seen.TryGetValue(senderId, out var last) ? last.Sequence : null;
}
