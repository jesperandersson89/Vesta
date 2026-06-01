using Npgsql;
using VestaCore.Storage;

namespace VestaServer.Storage;

/// <summary>Per-channel aggregate counters surfaced by the admin API.</summary>
/// <param name="EventCount">Total events currently stored for the channel.</param>
/// <param name="PayloadBytes">Approximate stored payload size in bytes. Postgres uses <c>SUM(pg_column_size(payload))</c>; in-memory mode sums the UTF-8 byte length of the serialized payload.</param>
/// <param name="LatestSequence">Highest assigned sequence (0 when the channel has no events).</param>
public sealed record ChannelStats(long EventCount, long PayloadBytes, long LatestSequence);

/// <summary>
/// Server-side helper that returns per-channel aggregates for the admin API.
/// Not part of <c>IEventStore</c> because <c>IEventStore</c> is shared with the
/// client SDK and these are operator-facing concerns.
/// </summary>
public interface IChannelStatsService
{
  Task<ChannelStats> GetStatsAsync(string channelId, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory implementation. Walks the event store via a wide
/// <see cref="IEventStore.GetEventsAsync"/> call; only suitable for the
/// in-memory / test backend where channels are small.
/// </summary>
public sealed class InMemoryChannelStatsService(IEventStore eventStore) : IChannelStatsService
{
  public async Task<ChannelStats> GetStatsAsync(string channelId, CancellationToken cancellationToken = default)
  {
    IReadOnlyList<VestaCore.Events.SequencedEvent> events =
        await eventStore.GetEventsAsync(channelId, fromSequence: 0, limit: int.MaxValue, cancellationToken);
    if (events.Count == 0)
      return new ChannelStats(0, 0, 0);

    long bytes = 0;
    foreach (VestaCore.Events.SequencedEvent se in events)
      bytes += System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(se.Event.Payload).LongLength;

    return new ChannelStats(events.Count, bytes, events[^1].Sequence);
  }
}

/// <summary>
/// PostgreSQL implementation. Single aggregate query against <c>events</c>.
/// </summary>
public sealed class NpgsqlChannelStatsService(NpgsqlDataSource dataSource) : IChannelStatsService
{
  public async Task<ChannelStats> GetStatsAsync(string channelId, CancellationToken cancellationToken = default)
  {
    const string sql = """
            SELECT COUNT(*)::bigint,
                   COALESCE(SUM(pg_column_size(payload))::bigint, 0),
                   COALESCE(MAX(sequence), 0)
            FROM events
            WHERE channel_id = $1
            """;
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channelId });
    await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
      return new ChannelStats(0, 0, 0);
    return new ChannelStats(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
  }
}
