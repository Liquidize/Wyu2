using System.Text;

namespace Wyu2.Protocol;

/// <summary>
/// A published, short-lived key agreement key, vouched for by the account's long-term signing key.
///
/// This is what gives presence forward secrecy. Senders encrypt to the recipient's current epoch key
/// rather than to their long-term key, and recipients throw the matching private key away once it ages
/// out. After that, anything an attacker captured from those epochs stays unreadable even if the
/// long-term key later leaks.
///
/// The signature is what stops the relay handing out an epoch key of its own and reading everything: a
/// bundle whose signature does not check against the sender's known identity key is not used.
/// </summary>
public sealed record PrekeyBundle
{
    /// <summary>True when this looks like a bundle at all, as opposed to an unset placeholder.</summary>
    public bool IsPresent => Epoch > 0 && EpochPublicKey.Length > 0 && Signature.Length > 0;

    /// <summary>Rises by one on each rotation. Identifies which private key opens a payload.</summary>
    public int Epoch { get; init; }

    /// <summary>Base64 SubjectPublicKeyInfo of the epoch's ECDH public key.</summary>
    public string EpochPublicKey { get; init; } = string.Empty;

    public long CreatedAtUnixMs { get; init; }

    /// <summary>After this the sender has moved on; recipients should stop encrypting to it.</summary>
    public long ExpiresAtUnixMs { get; init; }

    /// <summary>Base64 ECDSA signature over <see cref="CanonicalBytes"/>, by the identity signing key.</summary>
    public string Signature { get; init; } = string.Empty;

    /// <summary>
    /// Exactly what gets signed. The account id is included so a bundle cannot be lifted from one
    /// account and presented as another's, and the epoch so an old bundle cannot be replayed as current.
    /// Fields are separated by a character that none of them can contain: ids are hex, the key is base64,
    /// and the numbers are decimal.
    /// </summary>
    public static byte[] CanonicalBytes(
        string accountId,
        int epoch,
        string epochPublicKey,
        long createdAtUnixMs,
        long expiresAtUnixMs)
        => Encoding.UTF8.GetBytes(
            $"wyu2-prekey|v{ProtocolConstants.Version}|{accountId}|{epoch}|{epochPublicKey}|{createdAtUnixMs}|{expiresAtUnixMs}");

    /// <summary>Mints and signs a bundle for an epoch key.</summary>
    public static PrekeyBundle Create(
        SigningKeyPair signer,
        string accountId,
        int epoch,
        AccountKeyPair epochKey,
        long createdAtUnixMs,
        long expiresAtUnixMs)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(epochKey);

        var publicKey = epochKey.ExportPublicKeyBase64();
        var signature = signer.Sign(
            CanonicalBytes(accountId, epoch, publicKey, createdAtUnixMs, expiresAtUnixMs));

        return new PrekeyBundle
        {
            Epoch = epoch,
            EpochPublicKey = publicKey,
            CreatedAtUnixMs = createdAtUnixMs,
            ExpiresAtUnixMs = expiresAtUnixMs,
            Signature = Convert.ToBase64String(signature),
        };
    }

    /// <summary>
    /// Whether this bundle really came from the account it claims. Everything about a bundle arrives
    /// through the relay, so nothing in it is trusted until this passes.
    /// </summary>
    public bool Verify(string accountId, string? signingPublicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(EpochPublicKey))
            return false;

        return SigningKeyPair.Verify(
            signingPublicKeyBase64,
            CanonicalBytes(accountId, Epoch, EpochPublicKey, CreatedAtUnixMs, ExpiresAtUnixMs),
            Signature);
    }

    /// <summary>Whether the sender has moved past this epoch.</summary>
    public bool IsExpired(long nowUnixMs) => nowUnixMs >= ExpiresAtUnixMs;
}
