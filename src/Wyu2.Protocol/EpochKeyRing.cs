namespace Wyu2.Protocol;

/// <summary>One epoch's private key as it is written to disk, so a restart does not lose in-flight mail.</summary>
public sealed record EpochKeyRecord(
    int Epoch,
    string PrivateKeyBase64,
    long CreatedAtUnixMs,
    long ExpiresAtUnixMs);

/// <summary>
/// The rotating key agreement keys an account decrypts with, and the thing that actually delivers
/// forward secrecy: old private keys are destroyed on rotation, so once an epoch has aged out, nothing
/// captured from it can be opened again, whatever leaks later.
///
/// Only a couple of epochs are kept, which is the trade-off. Keep too few and a payload that was in
/// flight during a rotation cannot be read; keep too many and the window an attacker gains from a stolen
/// device grows.
/// </summary>
public sealed class EpochKeyRing : IDisposable
{
    /// <summary>Current plus one predecessor: enough to cover a rotation without widening the window.</summary>
    public const int DefaultRetainedEpochs = 2;

    private readonly Dictionary<int, Entry> entries = [];
    private readonly int retained;

    public EpochKeyRing(int retainedEpochs = DefaultRetainedEpochs)
        => retained = Math.Max(1, retainedEpochs);

    private sealed record Entry(AccountKeyPair Key, long CreatedAtUnixMs, long ExpiresAtUnixMs);

    /// <summary>Highest epoch held, or zero when the ring has never been rotated.</summary>
    public int CurrentEpoch { get; private set; }

    /// <summary>Epochs still held, newest first.</summary>
    public IReadOnlyList<int> Epochs => entries.Keys.OrderByDescending(e => e).ToList();

    /// <summary>The key new payloads are encrypted to, or null before the first rotation.</summary>
    public AccountKeyPair? Current => entries.TryGetValue(CurrentEpoch, out var entry) ? entry.Key : null;

    /// <summary>
    /// The private key for an epoch, when it is still held. A miss is the ordinary, intended outcome for
    /// anything old: it means forward secrecy did its job.
    /// </summary>
    public bool TryGet(int epoch, out AccountKeyPair key)
    {
        if (entries.TryGetValue(epoch, out var entry))
        {
            key = entry.Key;
            return true;
        }

        key = null!;
        return false;
    }

    /// <summary>
    /// Starts a new epoch, destroys any that have fallen out of the retention window, and returns the
    /// bundle to publish so contacts can encrypt to it.
    /// </summary>
    public PrekeyBundle Rotate(
        SigningKeyPair signer,
        string accountId,
        long nowUnixMs,
        long lifetimeMs)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var epoch = CurrentEpoch + 1;
        var key = AccountKeyPair.Create();
        var expires = nowUnixMs + Math.Max(1, lifetimeMs);

        entries[epoch] = new Entry(key, nowUnixMs, expires);
        CurrentEpoch = epoch;

        Prune();

        return PrekeyBundle.Create(signer, accountId, epoch, key, nowUnixMs, expires);
    }

    /// <summary>Whether the current epoch has run its course and a rotation is due.</summary>
    public bool NeedsRotation(long nowUnixMs)
        => !entries.TryGetValue(CurrentEpoch, out var entry) || nowUnixMs >= entry.ExpiresAtUnixMs;

    /// <summary>What to persist. Only the epochs still held, so pruning is durable.</summary>
    public IReadOnlyList<EpochKeyRecord> Export()
        => entries
            .OrderByDescending(e => e.Key)
            .Select(e => new EpochKeyRecord(
                e.Key, e.Value.Key.ExportPrivateKeyBase64(), e.Value.CreatedAtUnixMs, e.Value.ExpiresAtUnixMs))
            .ToList();

    /// <summary>
    /// Restores a ring. Records that cannot be read are skipped rather than throwing: a corrupt or
    /// half-written entry should cost one epoch, not stop the plugin loading.
    /// </summary>
    public static EpochKeyRing Import(
        IEnumerable<EpochKeyRecord>? records,
        int retainedEpochs = DefaultRetainedEpochs)
    {
        var ring = new EpochKeyRing(retainedEpochs);
        if (records is null)
            return ring;

        foreach (var record in records)
        {
            if (record.Epoch <= 0)
                continue;

            try
            {
                ring.entries[record.Epoch] = new Entry(
                    AccountKeyPair.Import(record.PrivateKeyBase64),
                    record.CreatedAtUnixMs,
                    record.ExpiresAtUnixMs);
            }
            catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
            {
                continue;
            }
        }

        ring.CurrentEpoch = ring.entries.Count == 0 ? 0 : ring.entries.Keys.Max();
        ring.Prune();
        return ring;
    }

    /// <summary>
    /// Destroys everything outside the retention window. This is the step that makes the whole scheme
    /// worth having, so it disposes the key rather than merely dropping the reference.
    /// </summary>
    private void Prune()
    {
        foreach (var epoch in entries.Keys.OrderByDescending(e => e).Skip(retained).ToList())
        {
            entries[epoch].Key.Dispose();
            entries.Remove(epoch);
        }
    }

    public void Dispose()
    {
        foreach (var entry in entries.Values)
            entry.Key.Dispose();

        entries.Clear();
        CurrentEpoch = 0;
    }
}
