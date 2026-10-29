using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSec.Cryptography;
using Org.Webpki.JsonCanonicalizer;

namespace Vesta;

/// <summary>
/// A member's X25519 key agreement pair, derived deterministically from the Ed25519 seed so that an
/// identity needs no second secret to store: <c>HKDF-SHA256(ikm = seed, salt = "", info = "vesta-x25519-v1")</c>.
/// Derived rather than converted because Ed25519→X25519 conversion is not available in every SDK's library.
/// </summary>
public sealed class ChannelKeyPair : IDisposable
{
    internal const string DerivationInfo = "vesta-x25519-v1";
    private static readonly KeyAgreementAlgorithm Algorithm = KeyAgreementAlgorithm.X25519;

    private readonly Key _key;

    /// <summary>The 32-byte X25519 public key, announced to channel admins in a key request.</summary>
    public byte[] PublicKey { get; }

    private ChannelKeyPair(Key key)
    {
        _key = key;
        PublicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    /// <summary>Derive the pair from a Vesta identity (its 32-byte Ed25519 seed).</summary>
    public static ChannelKeyPair FromIdentity(VestaIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        byte[] seed = identity.ExportPrivateKey();
        try
        {
            return FromSeed(seed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>Derive the pair from a raw 32-byte Ed25519 seed.</summary>
    public static ChannelKeyPair FromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32)
            throw new ArgumentException("Seed must be 32 bytes.", nameof(seed));
        byte[] scalar = HKDF.DeriveKey(HashAlgorithmName.SHA256, seed.ToArray(), 32, salt: null, info: Encoding.UTF8.GetBytes(DerivationInfo));
        try
        {
            return FromPrivateKey(scalar);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }
    }

    /// <summary>Wrap an existing raw 32-byte X25519 private key (used for ephemeral keys and test vectors).</summary>
    public static ChannelKeyPair FromPrivateKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != 32)
            throw new ArgumentException("X25519 private key must be 32 bytes.", nameof(privateKey));
        Key key = Key.Import(Algorithm, privateKey, KeyBlobFormat.RawPrivateKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return new ChannelKeyPair(key);
    }

    /// <summary>A fresh random pair (ephemeral keys for sealing).</summary>
    public static ChannelKeyPair Generate() => FromPrivateKey(RandomNumberGenerator.GetBytes(32));

    /// <summary>Export the raw 32-byte private scalar. Only needed for tests and key stores.</summary>
    public byte[] ExportPrivateKey() => _key.Export(KeyBlobFormat.RawPrivateKey);

    /// <summary>Raw X25519 shared secret with <paramref name="peerPublicKey"/>.</summary>
    internal byte[] Agree(ReadOnlySpan<byte> peerPublicKey)
    {
        NSec.Cryptography.PublicKey peer = NSec.Cryptography.PublicKey.Import(Algorithm, peerPublicKey, KeyBlobFormat.RawPublicKey);
        using SharedSecret shared = Algorithm.Agree(_key, peer, new SharedSecretCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport })
            ?? throw new CryptographicException("X25519 agreement failed (low-order peer key).");
        return shared.Export(SharedSecretBlobFormat.RawSharedSecret);
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>A channel's symmetric key for one epoch: 32 random bytes plus the integer epoch carried as <c>kid</c>.</summary>
public sealed record ChannelKey(int Epoch, byte[] Key)
{
    public const int KeyBytes = 32;

    /// <summary>A fresh random key for <paramref name="epoch"/>.</summary>
    public static ChannelKey Generate(int epoch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);
        return new ChannelKey(epoch, RandomNumberGenerator.GetBytes(KeyBytes));
    }
}

/// <summary>
/// A channel key sealed to one recipient's X25519 public key, as carried inside a <c>vesta.channel-key</c>
/// event: ephemeral public key, nonce and AES-256-GCM ciphertext of the 32-byte key.
/// </summary>
public sealed record SealedChannelKey(int Epoch, string Epk, string N, string Ct)
{
    public JsonObject ToJson() => new()
    {
        ["epoch"] = Epoch,
        ["epk"] = Epk,
        ["n"] = N,
        ["ct"] = Ct,
    };

