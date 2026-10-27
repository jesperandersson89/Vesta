using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vesta;
using VestaServer.Connections;
using VestaServer.Storage;

namespace VestaServer.Tests;

/// <summary>Per-app realtime quotas: <c>volatile_rate_per_minute</c> and <c>max_connections</c>.</summary>
public class RealtimeQuotaTests(WebApplicationFactory<Program> baseFactory) : IClassFixture<WebApplicationFactory<Program>>
{
  private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;
  private readonly WebApplicationFactory<Program> _factory = baseFactory.WithWebHostBuilder(b => b.UseSetting("UseInMemoryStore", "true"));

  [Fact]
  public async Task VolatileRate_HasItsOwnBucket_SeparateFromPersistedPublishes()
  {
    await SetupAppAsync("rtbucket", new AppQuotas(PublishRatePerMinute: 2, VolatileRatePerMinute: 3));
    using WebSocket ws = await HelloAsync("rt-client-1");

    // Volatile events are never ACKed, so the first reply is the first rejection.
    for (int i = 0; i < 3; i++)
      await SendAsync(ws, new PublishMessage("rtbucket/pos", CreateEvent("rtbucket/pos", "rt-client-1", isVolatile: true)));
    await SendAsync(ws, new PublishMessage("rtbucket/pos", CreateEvent("rtbucket/pos", "rt-client-1", isVolatile: true)));
    ErrorMessage volatileLimited = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("RATE_LIMITED", volatileLimited.Code);
    Assert.Contains("Volatile", volatileLimited.Message);

    // The persisted bucket is untouched by the volatile stream: both allowed, the third is not.
    for (int i = 0; i < 2; i++)
    {
      await SendAsync(ws, new PublishMessage("rtbucket/chat", CreateEvent("rtbucket/chat", "rt-client-1")));
      Assert.IsType<AckMessage>(await ReceiveAsync(ws));
    }
    await SendAsync(ws, new PublishMessage("rtbucket/chat", CreateEvent("rtbucket/chat", "rt-client-1")));
    ErrorMessage persistedLimited = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("RATE_LIMITED", persistedLimited.Code);
    Assert.DoesNotContain("Volatile", persistedLimited.Message);
  }

  [Fact]
  public async Task VolatileRate_WhenUnset_SharesThePublishBucket()
  {
    await SetupAppAsync("rtshared", new AppQuotas(PublishRatePerMinute: 2));
    using WebSocket ws = await HelloAsync("rt-client-2");

    for (int i = 0; i < 2; i++)
      await SendAsync(ws, new PublishMessage("rtshared/pos", CreateEvent("rtshared/pos", "rt-client-2", isVolatile: true)));
    await SendAsync(ws, new PublishMessage("rtshared/chat", CreateEvent("rtshared/chat", "rt-client-2")));

    Assert.Equal("RATE_LIMITED", Assert.IsType<ErrorMessage>(await ReceiveAsync(ws)).Code);
  }

  [Fact]
  public async Task MaxConnections_RejectsTheConnectionOverTheCap_OnSubscribeHelloAndPublish()
  {
    await SetupAppAsync("rtconn", new AppQuotas(MaxConnections: 2));
    using WebSocket first = await HelloAsync("rt-conn-1");
    await SendAsync(first, new SubscribeMessage("rtconn/world"));
    using WebSocket second = await HelloAsync("rt-conn-2");
    await SendAsync(second, new SubscribeMessage("rtconn/world"));
    // Same connection joining another channel of the same app is not a new connection.
    await SendAsync(first, new SubscribeMessage("rtconn/chat"));
    await SendAsync(first, new PublishMessage("rtconn/chat", CreateEvent("rtconn/chat", "rt-conn-1")));
    Assert.IsType<AckMessage>(await ReceiveAsync(first));

    using WebSocket third = await HelloAsync("rt-conn-3");
    await SendAsync(third, new SubscribeMessage("rtconn/world"));
    ErrorMessage subscribeLimited = Assert.IsType<ErrorMessage>(await ReceiveAsync(third));
    Assert.Equal("CONNECTION_LIMITED", subscribeLimited.Code);
    Assert.Equal("rtconn/world", subscribeLimited.ChannelId);

    VestaEvent evt = CreateEvent("rtconn/world", "rt-conn-3");
    await SendAsync(third, new PublishMessage("rtconn/world", evt));
    ErrorMessage publishLimited = Assert.IsType<ErrorMessage>(await ReceiveAsync(third));
    Assert.Equal("CONNECTION_LIMITED", publishLimited.Code);
    Assert.Equal(evt.Id, publishLimited.EventId);

    using WebSocket fourth = await ConnectAsync();
    await SendAsync(fourth, new HelloMessage("rt-conn-4", ["rtconn/world"], new Dictionary<string, long>()));
    Assert.Equal("CONNECTION_LIMITED", Assert.IsType<ErrorMessage>(await ReceiveAsync(fourth)).Code);
    WelcomeMessage welcome = Assert.IsType<WelcomeMessage>(await ReceiveAsync(fourth));
    Assert.DoesNotContain("rtconn/world", welcome.Channels);
  }

