namespace VestaCore.Protocol;

/// <summary>
/// CLIENT → SERVER: Initial handshake. Identifies the client and declares
/// which channels it wants to subscribe to with last-seen positions.
/// PublicKey is the base64url-encoded Ed25519 public key (32 bytes) for signature verification.
/// </summary>
public sealed record HelloMessage(
    string ClientId,
    IReadOnlyList<string> Channels,
    IReadOnlyDictionary<string, long> LastSequences,
    string? PublicKey = null
) : ProtocolMessage;

/// <summary>
/// CLIENT → SERVER: Explicitly create a new channel.
/// The issuing client becomes the channel admin. <see cref="InitialMembers"/>
/// are added as members in addition to the creator.
/// When <see cref="Visibility"/> is "private", only members can subscribe/publish/fetch.
/// </summary>
public sealed record CreateChannelMessage(
    string ChannelId,
    string Visibility,                              // "public" | "private"
    IReadOnlyList<string> InitialMembers
) : ProtocolMessage;

/// <summary>
/// CLIENT → SERVER: Grant access to a private channel. Admin-only.
/// </summary>
public sealed record GrantAccessMessage(
    string ChannelId,
    string ClientId,
    string Role                                     // "member" | "admin"
) : ProtocolMessage;

