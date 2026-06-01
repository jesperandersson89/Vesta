using Npgsql;

namespace VestaServer.Storage;

/// <summary>
/// PostgreSQL implementation of <see cref="IAppStore"/> via raw Npgsql.
/// </summary>
public sealed class NpgsqlAppStore(NpgsqlDataSource dataSource) : IAppStore
{
  public async Task<AppInfo?> GetAsync(string appId, CancellationToken cancellationToken = default)
  {
    const string sql = """
            SELECT id, owner_client_id, created_at,
                   max_payload_bytes, publish_rate_per_minute, max_channels,
                   max_events_per_channel, retention_days, total_storage_bytes
            FROM apps WHERE id = $1
            """;
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
      return null;
    AppQuotas quotas = new(
        MaxPayloadBytes: reader.IsDBNull(3) ? null : reader.GetInt32(3),
        PublishRatePerMinute: reader.IsDBNull(4) ? null : reader.GetInt32(4),
        MaxChannels: reader.IsDBNull(5) ? null : reader.GetInt32(5),
        MaxEventsPerChannel: reader.IsDBNull(6) ? null : reader.GetInt32(6),
        RetentionDays: reader.IsDBNull(7) ? null : reader.GetInt32(7),
        TotalStorageBytes: reader.IsDBNull(8) ? null : reader.GetInt64(8));
    return new AppInfo(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetFieldValue<DateTimeOffset>(2),
        quotas);
  }

  public async Task<bool> ExistsAsync(string appId, CancellationToken cancellationToken = default)
  {
    const string sql = "SELECT 1 FROM apps WHERE id = $1 LIMIT 1";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    object? result = await cmd.ExecuteScalarAsync(cancellationToken);
    return result is not null;
  }

  public async Task RegisterAsync(string appId, string ownerClientId, CancellationToken cancellationToken = default)
  {
    const string sql = "INSERT INTO apps (id, owner_client_id) VALUES ($1, $2)";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = ownerClientId });
    try
    {
      await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
    catch (PostgresException ex) when (ex.SqlState == "23505") // unique_violation
    {
      throw new AppAlreadyRegisteredException(appId);
    }
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
                total_storage_bytes = $7
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
    int rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
    return rows > 0;
  }
}