  [Fact]
  public async Task TakePeakJoined_KeepsASpikeThatEndedBeforeTheSample_ThenFallsToTheCurrentCount()
  {
    await SetupAppAsync("rtpeak", new AppQuotas(MaxConnections: 10));
    ConnectionManager connections = _factory.Services.GetRequiredService<ConnectionManager>();
    List<WebSocket> sockets = [];
    for (int i = 0; i < 3; i++)
    {
      WebSocket ws = await HelloAsync($"rt-peak-{i}");
      sockets.Add(ws);
      await SendAsync(ws, new PublishMessage("rtpeak/world", CreateEvent("rtpeak/world", $"rt-peak-{i}")));
      Assert.IsType<AckMessage>(await ReceiveAsync(ws));
    }

    foreach (WebSocket ws in sockets)
      await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

    Assert.True(connections.TakePeakJoined("rtpeak") >= 3);
    await WaitUntilAsync(() => connections.TakePeakJoined("rtpeak") == 0);
    foreach (WebSocket ws in sockets)
      ws.Dispose();
  }

  [Fact]
  public async Task MaxConnections_DoesNotAffectOtherApps()
  {
    await SetupAppAsync("rtcapped", new AppQuotas(MaxConnections: 1));
    await SetupAppAsync("rtfree", new AppQuotas());
    using WebSocket capped = await HelloAsync("rt-other-1");
    await SendAsync(capped, new SubscribeMessage("rtcapped/world"));

    using WebSocket other = await HelloAsync("rt-other-2");
    await SendAsync(other, new SubscribeMessage("rtfree/world"));
    await SendAsync(other, new PublishMessage("rtfree/world", CreateEvent("rtfree/world", "rt-other-2")));

    Assert.IsType<AckMessage>(await ReceiveAsync(other));
  }

  private static async Task WaitUntilAsync(Func<bool> condition)
  {
    DateTime deadline = DateTime.UtcNow.AddSeconds(10);
    while (!condition())
    {
      Assert.True(DateTime.UtcNow < deadline, "Condition was not met within 10 seconds.");
      await Task.Delay(50);
    }
  }

  private async Task SetupAppAsync(string appId, AppQuotas quotas)
  {
    IAppStore store = _factory.Services.GetRequiredService<IAppStore>();
    await store.RegisterAsync(appId, "owner-" + appId);
    await store.SetQuotasAsync(appId, quotas);
  }

  private async Task<WebSocket> HelloAsync(string clientId)
  {
    WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage(clientId, [], new Dictionary<string, long>()));
    Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));
    return ws;
  }

  private async Task<WebSocket> ConnectAsync()
  {
    Microsoft.AspNetCore.TestHost.WebSocketClient client = _factory.Server.CreateWebSocketClient();
    return await client.ConnectAsync(new Uri(_factory.Server.BaseAddress, "/ws"), CancellationToken.None);
  }

  private static async Task SendAsync(WebSocket ws, ProtocolMessage message)
  {
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes<ProtocolMessage>(message, JsonOptions);
    await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
  }

  private static async Task<ProtocolMessage?> ReceiveAsync(WebSocket ws)
  {
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
    byte[] buffer = new byte[16384];
    using MemoryStream stream = new();

    ValueWebSocketReceiveResult result;
    do
    {
      result = await ws.ReceiveAsync(buffer.AsMemory(), timeout.Token);
      if (result.MessageType == WebSocketMessageType.Close)
        return null;

      stream.Write(buffer, 0, result.Count);
    }
    while (!result.EndOfMessage);

    stream.Position = 0;
    return await JsonSerializer.DeserializeAsync<ProtocolMessage>(stream, JsonOptions, timeout.Token);
  }

  private static VestaEvent CreateEvent(string channelId, string clientId, bool isVolatile = false) => new(
      Id: Guid.NewGuid(),
      ChannelId: channelId,
      Timestamp: DateTimeOffset.UtcNow,
      ClientId: clientId,
      EventType: "test.message",
      Payload: JsonDocument.Parse("""{"x":1}""").RootElement,
      Volatile: isVolatile ? true : null);
}
