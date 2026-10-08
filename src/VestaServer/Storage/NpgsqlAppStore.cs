using Npgsql;

namespace VestaServer.Storage;

/// <summary>
/// PostgreSQL implementation of <see cref="IAppStore"/> via raw Npgsql.
/// </summary>
public sealed class NpgsqlAppStore(NpgsqlDataSource dataSource) : IAppStore
{
  private const string SelectColumns = """
            id, owner_client_id, created_at,
            max_payload_bytes, publish_rate_per_minute, max_channels,
            max_events_per_channel, retention_days, total_storage_bytes, discoverable,
            max_messages_per_month, deleted_at, paused_at, throttle_per_minute
            """;

  public async Task<AppInfo?> GetAsync(string appId, CancellationToken cancellationToken = default)
  {
    const string sql = $"SELECT {SelectColumns} FROM apps WHERE id = $1";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
      return null;
    return ReadApp(reader);
  }

  public async Task<bool> ExistsAsync(string appId, CancellationToken cancellationToken = default)
  {
    const string sql = "SELECT 1 FROM apps WHERE id = $1 AND deleted_at IS NULL LIMIT 1";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    object? result = await cmd.ExecuteScalarAsync(cancellationToken);
    return result is not null;
  }

  public async Task RegisterAsync(string appId, string ownerClientId, bool discoverable = false, CancellationToken cancellationToken = default)
  {
    const string sql = "INSERT INTO apps (id, owner_client_id, discoverable) VALUES ($1, $2, $3)";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = ownerClientId });
    cmd.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = discoverable });
    try
    {
      await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
    catch (PostgresException ex) when (ex.SqlState == "23505") // unique_violation
    {
      throw new AppAlreadyRegisteredException(appId);
    }
  }

  public async Task<bool> SetDiscoverableAsync(string appId, bool discoverable, CancellationToken cancellationToken = default)
  {
    const string sql = "UPDATE apps SET discoverable = $2 WHERE id = $1";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    cmd.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = discoverable });
    int rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
    return rows > 0;
  }

  public async Task<bool> SetOwnerAsync(string appId, string ownerClientId, CancellationToken cancellationToken = default)
  {
    const string sql = "UPDATE apps SET owner_client_id = $2 WHERE id = $1";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = ownerClientId });
    int rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
    return rows > 0;
  }

  public async Task<bool> SetQuotasAsync(string appId, AppQuotas quotas, CancellationToken cancellationToken = default)
  {
    const string sql = """
            UPDATE apps SET
                max_payload_bytes = $2,
                publish_rate_per_minute = $3,
                max_channels = $4,
                max_events_per_channel = $5,
                retention_days = $6,
                total_storage_bytes = $7,
                max_messages_per_month = $8
            WHERE id = $1
            """;
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)quotas.MaxPayloadBytes ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)quotas.PublishRatePerMinute ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)quotas.MaxChannels ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)quotas.MaxEventsPerChannel ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)quotas.RetentionDays ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)quotas.TotalStorageBytes ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)quotas.MaxMessagesPerMonth ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
    int rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
    return rows > 0;
  }

  public Task<IReadOnlyList<AppInfo>> ListAsync(CancellationToken cancellationToken = default)
      => ListCoreAsync(includeDeleted: false, cancellationToken);

  public Task<IReadOnlyList<AppInfo>> ListAllAsync(CancellationToken cancellationToken = default)
      => ListCoreAsync(includeDeleted: true, cancellationToken);

  public async Task<DateTimeOffset?> DeleteAsync(string appId, CancellationToken cancellationToken = default)
  {
    // COALESCE keeps the original tombstone on repeat calls; RETURNING hands back the stored
    // (microsecond-precise) value so the channel cascade can match it exactly.
    const string sql = "UPDATE apps SET deleted_at = COALESCE(deleted_at, now()) WHERE id = $1 RETURNING deleted_at";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
      return null;
    return reader.GetFieldValue<DateTimeOffset>(0);
  }

  public async Task<bool> RestoreAsync(string appId, CancellationToken cancellationToken = default)
  {
    const string sql = "UPDATE apps SET deleted_at = NULL WHERE id = $1 AND deleted_at IS NOT NULL";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    int rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
    return rows > 0;
  }

  public async Task<bool> SetPausedAsync(string appId, bool paused, CancellationToken cancellationToken = default)
  {
    // COALESCE keeps the original pause time when pausing an already-paused app.
    const string sql = "UPDATE apps SET paused_at = CASE WHEN $2 THEN COALESCE(paused_at, now()) ELSE NULL END WHERE id = $1";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    cmd.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = paused });
    int rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
    return rows > 0;
  }

  public async Task<bool> SetThrottleAsync(string appId, int? perMinute, CancellationToken cancellationToken = default)
  {
    const string sql = "UPDATE apps SET throttle_per_minute = $2 WHERE id = $1";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)perMinute ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
    int rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
    return rows > 0;
  }

  private async Task<IReadOnlyList<AppInfo>> ListCoreAsync(bool includeDeleted, CancellationToken cancellationToken)
  {
    string sql = includeDeleted
        ? $"SELECT {SelectColumns} FROM apps"
        : $"SELECT {SelectColumns} FROM apps WHERE deleted_at IS NULL";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
    List<AppInfo> apps = [];
    while (await reader.ReadAsync(cancellationToken))
      apps.Add(ReadApp(reader));
    return apps;
  }

  private static AppInfo ReadApp(NpgsqlDataReader reader)
  {
    AppQuotas quotas = new(
        MaxPayloadBytes: reader.IsDBNull(3) ? null : reader.GetInt32(3),
        PublishRatePerMinute: reader.IsDBNull(4) ? null : reader.GetInt32(4),
        MaxChannels: reader.IsDBNull(5) ? null : reader.GetInt32(5),
        MaxEventsPerChannel: reader.IsDBNull(6) ? null : reader.GetInt32(6),
        RetentionDays: reader.IsDBNull(7) ? null : reader.GetInt32(7),
        TotalStorageBytes: reader.IsDBNull(8) ? null : reader.GetInt64(8),
        MaxMessagesPerMonth: reader.IsDBNull(10) ? null : reader.GetInt64(10));
    return new AppInfo(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetFieldValue<DateTimeOffset>(2),
        quotas,
        reader.GetBoolean(9),
        reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
        reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
        reader.IsDBNull(13) ? null : reader.GetInt32(13));
  }
}
