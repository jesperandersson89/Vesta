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
