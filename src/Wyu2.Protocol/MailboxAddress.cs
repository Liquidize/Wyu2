using System.Security.Cryptography;
using System.Text;

namespace Wyu2.Protocol;

/// <summary>
/// A pairwise pseudonym that two accounts can both compute and nobody else can.
///
/// Envelopes are already unreadable to the relay, but until now they were addressed by account id, so
/// the relay's own tables spelled out who talks to whom. A mailbox address replaces that: the sender
/// posts to an opaque identifier derived from the secret only the two of them share, and the recipient
/// polls the same identifier. Two people who have never linked cannot guess each other's mailbox, and an
/// operator reading the database sees a heap of unrelated buckets rather than a friend graph.
///
/// The address is directional and rotates. Directional so that the two halves of a conversation do not
/// obviously belong together; rotating so that watching the same bucket for a month does not build up a
/// stable handle for a pair of people. Rotation is by wall clock rather than by negotiation, because
/// there is nowhere to negotiate: both sides divide the current time into windows and land on the same
/// answer without exchanging anything.
///
/// What this does not hide: the relay still sees which authenticated account posts to a mailbox and
/// which polls it, so an operator watching live traffic can rebuild the pairing. Breaking that needs
/// unlinkable write tokens, which is a separate piece of work. The guarantee here is about what the
/// stored data reveals, not about what a determined operator can observe as it happens.
/// </summary>
public static class MailboxAddress
{
    /// <summary>How long one mailbox address is used before it rotates.</summary>
    public const long WindowMs = 24 * 60 * 60 * 1000L;

    /// <summary>Info string keeping mailbox derivation clear of every key derivation.</summary>
    public const string DerivationInfo = "Wyu2/v1/mailbox";

    /// <summary>Length of the hex address. Sixteen bytes: far too wide to collide, short enough to index.</summary>
    public const int AddressLength = 32;

    /// <summary>Which rotation window a moment falls in.</summary>
    public static long WindowFor(long unixMs) => unixMs / WindowMs;

    /// <summary>
    /// Derives the address that <paramref name="senderAccountId"/> posts to for
    /// <paramref name="recipientAccountId"/> during one window.
    ///
    /// The shared secret is the raw ECDH agreement between the two long-term identity keys, not an epoch
    /// key: an address has to stay computable across a rotation, and it carries no message content, so
    /// there is nothing here for forward secrecy to protect. Someone who later steals a long-term key
    /// learns which buckets that account used, which they would learn from the account's own contact
    /// list anyway.
    /// </summary>
    public static string Derive(
        byte[] sharedSecret,
        string senderAccountId,
        string recipientAccountId,
        string purpose,
        long window)
    {
        ArgumentNullException.ThrowIfNull(sharedSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientAccountId);

        var info = Encoding.UTF8.GetBytes(
            $"{DerivationInfo}|v{ProtocolConstants.Version}|{purpose}|{senderAccountId}|{recipientAccountId}|{window}");

        var bytes = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, AddressLength / 2, salt: null, info);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// The pairwise secret two accounts agree on. Kept here rather than derived inline so that every
    /// caller mixes in the same context string and nobody accidentally reuses a raw agreement.
    /// </summary>
    public static byte[] SharedSecret(AccountKeyPair self, string otherPublicKeyBase64)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentException.ThrowIfNullOrWhiteSpace(otherPublicKeyBase64);

        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(Convert.FromBase64String(otherPublicKeyBase64), out _);

        return self.Key.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// The addresses a recipient should be watching right now: the current window plus the one either
    /// side of it.
    ///
    /// Both neighbours are needed. The previous window catches an envelope that was posted just before a
    /// rollover and has not expired yet; the next one catches a sender whose clock runs a little fast, who
    /// would otherwise post into a bucket nobody is reading until the rollover reaches them too. Three
    /// cheap subscriptions beats a class of bug that only shows up at midnight.
    /// </summary>
    public static IReadOnlyList<string> Inbox(
        byte[] sharedSecret,
        string senderAccountId,
        string recipientAccountId,
        string purpose,
        long nowUnixMs)
    {
        var window = WindowFor(nowUnixMs);

        return
        [
            Derive(sharedSecret, senderAccountId, recipientAccountId, purpose, window - 1),
            Derive(sharedSecret, senderAccountId, recipientAccountId, purpose, window),
            Derive(sharedSecret, senderAccountId, recipientAccountId, purpose, window + 1),
        ];
    }

    /// <summary>The single address to post to now.</summary>
    public static string Outbox(
        byte[] sharedSecret,
        string senderAccountId,
        string recipientAccountId,
        string purpose,
        long nowUnixMs)
        => Derive(sharedSecret, senderAccountId, recipientAccountId, purpose, WindowFor(nowUnixMs));

    /// <summary>Whether a string could be an address at all, so the relay can reject junk cheaply.</summary>
    public static bool IsWellFormed(string? address)
    {
        if (address is null || address.Length != AddressLength)
            return false;

        foreach (var c in address)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        return true;
    }
}
