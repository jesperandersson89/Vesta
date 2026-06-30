using System.Text;
using System.Text.Json;
using Org.Webpki.JsonCanonicalizer;
using VestaCore.Identity;
using VestaCore.Utilities;

namespace VestaCore.Relay;

/// <summary>
/// Signs and verifies <see cref="ServerDescriptor"/> records using Ed25519 and RFC 8785 (JCS)
/// canonical JSON — the same scheme as <see cref="ManifestSigner"/> and <see cref="EventSigner"/>,
/// so the signing input is reproducible across implementations.
///
/// Unlike a manifest (verified against an external trust anchor), a descriptor is <b>self-signed</b>:
/// it declares its own <see cref="ServerDescriptor.RelayPublicKey"/> and is signed by the matching
/// private key. <see cref="Verify"/> only proves the descriptor was authored by that key and not
/// altered in transit — it does not establish that the relay is trustworthy.
/// </summary>
public static class DescriptorSigner
{
    /// <summary>
    /// Sign a descriptor with the relay identity, returning a new descriptor with
    /// <see cref="ServerDescriptor.RelayPublicKey"/> set to the signer and
    /// <see cref="ServerDescriptor.Signature"/> populated. If the descriptor already declares a
    /// <see cref="ServerDescriptor.RelayPublicKey"/>, it must match the signing identity.
    /// </summary>
    public static ServerDescriptor Sign(ServerDescriptor descriptor, VestaIdentity identity)
    {
        string relayPublicKey = Base64Url.Encode(identity.PublicKey);

        if (!string.IsNullOrEmpty(descriptor.RelayPublicKey) && descriptor.RelayPublicKey != relayPublicKey)
        {
            throw new ArgumentException(
                $"Descriptor relayPublicKey '{descriptor.RelayPublicKey}' does not match signing identity '{relayPublicKey}'.",
                nameof(descriptor));
        }

        ServerDescriptor withKey = descriptor with { RelayPublicKey = relayPublicKey, Signature = null };
        byte[] signingInput = BuildSigningInput(withKey);
        byte[] signatureBytes = identity.Sign(signingInput);

        return withKey with { Signature = Base64Url.Encode(signatureBytes) };
    }

    /// <summary>
    /// Verify that a descriptor's <see cref="ServerDescriptor.Signature"/> is a valid Ed25519
    /// signature over its canonical form, authored by the key it declares in
    /// <see cref="ServerDescriptor.RelayPublicKey"/>. Returns false on any missing/malformed field.
    /// </summary>
    public static bool Verify(ServerDescriptor descriptor)
    {
        if (string.IsNullOrEmpty(descriptor.Signature) || string.IsNullOrEmpty(descriptor.RelayPublicKey))
        {
            return false;
        }

        byte[] relayPublicKey;
        byte[] signatureBytes;
        try
        {
            relayPublicKey = Base64Url.Decode(descriptor.RelayPublicKey);
            signatureBytes = Base64Url.Decode(descriptor.Signature);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] signingInput = BuildSigningInput(descriptor);

        try
        {
            return VestaIdentity.VerifyWithPublicKey(relayPublicKey, signingInput, signatureBytes);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Build the canonical signing input bytes for a descriptor per RFC 8785 (JCS).
    /// Includes every field except <see cref="ServerDescriptor.Signature"/>. JCS sorts object keys;
    /// array order is preserved as authored.
    /// </summary>
    internal static byte[] BuildSigningInput(ServerDescriptor descriptor)
    {
        Dictionary<string, object?> signingFields = new()
        {
            ["apps"] = descriptor.Apps
                .Select(a => new Dictionary<string, object?>
                {
                    ["appId"] = a.AppId,
                    ["ownerClientId"] = a.OwnerClientId
                })
                .ToList(),
            ["issuedAt"] = FormatTimestamp(descriptor.IssuedAt),
            ["relayPublicKey"] = descriptor.RelayPublicKey,
            ["ttlSeconds"] = descriptor.TtlSeconds,
            ["urls"] = descriptor.Urls.ToList()
        };

        string json = JsonSerializer.Serialize(signingFields);
        JsonCanonicalizer canonicalizer = new(json);
        string canonicalJson = canonicalizer.GetEncodedString();
        return Encoding.UTF8.GetBytes(canonicalJson);
    }

    private static string FormatTimestamp(DateTimeOffset timestamp)
    {
        DateTimeOffset utc = timestamp.ToUniversalTime();
        return utc.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }
}
