using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using VestaServer.Data;
using VestaServer.Storage;

namespace VestaServer.Tests;

/// <summary>
/// Read/write access rules of <see cref="NpgsqlChannelAccessStore"/> against a real PostgreSQL
/// instance. Requires Docker to be running.
/// </summary>
public sealed class NpgsqlChannelAccessStoreTests : IAsyncLifetime
{
  private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();

  private NpgsqlDataSource _dataSource = null!;
  private NpgsqlChannelAccessStore _store = null!;

  public async Task InitializeAsync()
  {
    await _postgres.StartAsync();
    string connectionString = _postgres.GetConnectionString();
    _dataSource = NpgsqlDataSource.Create(connectionString);

    DbContextOptionsBuilder<VestaDbContext> optionsBuilder = new();
    optionsBuilder.UseNpgsql(connectionString);
    await using VestaDbContext dbContext = new(optionsBuilder.Options);
    await dbContext.Database.MigrateAsync();

    _store = new NpgsqlChannelAccessStore(_dataSource);
  }

  public async Task DisposeAsync()
  {
    await _dataSource.DisposeAsync();
    await _postgres.DisposeAsync();
  }

  [Fact]
  public async Task Broadcast_RoundTripsVisibility_AndSplitsReadFromWrite()
  {
    await _store.CreateChannelAsync("shop/feed", ChannelVisibility.Broadcast, "shopkeeper", ["clerk"]);

    Assert.Equal(ChannelVisibility.Broadcast, await _store.GetVisibilityAsync("shop/feed"));

    Assert.True(await _store.CanAccessAsync("shop/feed", "stranger"));
    Assert.False(await _store.CanPublishAsync("shop/feed", "stranger"));
    Assert.False(await _store.CanPublishAsync("shop/feed", null));
    Assert.True(await _store.CanPublishAsync("shop/feed", "clerk"));
    Assert.True(await _store.CanPublishAsync("shop/feed", "shopkeeper"));
  }

  [Fact]
  public async Task Private_ReaderRole_CanReadButNotPublish()
  {
    await _store.CreateChannelAsync("room/one", ChannelVisibility.Private, "owner", []);
    await _store.GrantAccessAsync("room/one", "spectator", ChannelRoles.Reader);

    Assert.True(await _store.CanAccessAsync("room/one", "spectator"));
    Assert.False(await _store.CanPublishAsync("room/one", "spectator"));
    Assert.False(await _store.IsAdminAsync("room/one", "spectator"));
    Assert.False(await _store.CanAccessAsync("room/one", "outsider"));
    Assert.False(await _store.CanPublishAsync("room/one", "outsider"));

    await _store.GrantAccessAsync("room/one", "spectator", ChannelRoles.Member);

    Assert.True(await _store.CanPublishAsync("room/one", "spectator"));
  }

  [Fact]
  public async Task PublicAndUnknownChannels_AllowEveryoneToPublish()
  {
    await _store.CreateChannelAsync("open/room", ChannelVisibility.Public, "owner", []);

    Assert.True(await _store.CanPublishAsync("open/room", "anyone"));
    Assert.True(await _store.CanPublishAsync("never/created", "anyone"));
    Assert.Null(await _store.GetVisibilityAsync("never/created"));
  }

  [Fact]
  public async Task ListChannels_ReportsBroadcastVisibility()
  {
    await _store.CreateChannelAsync("list/feed", ChannelVisibility.Broadcast, "owner", []);

    IReadOnlyList<ChannelSummary> channels = await _store.ListChannelsAsync("list", includeDeleted: false);

    Assert.Equal(ChannelVisibility.Broadcast, Assert.Single(channels).Visibility);
  }
}
