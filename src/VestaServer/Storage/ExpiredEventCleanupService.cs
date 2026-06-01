using Microsoft.Extensions.Options;
using Npgsql;

namespace VestaServer.Storage;

/// <summary>
/// Configuration for the expired event cleanup background job.
/// </summary>
public sealed class ExpiredEventCleanupOptions
{
  /// <summary>
  /// When false, the cleanup loop never runs (default false so in-memory / test
  /// hosts don't spin up a postgres-only worker). Enabled in production via
  /// <c>EventCleanup:Enabled=true</c> in configuration.
  /// </summary>
  public bool Enabled { get; set; }

  /// <summary>
  /// How often to sweep expired events. Defaults to 60 seconds.
  /// </summary>
  public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(60);

  /// <summary>
  /// Max rows to delete per sweep. Defaults to 10,000.
  /// Set to 0 for no limit (uses an unbounded DELETE).
  /// </summary>
  public int BatchSize { get; set; } = 10_000;
}

/// <summary>
/// Periodically deletes rows from the <c>events</c> table whose <c>expires_at</c>
/// is in the past. Runs only when <see cref="ExpiredEventCleanupOptions.Enabled"/>
/// is true and a PostgreSQL data source is registered.
/// </summary>
public sealed class ExpiredEventCleanupService(
    NpgsqlDataSource dataSource,
    IOptions<ExpiredEventCleanupOptions> options,
    ILogger<ExpiredEventCleanupService> logger) : BackgroundService
{
  private readonly ExpiredEventCleanupOptions _options = options.Value;

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    if (!_options.Enabled)
    {
      logger.LogInformation("Expired event cleanup is disabled (set EventCleanup:Enabled=true to enable).");
      return;
    }

    logger.LogInformation(
        "Expired event cleanup enabled; sweeping every {Interval} (batch size {BatchSize}).",
        _options.Interval, _options.BatchSize);

    using PeriodicTimer timer = new(_options.Interval);

    // Run once immediately, then on the timer.
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
        logger.LogError(ex, "Expired event cleanup sweep failed; will retry on next tick.");
      }
    }
  }

  private async Task SweepOnceAsync(CancellationToken cancellationToken)
  {
    string sql = _options.BatchSize > 0
        ? """
              DELETE FROM events
              WHERE id IN (
                  SELECT id FROM events
                  WHERE expires_at IS NOT NULL AND expires_at <= now()
                  LIMIT $1
              )
              """
        : """
              DELETE FROM events
              WHERE expires_at IS NOT NULL AND expires_at <= now()
              """;

    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    if (_options.BatchSize > 0)
    {
      cmd.Parameters.AddWithValue(_options.BatchSize);
    }

    int deleted = await cmd.ExecuteNonQueryAsync(cancellationToken);
    if (deleted > 0)
    {
      logger.LogDebug("Cleanup sweep deleted {Count} expired event(s).", deleted);
    }
  }
}
