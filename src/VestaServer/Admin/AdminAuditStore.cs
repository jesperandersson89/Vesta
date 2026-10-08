using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace VestaServer.Admin;

internal static class AuditJson
{
  public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}

/// <summary>One recorded admin action.</summary>
public sealed record AdminAuditEntry(
    long Id,
    DateTimeOffset At,
    string AdminPublicKey,
    string Action,
    string? Target,
    JsonElement? Details);

/// <summary>Durable audit trail of mutating admin actions (who did what to which target).</summary>
public interface IAdminAuditStore
{
  /// <summary>Appends an entry. <paramref name="details"/> is serialized as a JSON object when non-null.</summary>
  Task RecordAsync(string adminPublicKey, string action, string? target, object? details, CancellationToken cancellationToken = default);

  /// <summary>Newest-first listing, optionally filtered by exact <paramref name="target"/>.</summary>
  Task<IReadOnlyList<AdminAuditEntry>> ListAsync(int limit, string? target, CancellationToken cancellationToken = default);
}

/// <summary>Bounded in-memory ring buffer for tests and in-memory dev mode.</summary>
public sealed class InMemoryAdminAuditStore(TimeProvider timeProvider) : IAdminAuditStore
{
  private const int Capacity = 1000;
  private readonly object _lock = new();
  private readonly LinkedList<AdminAuditEntry> _entries = new();
  private long _nextId = 1;

  public Task RecordAsync(string adminPublicKey, string action, string? target, object? details, CancellationToken cancellationToken = default)
  {
    JsonElement? element = details is null ? null : JsonSerializer.SerializeToElement(details, AuditJson.Options);
    lock (_lock)
    {
      _entries.AddFirst(new AdminAuditEntry(_nextId++, timeProvider.GetUtcNow(), adminPublicKey, action, target, element));
      while (_entries.Count > Capacity)
        _entries.RemoveLast();
    }
    return Task.CompletedTask;
  }

  public Task<IReadOnlyList<AdminAuditEntry>> ListAsync(int limit, string? target, CancellationToken cancellationToken = default)
  {
    lock (_lock)
    {
      IEnumerable<AdminAuditEntry> query = _entries;
      if (target is not null)
        query = query.Where(e => e.Target == target);
      return Task.FromResult<IReadOnlyList<AdminAuditEntry>>([.. query.Take(limit)]);
    }
  }
}

/// <summary>PostgreSQL implementation over the <c>admin_audit</c> table.</summary>
public sealed class NpgsqlAdminAuditStore(NpgsqlDataSource dataSource) : IAdminAuditStore
{
  public async Task RecordAsync(string adminPublicKey, string action, string? target, object? details, CancellationToken cancellationToken = default)
  {
    const string sql = "INSERT INTO admin_audit (admin_public_key, action, target, details) VALUES ($1, $2, $3, $4)";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = adminPublicKey });
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = action });
    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)target ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
    cmd.Parameters.Add(new NpgsqlParameter
    {
      Value = details is null ? DBNull.Value : JsonSerializer.Serialize(details, AuditJson.Options),
      NpgsqlDbType = NpgsqlDbType.Jsonb,
    });
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  public async Task<IReadOnlyList<AdminAuditEntry>> ListAsync(int limit, string? target, CancellationToken cancellationToken = default)
  {
    string sql = target is null
        ? "SELECT id, at, admin_public_key, action, target, details::text FROM admin_audit ORDER BY id DESC LIMIT $1"
        : "SELECT id, at, admin_public_key, action, target, details::text FROM admin_audit WHERE target = $2 ORDER BY id DESC LIMIT $1";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
    if (target is not null)
      cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = target });

    List<AdminAuditEntry> rows = [];
    await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
      JsonElement? details = reader.IsDBNull(5) ? null : JsonDocument.Parse(reader.GetString(5)).RootElement.Clone();
      rows.Add(new AdminAuditEntry(
          reader.GetInt64(0),
          reader.GetFieldValue<DateTimeOffset>(1),
          reader.GetString(2),
          reader.GetString(3),
          reader.IsDBNull(4) ? null : reader.GetString(4),
          details));
    }
    return rows;
  }
}
