using Npgsql;

namespace VestaServer.Storage;

/// <summary>Metric names used in <see cref="AppAlert.Metric"/>.</summary>
public static class AppAlertMetrics
{
  public const string Messages = "messages";
  public const string Storage = "storage";
  public const string Channels = "channels";
}

/// <summary>Quota-threshold alert configuration (section <c>AppAlerts</c>). Evaluated by <see cref="AppQuotaPrunerService"/>.</summary>
public sealed class AppAlertOptions
{
  /// <summary>Percent-of-quota thresholds that raise an alert when crossed. Default 80 and 100.</summary>
  public int[] ThresholdsPercent { get; set; } = [80, 100];
}

/// <summary>A persisted alert row. <see cref="ResolvedAt"/> is set once usage drops back under the threshold.</summary>
public sealed record AppAlert(
    long Id,
    string AppId,
    string Metric,
    int ThresholdPercent,
    long Observed,
    long LimitValue,
    DateOnly? PeriodStart,
    DateTimeOffset RaisedAt,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? AcknowledgedAt);

/// <summary>A threshold currently exceeded for one app + metric.</summary>
public sealed record AppAlertCrossing(string Metric, int ThresholdPercent, long Observed, long Limit, DateOnly? PeriodStart);

/// <summary>Pure threshold evaluation; no I/O.</summary>
public static class AppAlertEvaluator
{
  /// <summary>
  /// Returns one crossing per (metric, threshold) where <c>observed / limit</c> is at or above the threshold.
  /// Metrics whose quota is unset (or ≤ 0) or whose observation is <c>null</c> (unmeasured) are skipped.
  /// </summary>
  public static IReadOnlyList<AppAlertCrossing> Evaluate(
      AppQuotas quotas,
      long? messages,
      DateOnly messagesPeriod,
      long? storageBytes,
      long? channelCount,
      IReadOnlyList<int> thresholds)
  {
    List<AppAlertCrossing> crossings = [];
    Add(crossings, AppAlertMetrics.Messages, quotas.MaxMessagesPerMonth, messages, messagesPeriod, thresholds);
    Add(crossings, AppAlertMetrics.Storage, quotas.TotalStorageBytes, storageBytes, null, thresholds);
    Add(crossings, AppAlertMetrics.Channels, quotas.MaxChannels, channelCount, null, thresholds);
    return crossings;
  }

  private static void Add(
      List<AppAlertCrossing> crossings,
      string metric,
      long? limit,
      long? observed,
      DateOnly? period,
      IReadOnlyList<int> thresholds)
  {
    if (limit is not long max || max <= 0 || observed is not long value)
      return;

    foreach (int threshold in thresholds)
    {
      if ((decimal)value * 100m >= (decimal)max * threshold)
        crossings.Add(new AppAlertCrossing(metric, threshold, value, max, period));
    }
  }
}

/// <summary>Durable store for quota alerts.</summary>
public interface IAppAlertStore
{
  /// <summary>Newest-first. <paramref name="appId"/> filters to one app; <paramref name="activeOnly"/> hides resolved alerts.</summary>
  Task<IReadOnlyList<AppAlert>> ListAsync(string? appId, bool activeOnly, CancellationToken cancellationToken = default);

  /// <summary>Open (unresolved) alert counts keyed by app id.</summary>
  Task<IReadOnlyDictionary<string, int>> CountActiveByAppAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Reconciles the app's open alerts with <paramref name="crossings"/>: raises missing ones, refreshes
  /// the observed value of existing ones, and resolves open alerts that no longer apply (including
  /// message alerts raised for a previous usage period).
  /// </summary>
  Task SyncAsync(string appId, IReadOnlyList<AppAlertCrossing> crossings, CancellationToken cancellationToken = default);

  /// <summary>Marks an alert acknowledged (it stays open until usage drops). Returns false if it does not exist.</summary>
  Task<bool> AcknowledgeAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>In-memory implementation for tests and in-memory dev mode.</summary>
public sealed class InMemoryAppAlertStore(TimeProvider timeProvider) : IAppAlertStore
{
  private readonly object _lock = new();
  private readonly List<AppAlert> _alerts = [];
  private long _nextId = 1;

