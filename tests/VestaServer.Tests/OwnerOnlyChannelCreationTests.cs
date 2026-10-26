using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vesta;
using VestaServer.Storage;

namespace VestaServer.Tests;

public class OwnerOnlyChannelCreationTests(WebApplicationFactory<Program> baseFactory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;
    private readonly WebApplicationFactory<Program> _factory = baseFactory.WithWebHostBuilder(b => b.UseSetting("UseInMemoryStore", "true"));

    private IAppStore Apps => _factory.Services.GetRequiredService<IAppStore>();

    private async Task RegisterAsync(string appId, string owner, bool ownerOnly)
    {
        await Apps.RegisterAsync(appId, owner);
        await Apps.SetOwnerOnlyChannelCreationAsync(appId, ownerOnly);
    }

    [Fact]
    public async Task CreateChannel_OwnerOnlyApp_DeniesStrangerAndAllowsOwner()
    {
        await RegisterAsync("oocreate", "oo-owner", ownerOnly: true);
        using WebSocket stranger = await HelloAsync("oo-stranger");
        using WebSocket owner = await HelloAsync("oo-owner");

        await SendAsync(stranger, new CreateChannelMessage("oocreate/shop", "broadcast", []));
        ErrorMessage denied = Assert.IsType<ErrorMessage>(await ReceiveAsync(stranger));
        Assert.Equal("ACCESS_DENIED", denied.Code);

        await SendAsync(owner, new CreateChannelMessage("oocreate/shop", "broadcast", []));
        Assert.IsType<AckMessage>(await ReceiveAsync(owner));
    }

    [Fact]
    public async Task Publish_OwnerOnlyApp_StrangerCannotImplicitlyCreateChannel()
    {
        await RegisterAsync("ooimplicit", "oo-owner-2", ownerOnly: true);
        using WebSocket stranger = await HelloAsync("oo-stranger-2");

        await SendAsync(stranger, new PublishMessage("ooimplicit/squat", CreateEvent("ooimplicit/squat", "oo-stranger-2")));

        ErrorMessage denied = Assert.IsType<ErrorMessage>(await ReceiveAsync(stranger));
        Assert.Equal("ACCESS_DENIED", denied.Code);
        Assert.Null(await _factory.Services.GetRequiredService<IChannelAccessStore>().GetVisibilityAsync("ooimplicit/squat"));
    }

    [Fact]
    public async Task Publish_OwnerOnlyApp_StrangerMayStillPublishToAnExistingPublicChannel()
    {
        await RegisterAsync("ooexisting", "oo-owner-3", ownerOnly: true);
        using WebSocket owner = await HelloAsync("oo-owner-3");
        await SendAsync(owner, new PublishMessage("ooexisting/lobby", CreateEvent("ooexisting/lobby", "oo-owner-3")));
        Assert.IsType<AckMessage>(await ReceiveAsync(owner));

        using WebSocket stranger = await HelloAsync("oo-stranger-3");
        await SendAsync(stranger, new PublishMessage("ooexisting/lobby", CreateEvent("ooexisting/lobby", "oo-stranger-3")));

        Assert.IsType<AckMessage>(await ReceiveAsync(stranger));
    }

    [Fact]
    public async Task Publish_OwnerOnlyApp_StrangerCannotPublishToBroadcastChannelTheOwnerClaimed()
    {
        await RegisterAsync("ooshop", "oo-shopkeeper", ownerOnly: true);
        using WebSocket owner = await HelloAsync("oo-shopkeeper");
        await SendAsync(owner, new CreateChannelMessage("ooshop/shop", "broadcast", []));
        Assert.IsType<AckMessage>(await ReceiveAsync(owner));

        using WebSocket stranger = await HelloAsync("oo-stranger-4");
        await SendAsync(stranger, new PublishMessage("ooshop/shop", CreateEvent("ooshop/shop", "oo-stranger-4")));

        ErrorMessage denied = Assert.IsType<ErrorMessage>(await ReceiveAsync(stranger));
        Assert.Equal("ACCESS_DENIED", denied.Code);
    }

    [Fact]
    public async Task CreateChannel_DefaultApp_AnyoneMayCreate()
    {
        await RegisterAsync("ooopen", "oo-owner-5", ownerOnly: false);
        using WebSocket stranger = await HelloAsync("oo-stranger-5");

        await SendAsync(stranger, new CreateChannelMessage("ooopen/room", "private", []));

        Assert.IsType<AckMessage>(await ReceiveAsync(stranger));
    }

    [Fact]
    public async Task CreateChannel_OwnerOnlyApp_OpeningItAgainLetsStrangersCreate()
    {
        await RegisterAsync("oototoggle", "oo-owner-6", ownerOnly: true);
        await Apps.SetOwnerOnlyChannelCreationAsync("oototoggle", false);
        using WebSocket stranger = await HelloAsync("oo-stranger-6");

        await SendAsync(stranger, new CreateChannelMessage("oototoggle/room", "public", []));

        Assert.IsType<AckMessage>(await ReceiveAsync(stranger));
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

    private static VestaEvent CreateEvent(string channelId, string clientId) => new(
        Id: Guid.NewGuid(),
        ChannelId: channelId,
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: clientId,
        EventType: "test.message",
        Payload: JsonDocument.Parse("""{"text":"hello"}""").RootElement);
}
