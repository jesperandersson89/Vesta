using Npgsql;

namespace VestaServer.Storage;

/// <summary>
/// PostgreSQL implementation of <see cref="IChannelAccessStore"/> via raw Npgsql.
/// Reads <c>channels.visibility</c> and consults <c>channel_access</c> for private channels.
/// </summary>
public sealed class NpgsqlChannelAccessStore(NpgsqlDataSource dataSource) : IChannelAccessStore
{
  public async Task<ChannelVisibility?> GetVisibilityAsync(string channelId, CancellationToken cancellationToken = default)
  {
    const string sql = "SELECT visibility FROM channels WHERE id = $1";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channelId });
    object? result = await cmd.ExecuteScalarAsync(cancellationToken);
    return result switch
    {
      null => null,
      string s when string.Equals(s, "private", StringComparison.OrdinalIgnoreCase) => ChannelVisibility.Private,
      _ => ChannelVisibility.Public,
    };
  }

  public async Task<bool> CanAccessAsync(string channelId, string? clientId, CancellationToken cancellationToken = default)
  {
    ChannelVisibility? visibility = await GetVisibilityAsync(channelId, cancellationToken);
    if (visibility is null) return true;                       // implicit-create on first append
    if (visibility == ChannelVisibility.Public) return true;
    if (clientId is null) return false;

    const string sql = "SELECT 1 FROM channel_access WHERE channel_id = $1 AND client_id = $2 LIMIT 1";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channelId });
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = clientId });
    object? result = await cmd.ExecuteScalarAsync(cancellationToken);
    return result is not null;
  }

  public async Task<bool> IsAdminAsync(string channelId, string clientId, CancellationToken cancellationToken = default)
  {
    const string sql = "SELECT role FROM channel_access WHERE channel_id = $1 AND client_id = $2";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channelId });
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = clientId });
    object? result = await cmd.ExecuteScalarAsync(cancellationToken);
    return result is string role && role == "admin";
  }

  public async Task CreateChannelAsync(
      string channelId,
      ChannelVisibility visibility,
      string adminClientId,
      IReadOnlyList<string> memberClientIds,
      CancellationToken cancellationToken = default)
  {
    await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
    await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(cancellationToken);

    string visibilityStr = visibility == ChannelVisibility.Private ? "private" : "public";

    const string insertChannelSql = """
            INSERT INTO channels (id, visibility) VALUES ($1, $2)
            """;
    await using (NpgsqlCommand cmd = new(insertChannelSql, connection, tx))
    {
      cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channelId });
      cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = visibilityStr });
      try
      {
        await cmd.ExecuteNonQueryAsync(cancellationToken);
      }
      catch (PostgresException ex) when (ex.SqlState == "23505") // unique_violation
      {
        throw new ChannelAlreadyExistsException(channelId);
      }
    }

    // Insert admin + member rows.
    HashSet<string> seen = [];
    await InsertAccessAsync(connection, tx, channelId, adminClientId, "admin", cancellationToken);
    seen.Add(adminClientId);

    foreach (string member in memberClientIds)
    {
      if (!seen.Add(member)) continue;
      await InsertAccessAsync(connection, tx, channelId, member, "member", cancellationToken);
    }

    await tx.CommitAsync(cancellationToken);
  }

  public async Task GrantAccessAsync(
      string channelId,
      string clientId,
      string role,
      CancellationToken cancellationToken = default)
  {
    const string sql = """
            INSERT INTO channel_access (channel_id, client_id, role)
            VALUES ($1, $2, $3)
            ON CONFLICT (channel_id, client_id) DO UPDATE SET role = EXCLUDED.role
            """;
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channelId });
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = clientId });
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = role });
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  public async Task<int> CountChannelsByAppAsync(string appId, CancellationToken cancellationToken = default)
  {
    // Matches the app slug exactly or any channel starting with "{appId}/".
    const string sql = "SELECT COUNT(*) FROM channels WHERE id = $1 OR id LIKE $2";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId + "/%" });
    object? result = await cmd.ExecuteScalarAsync(cancellationToken);
    return result is long l ? (int)l : Convert.ToInt32(result);
  }

  public async Task<bool> IsDeletedAsync(string channelId, CancellationToken cancellationToken = default)
  {
    const string sql = "SELECT deleted_at FROM channels WHERE id = $1";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channelId });
    object? result = await cmd.ExecuteScalarAsync(cancellationToken);
    return result is not null && result is not DBNull;
  }

  public async Task<bool> DeleteChannelAsync(string channelId, CancellationToken cancellationToken = default)
  {
    // Idempotent: COALESCE keeps the original deleted_at if already set.
    const string sql = """
            UPDATE channels
               SET deleted_at = COALESCE(deleted_at, now())
             WHERE id = $1
            """;
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channelId });
    int rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
    return rows > 0;
  }

  // The Npgsql event store creates the channels row inside the same transaction
  // as the event insert, so this hook is a no-op for the SQL backend.
  public Task RecordImplicitChannelAsync(string channelId, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

  private static async Task InsertAccessAsync(
      NpgsqlConnection connection,
      NpgsqlTransaction tx,
      string channelId,
      string clientId,
      string role,
      CancellationToken cancellationToken)
  {
    const string sql = """
            INSERT INTO channel_access (channel_id, client_id, role)
            VALUES ($1, $2, $3)
            ON CONFLICT (channel_id, client_id) DO NOTHING
            """;
    await using NpgsqlCommand cmd = new(sql, connection, tx);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channelId });
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = clientId });
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = role });
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }
}
