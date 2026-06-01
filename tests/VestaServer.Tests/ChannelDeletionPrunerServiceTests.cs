using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using VestaCore.Events;
using VestaServer.Data;
using VestaServer.Storage;

namespace VestaServer.Tests;

/// <summary>
/// Integration tests for ChannelDeletionPrunerService against a real PostgreSQL
/// instance. Requires Docker to be running.
/// </summary>
public sealed class ChannelDeletionPrunerServiceTests : IAsyncLifetime
{
  private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();

  private NpgsqlDataSource _dataSource = null!;
  private NpgsqlEventStore _eventStore = null!;
  private NpgsqlChannelAccessStore _accessStore = null!;

  public async Task InitializeAsync()
  {
    await _postgres.StartAsync();
    string connectionString = _postgres.GetConnectionString();
    _dataSource = NpgsqlDataSource.Create(connectionString);

    DbContextOptionsBuilder<VestaDbContext> optionsBuilder = new();
    optionsBuilder.UseNpgsql(connectionString);
    await using VestaDbContext dbContext = new(optionsBuilder.Options);
    await dbContext.Database.MigrateAsync();

    _eventStore = new NpgsqlEventStore(_dataSource);
    _accessStore = new NpgsqlChannelAccessStore(_dataSource);
  }

  public async Task DisposeAsync()
  {
    await _dataSource.DisposeAsync();
    await _postgres.DisposeAsync();
  }

  [Fact]
  public async Task SweepOnce_NoSoftDeletedChannels_DoesNothing()
  {
    await _eventStore.AppendAsync(CreateEvent("app/keep"));

    ChannelDeletionPrunerService pruner = CreatePruner(TimeSpan.FromHours(24));
    int hardDeleted = await pruner.SweepOnceAsync(CancellationToken.None);

    Assert.Equal(0, hardDeleted);
    Assert.Equal(1, await CountEventsAsync("app/keep"));
  }

  [Fact]
  public async Task SweepOnce_WithinGracePeriod_DoesNotPrune()
  {
    await _eventStore.AppendAsync(CreateEvent("app/recent"));
    Assert.True(await _accessStore.DeleteChannelAsync("app/recent"));

    // Tombstone is fresh (now), grace period is generous → must not prune.
    ChannelDeletionPrunerService pruner = CreatePruner(TimeSpan.FromHours(24));
    int hardDeleted = await pruner.SweepOnceAsync(CancellationToken.None);

    Assert.Equal(0, hardDeleted);
    Assert.Equal(1, await CountEventsAsync("app/recent"));
    Assert.Equal(1, await CountChannelRowsAsync("app/recent"));
  }

  [Fact]
  public async Task SweepOnce_PastGracePeriod_HardDeletesEventsAndRow()
  {
    await _eventStore.AppendAsync(CreateEvent("app/expired"));
    await _eventStore.AppendAsync(CreateEvent("app/expired"));
    Assert.True(await _accessStore.DeleteChannelAsync("app/expired"));

    // Backdate the tombstone past the grace period.
    await BackdateTombstoneAsync("app/expired", TimeSpan.FromHours(2));

    ChannelDeletionPrunerService pruner = CreatePruner(TimeSpan.FromHours(1));
    int hardDeleted = await pruner.SweepOnceAsync(CancellationToken.None);

    Assert.Equal(1, hardDeleted);
    Assert.Equal(0, await CountEventsAsync("app/expired"));
    Assert.Equal(0, await CountChannelRowsAsync("app/expired"));
  }

