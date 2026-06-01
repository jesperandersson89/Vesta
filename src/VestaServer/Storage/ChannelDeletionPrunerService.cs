using Microsoft.Extensions.Options;
using Npgsql;

namespace VestaServer.Storage;

/// <summary>
/// Configuration for the channel hard-delete pruner.
/// Sweeps soft-deleted channels whose <c>deleted_at</c> tombstone has aged past
/// the grace period, deletes their events, and drops the <c>channels</c> row so
/// the channel id becomes available for reuse.
/// </summary>
public sealed class ChannelDeletionPrunerOptions
{
  /// <summary>
  /// When false the pruner never runs (default false so in-memory / test hosts
  /// don't spin up a postgres-only worker). Enabled in production via
  /// <c>ChannelDeletionPruner:Enabled=true</c>.
  /// </summary>
  public bool Enabled { get; set; }

  /// <summary>How often to sweep. Defaults to 5 minutes.</summary>
  public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

  /// <summary>
  /// Grace period between soft-delete (<c>deleted_at</c> stamp) and hard-delete.
  /// Defaults to 24 hours so an operator has a recovery window after a mistaken
  /// <c>DELETE_CHANNEL</c>. Set to <c>TimeSpan.Zero</c> for immediate hard-delete.
  /// </summary>
  public TimeSpan GracePeriod { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Background sweep that hard-deletes channels soft-deleted by
/// <c>DELETE_CHANNEL</c> once their grace period has elapsed. For each eligible
/// channel: deletes all rows in <c>events</c> with that <c>channel_id</c>, then
/// deletes the row in <c>channels</c>. Once the <c>channels</c> row is gone the
/// id can be reused (a fresh <c>PUBLISH</c> will recreate the channel
/// implicitly). PostgreSQL-only; in-memory mode does not prune.
/// </summary>
public sealed class ChannelDeletionPrunerService(
    NpgsqlDataSource dataSource,
    IOptions<ChannelDeletionPrunerOptions> options,
    ILogger<ChannelDeletionPrunerService> logger) : BackgroundService
{
  private readonly ChannelDeletionPrunerOptions _options = options.Value;

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    if (!_options.Enabled)
    {
      logger.LogInformation(
          "Channel deletion pruner is disabled (set ChannelDeletionPruner:Enabled=true to enable).");
      return;
    }

    logger.LogInformation(
        "Channel deletion pruner enabled; sweeping every {Interval}, grace period {Grace}.",
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
        logger.LogError(ex, "Channel deletion pruner sweep failed; will retry on next tick.");
      }
    }
  }

  /// <summary>
  /// Runs a single sweep pass. Exposed as <c>public</c> primarily for tests
  /// (the hosted service drives it on its own timer). Returns the number of
  /// channels hard-deleted.
  /// </summary>
  public async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
  {
    IReadOnlyList<string> eligible = await ListEligibleAsync(cancellationToken);
    if (eligible.Count == 0)
      return 0;

    int hardDeleted = 0;
    foreach (string channelId in eligible)
    {
      int eventsDeleted = await DeleteEventsAsync(channelId, cancellationToken);
      bool rowDropped = await DropChannelRowAsync(channelId, cancellationToken);
      if (rowDropped)
      {
        hardDeleted++;
        logger.LogInformation(
            "Hard-deleted channel '{Channel}' ({Count} event(s) purged).",
            channelId, eventsDeleted);
      }
    }
    return hardDeleted;
  }

  private async Task<IReadOnlyList<string>> ListEligibleAsync(CancellationToken cancellationToken)
  {
    // GracePeriod stored as TimeSpan; convert to seconds for make_interval.
    const string sql = """
            SELECT id FROM channels
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

  private async Task<int> DeleteEventsAsync(string channelId, CancellationToken cancellationToken)
  {
    const string sql = "DELETE FROM events WHERE channel_id = $1";
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channelId });
    return await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  private async Task<bool> DropChannelRowAsync(string channelId, CancellationToken cancellationToken)
  {
    // Re-check the tombstone in the DELETE predicate so a concurrent un-delete
    // (none today, but cheap insurance) can't cause us to drop a live channel.
    const string sql = """
            DELETE FROM channels
            WHERE id = $1
              AND deleted_at IS NOT NULL
              AND deleted_at < now() - make_interval(secs => $2)
            """;
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channelId });
    cmd.Parameters.Add(new NpgsqlParameter<double> { TypedValue = _options.GracePeriod.TotalSeconds });
    int rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
    return rows > 0;
  }
}
