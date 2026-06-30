using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using VestaCore.Events;
using VestaCore.Protocol;
using VestaCore.Serialization;
using VestaServer.Storage;

namespace VestaServer.Tests;

/// <summary>
/// Integration tests for the per-app quota enforcement added in TODO #9b:
/// <c>max_payload_bytes</c>, <c>publish_rate_per_minute</c>, and <c>max_channels</c>.
/// </summary>
public class AppQuotasTests : IClassFixture<WebApplicationFactory<Program>>
{
  private readonly WebApplicationFactory<Program> _factory;
  private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;

  public AppQuotasTests(WebApplicationFactory<Program> factory)
  {
    _factory = factory.WithWebHostBuilder(builder =>
    {
      builder.UseSetting("UseInMemoryStore", "true");
    });
  }

  [Fact]
  public async Task Publish_OverMaxPayloadBytes_RejectedWithQuotaExceeded()
  {
    IAppStore store = _factory.Services.GetRequiredService<IAppStore>();
    await store.RegisterAsync("sizeapp", "owner-1");
    await store.SetQuotasAsync("sizeapp", new AppQuotas(MaxPayloadBytes: 32));

    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-size", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws); // WELCOME

    // 128 bytes of payload — over the 32-byte cap.
    VestaEvent big = CreateEvent(
        "sizeapp/chat",
        "client-size",
        $$"""{"text":"{{new string('x', 100)}}"}""");
    await SendAsync(ws, new PublishMessage("sizeapp/chat", big));

    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("QUOTA_EXCEEDED", error.Code);
    Assert.Contains("max_payload_bytes", error.Message);
    Assert.Equal(big.Id, error.EventId);
    Assert.Equal("sizeapp/chat", error.ChannelId);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task Publish_UnderMaxPayloadBytes_Accepted()
  {
    IAppStore store = _factory.Services.GetRequiredService<IAppStore>();
    await store.RegisterAsync("smallok", "owner-2");
    await store.SetQuotasAsync("smallok", new AppQuotas(MaxPayloadBytes: 1024));

    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-small", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws);

    VestaEvent small = CreateEvent("smallok/chat", "client-small", """{"x":1}""");
    await SendAsync(ws, new PublishMessage("smallok/chat", small));

    Assert.IsType<AckMessage>(await ReceiveAsync(ws));

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task Publish_ExceedsRateLimit_RejectedWithRateLimited()
  {
    IAppStore store = _factory.Services.GetRequiredService<IAppStore>();
    await store.RegisterAsync("rateapp", "owner-3");
    await store.SetQuotasAsync("rateapp", new AppQuotas(PublishRatePerMinute: 3));

    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-rate", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws);

    for (int i = 0; i < 3; i++)
    {
      VestaEvent evt = CreateEvent("rateapp/chat", "client-rate");
      await SendAsync(ws, new PublishMessage("rateapp/chat", evt));
      Assert.IsType<AckMessage>(await ReceiveAsync(ws));
    }

    VestaEvent overflow = CreateEvent("rateapp/chat", "client-rate");
    await SendAsync(ws, new PublishMessage("rateapp/chat", overflow));
    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("RATE_LIMITED", error.Code);
    Assert.Equal(overflow.Id, error.EventId);
    Assert.Equal("rateapp/chat", error.ChannelId);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task CreateChannel_OverMaxChannels_RejectedWithQuotaExceeded()
  {
    IAppStore store = _factory.Services.GetRequiredService<IAppStore>();
    await store.RegisterAsync("chanapp", "owner-4");
    await store.SetQuotasAsync("chanapp", new AppQuotas(MaxChannels: 2));

    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-chan", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws);

    await SendAsync(ws, new CreateChannelMessage("chanapp/a", "private", []));
    Assert.IsType<AckMessage>(await ReceiveAsync(ws));

    await SendAsync(ws, new CreateChannelMessage("chanapp/b", "private", []));
    Assert.IsType<AckMessage>(await ReceiveAsync(ws));

    await SendAsync(ws, new CreateChannelMessage("chanapp/c", "private", []));
    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("QUOTA_EXCEEDED", error.Code);
    Assert.Contains("max_channels", error.Message);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task Publish_NoQuotas_AlwaysAccepted()
  {
    IAppStore store = _factory.Services.GetRequiredService<IAppStore>();
    await store.RegisterAsync("freeapp", "owner-5");

    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-free", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws);

    for (int i = 0; i < 5; i++)
    {
      VestaEvent evt = CreateEvent("freeapp/chat", "client-free");
      await SendAsync(ws, new PublishMessage("freeapp/chat", evt));
      Assert.IsType<AckMessage>(await ReceiveAsync(ws));
    }

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task Publish_OverTotalStorageBytes_RejectedWithQuotaExceeded()
  {
    IAppStore store = _factory.Services.GetRequiredService<IAppStore>();
    IAppStorageAccountant accountant = _factory.Services.GetRequiredService<IAppStorageAccountant>();

    await store.RegisterAsync("storageapp", "owner-6");
    await store.SetQuotasAsync("storageapp", new AppQuotas(TotalStorageBytes: 100));

    // Seed the cached rollup as if a pruner sweep already measured 95 bytes used.
    accountant.Set("storageapp", 95);

    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-storage", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws);

    // ~30-byte payload; 95 + 30 > 100 → reject.
    VestaEvent evt = CreateEvent("storageapp/chat", "client-storage", """{"x":"123456789012345678"}""");
    await SendAsync(ws, new PublishMessage("storageapp/chat", evt));

    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("QUOTA_EXCEEDED", error.Code);
    Assert.Contains("total_storage_bytes", error.Message);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task Publish_TotalStorageBytes_ColdCacheAllows()
  {
    IAppStore store = _factory.Services.GetRequiredService<IAppStore>();

    await store.RegisterAsync("coldapp", "owner-7");
    await store.SetQuotasAsync("coldapp", new AppQuotas(TotalStorageBytes: 10));

    // Don't seed the accountant — cold cache should allow until the next sweep.
    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-cold", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws);

    VestaEvent evt = CreateEvent("coldapp/chat", "client-cold");
    await SendAsync(ws, new PublishMessage("coldapp/chat", evt));
    Assert.IsType<AckMessage>(await ReceiveAsync(ws));

    // The successful publish should have incremented the cache.
    IAppStorageAccountant accountant = _factory.Services.GetRequiredService<IAppStorageAccountant>();
    Assert.NotNull(accountant.Get("coldapp"));
    Assert.True(accountant.Get("coldapp") > 0);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  // --- Helpers ---

  private async Task<WebSocket> ConnectAsync()
  {
    Microsoft.AspNetCore.TestHost.WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
    return await wsClient.ConnectAsync(
        new Uri(_factory.Server.BaseAddress, "/ws"),
        CancellationToken.None);
  }

  private static async Task SendAsync(WebSocket ws, ProtocolMessage message)
  {
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes<ProtocolMessage>(message, JsonOptions);
    await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
  }

  private static async Task<ProtocolMessage?> ReceiveAsync(WebSocket ws, CancellationToken cancellationToken = default)
  {
    byte[] buffer = new byte[16384];
    using MemoryStream stream = new();
    ValueWebSocketReceiveResult result;
    do
    {
      result = await ws.ReceiveAsync(buffer.AsMemory(), cancellationToken);
      if (result.MessageType == WebSocketMessageType.Close)
        return null;
      stream.Write(buffer, 0, result.Count);
    }
    while (!result.EndOfMessage);
    stream.Position = 0;
    return await JsonSerializer.DeserializeAsync<ProtocolMessage>(stream, JsonOptions, cancellationToken);
  }

  private static VestaEvent CreateEvent(string channelId, string clientId, string payloadJson = """{"x":1}""")
  {
    JsonElement payload = JsonDocument.Parse(payloadJson).RootElement;
    return new VestaEvent(
        Id: Guid.NewGuid(),
        ChannelId: channelId,
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: clientId,
        EventType: "test",
        Payload: payload);
  }
}
