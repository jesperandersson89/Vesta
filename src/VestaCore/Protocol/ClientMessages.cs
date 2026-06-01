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

/// <summary>
/// CLIENT → SERVER: Register an app namespace. The first slug segment of any
/// channel ID belongs to an app namespace; when the server is configured with
/// <c>Vesta:RequireAppRegistration=true</c>, all channel-creating operations
/// (PUBLISH, SUBSCRIBE, CREATE_CHANNEL) reject channels whose app namespace
/// has not been registered. The connecting client becomes the app owner.
/// </summary>
public sealed record RegisterAppMessage(
    string AppId
) : ProtocolMessage;

/// <summary>
/// CLIENT → SERVER: Soft-delete a channel. Admin-only — the connection must
/// have been promoted to admin during HELLO (its public key matched an entry
/// in <c>Admin:BootstrapPublicKeys</c>). The channel is marked deleted; any
/// subsequent PUBLISH, SUBSCRIBE, FETCH, or CREATE_CHANNEL for that ID is
/// rejected with <c>CHANNEL_DELETED</c>. Existing events are retained until a
/// future hard-delete sweep (see TODO #12b).
/// </summary>
public sealed record DeleteChannelMessage(
    string ChannelId
) : ProtocolMessage;

