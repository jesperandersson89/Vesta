using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSec.Cryptography;

namespace Vesta;

/// <summary>Thrown when an identity backup cannot be opened: wrong password, corrupted or unsupported blob.</summary>
public sealed class IdentityBackupException(string message) : Exception(message);

/// <summary>Argon2id cost for an identity backup. The defaults take about a second on a laptop.</summary>
/// <param name="MemoryKiB">Memory in KiB (default 64 MiB).</param>
/// <param name="Iterations">Passes over memory (default 3). Parallelism is fixed at 1.</param>
public sealed record IdentityBackupOptions(int MemoryKiB = 65536, int Iterations = 3)
{
    public static IdentityBackupOptions Default { get; } = new();
}

/// <summary>
/// Password-encrypted export of an identity's private seed: Argon2id derives 64 bytes from the
/// password, the first 32 are an AES-256-GCM key (the rest locate relay-stored backups, see
/// <see cref="RelayBackup"/>). The blob is versioned JSON, safe to store anywhere; whoever holds it can
/// attempt offline password guesses, so the password strength is the security.
/// </summary>
public static class IdentityBackup
{
    public const string Format = "vesta-identity-backup";
    public const int Version = 1;

    internal const int SaltLength = 16;
    internal const int NonceLength = 12;
    internal const int TagLength = 16;
    internal const int MaterialLength = 64;
    internal const int SeedLength = 32;

    // Blobs are untrusted input: refuse parameters that would make opening one a denial of service.
    private const int MaxMemoryKiB = 524_288;
    private const int MaxIterations = 10;
    private const int MinMemoryKiB = 8;

    /// <summary>Encrypts <paramref name="identity"/>'s seed under <paramref name="password"/> with a random salt.</summary>
    public static string Export(VestaIdentity identity, string password, IdentityBackupOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        IdentityBackupOptions opts = Validate(options ?? IdentityBackupOptions.Default);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] material = DeriveMaterial(password, salt, opts);
        return Seal(identity, material, salt, opts).ToJsonString();
    }

    /// <summary>Opens a blob made by <see cref="Export"/> and returns the identity.</summary>
    public static VestaIdentity Import(string blob, string password)
    {
        ArgumentNullException.ThrowIfNull(blob);
        ParsedBlob parsed = Parse(blob);
        byte[] material = DeriveMaterial(password, parsed.Salt, new IdentityBackupOptions(parsed.MemoryKiB, parsed.Iterations));
        return Open(parsed, material);
    }

    internal static byte[] DeriveMaterial(string password, byte[] salt, IdentityBackupOptions options)
    {
        PasswordBasedKeyDerivationAlgorithm argon2 = PasswordBasedKeyDerivationAlgorithm.Argon2id(new Argon2Parameters
        {
            DegreeOfParallelism = 1,
            // NSec's MemorySize is in KiB (verified against RFC 9106 implementations), not bytes.
            MemorySize = options.MemoryKiB,
            NumberOfPasses = options.Iterations,
        });
        return argon2.DeriveBytes(Encoding.UTF8.GetBytes(NormalizePassword(password)), salt, MaterialLength);
    }

    // NFKC so the same password typed on different platforms and keyboards derives the same key.
    internal static string NormalizePassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return password.Normalize(NormalizationForm.FormKC);
    }

    internal static IdentityBackupOptions Validate(IdentityBackupOptions options)
    {
        if (options.MemoryKiB is < MinMemoryKiB or > MaxMemoryKiB || options.Iterations is < 1 or > MaxIterations)
            throw new IdentityBackupException(
                $"Unsupported Argon2id cost (memory {options.MemoryKiB} KiB, {options.Iterations} passes).");
        return options;
    }

    internal static JsonObject Seal(VestaIdentity identity, byte[] material, byte[] salt, IdentityBackupOptions options)
    {
        byte[] seed = identity.ExportPrivateKey();
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[] sealedBytes = new byte[seed.Length + TagLength];
        using AesGcm aes = new(material.AsSpan(0, 32), TagLength);
        aes.Encrypt(nonce, seed, sealedBytes.AsSpan(0, seed.Length), sealedBytes.AsSpan(seed.Length, TagLength), Aad(identity.ClientId));

        return new JsonObject
        {
            ["format"] = Format,
            ["version"] = Version,
            ["clientId"] = identity.ClientId,
            ["kdf"] = new JsonObject
            {
                ["name"] = "argon2id",
                ["memoryKiB"] = options.MemoryKiB,
                ["iterations"] = options.Iterations,
                ["parallelism"] = 1,
                ["salt"] = Base64Url.Encode(salt),
            },
            ["cipher"] = new JsonObject { ["name"] = "aes-256-gcm", ["nonce"] = Base64Url.Encode(nonce) },
            ["ciphertext"] = Base64Url.Encode(sealedBytes),
        };
    }

    internal static VestaIdentity Open(ParsedBlob blob, byte[] material)
    {
        byte[] seed = new byte[SeedLength];
        try
        {
            using AesGcm aes = new(material.AsSpan(0, 32), TagLength);
            aes.Decrypt(blob.Nonce, blob.Ciphertext.AsSpan(0, SeedLength), blob.Ciphertext.AsSpan(SeedLength, TagLength), seed, Aad(blob.ClientId));
        }
        catch (CryptographicException)
        {
            throw new IdentityBackupException("Wrong password, or the backup is corrupted.");
        }

        VestaIdentity identity = VestaIdentity.FromPrivateKey(seed);
        if (identity.ClientId != blob.ClientId)
        {
            identity.Dispose();
            throw new IdentityBackupException("The backup does not match its client id.");
        }

        return identity;
    }

    internal static ParsedBlob Parse(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return Parse(doc.RootElement);
        }
        catch (JsonException)
        {
            throw new IdentityBackupException("The backup is not valid JSON.");
        }
    }

    internal static ParsedBlob Parse(JsonElement root)
    {
        try
        {
            if (root.GetProperty("format").GetString() != Format)
                throw new IdentityBackupException("Not a Vesta identity backup.");
            if (root.GetProperty("version").GetInt32() != Version)
                throw new IdentityBackupException($"Unsupported backup version {root.GetProperty("version")}.");

            JsonElement kdf = root.GetProperty("kdf");
            JsonElement cipher = root.GetProperty("cipher");
            if (kdf.GetProperty("name").GetString() != "argon2id" || kdf.GetProperty("parallelism").GetInt32() != 1 ||
                cipher.GetProperty("name").GetString() != "aes-256-gcm")
                throw new IdentityBackupException("Unsupported backup algorithms.");

            IdentityBackupOptions cost = Validate(new IdentityBackupOptions(
                kdf.GetProperty("memoryKiB").GetInt32(), kdf.GetProperty("iterations").GetInt32()));
            byte[] salt = Base64Url.Decode(kdf.GetProperty("salt").GetString()!);
            byte[] nonce = Base64Url.Decode(cipher.GetProperty("nonce").GetString()!);
            byte[] ciphertext = Base64Url.Decode(root.GetProperty("ciphertext").GetString()!);
            if (salt.Length != SaltLength || nonce.Length != NonceLength || ciphertext.Length != SeedLength + TagLength)
                throw new IdentityBackupException("The backup is malformed.");

            return new ParsedBlob(root.GetProperty("clientId").GetString()!, cost.MemoryKiB, cost.Iterations, salt, nonce, ciphertext);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or NullReferenceException)
        {
            throw new IdentityBackupException("The backup is malformed.");
        }
    }

    private static byte[] Aad(string clientId) => Encoding.UTF8.GetBytes($"vesta-identity-backup-v1\n{clientId}");

    internal sealed record ParsedBlob(string ClientId, int MemoryKiB, int Iterations, byte[] Salt, byte[] Nonce, byte[] Ciphertext);
}