  public Task<IReadOnlyList<AppAlert>> ListAsync(string? appId, bool activeOnly, CancellationToken cancellationToken = default)
  {
    lock (_lock)
    {
      IEnumerable<AppAlert> query = _alerts;
      if (appId is not null) query = query.Where(a => a.AppId == appId);
      if (activeOnly) query = query.Where(a => a.ResolvedAt is null);
      return Task.FromResult<IReadOnlyList<AppAlert>>([.. query.OrderByDescending(a => a.Id)]);
    }
  }

  public Task<IReadOnlyDictionary<string, int>> CountActiveByAppAsync(CancellationToken cancellationToken = default)
  {
    lock (_lock)
    {
      Dictionary<string, int> counts = _alerts
          .Where(a => a.ResolvedAt is null)
          .GroupBy(a => a.AppId)
          .ToDictionary(g => g.Key, g => g.Count());
      return Task.FromResult<IReadOnlyDictionary<string, int>>(counts);
    }
  }

  public Task SyncAsync(string appId, IReadOnlyList<AppAlertCrossing> crossings, CancellationToken cancellationToken = default)
  {
    DateTimeOffset now = timeProvider.GetUtcNow();
    lock (_lock)
    {
      HashSet<long> kept = [];
      foreach (AppAlertCrossing crossing in crossings)
      {
        int index = _alerts.FindIndex(a =>
            a.AppId == appId && a.ResolvedAt is null &&
            a.Metric == crossing.Metric && a.ThresholdPercent == crossing.ThresholdPercent &&
            a.PeriodStart == crossing.PeriodStart);
        if (index >= 0)
        {
          _alerts[index] = _alerts[index] with { Observed = crossing.Observed, LimitValue = crossing.Limit };
          kept.Add(_alerts[index].Id);
        }
        else
        {
          AppAlert created = new(_nextId++, appId, crossing.Metric, crossing.ThresholdPercent,
              crossing.Observed, crossing.Limit, crossing.PeriodStart, now, null, null);
          _alerts.Add(created);
          kept.Add(created.Id);
        }
      }

      for (int i = 0; i < _alerts.Count; i++)
      {
        AppAlert alert = _alerts[i];
        if (alert.AppId == appId && alert.ResolvedAt is null && !kept.Contains(alert.Id))
          _alerts[i] = alert with { ResolvedAt = now };
      }
    }
    return Task.CompletedTask;
  }

  public Task<bool> AcknowledgeAsync(long id, CancellationToken cancellationToken = default)
  {
    lock (_lock)
    {
      int index = _alerts.FindIndex(a => a.Id == id);
      if (index < 0) return Task.FromResult(false);
      if (_alerts[index].AcknowledgedAt is null)
        _alerts[index] = _alerts[index] with { AcknowledgedAt = timeProvider.GetUtcNow() };
      return Task.FromResult(true);
    }
  }
}

/// <summary>PostgreSQL implementation over <c>app_alerts</c>.</summary>
public sealed class NpgsqlAppAlertStore(NpgsqlDataSource dataSource) : IAppAlertStore
{
  private const string SelectColumns =
      "id, app_id, metric, threshold_pct, observed, limit_value, period_start, raised_at, resolved_at, acknowledged_at";

  public async Task<IReadOnlyList<AppAlert>> ListAsync(string? appId, bool activeOnly, CancellationToken cancellationToken = default)
  {
    List<string> predicates = [];
    if (appId is not null) predicates.Add("app_id = $1");
    if (activeOnly) predicates.Add("resolved_at IS NULL");
    string where = predicates.Count == 0 ? "" : " WHERE " + string.Join(" AND ", predicates);

    await using NpgsqlCommand cmd = dataSource.CreateCommand($"SELECT {SelectColumns} FROM app_alerts{where} ORDER BY id DESC");
    if (appId is not null)
      cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });

