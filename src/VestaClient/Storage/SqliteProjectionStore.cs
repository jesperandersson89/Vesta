using Microsoft.Data.Sqlite;
using VestaCore.Projections;

namespace VestaClient.Storage;

/// <summary>
/// SQLite-backed <see cref="IProjectionStore"/>. Snapshots are stored in a single table
/// keyed by <c>(channel_id, projection_id)</c>; the state blob is opaque JSON written
/// by the reducer that owns it.
///
/// Connection is held open for the lifetime of the instance. Safe to share across
/// projections within a single client.
/// </summary>
public sealed class SqliteProjectionStore : IProjectionStore, IDisposable
{
  private readonly SqliteConnection _connection;
  private readonly SemaphoreSlim _writeLock = new(1, 1);

  /// <summary>
  /// Open a SQLite projection store at the given connection string
  /// (e.g. <c>"Data Source=projections.db"</c>).
  /// </summary>
  public SqliteProjectionStore(string connectionString)
  {
    _connection = new SqliteConnection(connectionString);
    _connection.Open();
    InitializeSchema();
  }

  /// <summary>
  /// Convenience factory for an in-memory store (tests).
  /// </summary>
  public static SqliteProjectionStore CreateInMemory()
      => new("Data Source=:memory:");

  private void InitializeSchema()
  {
    using SqliteCommand cmd = _connection.CreateCommand();
    cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS projection_snapshots (
                channel_id    TEXT NOT NULL,
                projection_id TEXT NOT NULL,
                last_sequence INTEGER NOT NULL,
                state_json    TEXT NOT NULL,
                updated_at    TEXT NOT NULL,
                PRIMARY KEY (channel_id, projection_id)
            );
            """;
    cmd.ExecuteNonQuery();
  }

  /// <inheritdoc />
  public async Task SaveAsync(
      string channelId,
      string projectionId,
      ProjectionSnapshot snapshot,
      CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
    ArgumentException.ThrowIfNullOrWhiteSpace(projectionId);
    ArgumentNullException.ThrowIfNull(snapshot);

    await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      using SqliteCommand cmd = _connection.CreateCommand();
      cmd.CommandText = """
                INSERT INTO projection_snapshots (channel_id, projection_id, last_sequence, state_json, updated_at)
                VALUES ($channel_id, $projection_id, $last_sequence, $state_json, $updated_at)
                ON CONFLICT(channel_id, projection_id) DO UPDATE SET
                    last_sequence = excluded.last_sequence,
                    state_json    = excluded.state_json,
                    updated_at    = excluded.updated_at
                """;
      cmd.Parameters.AddWithValue("$channel_id", channelId);
      cmd.Parameters.AddWithValue("$projection_id", projectionId);
      cmd.Parameters.AddWithValue("$last_sequence", snapshot.LastSequence);
      cmd.Parameters.AddWithValue("$state_json", snapshot.StateJson);
      cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
      await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      _writeLock.Release();
    }
  }

  /// <inheritdoc />
  public async Task<ProjectionSnapshot?> LoadAsync(
      string channelId,
      string projectionId,
      CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
    ArgumentException.ThrowIfNullOrWhiteSpace(projectionId);

    using SqliteCommand cmd = _connection.CreateCommand();
    cmd.CommandText = """
            SELECT last_sequence, state_json
            FROM projection_snapshots
            WHERE channel_id = $channel_id AND projection_id = $projection_id
            """;
    cmd.Parameters.AddWithValue("$channel_id", channelId);
    cmd.Parameters.AddWithValue("$projection_id", projectionId);

    using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
    {
      return null;
    }

    long lastSequence = reader.GetInt64(0);
    string stateJson = reader.GetString(1);
    return new ProjectionSnapshot(lastSequence, stateJson);
  }

  /// <inheritdoc />
  public async Task DeleteAsync(
      string channelId,
      string projectionId,
      CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
    ArgumentException.ThrowIfNullOrWhiteSpace(projectionId);

    await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      using SqliteCommand cmd = _connection.CreateCommand();
      cmd.CommandText = """
                DELETE FROM projection_snapshots
                WHERE channel_id = $channel_id AND projection_id = $projection_id
                """;
      cmd.Parameters.AddWithValue("$channel_id", channelId);
      cmd.Parameters.AddWithValue("$projection_id", projectionId);
      await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      _writeLock.Release();
    }
  }

  public void Dispose()
  {
    _writeLock.Dispose();
    _connection.Dispose();
  }
}
