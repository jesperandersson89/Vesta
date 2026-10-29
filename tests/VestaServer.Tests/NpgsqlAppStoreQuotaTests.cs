using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using VestaServer.Data;
using VestaServer.Storage;

namespace VestaServer.Tests;

/// <summary>
/// Round-trips every <see cref="AppQuotas"/> field through <see cref="NpgsqlAppStore"/> against a real
/// PostgreSQL instance (which also proves the migrations apply). Requires Docker to be running.
/// </summary>
public sealed class NpgsqlAppStoreQuotaTests : IAsyncLifetime
{
  private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();

  private NpgsqlDataSource _dataSource = null!;
  private NpgsqlAppStore _store = null!;

  public async Task InitializeAsync()
  {
    await _postgres.StartAsync();
    string connectionString = _postgres.GetConnectionString();
    _dataSource = NpgsqlDataSource.Create(connectionString);

    DbContextOptionsBuilder<VestaDbContext> optionsBuilder = new();
    optionsBuilder.UseNpgsql(connectionString);
    await using VestaDbContext dbContext = new(optionsBuilder.Options);
    await dbContext.Database.MigrateAsync();

    _store = new NpgsqlAppStore(_dataSource);
  }

  public async Task DisposeAsync()
  {
    await _dataSource.DisposeAsync();
    await _postgres.DisposeAsync();
  }

  [Fact]
  public async Task SetQuotas_RoundTripsEveryField_IncludingRealtimeQuotas()
  {
    await _store.RegisterAsync("quotaapp", "owner-1");
    AppQuotas quotas = new(
        MaxPayloadBytes: 1, PublishRatePerMinute: 2, MaxChannels: 3, MaxEventsPerChannel: 4,
        RetentionDays: 5, TotalStorageBytes: 6, MaxMessagesPerMonth: 7, MaxConnections: 8, VolatileRatePerMinute: 9,
        MaxEncryptedChannels: 10);

    Assert.True(await _store.SetQuotasAsync("quotaapp", quotas));

    AppInfo? app = await _store.GetAsync("quotaapp");
    Assert.Equal(quotas, app!.Quotas);
  }

  [Fact]
  public async Task SetQuotas_NullRealtimeQuotas_StayNull()
  {
    await _store.RegisterAsync("plainapp", "owner-2");

    await _store.SetQuotasAsync("plainapp", new AppQuotas(PublishRatePerMinute: 10));

    AppQuotas stored = (await _store.GetAsync("plainapp"))!.Quotas;
    Assert.Null(stored.MaxConnections);
    Assert.Null(stored.VolatileRatePerMinute);
    Assert.Null(stored.MaxEncryptedChannels);
    Assert.Equal(10, stored.PublishRatePerMinute);
  }

  [Fact]
  public async Task SetOwnerOnlyChannelCreation_DefaultsOffAndRoundTrips()
  {
    await _store.RegisterAsync("creationapp", "owner-3");
    Assert.False((await _store.GetAsync("creationapp"))!.OwnerOnlyChannelCreation);

    Assert.True(await _store.SetOwnerOnlyChannelCreationAsync("creationapp", true));
    Assert.True((await _store.GetAsync("creationapp"))!.OwnerOnlyChannelCreation);

    Assert.True(await _store.SetOwnerOnlyChannelCreationAsync("creationapp", false));
    Assert.False((await _store.GetAsync("creationapp"))!.OwnerOnlyChannelCreation);
    Assert.False(await _store.SetOwnerOnlyChannelCreationAsync("nosuchapp", true));
  }
}
