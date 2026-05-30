namespace VestaCore.Protocol;

/// <summary>
/// CLIENT → SERVER: Initial handshake. Identifies the client and declares
/// which channels it wants to subscribe to with last-seen positions.
/// </summary>
public sealed record HelloMessage(
    string ClientId,
    IReadOnlyList<string> Channels,
    IReadOnlyDictionary<string, long> LastSequences
) : ProtocolMessage;
