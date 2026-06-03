using System.Text.Json;
using VestaCore.Events;
using VestaCore.Projections;
using VestaCore.Serialization;
using VestaCore.Utilities;

namespace VestaCore.Identity;

/// <summary>
/// Client-side reducer that materializes the current membership of a device
/// group by replaying its <c>vesta/identity/{groupId}</c> channel.
///
/// <para>
/// Trust rules (Phase 1):
/// </para>
/// <list type="bullet">
///   <item>
///     The <b>founder</b> — the author of the first <c>vesta.identity.announce</c>
///     event seen on the channel — is self-trusted. They are the seed of the
///     trust graph.
///   </item>
///   <item>
///     A <c>vesta.identity.link</c> event is honored only if its signer (the
///     event's <c>ClientId</c>) is already a trusted member. The link adds
///     <c>targetClientId</c> to the trusted set.
///   </item>
///   <item>
///     A subsequent <c>vesta.identity.announce</c> event is honored only if
///     the signer was already added via a prior link.
///   </item>
///   <item>
///     A <c>vesta.identity.unlink</c> event is honored only if signed by a
///     trusted member, and removes <c>targetClientId</c> from the trusted set.
///     (Conflict resolution for mutual revocation is a Phase 2 concern.)
///   </item>
///   <item>
///     Signature verification of the event itself is the caller's
///     responsibility — typically performed by the server (when server-side
///     verification is enabled) or by the client before applying. This
///     projection assumes events handed to it are already signature-valid.
///   </item>
/// </list>
/// </summary>
public sealed class DeviceGroupProjection : EventReducer<DeviceGroup>
{
    private readonly string _groupId;

    // clientId -> public key (base64url). Order of insertion is irrelevant for the public DeviceGroup view.
    private readonly Dictionary<string, string> _members = [];

    public DeviceGroupProjection(string groupId)
    {
        if (string.IsNullOrEmpty(groupId))
            throw new ArgumentException("Group ID must not be empty.", nameof(groupId));
        _groupId = groupId;
    }

    /// <inheritdoc />
    public override DeviceGroup State
    {
        get
        {
            lock (SyncRoot)
            {
                Dictionary<string, string> copy = new(_members);
                return new DeviceGroup(_groupId, copy);
            }
        }
    }

    /// <inheritdoc />
    protected override void Reduce(VestaEvent evt)
    {
        switch (evt.EventType)
        {
            case IdentityLinkBuilder.AnnounceEventType:
                ReduceAnnounce(evt);
                break;
            case IdentityLinkBuilder.LinkEventType:
                ReduceLink(evt);
                break;
            case IdentityLinkBuilder.UnlinkEventType:
                ReduceUnlink(evt);
                break;
            // Ignore any other event types on this channel.
        }
    }

    private void ReduceAnnounce(VestaEvent evt)
    {
        DeviceAnnounce? payload = JsonSerializer.Deserialize<DeviceAnnounce>(evt.Payload.GetRawText(), VestaJsonOptions.Default);
        if (payload is null || payload.GroupId != _groupId)
            return;

        // Founder bootstrap: if the group is empty, the first announce establishes the founder.
        // We cannot recover the founder's public key from the event alone — store empty string
        // as a placeholder. A reciprocal link event from the founder, or a verifiable public-key
        // lookup, can fill this in later. For Phase 1 this is acceptable: the trust set keys
        // on clientId, the public key is informational.
        if (_members.Count == 0)
        {
            _members[evt.ClientId] = string.Empty;
            return;
        }

        // For non-founder announces, the device must already have been linked by a trusted member.
        // If not in the set, ignore (the announce is unauthenticated until someone vouches).
        // If already in the set, this is a no-op.
    }

    private void ReduceLink(VestaEvent evt)
    {
        // Signer must already be a trusted member.
        if (!_members.ContainsKey(evt.ClientId))
            return;

        DeviceLink? payload = JsonSerializer.Deserialize<DeviceLink>(evt.Payload.GetRawText(), VestaJsonOptions.Default);
        if (payload is null || payload.GroupId != _groupId)
            return;

        // Verify the targetClientId matches the targetPublicKey (defense against malformed events).
        try
        {
            byte[] targetPubKey = Base64Url.Decode(payload.TargetPublicKey);
            if (targetPubKey.Length != 32)
                return;
            string derivedClientId = VestaIdentity.DeriveClientId(targetPubKey);
            if (derivedClientId != payload.TargetClientId)
                return;

            _members[payload.TargetClientId] = payload.TargetPublicKey;
        }
        catch (FormatException)
        {
            // Malformed base64url — ignore the link.
        }
    }

    private void ReduceUnlink(VestaEvent evt)
    {
        if (!_members.ContainsKey(evt.ClientId))
            return;

        DeviceLink? payload = JsonSerializer.Deserialize<DeviceLink>(evt.Payload.GetRawText(), VestaJsonOptions.Default);
        if (payload is null || payload.GroupId != _groupId)
            return;

        _members.Remove(payload.TargetClientId);
    }
}
