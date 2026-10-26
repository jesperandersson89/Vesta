using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vesta;

namespace VestaServer.Tests;

/// <summary>
/// The relay-stored identity backup end to end: a device parks the encrypted identity on the relay, and a
/// brand-new device (which has no key) recovers it knowing only app, username and password.
/// </summary>
public class IdentityBackupRecoveryFlowTests(WebApplicationFactory<Program> baseFactory) : IClassFixture<WebApplicationFactory<Program>>
{
  private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;
  private static readonly IdentityBackupOptions Fast = new(MemoryKiB: 256, Iterations: 1);
  private readonly WebApplicationFactory<Program> _factory = baseFactory.WithWebHostBuilder(b => b.UseSetting("UseInMemoryStore", "true"));

  [Fact]
  public async Task LostDevice_RecoversTheIdentityFromTheRelayWithJustUsernameAndPassword()
  {
    using VestaIdentity original = VestaIdentity.Generate();
    RelayBackup backup = RelayBackup.Derive("flowgame", "alice", "correct horse", Fast);

    using WebSocket oldDevice = await HelloAsync(original.ClientId);
    await SendAsync(oldDevice, new PublishMessage(backup.ChannelId, backup.CreateEvent(original)));
    Assert.IsType<AckMessage>(await ReceiveAsync(oldDevice));

    // New device: no key, just an ephemeral identity to talk to the relay.
    using VestaIdentity ephemeral = VestaIdentity.Generate();
    using WebSocket newDevice = await HelloAsync(ephemeral.ClientId);
    RelayBackup rederived = RelayBackup.Derive("flowgame", "alice", "correct horse", Fast);
    await SendAsync(newDevice, new FetchMessage(rederived.ChannelId, FromSequence: 1));
    EventsBatchMessage batch = Assert.IsType<EventsBatchMessage>(await ReceiveAsync(newDevice));

    using VestaIdentity? recovered = rederived.Restore(batch.Events.Select(e => e.Event));

    Assert.NotNull(recovered);
    Assert.Equal(original.ClientId, recovered.ClientId);
    Assert.Equal(original.ExportPrivateKey(), recovered.ExportPrivateKey());
  }

  [Fact]
  public async Task RepublishingTheBackup_ReplacesTheOldOne()
  {
    using VestaIdentity identity = VestaIdentity.Generate();
    RelayBackup backup = RelayBackup.Derive("flowgame2", "bob", "pw", Fast);
    using WebSocket device = await HelloAsync(identity.ClientId);

    for (int i = 0; i < 2; i++)
    {
      await SendAsync(device, new PublishMessage(backup.ChannelId, backup.CreateEvent(identity)));
      Assert.IsType<AckMessage>(await ReceiveAsync(device));
    }
    await SendAsync(device, new FetchMessage(backup.ChannelId, FromSequence: 1));

    EventsBatchMessage batch = Assert.IsType<EventsBatchMessage>(await ReceiveAsync(device));
    Assert.Single(batch.Events);
  }

  [Fact]
  public async Task JunkPublishedToTheChannel_DoesNotStopTheRealBackupFromRestoring()
  {
    using VestaIdentity victim = VestaIdentity.Generate();
    using VestaIdentity attacker = VestaIdentity.Generate();
    RelayBackup backup = RelayBackup.Derive("flowgame3", "carol", "pw", Fast);
    using WebSocket victimDevice = await HelloAsync(victim.ClientId);
    await SendAsync(victimDevice, new PublishMessage(backup.ChannelId, backup.CreateEvent(victim)));
    Assert.IsType<AckMessage>(await ReceiveAsync(victimDevice));

    // Someone who learned the channel id floods it with blobs under their own key and password.
    using WebSocket attackerDevice = await HelloAsync(attacker.ClientId);
    VestaEvent junk = RelayBackup.Derive("flowgame3", "carol", "attacker-pw", Fast).CreateEvent(attacker) with { ChannelId = backup.ChannelId };
    await SendAsync(attackerDevice, new PublishMessage(backup.ChannelId, EventSigner.SignEvent(junk with { Signature = null }, attacker)));
    Assert.IsType<AckMessage>(await ReceiveAsync(attackerDevice));

    await SendAsync(attackerDevice, new FetchMessage(backup.ChannelId, FromSequence: 1));
    EventsBatchMessage batch = Assert.IsType<EventsBatchMessage>(await ReceiveAsync(attackerDevice));
    Assert.Equal(2, batch.Events.Count);

    using VestaIdentity? recovered = backup.Restore(batch.Events.Select(e => e.Event));

    Assert.Equal(victim.ClientId, recovered?.ClientId);
  }

  private async Task<WebSocket> HelloAsync(string clientId)
  {
    Microsoft.AspNetCore.TestHost.WebSocketClient client = _factory.Server.CreateWebSocketClient();
    WebSocket ws = await client.ConnectAsync(new Uri(_factory.Server.BaseAddress, "/ws"), CancellationToken.None);
    await SendAsync(ws, new HelloMessage(clientId, [], new Dictionary<string, long>()));
    Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));
    return ws;
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
}
