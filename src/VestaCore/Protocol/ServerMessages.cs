using VestaCore.Events;

namespace VestaCore.Protocol;

/// <summary>
/// SERVER → CLIENT: Response to HELLO. Confirms connection and lists available channels.
/// </summary>
public sealed record WelcomeMessage(
    string ServerId,
    IReadOnlyList<string> Channels
) : ProtocolMessage;

/// <summary>
/// SERVER → CLIENT: A single real-time event pushed to subscribers.
/// </summary>
public sealed record EventMessage(
    string ChannelId,
    VestaEvent Event,
    long Sequence,
    DateTimeOffset ReceivedAt
) : ProtocolMessage;

/// <summary>
/// SERVER → CLIENT: A batch of sequenced events (response to FETCH or catch-up).
/// </summary>
public sealed record EventsBatchMessage(
    string ChannelId,
    IReadOnlyList<SequencedEvent> Events
) : ProtocolMessage;

/// <summary>
/// SERVER → CLIENT: Acknowledgement that a published event was accepted and sequenced.
/// </summary>
public sealed record AckMessage(
    string ChannelId,
    Guid EventId,
    long Sequence
) : ProtocolMessage;

/// <summary>
/// SERVER → CLIENT: Error response.
/// </summary>
/// <remarks>
/// When the error was triggered by a specific client request that carried an event
/// (notably PUBLISH rejections — quota, rate-limit, access, signature), the server
/// stamps <see cref="EventId"/> and <see cref="ChannelId"/> so the client can correlate
/// the failure back to the exact publish. This lets the client stop retrying a doomed
/// outbox entry and surface a precise "this event was limited" signal. Both fields are
/// optional and remain null for connection-level errors that aren't tied to one event.
/// </remarks>
public sealed record ErrorMessage(
    string Code,
    string Message,
    Guid? EventId = null,
    string? ChannelId = null
) : ProtocolMessage;
