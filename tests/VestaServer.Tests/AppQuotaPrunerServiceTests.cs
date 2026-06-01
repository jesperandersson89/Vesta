using System.Text.Json;
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
/// Integration tests for AppQuotaPrunerService against a real PostgreSQL instance.
/// Requires Docker to be running.
/// </summary>
public sealed class AppQuotaPrunerServiceTests : IAsyncLifetime
{
  private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();

  private NpgsqlDataSource _dataSource = null!;
  private NpgsqlEventStore _eventStore = null!;
  private NpgsqlAppStore _appStore = null!;
  private InMemoryAppStorageAccountant _accountant = null!;
  private AppQuotaPrunerService _pruner = null!;

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
    _appStore = new NpgsqlAppStore(_dataSource);
    _accountant = new InMemoryAppStorageAccountant();
    _pruner = new AppQuotaPrunerService(
        _dataSource,
        _appStore,
        _accountant,
        Options.Create(new AppQuotaPrunerOptions { Enabled = true }),
        NullLogger<AppQuotaPrunerService>.Instance);
  }

  public async Task DisposeAsync()
  {
    await _dataSource.DisposeAsync();
    await _postgres.DisposeAsync();
  }

  [Fact]
  public async Task SweepOnce_RetentionDays_DeletesOldEvents()
  {
    await _appStore.RegisterAsync("retentionapp", "owner");
    await _appStore.SetQuotasAsync("retentionapp", new AppQuotas(RetentionDays: 1));

    // Append 3 events, then backdate 2 of them to 5 days ago.
    SequencedEvent old1 = await _eventStore.AppendAsync(CreateEvent("retentionapp/log"));
    SequencedEvent old2 = await _eventStore.AppendAsync(CreateEvent("retentionapp/log"));
    SequencedEvent fresh = await _eventStore.AppendAsync(CreateEvent("retentionapp/log"));

    await BackdateAsync(old1.Event.Id, TimeSpan.FromDays(5));
    await BackdateAsync(old2.Event.Id, TimeSpan.FromDays(5));

    await _pruner.SweepOnceAsync(CancellationToken.None);

    Assert.Equal(1, await CountEventsAsync("retentionapp/log"));
    // Surviving row should be the fresh one.
    Assert.Equal(fresh.Sequence, await _eventStore.GetLatestSequenceAsync("retentionapp/log"));
  }

  [Fact]
  public async Task SweepOnce_MaxEventsPerChannel_KeepsLatestN()
  {
    await _appStore.RegisterAsync("capapp", "owner");
    await _appStore.SetQuotasAsync("capapp", new AppQuotas(MaxEventsPerChannel: 3));

    for (int i = 0; i < 7; i++)
      await _eventStore.AppendAsync(CreateEvent("capapp/log"));

    // Different channel under the same app — also capped at 3.
    for (int i = 0; i < 5; i++)
      await _eventStore.AppendAsync(CreateEvent("capapp/other"));

    await _pruner.SweepOnceAsync(CancellationToken.None);

    Assert.Equal(3, await CountEventsAsync("capapp/log"));
    Assert.Equal(3, await CountEventsAsync("capapp/other"));
  }

  [Fact]
  public async Task SweepOnce_TotalStorageBytes_PopulatesAccountant()
  {
    await _appStore.RegisterAsync("measapp", "owner");
    await _appStore.SetQuotasAsync("measapp", new AppQuotas(TotalStorageBytes: 1_000_000));

    await _eventStore.AppendAsync(CreateEvent("measapp/log"));
    await _eventStore.AppendAsync(CreateEvent("measapp/log"));

    Assert.Null(_accountant.Get("measapp"));

    await _pruner.SweepOnceAsync(CancellationToken.None);

    long? bytes = _accountant.Get("measapp");
    Assert.NotNull(bytes);
    Assert.True(bytes > 0);
  }

  [Fact]
  public async Task SweepOnce_AppWithoutQuotas_LeavesEventsAlone()
  {
    await _appStore.RegisterAsync("noquotaapp", "owner");

    for (int i = 0; i < 5; i++)
      await _eventStore.AppendAsync(CreateEvent("noquotaapp/log"));

    await _pruner.SweepOnceAsync(CancellationToken.None);

    Assert.Equal(5, await CountEventsAsync("noquotaapp/log"));
    Assert.Null(_accountant.Get("noquotaapp"));
  }

  private async Task BackdateAsync(Guid eventId, TimeSpan offset)
  {
    await using NpgsqlCommand cmd = _dataSource.CreateCommand(
        "UPDATE events SET received_at = now() - $2 WHERE id = $1");
    cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = eventId });
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

  private static VestaEvent CreateEvent(string channelId)
  {
    JsonElement payload = JsonDocument.Parse("""{"hello":"world"}""").RootElement;
    return new VestaEvent(
        Id: Guid.NewGuid(),
        ChannelId: channelId,
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: "test-client",
        EventType: "test",
        Payload: payload);
  }
}