    public static SealedChannelKey FromJson(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
            throw new FormatException("Sealed channel key must be a JSON object.");
        return new SealedChannelKey(
            json.GetProperty("epoch").GetInt32(),
            json.GetProperty("epk").GetString() ?? throw new FormatException("Missing epk."),
            json.GetProperty("n").GetString() ?? throw new FormatException("Missing n."),
            json.GetProperty("ct").GetString() ?? throw new FormatException("Missing ct."));
    }
}

/// <summary>
/// The encryption primitives for end-to-end encrypted channels (PLANNING #33, phase B). Byte-compatible
/// with <c>channel-crypto.ts</c> and <c>channel_crypto.py</c>; the shared vectors live in the three test suites.
/// <list type="bullet">
/// <item><b>Payload:</b> AES-256-GCM over the RFC 8785 canonical JSON of the app payload, 96-bit nonce,
/// AAD <c>"vesta-enc-v1\n{channelId}\n{eventId}\n{eventType}"</c>, carried as the <see cref="EncryptedPayload"/> envelope.</item>
/// <item><b>Key sealing:</b> ephemeral X25519 × recipient X25519 → HKDF-SHA256(salt = epk ‖ recipientPub,
/// info = "vesta-channel-key-seal-v1") → AES-256-GCM over the 32-byte channel key, AAD
/// <c>"vesta-channel-key-v1\n{channelId}\n{epoch}\n{recipientClientId}"</c>.</item>
/// </list>
/// Signing is unchanged: the Ed25519 signature covers the envelope, so authorship stays verifiable.
/// </summary>
public static class ChannelCrypto
{
    private const int TagBytes = 16;
    private const string PayloadAadPrefix = "vesta-enc-v1";
    private const string SealAadPrefix = "vesta-channel-key-v1";
    private const string SealInfo = "vesta-channel-key-seal-v1";

    /// <summary>Encrypt an app payload into an <see cref="EncryptedPayload"/> envelope.</summary>
    public static JsonElement EncryptPayload(ChannelKey key, string channelId, Guid eventId, string eventType, JsonElement payload) =>
        EncryptPayload(key, channelId, eventId, eventType, payload, RandomNumberGenerator.GetBytes(EncryptedPayload.NonceBytes));

    /// <summary>Deterministic variant for tests and vectors; production callers use the random-nonce overload.</summary>
    public static JsonElement EncryptPayload(ChannelKey key, string channelId, Guid eventId, string eventType, JsonElement payload, ReadOnlySpan<byte> nonce)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Key.Length != ChannelKey.KeyBytes)
            throw new ArgumentException("Channel key must be 32 bytes.", nameof(key));
        if (nonce.Length != EncryptedPayload.NonceBytes)
            throw new ArgumentException($"Nonce must be {EncryptedPayload.NonceBytes} bytes.", nameof(nonce));