    List<AppAlert> rows = [];
    await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
      rows.Add(Read(reader));
    return rows;
  }

  public async Task<IReadOnlyDictionary<string, int>> CountActiveByAppAsync(CancellationToken cancellationToken = default)
  {
    const string sql = "SELECT app_id, COUNT(*)::int FROM app_alerts WHERE resolved_at IS NULL GROUP BY app_id";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    Dictionary<string, int> counts = [];
    await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
      counts[reader.GetString(0)] = reader.GetInt32(1);
    return counts;
  }

  public async Task SyncAsync(string appId, IReadOnlyList<AppAlertCrossing> crossings, CancellationToken cancellationToken = default)
  {
    await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
    await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(cancellationToken);

    // Reconcile open alerts with the new crossing set (stale period or dropped under threshold).
    List<(long Id, string Metric, int Threshold, DateOnly? Period)> open = [];
    await using (NpgsqlCommand select = new(
        "SELECT id, metric, threshold_pct, period_start FROM app_alerts WHERE app_id = $1 AND resolved_at IS NULL FOR UPDATE",
        connection, tx))
    {
      select.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
      await using NpgsqlDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken))
        open.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateOnly>(3)));
    }

    // Resolve stale alerts first: the partial unique index allows one open row per (app, metric, threshold),
    // so a period rollover must close the old row before the new one is inserted.
    HashSet<long> matchedIds = [];
    foreach (AppAlertCrossing crossing in crossings)
    {
      (long Id, string Metric, int Threshold, DateOnly? Period) match = open.FirstOrDefault(o =>
          o.Metric == crossing.Metric && o.Threshold == crossing.ThresholdPercent && o.Period == crossing.PeriodStart);
      if (match.Id != 0)
        matchedIds.Add(match.Id);
    }

    foreach ((long id, _, _, _) in open)
    {
      if (matchedIds.Contains(id)) continue;
      await using NpgsqlCommand resolve = new("UPDATE app_alerts SET resolved_at = now() WHERE id = $1", connection, tx);
      resolve.Parameters.Add(new NpgsqlParameter<long> { TypedValue = id });
      await resolve.ExecuteNonQueryAsync(cancellationToken);
    }

    foreach (AppAlertCrossing crossing in crossings)
    {
      (long Id, string Metric, int Threshold, DateOnly? Period) match = open.FirstOrDefault(o =>
          o.Metric == crossing.Metric && o.Threshold == crossing.ThresholdPercent && o.Period == crossing.PeriodStart);
      if (match.Id != 0)
      {
        await using NpgsqlCommand update = new(
            "UPDATE app_alerts SET observed = $2, limit_value = $3 WHERE id = $1", connection, tx);
        update.Parameters.Add(new NpgsqlParameter<long> { TypedValue = match.Id });
        update.Parameters.Add(new NpgsqlParameter<long> { TypedValue = crossing.Observed });
        update.Parameters.Add(new NpgsqlParameter<long> { TypedValue = crossing.Limit });
        await update.ExecuteNonQueryAsync(cancellationToken);
      }
      else
      {
        await using NpgsqlCommand insert = new("""
            INSERT INTO app_alerts (app_id, metric, threshold_pct, observed, limit_value, period_start)
            VALUES ($1, $2, $3, $4, $5, $6)
            """, connection, tx);
        insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
        insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = crossing.Metric });
        insert.Parameters.Add(new NpgsqlParameter<int> { TypedValue = crossing.ThresholdPercent });
        insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = crossing.Observed });
        insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = crossing.Limit });
        insert.Parameters.Add(new NpgsqlParameter
        {
          Value = crossing.PeriodStart is DateOnly d ? d : DBNull.Value,
          NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date,
        });
        await insert.ExecuteNonQueryAsync(cancellationToken);
      }
    }

    await tx.CommitAsync(cancellationToken);
  }

  public async Task<bool> AcknowledgeAsync(long id, CancellationToken cancellationToken = default)
  {
    const string sql = "UPDATE app_alerts SET acknowledged_at = COALESCE(acknowledged_at, now()) WHERE id = $1";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = id });
    int rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
    return rows > 0;
  }

  private static AppAlert Read(NpgsqlDataReader reader) => new(
      reader.GetInt64(0),
      reader.GetString(1),
      reader.GetString(2),
      reader.GetInt32(3),
      reader.GetInt64(4),
      reader.GetInt64(5),
      reader.IsDBNull(6) ? null : reader.GetFieldValue<DateOnly>(6),
      reader.GetFieldValue<DateTimeOffset>(7),
      reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
      reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9));
}
