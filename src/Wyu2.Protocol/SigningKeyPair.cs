using System.Security.Cryptography;

namespace Wyu2.Protocol;

/// <summary>
/// The long-term identity key an account signs with. Separate from the ECDH key it agrees secrets with:
/// reusing one keypair for both signing and key agreement is a well-known way to get subtle cross
/// protocol attacks, and there is no reason to accept that risk when we control both ends.
///
/// Its job is to vouch for the rotating epoch keys, so a relay cannot substitute an epoch key of its own
/// and read everything addressed to somebody.
/// </summary>
public sealed class SigningKeyPair : IDisposable
{
    private SigningKeyPair(ECDsa key) => Key = key;

    private ECDsa Key { get; }

    public static SigningKeyPair Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Restores a keypair previously produced by <see cref="ExportPrivateKeyBase64"/>.</summary>
    public static SigningKeyPair Import(string pkcs8Base64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pkcs8Base64);

        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8Base64), out _);
        }
        catch
        {
            key.Dispose();
            throw;
        }

        return new SigningKeyPair(key);
    }

    /// <summary>PKCS#8 private key, base64. Treat this like a password.</summary>
    public string ExportPrivateKeyBase64() => Convert.ToBase64String(Key.ExportPkcs8PrivateKey());

    /// <summary>SubjectPublicKeyInfo public key, base64. Safe to publish.</summary>
    public string ExportPublicKeyBase64() => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());

    public byte[] Sign(ReadOnlySpan<byte> data) => Key.SignData(data, HashAlgorithmName.SHA256);

    /// <summary>
    /// Checks a signature against a published public key. Returns false rather than throwing for
    /// malformed input, because everything here arrives over the network and none of it is trusted.
    /// </summary>
    public static bool Verify(string? publicKeyBase64, ReadOnlySpan<byte> data, string? signatureBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64) || string.IsNullOrWhiteSpace(signatureBase64))
            return false;

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyData(data, Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public void Dispose() => Key.Dispose();
}
