using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VestaCore.Events;
using VestaCore.Protocol;
using VestaCore.Serialization;

namespace VestaServer.Tests;

/// <summary>
/// Integration tests for the strict <c>Protocol:RequireAppRegistration=true</c> mode.
/// Every channel-creating message must target a registered app namespace
/// (the first slug segment of the channel ID).
/// </summary>
public class RequireAppRegistrationTests : IClassFixture<WebApplicationFactory<Program>>
{
  private readonly WebApplicationFactory<Program> _factory;
  private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;

  public RequireAppRegistrationTests(WebApplicationFactory<Program> factory)
  {
    _factory = factory.WithWebHostBuilder(builder =>
    {
      builder.UseSetting("UseInMemoryStore", "true");
      builder.UseSetting("Protocol:RequireAppRegistration", "true");
    });
  }

  [Fact]
  public async Task RegisterApp_Succeeds()
  {
    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-1", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws); // WELCOME

    await SendAsync(ws, new RegisterAppMessage("myapp"));
    AckMessage ack = Assert.IsType<AckMessage>(await ReceiveAsync(ws));
    Assert.Equal("myapp", ack.ChannelId);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task RegisterApp_BeforeHello_Rejected()
  {
    using WebSocket ws = await ConnectAsync();

    await SendAsync(ws, new RegisterAppMessage("myapp"));
    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("HELLO_REQUIRED", error.Code);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task RegisterApp_Duplicate_Rejected()
  {
    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-2", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws);

    await SendAsync(ws, new RegisterAppMessage("dupapp"));
    Assert.IsType<AckMessage>(await ReceiveAsync(ws));

    await SendAsync(ws, new RegisterAppMessage("dupapp"));
    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("DUPLICATE_APP", error.Code);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task RegisterApp_InvalidId_Rejected()
  {
    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-3", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws);

    await SendAsync(ws, new RegisterAppMessage("Bad/Id"));
    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("INVALID_APP", error.Code);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task Publish_UnknownApp_Rejected()
  {
    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-4", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws);

    VestaEvent evt = CreateEvent("unregistered/chat", "client-4");
    await SendAsync(ws, new PublishMessage("unregistered/chat", evt));

    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("UNKNOWN_APP", error.Code);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task Publish_RegisteredApp_Accepted()
  {
    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-5", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws);

    await SendAsync(ws, new RegisterAppMessage("realapp"));
    Assert.IsType<AckMessage>(await ReceiveAsync(ws));

    VestaEvent evt = CreateEvent("realapp/chat", "client-5");
    await SendAsync(ws, new PublishMessage("realapp/chat", evt));

    AckMessage ack = Assert.IsType<AckMessage>(await ReceiveAsync(ws));
    Assert.Equal("realapp/chat", ack.ChannelId);
    Assert.Equal(1L, ack.Sequence);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task Subscribe_UnknownApp_Rejected()
  {
    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-6", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws);

    await SendAsync(ws, new SubscribeMessage("ghost/room", FromSequence: null));
    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("UNKNOWN_APP", error.Code);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task CreateChannel_UnknownApp_Rejected()
  {
    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage("client-7", [], new Dictionary<string, long>()));
    await ReceiveAsync(ws);

    await SendAsync(ws, new CreateChannelMessage("ghost/priv", "private", []));
    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("UNKNOWN_APP", error.Code);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task Hello_WithUnknownAppChannel_RejectsThatChannel()
  {
    using WebSocket ws = await ConnectAsync();

    // HELLO referencing a channel under an unregistered app — server should send an
    // UNKNOWN_APP error frame and still WELCOME the connection with that channel rejected.
    await SendAsync(ws, new HelloMessage("client-8", ["nope/chat"], new Dictionary<string, long>()));

    ProtocolMessage? first = await ReceiveAsync(ws);
    ErrorMessage error = Assert.IsType<ErrorMessage>(first);
    Assert.Equal("UNKNOWN_APP", error.Code);

    ProtocolMessage? second = await ReceiveAsync(ws);
    WelcomeMessage welcome = Assert.IsType<WelcomeMessage>(second);
    Assert.DoesNotContain("nope/chat", welcome.Channels);

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