  [Fact]
  public async Task SweepOnce_AfterHardDelete_ChannelIdCanBeReused()
  {
    await _eventStore.AppendAsync(CreateEvent("app/reuse"));
    Assert.True(await _accessStore.DeleteChannelAsync("app/reuse"));
    await BackdateTombstoneAsync("app/reuse", TimeSpan.FromHours(2));

    ChannelDeletionPrunerService pruner = CreatePruner(TimeSpan.FromHours(1));
    await pruner.SweepOnceAsync(CancellationToken.None);

    // Channel is gone — a fresh PUBLISH should create it again (no CHANNEL_DELETED).
    Assert.False(await _accessStore.IsDeletedAsync("app/reuse"));
    await _eventStore.AppendAsync(CreateEvent("app/reuse"));
    Assert.Equal(1, await CountEventsAsync("app/reuse"));
    Assert.Equal(1, await CountChannelRowsAsync("app/reuse"));
  }

  [Fact]
  public async Task SweepOnce_ZeroGracePeriod_PrunesImmediately()
  {
    await _eventStore.AppendAsync(CreateEvent("app/instant"));
    Assert.True(await _accessStore.DeleteChannelAsync("app/instant"));

    ChannelDeletionPrunerService pruner = CreatePruner(TimeSpan.Zero);
    int hardDeleted = await pruner.SweepOnceAsync(CancellationToken.None);

    Assert.Equal(1, hardDeleted);
    Assert.Equal(0, await CountEventsAsync("app/instant"));
    Assert.Equal(0, await CountChannelRowsAsync("app/instant"));
  }

  [Fact]
  public async Task SweepOnce_LiveChannelsUntouched()
  {
    // One live channel, one soft-deleted+expired channel.
    await _eventStore.AppendAsync(CreateEvent("app/live"));
    await _eventStore.AppendAsync(CreateEvent("app/dead"));
    Assert.True(await _accessStore.DeleteChannelAsync("app/dead"));
    await BackdateTombstoneAsync("app/dead", TimeSpan.FromHours(2));

    ChannelDeletionPrunerService pruner = CreatePruner(TimeSpan.FromHours(1));
    int hardDeleted = await pruner.SweepOnceAsync(CancellationToken.None);

    Assert.Equal(1, hardDeleted);
    Assert.Equal(1, await CountEventsAsync("app/live"));
    Assert.Equal(1, await CountChannelRowsAsync("app/live"));
    Assert.Equal(0, await CountEventsAsync("app/dead"));
    Assert.Equal(0, await CountChannelRowsAsync("app/dead"));
  }

  private ChannelDeletionPrunerService CreatePruner(TimeSpan grace) =>
      new(
          _dataSource,
          Options.Create(new ChannelDeletionPrunerOptions { Enabled = true, GracePeriod = grace }),
          NullLogger<ChannelDeletionPrunerService>.Instance);

  private async Task BackdateTombstoneAsync(string channelId, TimeSpan offset)
  {
    await using NpgsqlCommand cmd = _dataSource.CreateCommand(
        "UPDATE channels SET deleted_at = now() - $2 WHERE id = $1");
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channelId });
    cmd.Parameters.Add(new NpgsqlParameter<TimeSpan> { TypedValue = offset });
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task<int> CountEventsAsync(string channelId)
  {
    await using NpgsqlCommand cmd = _dataSource.CreateCommand(
        "SELECT COUNT(*) FROM events WHERE channel_id = $1");
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channelId });
    object? result = await cmd.ExecuteScalarAsync();
    return Convert.ToInt32(result);
  }

  private async Task<int> CountChannelRowsAsync(string channelId)
  {
    await using NpgsqlCommand cmd = _dataSource.CreateCommand(
        "SELECT COUNT(*) FROM channels WHERE id = $1");
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channelId });
    object? result = await cmd.ExecuteScalarAsync();
    return Convert.ToInt32(result);
  }

  private static VestaEvent CreateEvent(string channelId)
  {
    System.Text.Json.JsonElement payload =
        System.Text.Json.JsonDocument.Parse("""{"hello":"world"}""").RootElement;
    return new VestaEvent(
        Id: Guid.NewGuid(),
        ChannelId: channelId,
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: "test-client",
        EventType: "test",
        Payload: payload);
  }
}
