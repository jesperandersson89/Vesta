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
/// Integration tests for <c>DELETE_CHANNEL</c> and the soft-delete behaviour.
/// Admin status is granted by listing a client's Ed25519 public key in
/// <c>Admin:BootstrapPublicKeys</c>. Once a channel is deleted, further
/// PUBLISH / SUBSCRIBE / FETCH / CREATE_CHANNEL for that ID are rejected with
/// <c>CHANNEL_DELETED</c>.
/// </summary>
public class DeleteChannelTests : IClassFixture<DeleteChannelTests.Fixture>
{
  private readonly Fixture _fixture;
  private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;

  public DeleteChannelTests(Fixture fixture) => _fixture = fixture;

  /// <summary>
  /// xUnit fixture that owns the admin identity and a <see cref="WebApplicationFactory{TEntryPoint}"/>
  /// configured with that identity's public key in <c>Admin:BootstrapPublicKeys</c>.
  /// </summary>
  public sealed class Fixture : IDisposable
  {
    public VestaIdentity AdminIdentity { get; } = VestaIdentity.Generate();
    public WebApplicationFactory<Program> Factory { get; }

    public Fixture()
    {
      VestaIdentity admin = AdminIdentity;
      Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
      {
        builder.UseSetting("UseInMemoryStore", "true");
        builder.UseSetting("Admin:BootstrapPublicKeys:0", Base64Url.Encode(admin.PublicKey));
      });
    }

