using VestaCore.Events;

namespace VestaClient.Storage;

/// <summary>
/// Client-side local storage abstraction.
/// Stores events received from the server (already sequenced) and
/// manages the outbox for events created offline.
/// </summary>
public interface IClientEventStore
{
    /// <summary>
    /// Store a sequenced event received from the server.
    /// Idempotent — duplicate (channelId, sequence) pairs are ignored.
    /// </summary>
    Task StoreEventAsync(SequencedEvent sequencedEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Store a batch of sequenced events received from the server.
    /// </summary>
    Task StoreEventsAsync(IEnumerable<SequencedEvent> events, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read events from the local cache for a channel starting at a given sequence (inclusive).
    /// </summary>
    Task<IReadOnlyList<SequencedEvent>> GetEventsAsync(
        string channelId,
        long fromSequence,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the latest sequence stored locally for a channel.
    /// Returns 0 if no events are cached for the channel.
    /// </summary>
    Task<long> GetLatestSequenceAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Add an event to the outbox (created offline, pending sync to server).
    /// </summary>
    Task EnqueueOutboxAsync(VestaEvent evt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all pending outbox events ordered by creation time.
    /// </summary>
    Task<IReadOnlyList<OutboxEntry>> GetPendingOutboxAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark an outbox entry as sent (awaiting server confirmation).
    /// </summary>
    Task MarkOutboxSentAsync(Guid eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark an outbox entry as confirmed (server acknowledged with ACK).
    /// Removes it from the outbox.
    /// </summary>
    Task MarkOutboxConfirmedAsync(Guid eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all channels that have cached events, with their latest sequence.
    /// Used on reconnect to request catch-up from the server.
    /// </summary>
    Task<IReadOnlyDictionary<string, long>> GetChannelPositionsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// An entry in the client outbox — an event created offline pending sync.
/// </summary>
public sealed record OutboxEntry(
    VestaEvent Event,
    DateTimeOffset CreatedAt,
    OutboxStatus Status);

/// <summary>
/// Status of an outbox entry.
/// </summary>
public enum OutboxStatus
{
    Pending,
    Sent,
    Confirmed
}
