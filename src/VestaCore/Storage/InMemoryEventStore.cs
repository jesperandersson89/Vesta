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
        private readonly List<SequencedEvent> _events = [];
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
                if (evt.Replace)
                {
                    // Mark all previous events of same (clientId, eventType) as superseded
                    foreach (SequencedEvent existing in _events)
                    {
                        if (existing.Event.ClientId == evt.ClientId &&
                            existing.Event.EventType == evt.EventType)
                        {
                            _superseded.Add(existing.Event.Id);
                        }
                    }
                }

                long sequence = _nextSequence++;
                SequencedEvent sequenced = new(evt, sequence, DateTimeOffset.UtcNow);
                _events.Add(sequenced);
                return sequenced;
            }
        }

        public IReadOnlyList<SequencedEvent> GetEvents(long fromSequence, int limit)
        {
            lock (_lock)
            {
                List<SequencedEvent> results = [];
                foreach (SequencedEvent evt in _events)
                {
                    if (evt.Sequence < fromSequence) continue;
                    if (_superseded.Contains(evt.Event.Id)) continue;
                    results.Add(evt);
                    if (results.Count >= limit) break;
                }
                return results.AsReadOnly();
            }
        }
    }
}