    public void Dispose()
    {
      Factory.Dispose();
      AdminIdentity.Dispose();
    }
  }

  [Fact]
  public async Task DeleteChannel_AsAdmin_Succeeds()
  {
    using WebSocket ws = await ConnectAsync();
    await HelloAsync(ws, _fixture.AdminIdentity, ["delete/me"]);
    Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));

    // Implicitly create the channel by publishing.
    VestaEvent evt = EventSigner.SignEvent(
        CreateEvent("delete/me", _fixture.AdminIdentity.ClientId), _fixture.AdminIdentity);
    await SendAsync(ws, new PublishMessage("delete/me", evt));
    Assert.IsType<AckMessage>(await ReceiveAsync(ws));

    await SendAsync(ws, new DeleteChannelMessage("delete/me"));
    AckMessage ack = Assert.IsType<AckMessage>(await ReceiveAsync(ws));
    Assert.Equal("delete/me", ack.ChannelId);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task DeleteChannel_AsNonAdmin_Rejected()
  {
    using VestaIdentity nonAdmin = VestaIdentity.Generate();
    using WebSocket ws = await ConnectAsync();
    await HelloAsync(ws, nonAdmin, ["nondel/chan"]);
    Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));

    // Create the channel first so we know it exists.
    VestaEvent evt = EventSigner.SignEvent(
        CreateEvent("nondel/chan", nonAdmin.ClientId), nonAdmin);
    await SendAsync(ws, new PublishMessage("nondel/chan", evt));
    Assert.IsType<AckMessage>(await ReceiveAsync(ws));

    await SendAsync(ws, new DeleteChannelMessage("nondel/chan"));
    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("NOT_ADMIN", error.Code);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task DeleteChannel_WithoutHello_Rejected()
  {
    using WebSocket ws = await ConnectAsync();

    await SendAsync(ws, new DeleteChannelMessage("any/thing"));
    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("HELLO_REQUIRED", error.Code);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task DeleteChannel_NotFound_Rejected()
  {
    using WebSocket ws = await ConnectAsync();
    await HelloAsync(ws, _fixture.AdminIdentity, []);
    Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));

    await SendAsync(ws, new DeleteChannelMessage("missing/channel"));
    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("CHANNEL_NOT_FOUND", error.Code);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task PublishToDeletedChannel_Rejected()
  {
    using WebSocket admin = await ConnectAsync();
    await HelloAsync(admin, _fixture.AdminIdentity, ["gone/chan"]);
    Assert.IsType<WelcomeMessage>(await ReceiveAsync(admin));

    VestaEvent created = EventSigner.SignEvent(
        CreateEvent("gone/chan", _fixture.AdminIdentity.ClientId), _fixture.AdminIdentity);
    await SendAsync(admin, new PublishMessage("gone/chan", created));
    Assert.IsType<AckMessage>(await ReceiveAsync(admin));

    await SendAsync(admin, new DeleteChannelMessage("gone/chan"));
    Assert.IsType<AckMessage>(await ReceiveAsync(admin));

    // Same connection now tries to publish again.
    VestaEvent again = EventSigner.SignEvent(
        CreateEvent("gone/chan", _fixture.AdminIdentity.ClientId), _fixture.AdminIdentity);
    await SendAsync(admin, new PublishMessage("gone/chan", again));
    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(admin));
    Assert.Equal("CHANNEL_DELETED", error.Code);

    await admin.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task SubscribeToDeletedChannel_Rejected()
  {
    using WebSocket admin = await ConnectAsync();
    await HelloAsync(admin, _fixture.AdminIdentity, ["bye/chan"]);
    Assert.IsType<WelcomeMessage>(await ReceiveAsync(admin));

    VestaEvent created = EventSigner.SignEvent(
        CreateEvent("bye/chan", _fixture.AdminIdentity.ClientId), _fixture.AdminIdentity);
    await SendAsync(admin, new PublishMessage("bye/chan", created));
    Assert.IsType<AckMessage>(await ReceiveAsync(admin));

    await SendAsync(admin, new DeleteChannelMessage("bye/chan"));
    Assert.IsType<AckMessage>(await ReceiveAsync(admin));

    // A new client tries to subscribe.
    using VestaIdentity other = VestaIdentity.Generate();
    using WebSocket sub = await ConnectAsync();
    await HelloAsync(sub, other, []);
    Assert.IsType<WelcomeMessage>(await ReceiveAsync(sub));

    await SendAsync(sub, new SubscribeMessage("bye/chan", FromSequence: null));
    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(sub));
    Assert.Equal("CHANNEL_DELETED", error.Code);

    await sub.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    await admin.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task DeleteChannel_Idempotent()
  {
    using WebSocket ws = await ConnectAsync();
    await HelloAsync(ws, _fixture.AdminIdentity, ["double/del"]);
    Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));

    VestaEvent evt = EventSigner.SignEvent(
        CreateEvent("double/del", _fixture.AdminIdentity.ClientId), _fixture.AdminIdentity);
    await SendAsync(ws, new PublishMessage("double/del", evt));
    Assert.IsType<AckMessage>(await ReceiveAsync(ws));

    await SendAsync(ws, new DeleteChannelMessage("double/del"));
    Assert.IsType<AckMessage>(await ReceiveAsync(ws));

    // Second delete on a tombstoned channel should still ACK \u2014 row exists,
    // deleted_at is preserved.
    await SendAsync(ws, new DeleteChannelMessage("double/del"));
    Assert.IsType<AckMessage>(await ReceiveAsync(ws));

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  [Fact]
  public async Task NonAdminConnection_DoesNotInheritAdmin()
  {
    // Sanity check: a connection without a public key is never admin.
    using WebSocket ws = await ConnectAsync();
    await SendAsync(ws, new HelloMessage(
        ClientId: "no-key-client",
        Channels: [],
        LastSequences: new Dictionary<string, long>()));
    Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));

    await SendAsync(ws, new DeleteChannelMessage("any/thing"));
    ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
    Assert.Equal("NOT_ADMIN", error.Code);

    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
  }

  // --- Helpers ---

  private async Task<WebSocket> ConnectAsync()
  {
    Microsoft.AspNetCore.TestHost.WebSocketClient wsClient = _fixture.Factory.Server.CreateWebSocketClient();
    return await wsClient.ConnectAsync(
        new Uri(_fixture.Factory.Server.BaseAddress, "/ws"),
        CancellationToken.None);
  }

  private static async Task HelloAsync(WebSocket ws, VestaIdentity identity, IReadOnlyList<string> channels)
  {
    await SendAsync(ws, new HelloMessage(
        ClientId: identity.ClientId,
        Channels: channels,
        LastSequences: new Dictionary<string, long>(),
        PublicKey: Base64Url.Encode(identity.PublicKey)));
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
