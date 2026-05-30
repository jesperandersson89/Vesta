using VestaCore.Events;

namespace VestaCore.Protocol;

/// <summary>
/// CLIENT → SERVER: Publish a new event to a channel.
/// </summary>
public sealed record PublishMessage(
    string ChannelId,
    VestaEvent Event
) : ProtocolMessage;

/// <summary>
/// CLIENT → SERVER: Subscribe to a channel, optionally resuming from a sequence.
/// </summary>
public sealed record SubscribeMessage(
    string ChannelId,
    long? FromSequence = null
) : ProtocolMessage;

/// <summary>
/// CLIENT → SERVER: Stop receiving real-time events for a channel.
/// </summary>
public sealed record UnsubscribeMessage(
    string ChannelId
) : ProtocolMessage;

/// <summary>
/// CLIENT → SERVER: Request a batch of historical events from a channel.
/// </summary>
public sealed record FetchMessage(
    string ChannelId,
    long FromSequence,
    long? ToSequence = null,
    int? Limit = null
) : ProtocolMessage;