        byte[] plaintext = Encoding.UTF8.GetBytes(Canonicalize(payload));
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagBytes];
        using (AesGcm aes = new(key.Key, TagBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, PayloadAad(channelId, eventId, eventType));
        }

        JsonObject envelope = new()
        {
            ["enc"] = EncryptedPayload.Version,
            ["kid"] = key.Epoch,
            ["n"] = Base64Url.Encode(nonce),
            ["ct"] = Base64Url.Encode(Concat(ciphertext, tag)),
        };
        return JsonSerializer.SerializeToElement(envelope);
    }

    /// <summary>Read the epoch (<c>kid</c>) an envelope was encrypted under, so the caller can pick the key.</summary>
    public static int ReadEpoch(JsonElement envelope)
    {
        if (!EncryptedPayload.IsEnvelope(envelope))
            throw new FormatException("Payload is not an encrypted envelope.");
        return envelope.GetProperty("kid").GetInt32();
    }

    /// <summary>
    /// Decrypt an envelope back to the app payload. Throws <see cref="CryptographicException"/> when the key,
    /// nonce or AAD do not match (tampering, wrong epoch, event re-homed to another channel/id/type).
    /// </summary>
    public static JsonElement DecryptPayload(ChannelKey key, string channelId, Guid eventId, string eventType, JsonElement envelope)
    {
        ArgumentNullException.ThrowIfNull(key);
        int epoch = ReadEpoch(envelope);
        if (epoch != key.Epoch)
            throw new CryptographicException($"Envelope uses key epoch {epoch}, but key epoch {key.Epoch} was supplied.");

        byte[] nonce = Base64Url.Decode(envelope.GetProperty("n").GetString()!);
        byte[] sealedBytes = Base64Url.Decode(envelope.GetProperty("ct").GetString()!);
        if (sealedBytes.Length < TagBytes)
            throw new CryptographicException("Ciphertext shorter than the authentication tag.");

        byte[] plaintext = new byte[sealedBytes.Length - TagBytes];
        using (AesGcm aes = new(key.Key, TagBytes))
        {
            aes.Decrypt(nonce, sealedBytes.AsSpan(0, plaintext.Length), sealedBytes.AsSpan(plaintext.Length, TagBytes), plaintext, PayloadAad(channelId, eventId, eventType));
        }
        using JsonDocument document = JsonDocument.Parse(plaintext);
        return document.RootElement.Clone();
    }

    /// <summary>Seal a channel key to a recipient so only the holder of the matching X25519 private key can open it.</summary>
    public static SealedChannelKey SealKey(ChannelKey key, string channelId, string recipientClientId, ReadOnlySpan<byte> recipientPublicKey)
    {
        using ChannelKeyPair ephemeral = ChannelKeyPair.Generate();
        return SealKey(key, channelId, recipientClientId, recipientPublicKey, ephemeral, RandomNumberGenerator.GetBytes(EncryptedPayload.NonceBytes));
    }

    /// <summary>Deterministic variant for tests and vectors.</summary>
    public static SealedChannelKey SealKey(ChannelKey key, string channelId, string recipientClientId, ReadOnlySpan<byte> recipientPublicKey, ChannelKeyPair ephemeral, ReadOnlySpan<byte> nonce)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(ephemeral);
        if (key.Key.Length != ChannelKey.KeyBytes)
            throw new ArgumentException("Channel key must be 32 bytes.", nameof(key));
        if (recipientPublicKey.Length != 32)
            throw new ArgumentException("Recipient X25519 public key must be 32 bytes.", nameof(recipientPublicKey));
        if (nonce.Length != EncryptedPayload.NonceBytes)
            throw new ArgumentException($"Nonce must be {EncryptedPayload.NonceBytes} bytes.", nameof(nonce));

        byte[] wrapKey = WrapKey(ephemeral.Agree(recipientPublicKey), ephemeral.PublicKey, recipientPublicKey);
        byte[] ciphertext = new byte[ChannelKey.KeyBytes];
        byte[] tag = new byte[TagBytes];
        try
        {
            using AesGcm aes = new(wrapKey, TagBytes);
            aes.Encrypt(nonce, key.Key, ciphertext, tag, SealAad(channelId, key.Epoch, recipientClientId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
        }

        return new SealedChannelKey(key.Epoch, Base64Url.Encode(ephemeral.PublicKey), Base64Url.Encode(nonce), Base64Url.Encode(Concat(ciphertext, tag)));
    }

    /// <summary>Open a sealed key with the recipient's pair. Throws <see cref="CryptographicException"/> if it was not sealed to this recipient.</summary>
    public static ChannelKey OpenKey(SealedChannelKey sealedKey, string channelId, string recipientClientId, ChannelKeyPair recipient)
    {
        ArgumentNullException.ThrowIfNull(sealedKey);
        ArgumentNullException.ThrowIfNull(recipient);

        byte[] epk = Base64Url.Decode(sealedKey.Epk);
        byte[] nonce = Base64Url.Decode(sealedKey.N);
        byte[] sealedBytes = Base64Url.Decode(sealedKey.Ct);
        if (epk.Length != 32 || nonce.Length != EncryptedPayload.NonceBytes || sealedBytes.Length != ChannelKey.KeyBytes + TagBytes)
            throw new CryptographicException("Malformed sealed channel key.");

        byte[] wrapKey = WrapKey(recipient.Agree(epk), epk, recipient.PublicKey);
        byte[] channelKey = new byte[ChannelKey.KeyBytes];
        try
        {
            using AesGcm aes = new(wrapKey, TagBytes);
            aes.Decrypt(nonce, sealedBytes.AsSpan(0, ChannelKey.KeyBytes), sealedBytes.AsSpan(ChannelKey.KeyBytes, TagBytes), channelKey, SealAad(channelId, sealedKey.Epoch, recipientClientId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
        }
        return new ChannelKey(sealedKey.Epoch, channelKey);
    }

    internal static byte[] PayloadAad(string channelId, Guid eventId, string eventType) =>
        Encoding.UTF8.GetBytes($"{PayloadAadPrefix}\n{channelId}\n{eventId:D}\n{eventType}");

    // JsonCanonicalizer only accepts an object or array at the top level; a scalar payload is already canonical as written.
    private static string Canonicalize(JsonElement payload) => payload.ValueKind is JsonValueKind.Object or JsonValueKind.Array
        ? new JsonCanonicalizer(payload.GetRawText()).GetEncodedString()
        : payload.GetRawText();

    internal static byte[] SealAad(string channelId, int epoch, string recipientClientId) =>
        Encoding.UTF8.GetBytes($"{SealAadPrefix}\n{channelId}\n{epoch}\n{recipientClientId}");

    private static byte[] WrapKey(byte[] sharedSecret, ReadOnlySpan<byte> ephemeralPublicKey, ReadOnlySpan<byte> recipientPublicKey)
    {
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, Concat(ephemeralPublicKey, recipientPublicKey), Encoding.UTF8.GetBytes(SealInfo));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        byte[] result = new byte[a.Length + b.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        return result;
    }
}
