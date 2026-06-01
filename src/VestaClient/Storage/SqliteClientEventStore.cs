using System.Text.Json;
using Microsoft.Data.Sqlite;
using VestaCore.Events;

namespace VestaClient.Storage;

/// <summary>
/// SQLite implementation of <see cref="IClientEventStore"/>.
/// Stores received events locally for offline access and manages the outbox
/// for events created while disconnected.
/// </summary>
public sealed class SqliteClientEventStore : IClientEventStore, IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>
    /// Create a new SQLite client event store.
    /// </summary>
    /// <param name="connectionString">SQLite connection string (e.g. "Data Source=vesta.db")</param>
    public SqliteClientEventStore(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        InitializeSchema();
    }

    /// <summary>
    /// Create an in-memory SQLite client event store (for testing).
    /// </summary>
    public static SqliteClientEventStore CreateInMemory()
    {
        return new SqliteClientEventStore("Data Source=:memory:");
    }

    public Task StoreEventAsync(SequencedEvent sequencedEvent, CancellationToken cancellationToken = default)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO events (id, channel_id, sequence, timestamp, client_id, event_type, payload, parent_id, signature, received_at)
            VALUES ($id, $channel_id, $sequence, $timestamp, $client_id, $event_type, $payload, $parent_id, $signature, $received_at)
            """;

        AddEventParameters(cmd, sequencedEvent);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task StoreEventsAsync(IEnumerable<SequencedEvent> events, CancellationToken cancellationToken = default)
    {
        using SqliteTransaction transaction = _connection.BeginTransaction();

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT OR IGNORE INTO events (id, channel_id, sequence, timestamp, client_id, event_type, payload, parent_id, signature, received_at)
            VALUES ($id, $channel_id, $sequence, $timestamp, $client_id, $event_type, $payload, $parent_id, $signature, $received_at)
            """;

        // Create parameters once
        SqliteParameter pId = cmd.Parameters.Add("$id", SqliteType.Text);
        SqliteParameter pChannelId = cmd.Parameters.Add("$channel_id", SqliteType.Text);
        SqliteParameter pSequence = cmd.Parameters.Add("$sequence", SqliteType.Integer);
        SqliteParameter pTimestamp = cmd.Parameters.Add("$timestamp", SqliteType.Text);
        SqliteParameter pClientId = cmd.Parameters.Add("$client_id", SqliteType.Text);
        SqliteParameter pEventType = cmd.Parameters.Add("$event_type", SqliteType.Text);
        SqliteParameter pPayload = cmd.Parameters.Add("$payload", SqliteType.Text);
        SqliteParameter pParentId = cmd.Parameters.Add("$parent_id", SqliteType.Text);
        SqliteParameter pSignature = cmd.Parameters.Add("$signature", SqliteType.Text);
        SqliteParameter pReceivedAt = cmd.Parameters.Add("$received_at", SqliteType.Text);

        cmd.Prepare();

        foreach (SequencedEvent sequencedEvent in events)
        {
            VestaEvent evt = sequencedEvent.Event;
            pId.Value = evt.Id.ToString();
            pChannelId.Value = evt.ChannelId;
            pSequence.Value = sequencedEvent.Sequence;
            pTimestamp.Value = evt.Timestamp.ToString("O");
            pClientId.Value = evt.ClientId;
            pEventType.Value = evt.EventType;
            pPayload.Value = evt.Payload.GetRawText();
            pParentId.Value = evt.ParentId.HasValue ? evt.ParentId.Value.ToString() : (object)DBNull.Value;
            pSignature.Value = (object?)evt.Signature ?? DBNull.Value;
            pReceivedAt.Value = sequencedEvent.ReceivedAt.ToString("O");

            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SequencedEvent>> GetEventsAsync(
        string channelId,
        long fromSequence,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, channel_id, sequence, timestamp, client_id, event_type, payload, parent_id, signature, received_at
            FROM events
            WHERE channel_id = $channel_id AND sequence >= $from_sequence
            ORDER BY sequence ASC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$channel_id", channelId);
        cmd.Parameters.AddWithValue("$from_sequence", fromSequence);
        cmd.Parameters.AddWithValue("$limit", limit);

        List<SequencedEvent> results = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadSequencedEvent(reader));
        }

        return Task.FromResult<IReadOnlyList<SequencedEvent>>(results);
    }

    public Task<long> GetLatestSequenceAsync(string channelId, CancellationToken cancellationToken = default)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM events WHERE channel_id = $channel_id";
        cmd.Parameters.AddWithValue("$channel_id", channelId);

        object? result = cmd.ExecuteScalar();
        long sequence = result is long seq ? seq : 0L;
        return Task.FromResult(sequence);
    }

    public Task EnqueueOutboxAsync(VestaEvent evt, CancellationToken cancellationToken = default)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO outbox (id, channel_id, timestamp, client_id, event_type, payload, parent_id, signature, created_at, status)
            VALUES ($id, $channel_id, $timestamp, $client_id, $event_type, $payload, $parent_id, $signature, $created_at, 'pending')
            """;
        cmd.Parameters.AddWithValue("$id", evt.Id.ToString());
        cmd.Parameters.AddWithValue("$channel_id", evt.ChannelId);
        cmd.Parameters.AddWithValue("$timestamp", evt.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("$client_id", evt.ClientId);
        cmd.Parameters.AddWithValue("$event_type", evt.EventType);
        cmd.Parameters.AddWithValue("$payload", evt.Payload.GetRawText());
        cmd.Parameters.AddWithValue("$parent_id", evt.ParentId.HasValue ? evt.ParentId.Value.ToString() : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$signature", (object?)evt.Signature ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));

        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxEntry>> GetPendingOutboxAsync(CancellationToken cancellationToken = default)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        // Returns both 'pending' (never sent) AND 'sent' (sent but not yet confirmed
        // — e.g. crash or disconnect between SEND and ACK). The server-side AppendAsync
        // is idempotent on event id, so re-flushing a 'sent' entry on reconnect is safe.
        cmd.CommandText = """
            SELECT id, channel_id, timestamp, client_id, event_type, payload, parent_id, signature, created_at, status
            FROM outbox
            WHERE status IN ('pending', 'sent')
            ORDER BY created_at ASC
            """;

        List<OutboxEntry> results = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadOutboxEntry(reader));
        }

        return Task.FromResult<IReadOnlyList<OutboxEntry>>(results);
    }

    public Task MarkOutboxSentAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE outbox SET status = 'sent' WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", eventId.ToString());
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task MarkOutboxConfirmedAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM outbox WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", eventId.ToString());
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, long>> GetChannelPositionsAsync(CancellationToken cancellationToken = default)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT channel_id, MAX(sequence) FROM events GROUP BY channel_id";

        Dictionary<string, long> positions = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string channelId = reader.GetString(0);
            long sequence = reader.GetInt64(1);
            positions[channelId] = sequence;
        }

        return Task.FromResult<IReadOnlyDictionary<string, long>>(positions);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private void InitializeSchema()
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS events (
                id              TEXT PRIMARY KEY,
                channel_id      TEXT NOT NULL,
                sequence        INTEGER NOT NULL,
                timestamp       TEXT NOT NULL,
                client_id       TEXT NOT NULL,
                event_type      TEXT NOT NULL,
                payload         TEXT NOT NULL,
                parent_id       TEXT,
                signature       TEXT,
                received_at     TEXT NOT NULL,
                UNIQUE(channel_id, sequence)
            );

            CREATE INDEX IF NOT EXISTS idx_events_channel_seq ON events(channel_id, sequence);

            CREATE TABLE IF NOT EXISTS outbox (
                id              TEXT PRIMARY KEY,
                channel_id      TEXT NOT NULL,
                timestamp       TEXT NOT NULL,
                client_id       TEXT NOT NULL,
                event_type      TEXT NOT NULL,
                payload         TEXT NOT NULL,
                parent_id       TEXT,
                signature       TEXT,
                created_at      TEXT NOT NULL,
                status          TEXT NOT NULL DEFAULT 'pending'
            );

            CREATE INDEX IF NOT EXISTS idx_outbox_status ON outbox(status, created_at);
            """;
        cmd.ExecuteNonQuery();
    }

    private static void AddEventParameters(SqliteCommand cmd, SequencedEvent sequencedEvent)
    {
        VestaEvent evt = sequencedEvent.Event;
        cmd.Parameters.AddWithValue("$id", evt.Id.ToString());
        cmd.Parameters.AddWithValue("$channel_id", evt.ChannelId);
        cmd.Parameters.AddWithValue("$sequence", sequencedEvent.Sequence);
        cmd.Parameters.AddWithValue("$timestamp", evt.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("$client_id", evt.ClientId);
        cmd.Parameters.AddWithValue("$event_type", evt.EventType);
        cmd.Parameters.AddWithValue("$payload", evt.Payload.GetRawText());
        cmd.Parameters.AddWithValue("$parent_id", evt.ParentId.HasValue ? evt.ParentId.Value.ToString() : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$signature", (object?)evt.Signature ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$received_at", sequencedEvent.ReceivedAt.ToString("O"));
    }

    private static SequencedEvent ReadSequencedEvent(SqliteDataReader reader)
    {
        Guid id = Guid.Parse(reader.GetString(0));
        string channelId = reader.GetString(1);
        long sequence = reader.GetInt64(2);
        DateTimeOffset timestamp = DateTimeOffset.Parse(reader.GetString(3));
        string clientId = reader.GetString(4);
        string eventType = reader.GetString(5);
        JsonElement payload = JsonDocument.Parse(reader.GetString(6)).RootElement.Clone();
        Guid? parentId = reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7));
        string? signature = reader.IsDBNull(8) ? null : reader.GetString(8);
        DateTimeOffset receivedAt = DateTimeOffset.Parse(reader.GetString(9));

        VestaEvent evt = new(id, channelId, timestamp, clientId, eventType, payload, parentId, signature);
        return new SequencedEvent(evt, sequence, receivedAt);
    }

    private static OutboxEntry ReadOutboxEntry(SqliteDataReader reader)
    {
        Guid id = Guid.Parse(reader.GetString(0));
        string channelId = reader.GetString(1);
        DateTimeOffset timestamp = DateTimeOffset.Parse(reader.GetString(2));
        string clientId = reader.GetString(3);
        string eventType = reader.GetString(4);
        JsonElement payload = JsonDocument.Parse(reader.GetString(5)).RootElement.Clone();
        Guid? parentId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6));
        string? signature = reader.IsDBNull(7) ? null : reader.GetString(7);
        DateTimeOffset createdAt = DateTimeOffset.Parse(reader.GetString(8));
        string statusStr = reader.GetString(9);

        OutboxStatus status = statusStr switch
        {
            "sent" => OutboxStatus.Sent,
            "confirmed" => OutboxStatus.Confirmed,
            _ => OutboxStatus.Pending
        };

        VestaEvent evt = new(id, channelId, timestamp, clientId, eventType, payload, parentId, signature);
        return new OutboxEntry(evt, createdAt, status);
    }
}
