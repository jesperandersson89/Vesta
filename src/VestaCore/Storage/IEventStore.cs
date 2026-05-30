using VestaCore.Events;

namespace VestaCore.Storage;

/// <summary>
/// Storage abstraction for the event log.
/// Server and client use different implementations (NpgsqlEventStore, SqliteEventStore)
/// but share this contract.
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// Append a client-authored event to the store.
    /// The server assigns a sequence number and returns the sequenced wrapper.
    /// </summary>
    Task<SequencedEvent> AppendAsync(VestaEvent evt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read events from a channel starting at a given sequence (inclusive).
    /// </summary>
    Task<IReadOnlyList<SequencedEvent>> GetEventsAsync(
        string channelId,
        long fromSequence,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the latest assigned sequence for a channel.
    /// Returns 0 if the channel has no events.
    /// </summary>
    Task<long> GetLatestSequenceAsync(string channelId, CancellationToken cancellationToken = default);
}
