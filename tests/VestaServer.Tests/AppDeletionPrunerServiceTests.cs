using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Vesta;
using VestaServer.Admin;
using VestaServer.Data;
using VestaServer.Storage;

namespace VestaServer.Tests;

/// <summary>
/// Integration tests for app soft-delete, restore and the AppDeletionPrunerService against a
/// real PostgreSQL instance. Requires Docker to be running.
/// </summary>
public sealed class AppDeletionPrunerServiceTests : IAsyncLifetime
{
  private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();

  private NpgsqlDataSource _dataSource = null!;
  private NpgsqlEventStore _eventStore = null!;
  private NpgsqlChannelAccessStore _channels = null!;
  private NpgsqlAppStore _apps = null!;
  private InMemoryAppStorageAccountant _storage = null!;
  private InMemoryAppUsageAccountant _usage = null!;
  private InMemoryAdminAuditStore _audit = null!;

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
    _channels = new NpgsqlChannelAccessStore(_dataSource);
    _apps = new NpgsqlAppStore(_dataSource);
    _storage = new InMemoryAppStorageAccountant();
    _usage = new InMemoryAppUsageAccountant(TimeProvider.System);
    _audit = new InMemoryAdminAuditStore(TimeProvider.System);
  }

  public async Task DisposeAsync()
  {
    await _dataSource.DisposeAsync();
    await _postgres.DisposeAsync();
  }

  [Fact]
  public async Task DeleteAsync_ThenCascadeAndRestore_RoundTripsChannelTombstones()
  {
    await _apps.RegisterAsync("roundtrip", "owner");
    await _eventStore.AppendAsync(CreateEvent("roundtrip/a"));
    await _eventStore.AppendAsync(CreateEvent("roundtrip/b"));
    await _channels.DeleteChannelAsync("roundtrip/b");
    await Task.Delay(20);

    DateTimeOffset? deletedAt = await _apps.DeleteAsync("roundtrip");
    Assert.NotNull(deletedAt);
    Assert.Equal(1, await _channels.DeleteChannelsByAppAsync("roundtrip", deletedAt.Value));

    Assert.False(await _apps.ExistsAsync("roundtrip"));
    Assert.True(await _channels.IsDeletedAsync("roundtrip/a"));

    AppInfo? stored = await _apps.GetAsync("roundtrip");
    Assert.Equal(deletedAt, stored?.DeletedAt);

    Assert.Equal(1, await _channels.RestoreChannelsByAppAsync("roundtrip", stored!.DeletedAt!.Value));
    Assert.True(await _apps.RestoreAsync("roundtrip"));
    Assert.True(await _apps.ExistsAsync("roundtrip"));
    Assert.False(await _channels.IsDeletedAsync("roundtrip/a"));
    Assert.True(await _channels.IsDeletedAsync("roundtrip/b"));
  }

  [Fact]
  public async Task ListAsync_ExcludesDeleted_ListAllAsync_Includes()
  {
    await _apps.RegisterAsync("listlive", "owner");
    await _apps.RegisterAsync("listdead", "owner");
    await _apps.DeleteAsync("listdead");

    Assert.DoesNotContain(await _apps.ListAsync(), a => a.Id == "listdead");
    Assert.Contains(await _apps.ListAllAsync(), a => a.Id == "listdead");
    Assert.Contains(await _apps.ListAsync(), a => a.Id == "listlive");
  }

  [Fact]
  public async Task RegisterAsync_SoftDeletedId_ThrowsAlreadyRegistered()
  {
    await _apps.RegisterAsync("taken", "owner");
    await _apps.DeleteAsync("taken");

    await Assert.ThrowsAsync<AppAlreadyRegisteredException>(() => _apps.RegisterAsync("taken", "other"));
  }

  [Fact]
  public async Task SweepOnce_WithinGracePeriod_DoesNotPurge()
  {
    await _apps.RegisterAsync("recent", "owner");
    await _eventStore.AppendAsync(CreateEvent("recent/room"));
    await _apps.DeleteAsync("recent");

    int purged = await CreatePruner(TimeSpan.FromDays(7)).SweepOnceAsync(CancellationToken.None);

    Assert.Equal(0, purged);
    Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM events WHERE channel_id LIKE 'recent/%'"));
  }

  [Fact]
  public async Task SweepOnce_PastGracePeriod_PurgesEverythingForTheApp()
  {
    await _apps.RegisterAsync("purgeme", "owner");
    await _apps.RegisterAsync("keepme", "owner");
    await _eventStore.AppendAsync(CreateEvent("purgeme/a"));
    await _eventStore.AppendAsync(CreateEvent("purgeme/b"));
    await _eventStore.AppendAsync(CreateEvent("keepme/a"));
    await _channels.GrantAccessAsync("purgeme/a", "someone", "member");
    await UpsertUsageAsync("purgeme");
    _storage.Set("purgeme", 10);
    _usage.SetMessages("purgeme", 2);

    DateTimeOffset? deletedAt = await _apps.DeleteAsync("purgeme");
    await _channels.DeleteChannelsByAppAsync("purgeme", deletedAt!.Value);
    await BackdateAppAsync("purgeme", TimeSpan.FromDays(2));

    int purged = await CreatePruner(TimeSpan.FromDays(1)).SweepOnceAsync(CancellationToken.None);

    Assert.Equal(1, purged);
    Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM events WHERE channel_id LIKE 'purgeme/%'"));
    Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM channels WHERE id LIKE 'purgeme/%'"));
    Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM channel_access WHERE channel_id LIKE 'purgeme/%'"));
    Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM channel_sequences WHERE channel_id LIKE 'purgeme/%'"));
    Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM app_usage WHERE app_id = 'purgeme'"));
    Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM apps WHERE id = 'purgeme'"));
    Assert.Null(_storage.Get("purgeme"));
    Assert.Null(_usage.GetMessages("purgeme"));

    Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM events WHERE channel_id LIKE 'keepme/%'"));
    Assert.True(await _apps.ExistsAsync("keepme"));

    IReadOnlyList<AdminAuditEntry> audit = await _audit.ListAsync(10, "purgeme");
    Assert.Equal("app.purge", Assert.Single(audit).Action);
  }

  [Fact]
  public async Task SweepOnce_AfterPurge_IdCanBeRegisteredAgain()
  {
    await _apps.RegisterAsync("recycle", "owner");
    await _apps.DeleteAsync("recycle");
    await BackdateAppAsync("recycle", TimeSpan.FromDays(2));

    await CreatePruner(TimeSpan.FromDays(1)).SweepOnceAsync(CancellationToken.None);

    await _apps.RegisterAsync("recycle", "new-owner");
    Assert.Equal("new-owner", (await _apps.GetAsync("recycle"))?.OwnerClientId);
  }

  [Fact]
  public async Task SweepOnce_RestoredApp_IsNeverPurged()
  {
    await _apps.RegisterAsync("saved", "owner");
    await _apps.DeleteAsync("saved");
    await BackdateAppAsync("saved", TimeSpan.FromDays(2));
    await _apps.RestoreAsync("saved");

    int purged = await CreatePruner(TimeSpan.FromDays(1)).SweepOnceAsync(CancellationToken.None);

    Assert.Equal(0, purged);
    Assert.True(await _apps.ExistsAsync("saved"));
  }

  [Fact]
  public async Task ChannelDeletionPruner_ChannelsOfSoftDeletedApp_AreLeftForAppPruner()
  {
    await _apps.RegisterAsync("heldapp", "owner");
    await _eventStore.AppendAsync(CreateEvent("heldapp/room"));
    await _eventStore.AppendAsync(CreateEvent("looseapp/room"));
    DateTimeOffset? deletedAt = await _apps.DeleteAsync("heldapp");
    await _channels.DeleteChannelsByAppAsync("heldapp", deletedAt!.Value);
    await _channels.DeleteChannelAsync("looseapp/room");
    await BackdateChannelsAsync(TimeSpan.FromDays(3));

    ChannelDeletionPrunerService channelPruner = new(
        _dataSource,
        Options.Create(new ChannelDeletionPrunerOptions { Enabled = true, GracePeriod = TimeSpan.FromHours(24) }),
        NullLogger<ChannelDeletionPrunerService>.Instance);
    int hardDeleted = await channelPruner.SweepOnceAsync(CancellationToken.None);

    Assert.Equal(1, hardDeleted);
    Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM events WHERE channel_id = 'heldapp/room'"));
    Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM events WHERE channel_id = 'looseapp/room'"));
  }

  private AppDeletionPrunerService CreatePruner(TimeSpan grace) => new(
      _dataSource,
      _storage,
      _usage,
      Options.Create(new AppDeletionPrunerOptions { Enabled = true, GracePeriod = grace }),
      NullLogger<AppDeletionPrunerService>.Instance,
      _audit);

  private async Task BackdateAppAsync(string appId, TimeSpan offset)
  {
    await using NpgsqlCommand cmd = _dataSource.CreateCommand("UPDATE apps SET deleted_at = now() - $2 WHERE id = $1");
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    cmd.Parameters.Add(new NpgsqlParameter<TimeSpan> { TypedValue = offset });
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task BackdateChannelsAsync(TimeSpan offset)
  {
    await using NpgsqlCommand cmd = _dataSource.CreateCommand(
        "UPDATE channels SET deleted_at = now() - $1 WHERE deleted_at IS NOT NULL");
    cmd.Parameters.Add(new NpgsqlParameter<TimeSpan> { TypedValue = offset });
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task UpsertUsageAsync(string appId)
  {
    await using NpgsqlCommand cmd = _dataSource.CreateCommand(
        "INSERT INTO app_usage (app_id, period_start, messages, updated_at) VALUES ($1, date_trunc('month', now())::date, 5, now())");
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task<int> CountAsync(string sql)
  {
    await using NpgsqlCommand cmd = _dataSource.CreateCommand(sql);
    return Convert.ToInt32(await cmd.ExecuteScalarAsync());
  }

  private static VestaEvent CreateEvent(string channelId) => new(
      Id: Guid.NewGuid(),
      ChannelId: channelId,
      Timestamp: DateTimeOffset.UtcNow,
      ClientId: "test-client",
      EventType: "test",
      Payload: System.Text.Json.JsonDocument.Parse("""{"hello":"world"}""").RootElement);
}
