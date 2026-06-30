using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VestaCore.Events;
using VestaCore.Protocol;
using VestaCore.Serialization;

namespace VestaServer.Tests;

/// <summary>
/// Integration tests for the <c>Protocol:AllowedApps</c> operator allow-list — the simple
/// "flip a flag in appsettings" admission gate. With a non-empty allow-list only the listed
/// app namespaces may be used or registered, independent of <c>RequireAppRegistration</c>.
/// </summary>
public class AllowedAppsTests : IClassFixture<WebApplicationFactory<Program>>
{
  private readonly WebApplicationFactory<Program> _factory;
  private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;

  public AllowedAppsTests(WebApplicationFactory<Program> factory)
  {
    _factory = factory.WithWebHostBuilder(builder =>
    {
      builder.UseSetting("UseInMemoryStore", "true");
      // Only "myapp" is acknowledged. Note: RequireAppRegistration is left at its default
      // (false) to prove the allow-list gates on its own.
      builder.UseSetting("Protocol:AllowedApps:0", "myapp");
    });
  }

  [Fact]
  public async Task Publish_AllowedApp_Accepted_WithoutRegistration()
  {
    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-1", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws); // WELCOME

    VestaEvent evt = CreateEvent("myapp/chat", "client-1");
    await SendAsync(ws, new PublishMessage("myapp/chat", evt));

    AckMessage ack = Assert.IsType<AckMessage>(await ReceiveAsync(ws));
    Assert.Equal("myapp/chat", ack.ChannelId);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task Publish_DisallowedApp_Rejected()
  {
    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-2", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws);

    VestaEvent evt = CreateEvent("intruder/chat", "client-2");
    await SendAsync(ws, new PublishMessage("intruder/chat", evt));

    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("APP_NOT_ALLOWED", error.Code);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task Subscribe_DisallowedApp_Rejected()
  {
    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-3", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws);

    await SendAsync(ws, new SubscribeMessage("intruder/room", FromSequence: null));
    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("APP_NOT_ALLOWED", error.Code);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task RegisterApp_DisallowedApp_Rejected()
  {
    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-4", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws);

    await SendAsync(ws, new RegisterAppMessage("intruder"));
    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("APP_NOT_ALLOWED", error.Code);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task RegisterApp_AllowedApp_Succeeds()
  {
    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-5", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws);

    await SendAsync(ws, new RegisterAppMessage("myapp"));
    AckMessage ack = Assert.IsType<AckMessage>(await ReceiveAsync(ws));
    Assert.Equal("myapp", ack.ChannelId);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  // --- Helpers (copies of WebSocketIntegrationTests helpers) ---

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

  private static VestaEvent CreateEvent(string channelId, string clientId)
  {
    JsonElement payload = JsonDocument.Parse("""{"hi":1}""").RootElement;
    return new VestaEvent(
        Id: Guid.NewGuid(),
        ChannelId: channelId,
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: clientId,
        EventType: "test",
        Payload: payload);
  }
}
