using System.Text;
using System.Text.Json;
using Org.Webpki.JsonCanonicalizer;
using VestaCore.Events;

namespace VestaCore.Identity;

/// <summary>
/// Signs and verifies VestaEvents using Ed25519 and RFC 8785 (JCS) canonical JSON.
/// </summary>
public static class EventSigner
{
    /// <summary>
    /// Sign an event, returning a new event record with the signature populated.
    /// The event's ClientId must match the identity's ClientId.
    /// </summary>
    public static VestaEvent SignEvent(VestaEvent vestaEvent, VestaIdentity identity)
    {
        if (vestaEvent.ClientId != identity.ClientId)
        {
            throw new ArgumentException(
                $"Event clientId '{vestaEvent.ClientId}' does not match identity clientId '{identity.ClientId}'.",
                nameof(vestaEvent));
        }

        byte[] signingInput = BuildSigningInput(vestaEvent);
        byte[] signatureBytes = identity.Sign(signingInput);
        string signature = Base64UrlEncode(signatureBytes);

        return vestaEvent with { Signature = signature };
    }

    /// <summary>
    /// Verify an event's signature using the provided public key.
    /// Returns true if the signature is valid.
    /// </summary>
    public static bool VerifyEvent(VestaEvent vestaEvent, byte[] publicKey)
    {
        if (string.IsNullOrEmpty(vestaEvent.Signature))
        {
            return false;
        }

        byte[] signingInput = BuildSigningInput(vestaEvent);
        byte[] signatureBytes = Base64UrlDecode(vestaEvent.Signature);

        return VestaIdentity.VerifyWithPublicKey(publicKey, signingInput, signatureBytes);
    }

    /// <summary>
    /// Verify that an event's clientId matches the expected derivation from the public key.
    /// </summary>
    public static bool VerifyClientId(VestaEvent vestaEvent, byte[] publicKey)
    {
        string expectedClientId = VestaIdentity.DeriveClientId(publicKey);
        return vestaEvent.ClientId == expectedClientId;
    }

    /// <summary>
    /// Build the canonical signing input bytes for a VestaEvent per RFC 8785 (JCS).
    /// Includes: id, channelId, timestamp, clientId, type, payload, parentId.
    /// Keys are sorted lexicographically.
    /// </summary>
    internal static byte[] BuildSigningInput(VestaEvent vestaEvent)
    {
        // Build a JSON object with the signed fields, then canonicalize with JCS.
        // JCS handles key sorting, so we just need to produce valid JSON.
        Dictionary<string, object?> signingFields = new()
        {
            ["channelId"] = vestaEvent.ChannelId,
            ["clientId"] = vestaEvent.ClientId,
            ["id"] = vestaEvent.Id.ToString(),
            ["parentId"] = vestaEvent.ParentId?.ToString(),
            ["payload"] = vestaEvent.Payload,
            ["timestamp"] = FormatTimestamp(vestaEvent.Timestamp),
            ["type"] = vestaEvent.EventType
        };

        string json = JsonSerializer.Serialize(signingFields);
        JsonCanonicalizer canonicalizer = new(json);
        string canonicalJson = canonicalizer.GetEncodedString();
        return Encoding.UTF8.GetBytes(canonicalJson);
    }

    /// <summary>
    /// Format a DateTimeOffset as an ISO 8601 UTC string with Z suffix.
    /// Truncates to seconds precision for deterministic output.
    /// </summary>
    private static string FormatTimestamp(DateTimeOffset timestamp)
    {
        // Use UTC and format consistently for cross-platform determinism.
        DateTimeOffset utc = timestamp.ToUniversalTime();
        return utc.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static byte[] Base64UrlDecode(string base64Url)
    {
        string base64 = base64Url
            .Replace('-', '+')
            .Replace('_', '/');

        int padding = (4 - base64.Length % 4) % 4;
        base64 += new string('=', padding);

        return Convert.FromBase64String(base64);
    }
}