/// <summary>
/// A backup an app can park on a relay so a player who lost every device can recover with just
/// (username, password). Everything is derived from the app id, username and password with one
/// Argon2id run: the encryption key and the unguessable channel the blob lives on
/// (<c>vesta/identity-backup/{hex}</c>), so the relay learns neither who the player is nor can enumerate
/// users. The blob is published as a <c>replace</c> event signed by the identity; because anyone can
/// publish junk to the channel, <see cref="Restore"/> trusts only blobs that decrypt under the derived key.
/// </summary>
public sealed class RelayBackup
{
    public const string EventType = "vesta.identity-backup";

    private readonly byte[] _material;
    private readonly byte[] _salt;
    private readonly IdentityBackupOptions _options;

    /// <summary>The channel the backup is published to and fetched from.</summary>
    public string ChannelId { get; }

    private RelayBackup(byte[] material, byte[] salt, IdentityBackupOptions options)
    {
        _material = material;
        _salt = salt;
        _options = options;
        ChannelId = $"vesta/identity-backup/{Convert.ToHexString(material.AsSpan(32, 16)).ToLowerInvariant()}";
    }

    /// <summary>
    /// Derives the keys and channel for a (app, username, password). The username is trimmed, NFKC-normalised
    /// and lower-cased; use the same spelling rules everywhere.
    /// </summary>
    public static RelayBackup Derive(string appId, string username, string password, IdentityBackupOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        ArgumentException.ThrowIfNullOrEmpty(username);
        IdentityBackupOptions opts = IdentityBackup.Validate(options ?? IdentityBackupOptions.Default);
        string normalized = username.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        byte[] salt = SHA256.HashData(Encoding.UTF8.GetBytes($"vesta-backup-v1\n{appId}\n{normalized}"))[..IdentityBackup.SaltLength];
        return new RelayBackup(IdentityBackup.DeriveMaterial(password, salt, opts), salt, opts);
    }

    /// <summary>A signed, replace-style event carrying the encrypted identity; publish it on <see cref="ChannelId"/>.</summary>
    public VestaEvent CreateEvent(VestaIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        JsonElement payload = JsonDocument.Parse(IdentityBackup.Seal(identity, _material, _salt, _options).ToJsonString()).RootElement.Clone();
        VestaEvent evt = new(
            Id: Guid.NewGuid(),
            ChannelId: ChannelId,
            Timestamp: DateTimeOffset.UtcNow,
            ClientId: identity.ClientId,
            EventType: EventType,
            Payload: payload,
            Replace: true);
        return EventSigner.SignEvent(evt, identity);
    }

    /// <summary>
    /// The identity from the first event on <see cref="ChannelId"/> that decrypts under the derived key, or
    /// <c>null</c>. Junk, foreign blobs and blobs with other KDF settings are skipped without further KDF work.
    /// </summary>
    public VestaIdentity? Restore(IEnumerable<VestaEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (VestaEvent evt in events)
        {
            if (evt.EventType != EventType || evt.ChannelId != ChannelId)
                continue;

            try
            {
                IdentityBackup.ParsedBlob blob = IdentityBackup.Parse(evt.Payload);
                if (!blob.Salt.AsSpan().SequenceEqual(_salt) || blob.MemoryKiB != _options.MemoryKiB || blob.Iterations != _options.Iterations)
                    continue;
                return IdentityBackup.Open(blob, _material);
            }
            catch (IdentityBackupException)
            {
            }
        }

        return null;
    }
}
