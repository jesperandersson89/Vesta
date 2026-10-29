using System.Text.Json;

namespace Vesta;

/// <summary>
/// The payload envelope every non-<c>vesta.*</c> event on an encrypted channel must carry:
/// <c>{ "enc": 1, "kid": &lt;int&gt;, "n": "&lt;base64url 12 bytes&gt;", "ct": "&lt;base64url&gt;" }</c>.
/// The relay checks only this shape (it cannot verify ciphertext and never holds a key); the
/// SDKs produce and consume it. Signing is unchanged: the Ed25519 signature covers the envelope.
/// </summary>
public static class EncryptedPayload
{
    /// <summary>Envelope version carried in <c>enc</c>.</summary>
    public const int Version = 1;

    /// <summary>AES-256-GCM nonce length in bytes, as carried base64url-encoded in <c>n</c>.</summary>
    public const int NonceBytes = 12;

    /// <summary>
    /// Returns true when <paramref name="payload"/> is a well-formed envelope: an object with exactly
    /// the four members <c>enc</c> (== <see cref="Version"/>), <c>kid</c> (non-negative integer),
    /// <c>n</c> (base64url of <see cref="NonceBytes"/> bytes) and <c>ct</c> (non-empty base64url).
    /// </summary>
    public static bool IsEnvelope(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return false;

        int members = 0;
        bool enc = false, kid = false, nonce = false, ct = false;
        foreach (JsonProperty property in payload.EnumerateObject())
        {
            members++;
            switch (property.Name)
            {
                case "enc":
                    enc = property.Value.ValueKind == JsonValueKind.Number &&
                          property.Value.TryGetInt32(out int version) && version == Version;
                    break;
                case "kid":
                    kid = property.Value.ValueKind == JsonValueKind.Number &&
                          property.Value.TryGetInt32(out int epoch) && epoch >= 0;
                    break;
                case "n":
                    nonce = property.Value.ValueKind == JsonValueKind.String &&
                            IsBase64UrlOfLength(property.Value.GetString(), NonceBytes);
                    break;
                case "ct":
                    ct = property.Value.ValueKind == JsonValueKind.String &&
                         IsBase64Url(property.Value.GetString(), minBytes: 1);
                    break;
                default:
                    return false;
            }
        }

        return members == 4 && enc && kid && nonce && ct;
    }

    private static bool IsBase64UrlOfLength(string? value, int bytes)
    {
        byte[]? decoded = TryDecode(value);
        return decoded is not null && decoded.Length == bytes;
    }

    private static bool IsBase64Url(string? value, int minBytes)
    {
        byte[]? decoded = TryDecode(value);
        return decoded is not null && decoded.Length >= minBytes;
    }

    private static byte[]? TryDecode(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        try
        {
            return Base64Url.Decode(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
