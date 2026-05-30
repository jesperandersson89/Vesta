namespace VestaCore.Events;

/// <summary>
/// Server-assigned wrapper around a <see cref="VestaEvent"/>.
/// Preserves the original event untouched (for signature verification)
/// and adds server-assigned ordering metadata.
/// </summary>
public sealed record SequencedEvent(
    VestaEvent Event,           // The original client event, untouched
    long Sequence,              // Server-assigned, per-channel monotonic
    DateTimeOffset ReceivedAt   // Server wall-clock when the event was accepted
);
