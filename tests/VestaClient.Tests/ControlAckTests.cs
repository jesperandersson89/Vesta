using Vesta;

namespace VestaClient.Tests;

/// <summary>Control requests (<c>CREATE_CHANNEL</c> etc.) are acknowledged with the nil event id and reach <see cref="VestaConnection.OnAck"/>.</summary>
public class ControlAckTests
{
    [Fact]
    public async Task CreateChannelAsync_SurfacesTheControlAckOnOnAck()
    {
        await using ScriptedFetchRelay relay = await ScriptedFetchRelay.StartAsync(eventCount: 0, maxPage: 3);
        using VestaIdentity identity = VestaIdentity.Generate();
        VestaAppConfig config = new("game", identity.PublicKey, [relay.Url]);
        await using VestaConnection connection = new(identity, config, relayDirectory: new RelayDirectory(config));
        await connection.ConnectAsync(relay.Url, []);
        TaskCompletionSource<AckMessage> acked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnAck += ack => acked.TrySetResult(ack);

        await connection.CreateChannelAsync("game/shop", "broadcast");

        AckMessage ack = await acked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("game/shop", ack.ChannelId);
        Assert.Equal(Guid.Empty, ack.EventId);
        Assert.Equal(0, ack.Sequence);
    }
}
