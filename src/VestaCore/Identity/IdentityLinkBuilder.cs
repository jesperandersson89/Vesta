using System.Security.Cryptography;
using System.Text.Json;
using VestaCore.Events;
using VestaCore.Serialization;
using VestaCore.Utilities;

namespace VestaCore.Identity;

/// <summary>
/// Constructs the three identity-channel event types — <c>announce</c>,
/// <c>link</c>, and <c>unlink</c> — as signed <see cref="VestaEvent"/>s
/// ready to publish to <c>vesta/identity/{groupId}</c>.
///
/// <para>
/// These are ordinary signed events. The server treats them like any other
/// event; only the client interprets them via <see cref="DeviceGroupProjection"/>.
/// </para>
/// </summary>
public static class IdentityLinkBuilder
{
    /// <summary>
    /// The reserved event type for a device announcing itself in a group.
    /// Published by the new (or founding) device, signed by the device's own key.
    /// </summary>
    public const string AnnounceEventType = "vesta.identity.announce";

    /// <summary>
    /// The reserved event type for a device vouching for another public key as a group member.
    /// Published by an existing member, signed by that member's key.
    /// </summary>
    public const string LinkEventType = "vesta.identity.link";

    /// <summary>
    /// The reserved event type for a device removing another public key from a group.
    /// Published by an existing member, signed by that member's key.
    /// </summary>
    public const string UnlinkEventType = "vesta.identity.unlink";

    private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;

    /// <summary>
    /// Generate a new random group identifier suitable for embedding in a channel ID.
    /// <para>
    /// Returns 32 lowercase hex chars (128 bits of entropy). Hex is used rather than
    /// base64url because the latter's <c>_</c> character is not permitted in channel
    /// IDs (see <see cref="Channels.ChannelId"/>).
    /// </para>
    /// </summary>
    public static string GenerateGroupId()
    {
        byte[] random = new byte[16];
        RandomNumberGenerator.Fill(random);
        return Convert.ToHexString(random).ToLowerInvariant();
    }

    /// <summary>
    /// Build and sign an <c>announce</c> event from the given identity into the
    /// given group's identity channel. The signer becomes the author of the event
    /// (i.e. <c>evt.ClientId == identity.ClientId</c>).
    /// </summary>
    public static VestaEvent BuildAnnounce(
        VestaIdentity identity,
        string groupId,
        string? deviceName = null,
        DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateGroupId(groupId);

        DeviceAnnounce payload = new(groupId, deviceName);
        VestaEvent evt = new(
            Id: Guid.NewGuid(),
            ChannelId: DeviceGroupChannel.For(groupId),
            Timestamp: timestamp ?? DateTimeOffset.UtcNow,
            ClientId: identity.ClientId,
            EventType: AnnounceEventType,
            Payload: SerializePayload(payload));

        return EventSigner.SignEvent(evt, identity);
    }

    /// <summary>
    /// Build and sign a <c>link</c> event vouching for <paramref name="targetPublicKey"/>
    /// as a member of <paramref name="groupId"/>. Signed by <paramref name="signer"/>,
    /// who must already be a trusted member of the group for the link to be honored
    /// by other clients.
    /// </summary>
    public static VestaEvent BuildLink(
        VestaIdentity signer,
        string groupId,
        byte[] targetPublicKey,
        string? reason = null,
        DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(targetPublicKey);
        ValidateGroupId(groupId);
        if (targetPublicKey.Length != 32)
            throw new ArgumentException("Target public key must be 32 bytes (Ed25519).", nameof(targetPublicKey));

        DeviceLink link = new(
            TargetPublicKey: Base64Url.Encode(targetPublicKey),
            TargetClientId: VestaIdentity.DeriveClientId(targetPublicKey),
            GroupId: groupId,
            Reason: reason);

        VestaEvent evt = new(
            Id: Guid.NewGuid(),
            ChannelId: DeviceGroupChannel.For(groupId),
            Timestamp: timestamp ?? DateTimeOffset.UtcNow,
            ClientId: signer.ClientId,
            EventType: LinkEventType,
            Payload: SerializePayload(link));

        return EventSigner.SignEvent(evt, signer);
    }

    /// <summary>
    /// Build and sign an <c>unlink</c> event removing <paramref name="targetPublicKey"/>
    /// from <paramref name="groupId"/>. Signed by <paramref name="signer"/>, who must
    /// already be a trusted member of the group.
    /// </summary>
    public static VestaEvent BuildUnlink(
        VestaIdentity signer,
        string groupId,
        byte[] targetPublicKey,
        string? reason = null,
        DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(targetPublicKey);
        ValidateGroupId(groupId);
        if (targetPublicKey.Length != 32)
            throw new ArgumentException("Target public key must be 32 bytes (Ed25519).", nameof(targetPublicKey));

        DeviceLink link = new(
            TargetPublicKey: Base64Url.Encode(targetPublicKey),
            TargetClientId: VestaIdentity.DeriveClientId(targetPublicKey),
            GroupId: groupId,
            Reason: reason);

        VestaEvent evt = new(
            Id: Guid.NewGuid(),
            ChannelId: DeviceGroupChannel.For(groupId),
            Timestamp: timestamp ?? DateTimeOffset.UtcNow,
            ClientId: signer.ClientId,
            EventType: UnlinkEventType,
            Payload: SerializePayload(link));

        return EventSigner.SignEvent(evt, signer);
    }

    private static JsonElement SerializePayload<T>(T payload)
    {
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        return JsonDocument.Parse(json).RootElement;
    }

    private static void ValidateGroupId(string groupId)
    {
        if (string.IsNullOrEmpty(groupId))
            throw new ArgumentException("Group ID must not be empty.", nameof(groupId));

        // Must be safe to embed in a channel ID — see Channels.ChannelId for the full charset.
        foreach (char c in groupId)
        {
            bool isAllowed = (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '-';
            if (!isAllowed)
                throw new ArgumentException(
                    $"Group ID '{groupId}' contains invalid character '{c}'. " +
                    "Only [a-z0-9-] are allowed.",
                    nameof(groupId));
        }
    }
}
