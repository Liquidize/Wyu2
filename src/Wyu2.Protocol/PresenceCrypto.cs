using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wyu2.Protocol;

/// <summary>
/// Sealed box for anything sent between two accounts: ECDH P-256 to agree a per-pair secret,
/// HKDF-SHA256 to derive a per-direction key, AES-256-GCM to encrypt. The relay only ever sees nonce
/// plus ciphertext, so it cannot read where anybody is or what they marked.
///
/// The <c>purpose</c> argument gives each kind of message its own derived key, so a presence blob and a
/// beacon cannot be substituted for one another even between the same pair.
/// </summary>
public static class PresenceCrypto
{
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const int KeyBytes = 32;

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Derives the symmetric key used for messages flowing from <paramref name="senderAccountId"/> to
    /// <paramref name="recipientAccountId"/>. Both sides compute the same value; swapping the ids gives a
    /// different key, so a blob cannot be replayed back at its author.
    /// </summary>
    public static byte[] DeriveKey(
        AccountKeyPair self,
        string otherPublicKeyBase64,
        string senderAccountId,
        string recipientAccountId,
        string purpose = ProtocolConstants.KeyDerivationInfo)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentException.ThrowIfNullOrWhiteSpace(otherPublicKeyBase64);

        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(Convert.FromBase64String(otherPublicKeyBase64), out _);

        var ikm = self.Key.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256);
        var info = Encoding.UTF8.GetBytes($"{purpose}|{senderAccountId}|{recipientAccountId}");

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeyBytes, salt: null, info);
    }

    /// <summary>Encrypts a payload for one recipient.</summary>
    public static PresenceEnvelope Seal<T>(
        byte[] key,
        T payload,
        string senderAccountId,
        string recipientAccountId)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(payload);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, PayloadJson);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        using (var aes = new AesGcm(key, TagBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(senderAccountId, recipientAccountId));
        }

        var combined = new byte[ciphertext.Length + TagBytes];
        ciphertext.CopyTo(combined, 0);
        tag.CopyTo(combined, ciphertext.Length);

        return new PresenceEnvelope
        {
            RecipientAccountId = recipientAccountId,
            Nonce = Convert.ToBase64String(nonce),
            Ciphertext = Convert.ToBase64String(combined),
        };
    }

    /// <summary>
    /// Decrypts a blob received from <paramref name="senderAccountId"/>. Returns null when the blob was
    /// tampered with, was addressed to somebody else, or was produced with a different key.
    /// </summary>
    public static T? Open<T>(
        byte[] key,
        string nonceBase64,
        string ciphertextBase64,
        string senderAccountId,
        string recipientAccountId)
        where T : class
    {
        try
        {
            var nonce = Convert.FromBase64String(nonceBase64);
            var combined = Convert.FromBase64String(ciphertextBase64);
            if (nonce.Length != NonceBytes || combined.Length < TagBytes)
                return null;

            var ciphertext = combined.AsSpan(0, combined.Length - TagBytes);
            var tag = combined.AsSpan(combined.Length - TagBytes, TagBytes);
            var plaintext = new byte[ciphertext.Length];

            using (var aes = new AesGcm(key, TagBytes))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(senderAccountId, recipientAccountId));
            }

            return JsonSerializer.Deserialize<T>(plaintext, PayloadJson);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[] AssociatedData(string senderAccountId, string recipientAccountId)
        => Encoding.UTF8.GetBytes($"{ProtocolConstants.Version}|{senderAccountId}|{recipientAccountId}");
}
