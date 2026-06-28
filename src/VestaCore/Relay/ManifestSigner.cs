using System.Text;
using System.Text.Json;
using Org.Webpki.JsonCanonicalizer;
using VestaCore.Identity;
using VestaCore.Utilities;

namespace VestaCore.Relay;

/// <summary>
/// Signs and verifies <see cref="RelayManifest"/> records using Ed25519 and RFC 8785 (JCS)
/// canonical JSON — the same scheme as <see cref="EventSigner"/>, so the signing input is
/// reproducible across the C#, TypeScript and Python clients.
/// </summary>
public static class ManifestSigner
{
    /// <summary>
    /// Sign a manifest with the owner identity, returning a new manifest with
    /// <see cref="RelayManifest.OwnerPublicKey"/> set to the signer and <see cref="RelayManifest.Signature"/>
    /// populated. If the manifest already declares an <see cref="RelayManifest.OwnerPublicKey"/>, it must
    /// match the signing identity.
    /// </summary>
    public static RelayManifest Sign(RelayManifest manifest, VestaIdentity identity)
    {
        string ownerPublicKey = Base64Url.Encode(identity.PublicKey);

        if (!string.IsNullOrEmpty(manifest.OwnerPublicKey) && manifest.OwnerPublicKey != ownerPublicKey)
        {
            throw new ArgumentException(
                $"Manifest ownerPublicKey '{manifest.OwnerPublicKey}' does not match signing identity '{ownerPublicKey}'.",
                nameof(manifest));
        }

        RelayManifest withOwner = manifest with { OwnerPublicKey = ownerPublicKey, Signature = null };
        byte[] signingInput = BuildSigningInput(withOwner);
        byte[] signatureBytes = identity.Sign(signingInput);

        return withOwner with { Signature = Base64Url.Encode(signatureBytes) };
    }

    /// <summary>
    /// Verify a manifest against the expected owner public key (the app's compiled-in trust anchor).
    /// Returns true only if the manifest's declared owner matches the expected key AND the signature
    /// verifies against it.
    /// </summary>
    public static bool Verify(RelayManifest manifest, byte[] expectedOwnerPublicKey)
    {
        if (string.IsNullOrEmpty(manifest.Signature))
        {
            return false;
        }

        // The declared owner must match the trust anchor — never accept a manifest that
        // re-points the trust anchor at a different key.
        string expectedOwner = Base64Url.Encode(expectedOwnerPublicKey);
        if (manifest.OwnerPublicKey != expectedOwner)
        {
            return false;
        }

        byte[] signingInput = BuildSigningInput(manifest);
        byte[] signatureBytes = Base64Url.Decode(manifest.Signature);

        return VestaIdentity.VerifyWithPublicKey(expectedOwnerPublicKey, signingInput, signatureBytes);
    }

    /// <summary>
    /// Build the canonical signing input bytes for a manifest per RFC 8785 (JCS).
    /// Includes every field except <see cref="RelayManifest.Signature"/>. JCS sorts object keys;
    /// array order is preserved as authored.
    /// </summary>
    internal static byte[] BuildSigningInput(RelayManifest manifest)
    {
        Dictionary<string, object?> signingFields = new()
        {
            ["appId"] = manifest.AppId,
            ["escapeFallbacks"] = manifest.EscapeFallbacks
                .Select(f => new Dictionary<string, object?>
                {
                    ["url"] = f.Url,
                    ["validFrom"] = FormatTimestamp(f.ValidFrom)
                })
                .ToList(),
            ["issuedAt"] = FormatTimestamp(manifest.IssuedAt),
            ["ownerPublicKey"] = manifest.OwnerPublicKey,
            ["relays"] = manifest.Relays
                .Select(r => new Dictionary<string, object?>
                {
                    ["label"] = r.Label,
                    ["priority"] = r.Priority,
                    ["url"] = r.Url
                })
                .ToList(),
            ["version"] = manifest.Version
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
