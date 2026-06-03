using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VestaCore.Serialization;
using VestaCore.Utilities;

namespace VestaCore.Identity;

/// <summary>
/// The information one device needs to deliver to another in order to bootstrap
/// a device-group link. Exchanged out-of-band — QR code, deep link, paste,
/// numeric short code (future).
///
/// <para>
/// The pairing payload is <b>not secret</b>. It contains only the existing
/// device's public information (its public key, the group ID it wants the
/// new device to join, and optionally the server URL for convenience). The
/// new device generates its own keypair locally and publishes an announce
/// event signed by that key; the existing device then publishes a link event
/// vouching for the new device.
/// </para>
/// </summary>
public sealed record PairingPayload(
    string GroupId,

    /// <summary>Base64url-encoded 32-byte Ed25519 public key of the inviting device.</summary>
    [property: JsonPropertyName("publicKey")]
    string PublicKey,

    /// <summary>Optional Vesta server URL the new device should connect to.</summary>
    string? ServerUrl = null
)
{
    private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;

    /// <summary>
    /// Encode the payload as a compact base64url string suitable for QR codes,
    /// deep links, or paste-buffer exchange.
    /// </summary>
    public string ToBase64()
    {
        string json = JsonSerializer.Serialize(this, JsonOptions);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        return Base64Url.Encode(bytes);
    }

    /// <summary>
    /// Decode a base64url-encoded pairing payload previously produced by <see cref="ToBase64"/>.
    /// </summary>
    public static PairingPayload FromBase64(string encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            throw new ArgumentException("Encoded pairing payload must not be empty.", nameof(encoded));

        byte[] bytes = Base64Url.Decode(encoded);
        string json = Encoding.UTF8.GetString(bytes);
        PairingPayload? payload = JsonSerializer.Deserialize<PairingPayload>(json, JsonOptions)
            ?? throw new ArgumentException("Pairing payload deserialized to null.", nameof(encoded));
        return payload;
    }
}
