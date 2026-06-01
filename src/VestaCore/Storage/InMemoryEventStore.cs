using System.Collections.Concurrent;
using VestaCore.Events;

namespace VestaCore.Storage;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IEventStore"/>.
/// Used for development, testing, and early prototyping without any DB dependency.
/// Per-channel sequences are assigned atomically.
/// </summary>
public sealed class InMemoryEventStore : IEventStore
{
    private readonly ConcurrentDictionary<string, ChannelLog> _channels = new();

    public Task<SequencedEvent> AppendAsync(VestaEvent evt, CancellationToken cancellationToken = default)
    {
        ChannelLog log = _channels.GetOrAdd(evt.ChannelId, _ => new ChannelLog());
        SequencedEvent sequenced = log.Append(evt);
        return Task.FromResult(sequenced);
    }

    public Task<IReadOnlyList<SequencedEvent>> GetEventsAsync(
        string channelId,
        long fromSequence,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (!_channels.TryGetValue(channelId, out ChannelLog? log))
        {
            return Task.FromResult<IReadOnlyList<SequencedEvent>>(Array.Empty<SequencedEvent>());
        }

        IReadOnlyList<SequencedEvent> events = log.GetEvents(fromSequence, limit);
        return Task.FromResult(events);
    }

    public Task<long> GetLatestSequenceAsync(string channelId, CancellationToken cancellationToken = default)
    {
        if (!_channels.TryGetValue(channelId, out ChannelLog? log))
        {
            return Task.FromResult(0L);
        }

        return Task.FromResult(log.LatestSequence);
    }

    /// <summary>
    /// Internal per-channel log that manages sequence assignment and storage.
    /// Thread-safe via locking on the event list.
    /// </summary>
    private sealed class ChannelLog
    {
        private readonly List<StoredEvent> _events = [];
        private readonly HashSet<Guid> _superseded = [];
        private readonly object _lock = new();
        private long _nextSequence = 1;

        public long LatestSequence
        {
            get
            {
                lock (_lock)
                {
                    return _nextSequence - 1;
                }
            }
        }

        public SequencedEvent Append(VestaEvent evt)
        {
            lock (_lock)
            {
                // Idempotent on event id: a client that didn't see an ACK and
                // republishes the same event must get the original sequence back,
                // not a fresh one. Matches NpgsqlEventStore behaviour.
                foreach (StoredEvent existing in _events)
                {
                    if (existing.Sequenced.Event.Id == evt.Id)
                        return existing.Sequenced;
                }

                if (evt.Replace)
                {
                    // Mark all previous events of same (clientId, eventType) as superseded
                    foreach (StoredEvent existing in _events)
                    {
                        if (existing.Sequenced.Event.ClientId == evt.ClientId &&
                            existing.Sequenced.Event.EventType == evt.EventType)
                        {
                            _superseded.Add(existing.Sequenced.Event.Id);
                        }
                    }
                }

                long sequence = _nextSequence++;
                DateTimeOffset receivedAt = DateTimeOffset.UtcNow;
                DateTimeOffset? expiresAt = VestaEventMetadata.TryGetTtlSeconds(evt, out int ttl)
                    ? receivedAt.AddSeconds(ttl)
                    : null;

                SequencedEvent sequenced = new(evt, sequence, receivedAt);
                _events.Add(new StoredEvent(sequenced, expiresAt));
                return sequenced;
            }
        }

        public IReadOnlyList<SequencedEvent> GetEvents(long fromSequence, int limit)
        {
            lock (_lock)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                List<SequencedEvent> results = [];
                foreach (StoredEvent stored in _events)
                {
                    if (stored.Sequenced.Sequence < fromSequence) continue;
                    if (_superseded.Contains(stored.Sequenced.Event.Id)) continue;
                    if (stored.ExpiresAt is DateTimeOffset expires && expires <= now) continue;
                    results.Add(stored.Sequenced);
                    if (results.Count >= limit) break;
                }
                return results.AsReadOnly();
            }
        }

        private readonly record struct StoredEvent(SequencedEvent Sequenced, DateTimeOffset? ExpiresAt);
    }
}
