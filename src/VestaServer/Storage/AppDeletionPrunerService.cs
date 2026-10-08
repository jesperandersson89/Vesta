using Microsoft.Extensions.Options;
using Npgsql;
using VestaServer.Admin;

namespace VestaServer.Storage;

/// <summary>Configuration for the app hard-delete pruner (section <c>AppDeletionPruner</c>).</summary>
public sealed class AppDeletionPrunerOptions
{
  /// <summary>When false the pruner never runs. Enabled in production via <c>AppDeletionPruner:Enabled=true</c>.</summary>
  public bool Enabled { get; set; }

  /// <summary>How often to sweep. Defaults to 5 minutes.</summary>
  public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

  /// <summary>
  /// Grace period between soft-delete (<c>apps.deleted_at</c>) and irreversible purge. Defaults to
  /// 7 days so an operator can restore an app deleted by mistake.
  /// </summary>
  public TimeSpan GracePeriod { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
/// Background sweep that irreversibly purges apps soft-deleted via the admin API once their grace
/// period elapses: events, channels (+ access, sequences, client positions), usage rows, alerts and
/// the <c>apps</c> row, in one transaction per app. The id becomes registrable again afterwards.
/// PostgreSQL-only.
/// </summary>
public sealed class AppDeletionPrunerService(
    NpgsqlDataSource dataSource,
    IAppStorageAccountant storageAccountant,
    IAppUsageAccountant usageAccountant,
    IOptions<AppDeletionPrunerOptions> options,
    ILogger<AppDeletionPrunerService> logger,
    IAdminAuditStore? auditStore = null) : BackgroundService
{
  private readonly AppDeletionPrunerOptions _options = options.Value;

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    if (!_options.Enabled)
    {
      logger.LogInformation("App deletion pruner is disabled (set AppDeletionPruner:Enabled=true to enable).");
      return;
    }

    logger.LogInformation(
        "App deletion pruner enabled; sweeping every {Interval}, grace period {Grace}.",
        _options.Interval, _options.GracePeriod);

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
        logger.LogError(ex, "App deletion pruner sweep failed; will retry on next tick.");
      }
    }
  }

  /// <summary>Runs a single sweep pass (public for tests). Returns the number of apps purged.</summary>
  public async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
  {
    IReadOnlyList<string> eligible = await ListEligibleAsync(cancellationToken);
    int purged = 0;
    foreach (string appId in eligible)
    {
      int eventsDeleted = await PurgeAsync(appId, cancellationToken);
      if (eventsDeleted < 0)
        continue;

      storageAccountant.Remove(appId);
      usageAccountant.Remove(appId);
      purged++;
      logger.LogInformation("Purged app '{App}' ({Count} event(s) deleted).", appId, eventsDeleted);

      if (auditStore is not null)
        await auditStore.RecordAsync("system", "app.purge", appId, new { events = eventsDeleted }, cancellationToken);
    }
    return purged;
  }

  private async Task<IReadOnlyList<string>> ListEligibleAsync(CancellationToken cancellationToken)
  {
    const string sql = """
            SELECT id FROM apps
            WHERE deleted_at IS NOT NULL
              AND deleted_at < now() - make_interval(secs => $1)
            """;
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<double> { TypedValue = _options.GracePeriod.TotalSeconds });
    List<string> ids = [];
    await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
      ids.Add(reader.GetString(0));
    return ids;
  }

  /// <summary>Returns the number of events deleted, or -1 if the app was restored concurrently and nothing was purged.</summary>
  private async Task<int> PurgeAsync(string appId, CancellationToken cancellationToken)
  {
    await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
    await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(cancellationToken);

    // Lock the row and re-check the tombstone so a concurrent restore wins.
    await using (NpgsqlCommand lockCmd = new(
        "SELECT 1 FROM apps WHERE id = $1 AND deleted_at IS NOT NULL FOR UPDATE", connection, tx))
    {
      lockCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
      if (await lockCmd.ExecuteScalarAsync(cancellationToken) is null)
        return -1;
    }

    int events = await RunAsync(connection, tx, "DELETE FROM events WHERE channel_id = $1 OR channel_id LIKE $2", appId, true, cancellationToken);
    await RunAsync(connection, tx, "DELETE FROM client_positions WHERE channel_id = $1 OR channel_id LIKE $2", appId, true, cancellationToken);
    await RunAsync(connection, tx, "DELETE FROM channel_access WHERE channel_id = $1 OR channel_id LIKE $2", appId, true, cancellationToken);
    await RunAsync(connection, tx, "DELETE FROM channel_sequences WHERE channel_id = $1 OR channel_id LIKE $2", appId, true, cancellationToken);
    await RunAsync(connection, tx, "DELETE FROM channels WHERE id = $1 OR id LIKE $2", appId, true, cancellationToken);
    await RunAsync(connection, tx, "DELETE FROM app_usage WHERE app_id = $1", appId, false, cancellationToken);
    await RunAsync(connection, tx, "DELETE FROM app_alerts WHERE app_id = $1", appId, false, cancellationToken);
    await RunAsync(connection, tx, "DELETE FROM apps WHERE id = $1", appId, false, cancellationToken);

    await tx.CommitAsync(cancellationToken);
    return events;
  }

  private static async Task<int> RunAsync(
      NpgsqlConnection connection,
      NpgsqlTransaction tx,
      string sql,
      string appId,
      bool withPrefix,
      CancellationToken cancellationToken)
  {
    await using NpgsqlCommand cmd = new(sql, connection, tx);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    if (withPrefix)
      cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId + "/%" });
    return await cmd.ExecuteNonQueryAsync(cancellationToken);
  }
}
