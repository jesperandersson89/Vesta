using Microsoft.Extensions.Options;
using Npgsql;

namespace VestaServer.Storage;

/// <summary>
/// Configuration for the per-app quota pruner background job.
/// Sweeps registered apps and enforces <c>retention_days</c> + <c>max_events_per_channel</c>
/// by deleting events from the <c>events</c> table.
/// </summary>
public sealed class AppQuotaPrunerOptions
{
  /// <summary>
  /// When false, the pruner never runs (default false so in-memory / test hosts
  /// don't spin up a postgres-only worker). Enabled in production via
  /// <c>AppQuotaPruner:Enabled=true</c>.
  /// </summary>
  public bool Enabled { get; set; }

  /// <summary>How often to sweep. Defaults to 5 minutes.</summary>
  public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Periodically deletes events that exceed per-app quotas:
/// <list type="bullet">
///   <item><c>retention_days</c> — deletes events from any channel in the app namespace whose <c>received_at</c> is older than the cutoff.</item>
///   <item><c>max_events_per_channel</c> — for each channel under the app, keeps only the most recent N events (by <c>sequence</c>).</item>
/// </list>
/// Apps with neither quota set are skipped. PostgreSQL-only; in-memory mode does not prune.
/// </summary>
public sealed class AppQuotaPrunerService(
    NpgsqlDataSource dataSource,
    IAppStore appStore,
    IAppStorageAccountant accountant,
    IOptions<AppQuotaPrunerOptions> options,
    ILogger<AppQuotaPrunerService> logger) : BackgroundService
{
  private readonly AppQuotaPrunerOptions _options = options.Value;

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    if (!_options.Enabled)
    {
      logger.LogInformation("App quota pruner is disabled (set AppQuotaPruner:Enabled=true to enable).");
      return;
    }

    logger.LogInformation("App quota pruner enabled; sweeping every {Interval}.", _options.Interval);

    using PeriodicTimer timer = new(_options.Interval);

    await SweepOnceAsync(stoppingToken);

    while (await timer.WaitForNextTickAsync(stoppingToken))
    {
      try
      {
        await SweepOnceAsync(stoppingToken);
      }
      catch (OperationCanceledException)
      {
        break;
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "App quota pruner sweep failed; will retry on next tick.");
      }
    }
  }

  /// <summary>
  /// Runs a single sweep pass. Exposed as <c>public</c> primarily for tests
  /// (the hosted service drives it on its own timer).
  /// </summary>
  public async Task SweepOnceAsync(CancellationToken cancellationToken)
  {
    IReadOnlyList<AppInfo> apps = await appStore.ListAsync(cancellationToken);
    foreach (AppInfo app in apps)
    {
      if (app.Quotas.RetentionDays is int days && days > 0)
      {
        int deleted = await PruneByRetentionAsync(app.Id, days, cancellationToken);
        if (deleted > 0)
          logger.LogInformation("Pruned {Count} event(s) for app '{App}' (retention_days = {Days}).", deleted, app.Id, days);
      }

      if (app.Quotas.MaxEventsPerChannel is int max && max > 0)
      {
        int deleted = await PruneByMaxPerChannelAsync(app.Id, max, cancellationToken);
        if (deleted > 0)
          logger.LogInformation("Pruned {Count} event(s) for app '{App}' (max_events_per_channel = {Max}).", deleted, app.Id, max);
      }

      // Refresh the cached storage-bytes total for every app that has a quota,
      // so EnforcePublishQuotas can check it on the hot path without a SQL aggregate.
      if (app.Quotas.TotalStorageBytes is not null)
      {
        long bytes = await MeasureStorageBytesAsync(app.Id, cancellationToken);
        accountant.Set(app.Id, bytes);
      }
    }
  }

  /// <summary>
  /// Sums <c>pg_column_size(payload) + pg_column_size(metadata)</c> across every
  /// event in the app namespace. Approximates wire bytes; ignores per-row overhead.
  /// </summary>
  private async Task<long> MeasureStorageBytesAsync(string appId, CancellationToken cancellationToken)
  {
    const string sql = """
            SELECT COALESCE(SUM(pg_column_size(payload))::bigint, 0)
            FROM events
            WHERE channel_id = $1 OR channel_id LIKE $2
            """;
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId + "/%" });
    object? result = await cmd.ExecuteScalarAsync(cancellationToken);
    return result is long bytes ? bytes : 0L;
  }

  /// <summary>
  /// Deletes events for the app whose <c>received_at</c> is older than now - <paramref name="days"/>.
  /// Matches the app namespace via <c>channel_id = appId OR channel_id LIKE 'appId/%'</c>.
  /// </summary>
  private async Task<int> PruneByRetentionAsync(string appId, int days, CancellationToken cancellationToken)
  {
    const string sql = """
            DELETE FROM events
            WHERE (channel_id = $1 OR channel_id LIKE $2)
              AND received_at < now() - make_interval(days => $3)
            """;
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId + "/%" });
    cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = days });
    return await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  /// <summary>
  /// For each channel under the app, keeps only the most recent <paramref name="max"/>
  /// events (ordered by <c>sequence</c> DESC) and deletes the rest.
  /// Single SQL via <c>ROW_NUMBER() OVER (PARTITION BY channel_id ...)</c>.
  /// </summary>
  private async Task<int> PruneByMaxPerChannelAsync(string appId, int max, CancellationToken cancellationToken)
  {
    const string sql = """
            DELETE FROM events WHERE id IN (
              SELECT id FROM (
                SELECT id, ROW_NUMBER() OVER (PARTITION BY channel_id ORDER BY sequence DESC) AS rn
                FROM events
                WHERE channel_id = $1 OR channel_id LIKE $2
              ) ranked
              WHERE rn > $3
            )
            """;
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId + "/%" });
    cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = max });
    return await cmd.ExecuteNonQueryAsync(cancellationToken);
  }
}
