using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vesta;

/// <summary>A verified <c>vesta.channel-key-request</c>: a member announcing its X25519 public key.</summary>
/// <param name="ClientId">The requester (equals the event author).</param>
/// <param name="PublicKey">The requester's Ed25519 public key (verified to derive <paramref name="ClientId"/>).</param>
/// <param name="X25519PublicKey">The key the admin should seal channel keys to.</param>
public sealed record ChannelKeyRequest(string ClientId, byte[] PublicKey, byte[] X25519PublicKey);

/// <summary>A verified <c>vesta.channel-key</c>: an admin handing one recipient one or more sealed epochs.</summary>
public sealed record ChannelKeyGrant(string AdminClientId, string RecipientClientId, IReadOnlyList<SealedChannelKey> Keys);

/// <summary>
/// The two control events that distribute channel keys on an encrypted channel (PLANNING #33 phase C).
/// Both are ordinary signed events with <c>vesta.*</c> types, so the relay lets them through the
/// envelope check and they ride the normal catch-up.
/// <list type="bullet">
/// <item><c>vesta.channel-key-request</c> — payload <c>{ publicKey, x25519PublicKey }</c> (both base64url).
/// Self-authenticating: the Ed25519 key must derive the author's <c>clientId</c> and the signature must verify.</item>
/// <item><c>vesta.channel-key</c> — payload <c>{ recipient, keys: [{ epoch, epk, n, ct }] }</c>. Accepted only
/// when signed by one of the admin keys the app trusts.</item>
/// </list>
/// </summary>
public static class ChannelKeyEvents
{
    public const string KeyRequestEventType = "vesta.channel-key-request";
    public const string KeyGrantEventType = "vesta.channel-key";

    /// <summary>Build and sign a key request from <paramref name="identity"/> for <paramref name="channelId"/>.</summary>
    public static VestaEvent BuildKeyRequest(VestaIdentity identity, string channelId, ChannelKeyPair keyPair, DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(keyPair);
        JsonObject payload = new()
        {
            ["publicKey"] = Base64Url.Encode(identity.PublicKey),
            ["x25519PublicKey"] = Base64Url.Encode(keyPair.PublicKey),
        };
        return Sign(identity, channelId, KeyRequestEventType, payload, timestamp);
    }

    /// <summary>Build and sign a key grant from <paramref name="admin"/> to <paramref name="recipientClientId"/>.</summary>
    public static VestaEvent BuildKeyGrant(VestaIdentity admin, string channelId, string recipientClientId, IReadOnlyList<SealedChannelKey> keys, DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientClientId);
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
            throw new ArgumentException("A key grant must carry at least one sealed key.", nameof(keys));

        JsonArray sealedKeys = [];
        foreach (SealedChannelKey key in keys)
            sealedKeys.Add(key.ToJson());
        JsonObject payload = new()
        {
            ["recipient"] = recipientClientId,
            ["keys"] = sealedKeys,
        };
        return Sign(admin, channelId, KeyGrantEventType, payload, timestamp);
    }

    /// <summary>
    /// Parse and verify a key request. Returns <c>null</c> for any other event type, a malformed payload,
    /// a public key that does not derive the author's client id, or a bad signature.
    /// </summary>
    public static ChannelKeyRequest? TryParseKeyRequest(VestaEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (evt.EventType != KeyRequestEventType || evt.Payload.ValueKind != JsonValueKind.Object)
            return null;
        if (!evt.Payload.TryGetProperty("publicKey", out JsonElement publicKeyElement) ||
            !evt.Payload.TryGetProperty("x25519PublicKey", out JsonElement x25519Element))
            return null;

        byte[]? publicKey = TryDecode(publicKeyElement, 32);
        byte[]? x25519PublicKey = TryDecode(x25519Element, 32);
        if (publicKey is null || x25519PublicKey is null)
            return null;
        if (VestaIdentity.DeriveClientId(publicKey) != evt.ClientId)
            return null;
        if (string.IsNullOrEmpty(evt.Signature) || !EventSigner.VerifyEvent(evt, publicKey))
            return null;

        return new ChannelKeyRequest(evt.ClientId, publicKey, x25519PublicKey);
    }

    /// <summary>
    /// Parse and verify a key grant against the admin keys the app trusts. Returns <c>null</c> for any other
    /// event type, an author that is not one of <paramref name="adminPublicKeys"/>, a bad signature or a
    /// malformed payload. The caller still has to check <see cref="ChannelKeyGrant.RecipientClientId"/>.
    /// </summary>
    public static ChannelKeyGrant? TryParseKeyGrant(VestaEvent evt, IReadOnlyList<byte[]> adminPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(adminPublicKeys);
        if (evt.EventType != KeyGrantEventType || evt.Payload.ValueKind != JsonValueKind.Object)
            return null;

        byte[]? admin = null;
        foreach (byte[] candidate in adminPublicKeys)
        {
            if (VestaIdentity.DeriveClientId(candidate) == evt.ClientId)
            {
                admin = candidate;
                break;
            }
        }
        if (admin is null || string.IsNullOrEmpty(evt.Signature) || !EventSigner.VerifyEvent(evt, admin))
            return null;

        if (!evt.Payload.TryGetProperty("recipient", out JsonElement recipientElement) || recipientElement.ValueKind != JsonValueKind.String ||
            !evt.Payload.TryGetProperty("keys", out JsonElement keysElement) || keysElement.ValueKind != JsonValueKind.Array)
            return null;

        List<SealedChannelKey> keys = [];
        try
        {
            foreach (JsonElement item in keysElement.EnumerateArray())
                keys.Add(SealedChannelKey.FromJson(item));
        }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
        if (keys.Count == 0)
            return null;

        return new ChannelKeyGrant(evt.ClientId, recipientElement.GetString()!, keys);
    }

    private static VestaEvent Sign(VestaIdentity identity, string channelId, string eventType, JsonObject payload, DateTimeOffset? timestamp)
    {
        VestaEvent evt = new(
            Id: Guid.NewGuid(),
            ChannelId: channelId,
            Timestamp: timestamp ?? DateTimeOffset.UtcNow,
            ClientId: identity.ClientId,
            EventType: eventType,
            Payload: JsonSerializer.SerializeToElement(payload));
        return EventSigner.SignEvent(evt, identity);
    }

    private static byte[]? TryDecode(JsonElement element, int expectedLength)
    {
        if (element.ValueKind != JsonValueKind.String)
            return null;
        try
        {
            byte[] bytes = Base64Url.Decode(element.GetString()!);
            return bytes.Length == expectedLength ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
