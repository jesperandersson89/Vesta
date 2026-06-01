using VestaServer.Storage;

namespace VestaServer.Tests;

public class InMemoryAppStoreTests
{
  [Fact]
  public async Task RegisterAndGet_RoundTrips()
  {
    InMemoryAppStore store = new();

    await store.RegisterAsync("myapp", "client-1");

    AppInfo? info = await store.GetAsync("myapp");
    Assert.NotNull(info);
    Assert.Equal("myapp", info!.Id);
    Assert.Equal("client-1", info.OwnerClientId);
  }

  [Fact]
  public async Task Exists_TrueAfterRegister()
  {
    InMemoryAppStore store = new();
    Assert.False(await store.ExistsAsync("myapp"));
    await store.RegisterAsync("myapp", "client-1");
    Assert.True(await store.ExistsAsync("myapp"));
  }

  [Fact]
  public async Task Register_DuplicateThrows()
  {
    InMemoryAppStore store = new();
    await store.RegisterAsync("myapp", "client-1");
    await Assert.ThrowsAsync<AppAlreadyRegisteredException>(
        () => store.RegisterAsync("myapp", "client-2"));
  }

  [Fact]
  public async Task Get_Missing_ReturnsNull()
  {
    InMemoryAppStore store = new();
    Assert.Null(await store.GetAsync("missing"));
  }

  [Fact]
  public async Task SetQuotas_RoundTrips()
  {
    InMemoryAppStore store = new();
    await store.RegisterAsync("myapp", "client-1");

    bool ok = await store.SetQuotasAsync("myapp",
        new AppQuotas(MaxPayloadBytes: 4096, PublishRatePerMinute: 60, MaxChannels: 10));
    Assert.True(ok);

    AppInfo info = (await store.GetAsync("myapp"))!;
    Assert.Equal(4096, info.Quotas.MaxPayloadBytes);
    Assert.Equal(60, info.Quotas.PublishRatePerMinute);
    Assert.Equal(10, info.Quotas.MaxChannels);
  }

  [Fact]
  public async Task SetQuotas_UnknownApp_ReturnsFalse()
  {
    InMemoryAppStore store = new();
    Assert.False(await store.SetQuotasAsync("nope", new AppQuotas(MaxPayloadBytes: 1)));
  }
}
