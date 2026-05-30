using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using VestaCore.Events;
using VestaCore.Storage;

namespace VestaServer.Storage;

/// <summary>
/// PostgreSQL implementation of <see cref="IEventStore"/> using raw Npgsql.
/// Uses the "channel_sequences" table for atomic per-channel sequence generation (Option B).
/// The event hot path (append/read) bypasses EF Core for performance.
/// </summary>
public sealed class NpgsqlEventStore(NpgsqlDataSource dataSource) : IEventStore
{
    public async Task<SequencedEvent> AppendAsync(VestaEvent evt, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Ensure channel exists (implicit creation per protocol spec)
        await EnsureChannelExistsAsync(connection, transaction, evt.ChannelId, cancellationToken);

        // Atomically get next sequence for this channel
        long sequence = await GetNextSequenceAsync(connection, transaction, evt.ChannelId, cancellationToken);

        DateTimeOffset receivedAt = DateTimeOffset.UtcNow;

        // Insert the event
        const string insertSql = """
            INSERT INTO events (id, channel_id, sequence, timestamp, client_id, event_type, payload, parent_id, signature, received_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            """;

        await using NpgsqlCommand cmd = new(insertSql, connection, transaction);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = evt.Id });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = evt.ChannelId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = sequence });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = evt.Timestamp });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = evt.ClientId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = evt.EventType });
        cmd.Parameters.Add(new NpgsqlParameter { Value = evt.Payload.GetRawText(), NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = evt.ParentId.HasValue ? evt.ParentId.Value : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)evt.Signature ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = receivedAt });

        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SequencedEvent(evt, sequence, receivedAt);
    }

    public async Task<IReadOnlyList<SequencedEvent>> GetEventsAsync(
        string channelId,
        long fromSequence,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, channel_id, sequence, timestamp, client_id, event_type, payload, parent_id, signature, received_at
            FROM events
            WHERE channel_id = $1 AND sequence >= $2
            ORDER BY sequence ASC
            LIMIT $3
            """;

        await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channelId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = fromSequence });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        List<SequencedEvent> events = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            SequencedEvent sequenced = ReadSequencedEvent(reader);
            events.Add(sequenced);
        }

        return events;
    }

    public async Task<long> GetLatestSequenceAsync(string channelId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT COALESCE(MAX(sequence), 0) FROM events WHERE channel_id = $1
            """;

        await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channelId });

        object? result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long seq ? seq : 0L;
    }

    private static async Task EnsureChannelExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string channelId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO channels (id) VALUES ($1) ON CONFLICT (id) DO NOTHING
            """;

        await using NpgsqlCommand cmd = new(sql, connection, transaction);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channelId });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> GetNextSequenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string channelId,
        CancellationToken cancellationToken)
    {
        // Upsert into channel_sequences and atomically increment
        const string sql = """
            INSERT INTO channel_sequences (channel_id, next_seq)
            VALUES ($1, 2)
            ON CONFLICT (channel_id)
            DO UPDATE SET next_seq = channel_sequences.next_seq + 1
            RETURNING next_seq - 1
            """;

        await using NpgsqlCommand cmd = new(sql, connection, transaction);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channelId });

        object? result = await cmd.ExecuteScalarAsync(cancellationToken);
        return (long)result!;
    }

    private static SequencedEvent ReadSequencedEvent(NpgsqlDataReader reader)
    {
        Guid id = reader.GetGuid(0);
        string channelId = reader.GetString(1);
        long sequence = reader.GetInt64(2);
        DateTimeOffset timestamp = reader.GetFieldValue<DateTimeOffset>(3);
        string clientId = reader.GetString(4);
        string eventType = reader.GetString(5);
        string payloadJson = reader.GetString(6);
        Guid? parentId = reader.IsDBNull(7) ? null : reader.GetGuid(7);
        string? signature = reader.IsDBNull(8) ? null : reader.GetString(8);
        DateTimeOffset receivedAt = reader.GetFieldValue<DateTimeOffset>(9);

        JsonElement payload = JsonDocument.Parse(payloadJson).RootElement.Clone();

        VestaEvent evt = new(id, channelId, timestamp, clientId, eventType, payload, parentId, signature);
        return new SequencedEvent(evt, sequence, receivedAt);
    }
}
