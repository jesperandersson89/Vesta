using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vesta;

namespace VestaServer.Tests;

public class ChannelWriteControlTests(WebApplicationFactory<Program> baseFactory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;
    private readonly WebApplicationFactory<Program> _factory = baseFactory.WithWebHostBuilder(b => b.UseSetting("UseInMemoryStore", "true"));

    [Fact]
    public async Task Broadcast_AnyoneCanSubscribe_OnlyMembersAndAdminCanPublish()
    {
        const string channel = "bcast/shop";
        using WebSocket admin = await HelloAsync("shopkeeper");
        await SendAsync(admin, new CreateChannelMessage(channel, "broadcast", ["clerk"]));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));

        using WebSocket player = await HelloAsync("player-1", channel);
        using WebSocket clerk = await HelloAsync("clerk", channel);

        await SendAsync(player, new PublishMessage(channel, CreateEvent(channel, "player-1")));
        ErrorMessage denied = Assert.IsType<ErrorMessage>(await ReceiveAsync(player));
        Assert.Equal("ACCESS_DENIED", denied.Code);

        await SendAsync(clerk, new PublishMessage(channel, CreateEvent(channel, "clerk")));
        Assert.IsType<AckMessage>(await ReceiveAsync(clerk));
        Assert.IsType<EventMessage>(await ReceiveAsync(player));
        Assert.IsType<EventMessage>(await ReceiveAsync(admin));

        await SendAsync(admin, new PublishMessage(channel, CreateEvent(channel, "shopkeeper")));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));
        Assert.IsType<EventMessage>(await ReceiveAsync(player));
    }

    [Fact]
    public async Task Broadcast_Fetch_IsOpenToNonMembers()
    {
        const string channel = "bcast/history";
        using WebSocket admin = await HelloAsync("shopkeeper-2");
        await SendAsync(admin, new CreateChannelMessage(channel, "broadcast", []));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));
        await SendAsync(admin, new PublishMessage(channel, CreateEvent(channel, "shopkeeper-2")));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));

        using WebSocket player = await HelloAsync("player-2");
        await SendAsync(player, new FetchMessage(channel, FromSequence: 1));

        EventsBatchMessage batch = Assert.IsType<EventsBatchMessage>(await ReceiveAsync(player));
        Assert.Single(batch.Events);
    }

    [Fact]
    public async Task Broadcast_VolatilePublish_FromNonMember_IsDenied()
    {
        const string channel = "bcast/volatile";
        using WebSocket admin = await HelloAsync("shopkeeper-3");
        await SendAsync(admin, new CreateChannelMessage(channel, "broadcast", []));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));

        using WebSocket player = await HelloAsync("player-3", channel);
        VestaEvent volatileEvent = CreateEvent(channel, "player-3") with { Volatile = true };
        await SendAsync(player, new PublishMessage(channel, volatileEvent));

        ErrorMessage denied = Assert.IsType<ErrorMessage>(await ReceiveAsync(player));
        Assert.Equal("ACCESS_DENIED", denied.Code);
    }

    [Fact]
    public async Task Private_ReaderRole_CanReadButNotPublish_UntilUpgradedToMember()
    {
        const string channel = "rolechan/room";
        using WebSocket admin = await HelloAsync("room-admin");
        await SendAsync(admin, new CreateChannelMessage(channel, "private", []));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));
        await SendAsync(admin, new GrantAccessMessage(channel, "spectator", "reader"));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));

        using WebSocket spectator = await HelloAsync("spectator", channel);

        await SendAsync(admin, new PublishMessage(channel, CreateEvent(channel, "room-admin")));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));
        Assert.IsType<EventMessage>(await ReceiveAsync(spectator));

        await SendAsync(spectator, new PublishMessage(channel, CreateEvent(channel, "spectator")));
        ErrorMessage denied = Assert.IsType<ErrorMessage>(await ReceiveAsync(spectator));
        Assert.Equal("ACCESS_DENIED", denied.Code);

        await SendAsync(admin, new GrantAccessMessage(channel, "spectator", "member"));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));

        await SendAsync(spectator, new PublishMessage(channel, CreateEvent(channel, "spectator")));
        Assert.IsType<AckMessage>(await ReceiveAsync(spectator));
    }

    [Fact]
    public async Task Private_ReaderRole_CannotGrantAccess()
    {
        const string channel = "rolechan/grant";
        using WebSocket admin = await HelloAsync("grant-admin");
        await SendAsync(admin, new CreateChannelMessage(channel, "private", ["grant-reader"]));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));
        await SendAsync(admin, new GrantAccessMessage(channel, "grant-reader", "reader"));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));

        using WebSocket reader = await HelloAsync("grant-reader");
        await SendAsync(reader, new GrantAccessMessage(channel, "grant-reader", "admin"));

        ErrorMessage denied = Assert.IsType<ErrorMessage>(await ReceiveAsync(reader));
        Assert.Equal("ACCESS_DENIED", denied.Code);
    }

    [Fact]
    public async Task RelayManifestChannel_OnlyAppOwnerMayPublish_ButAnyoneMayRead()
    {
        const string channel = "mfapp/vesta/relays";
        using WebSocket owner = await HelloAsync("mf-owner");
        await SendAsync(owner, new RegisterAppMessage("mfapp"));
        Assert.IsType<AckMessage>(await ReceiveAsync(owner));

        using WebSocket stranger = await HelloAsync("mf-stranger", channel);
        await SendAsync(stranger, new PublishMessage(channel, CreateEvent(channel, "mf-stranger", "vesta.relay-manifest")));
        ErrorMessage denied = Assert.IsType<ErrorMessage>(await ReceiveAsync(stranger));
        Assert.Equal("ACCESS_DENIED", denied.Code);

        await SendAsync(owner, new PublishMessage(channel, CreateEvent(channel, "mf-owner", "vesta.relay-manifest")));
        Assert.IsType<AckMessage>(await ReceiveAsync(owner));
        Assert.IsType<EventMessage>(await ReceiveAsync(stranger));
    }

    [Fact]
    public async Task RelayManifestChannel_UnregisteredApp_StaysOpen()
    {
        const string channel = "openapp/vesta/relays";
        using WebSocket anyone = await HelloAsync("mf-anyone");

        await SendAsync(anyone, new PublishMessage(channel, CreateEvent(channel, "mf-anyone", "vesta.relay-manifest")));

        Assert.IsType<AckMessage>(await ReceiveAsync(anyone));
    }

    [Fact]
    public async Task Fetch_DeniedForNonMember_CarriesTheChannelIdSoClientsCanCorrelateIt()
    {
        const string channel = "fetchacl/room";
        using WebSocket admin = await HelloAsync("fetch-admin");
        await SendAsync(admin, new CreateChannelMessage(channel, "private", []));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));

        using WebSocket stranger = await HelloAsync("fetch-stranger");
        await SendAsync(stranger, new FetchMessage(channel, FromSequence: 0));

        ErrorMessage denied = Assert.IsType<ErrorMessage>(await ReceiveAsync(stranger));
        Assert.Equal("ACCESS_DENIED", denied.Code);
        Assert.Equal(channel, denied.ChannelId);
    }

    private async Task<WebSocket> HelloAsync(string clientId, params string[] channels)
    {
        Microsoft.AspNetCore.TestHost.WebSocketClient client = _factory.Server.CreateWebSocketClient();
        WebSocket ws = await client.ConnectAsync(new Uri(_factory.Server.BaseAddress, "/ws"), CancellationToken.None);
        await SendAsync(ws, new HelloMessage(clientId, channels, new Dictionary<string, long>()));
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

    private static VestaEvent CreateEvent(string channelId, string clientId, string eventType = "test.message") => new(
        Id: Guid.NewGuid(),
        ChannelId: channelId,
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: clientId,
        EventType: eventType,
        Payload: JsonDocument.Parse("""{"text":"hello"}""").RootElement);
}
