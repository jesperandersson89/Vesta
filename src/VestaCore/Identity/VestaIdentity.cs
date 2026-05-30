using System.Security.Cryptography;
using NSec.Cryptography;

namespace VestaCore.Identity;

/// <summary>
/// Represents a Vesta client identity — an Ed25519 keypair.
/// The public key is the client's verifiable identity; the private key signs events.
/// </summary>
public sealed class VestaIdentity : IDisposable
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    private readonly Key _key;

    /// <summary>
    /// The Ed25519 public key bytes (32 bytes).
    /// </summary>
    public byte[] PublicKey { get; }

    /// <summary>
    /// The client ID derived from the public key: base64url(sha256(publicKey))[:22]
    /// Provides ~128 bits of identity, URL-safe.
    /// </summary>
    public string ClientId { get; }

    private VestaIdentity(Key key)
    {
        _key = key;
        PublicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        ClientId = DeriveClientId(PublicKey);
    }

    /// <summary>
    /// Generate a new random identity (keypair).
    /// </summary>
    public static VestaIdentity Generate()
    {
        Key key = Key.Create(Algorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return new VestaIdentity(key);
    }

    /// <summary>
    /// Load an identity from an existing private key (32-byte Ed25519 seed).
    /// </summary>
    public static VestaIdentity FromPrivateKey(byte[] privateKey)
    {
        Key key = Key.Import(Algorithm, privateKey, KeyBlobFormat.RawPrivateKey, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return new VestaIdentity(key);
    }

    /// <summary>
    /// Export the private key (32-byte Ed25519 seed) for storage.
    /// </summary>
    public byte[] ExportPrivateKey()
    {
        return _key.Export(KeyBlobFormat.RawPrivateKey);
    }

    /// <summary>
    /// Sign arbitrary data with this identity's private key.
    /// Returns the raw Ed25519 signature (64 bytes).
    /// </summary>
    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        return Algorithm.Sign(_key, data);
    }

    /// <summary>
    /// Verify a signature against this identity's public key.
    /// </summary>
    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        NSec.Cryptography.PublicKey pubKey = _key.PublicKey;
        return Algorithm.Verify(pubKey, data, signature);
    }

    /// <summary>
    /// Verify a signature using a standalone public key (no private key needed).
    /// </summary>
    public static bool VerifyWithPublicKey(byte[] publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        NSec.Cryptography.PublicKey pubKey = NSec.Cryptography.PublicKey.Import(Algorithm, publicKey, KeyBlobFormat.RawPublicKey);
        return Algorithm.Verify(pubKey, data, signature);
    }

    /// <summary>
    /// Derive a client ID from a public key: base64url(sha256(publicKey))[:22]
    /// </summary>
    public static string DeriveClientId(byte[] publicKey)
    {
        byte[] hash = SHA256.HashData(publicKey);
        string base64Url = Convert.ToBase64String(hash)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return base64Url[..22];
    }

    public void Dispose()
    {
        _key.Dispose();
    }
}
