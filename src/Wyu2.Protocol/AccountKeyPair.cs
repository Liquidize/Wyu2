using System.Security.Cryptography;

namespace Wyu2.Protocol;

/// <summary>
/// The ECDH P-256 keypair that identifies an account for end to end encryption.
/// The private half never leaves the machine that generated it.
/// </summary>
public sealed class AccountKeyPair : IDisposable
{
    private AccountKeyPair(ECDiffieHellman key)
    {
        Key = key;
    }

    internal ECDiffieHellman Key { get; }

    /// <summary>Generates a brand new keypair.</summary>
    public static AccountKeyPair Create()
        => new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Restores a keypair previously produced by <see cref="ExportPrivateKeyBase64"/>.</summary>
    public static AccountKeyPair Import(string pkcs8Base64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pkcs8Base64);

        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8Base64), out _);
        }
        catch
        {
            key.Dispose();
            throw;
        }

        return new AccountKeyPair(key);
    }

    /// <summary>PKCS#8 private key, base64. Treat this like a password.</summary>
    public string ExportPrivateKeyBase64()
        => Convert.ToBase64String(Key.ExportPkcs8PrivateKey());

    /// <summary>SubjectPublicKeyInfo public key, base64. Safe to publish.</summary>
    public string ExportPublicKeyBase64()
        => Convert.ToBase64String(Key.PublicKey.ExportSubjectPublicKeyInfo());

    public void Dispose() => Key.Dispose();
}
