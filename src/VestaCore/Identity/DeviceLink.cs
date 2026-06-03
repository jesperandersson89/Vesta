namespace VestaCore.Identity;

/// <summary>
/// The payload shape carried by <c>vesta.identity.link</c> and
/// <c>vesta.identity.unlink</c> events.
///
/// <para>
/// A link event is a signed statement from one device in a group asserting
/// that another public key also belongs to the same group. An unlink event
/// is the inverse — a signed statement that a public key should no longer
/// be considered a member.
/// </para>
/// <para>
/// The event's signature (over the enclosing <see cref="Events.VestaEvent"/>)
/// proves which device made the claim. The <see cref="TargetClientId"/> is
/// included for convenience and is verifiable by re-deriving it from
/// <see cref="TargetPublicKey"/>.
/// </para>
/// </summary>
public sealed record DeviceLink(
    string TargetPublicKey,   // base64url-encoded 32-byte Ed25519 public key
    string TargetClientId,    // base64url(sha256(targetPublicKey))[:22]
    string GroupId,           // the device group this link is asserted within
    string? Reason = null     // optional human-readable hint (e.g. "device-link", "user-removed")
);

/// <summary>
/// The payload shape carried by <c>vesta.identity.announce</c> events.
///
/// <para>
/// A device publishes an announce event when it joins a group — either as
/// the founder of a new group (self-trusted bootstrap) or after receiving
/// a link event vouching for it from an existing member.
/// </para>
/// </summary>
public sealed record DeviceAnnounce(
    string GroupId,
    string? DeviceName = null
);
