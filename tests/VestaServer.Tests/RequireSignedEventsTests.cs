using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VestaCore.Events;
using VestaCore.Identity;
using VestaCore.Protocol;
using VestaCore.Serialization;
using VestaCore.Utilities;

namespace VestaServer.Tests;

/// <summary>
/// Integration tests for the strict <c>Protocol:RequireSignedEvents=true</c> mode.
/// In that mode HELLO must include a PublicKey and every PUBLISH must carry a
/// valid Ed25519 signature.
/// </summary>
public class RequireSignedEventsTests : IClassFixture<WebApplicationFactory<Program>>
{
  private readonly WebApplicationFactory<Program> _factory;
  private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;

  public RequireSignedEventsTests(WebApplicationFactory<Program> factory)
  {
    _factory = factory.WithWebHostBuilder(builder =>
    {
      builder.UseSetting("UseInMemoryStore", "true");
      builder.UseSetting("Protocol:RequireSignedEvents", "true");
    });
  }

  [Fact]
  public async Task Hello_WithoutPublicKey_IsRejected()
  {
    using WebSocket ws = await ConnectAsync();

    await SendAsync(ws, new HelloMessage(
        ClientId: "anon-client",
        Channels: ["sig/required"],
        LastSequences: new Dictionary<string, long>()));

    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("PUBLIC_KEY_REQUIRED", error.Code);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task Publish_SignedEvent_RoundTrips()
  {
    using VestaIdentity identity = VestaIdentity.Generate();
    using WebSocket ws = await ConnectAsync();

    await SendAsync(ws, new HelloMessage(
        ClientId: identity.ClientId,
        Channels: ["sig/ok"],
        LastSequences: new Dictionary<string, long>(),
        PublicKey: Base64Url.Encode(identity.PublicKey)));
    Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));

    JsonElement payload = JsonDocument.Parse("""{"text":"signed hello"}""").RootElement;
    VestaEvent evt = new(
        Id: Guid.NewGuid(),
        ChannelId: "sig/ok",
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: identity.ClientId,
        EventType: "sig.test",
        Payload: payload);

    VestaEvent signed = EventSigner.SignEvent(evt, identity);
    await SendAsync(ws, new PublishMessage("sig/ok", signed));

    AckMessage ack = Assert.IsType<AckMessage>(await ReceiveAsync(ws));
    Assert.Equal("sig/ok", ack.ChannelId);
    Assert.Equal(signed.Id, ack.EventId);
    Assert.Equal(1L, ack.Sequence);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task Publish_UnsignedEvent_IsRejected()
  {
    using VestaIdentity identity = VestaIdentity.Generate();
    using WebSocket ws = await ConnectAsync();

    await SendAsync(ws, new HelloMessage(
        ClientId: identity.ClientId,
        Channels: ["sig/unsigned"],
        LastSequences: new Dictionary<string, long>(),
        PublicKey: Base64Url.Encode(identity.PublicKey)));
    Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));

    // Build the event but do NOT sign it.
    JsonElement payload = JsonDocument.Parse("""{"text":"no sig"}""").RootElement;
    VestaEvent unsigned = new(
        Id: Guid.NewGuid(),
        ChannelId: "sig/unsigned",
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: identity.ClientId,
        EventType: "sig.test",
        Payload: payload);

    await SendAsync(ws, new PublishMessage("sig/unsigned", unsigned));

    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("SIGNATURE_REQUIRED", error.Code);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task Publish_TamperedSignature_IsRejected()
  {
    using VestaIdentity identity = VestaIdentity.Generate();
    using WebSocket ws = await ConnectAsync();

    await SendAsync(ws, new HelloMessage(
        ClientId: identity.ClientId,
        Channels: ["sig/tamper"],
        LastSequences: new Dictionary<string, long>(),
        PublicKey: Base64Url.Encode(identity.PublicKey)));
    Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));

    JsonElement payload = JsonDocument.Parse("""{"text":"original"}""").RootElement;
    VestaEvent evt = new(
        Id: Guid.NewGuid(),
        ChannelId: "sig/tamper",
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: identity.ClientId,
        EventType: "sig.test",
        Payload: payload);

    VestaEvent signed = EventSigner.SignEvent(evt, identity);

    // Tamper with the payload but keep the original signature.
    JsonElement tamperedPayload = JsonDocument.Parse("""{"text":"tampered"}""").RootElement;
    VestaEvent tampered = signed with { Payload = tamperedPayload };

    await SendAsync(ws, new PublishMessage("sig/tamper", tampered));

    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("INVALID_SIGNATURE", error.Code);

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
}
