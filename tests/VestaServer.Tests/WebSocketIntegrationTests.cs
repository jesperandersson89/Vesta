using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VestaCore.Events;
using VestaCore.Protocol;
using VestaCore.Serialization;

namespace VestaServer.Tests;

public class WebSocketIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;

    public WebSocketIntegrationTests(WebApplicationFactory<Program> factory)
    {
        // Use in-memory store for integration tests
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("UseInMemoryStore", "true");
        });
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsOk()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        string content = await response.Content.ReadAsStringAsync();
        Assert.Contains("healthy", content);
    }

    [Fact]
    public async Task WebSocket_Hello_ReturnsWelcome()
    {
        using WebSocket ws = await ConnectAsync();

        await SendAsync(ws, new HelloMessage(
            ClientId: "test-client-001",
            Channels: ["chat/general"],
            LastSequences: new Dictionary<string, long>()));

        ProtocolMessage? response = await ReceiveAsync(ws);

        WelcomeMessage welcome = Assert.IsType<WelcomeMessage>(response);
        Assert.Contains("chat/general", welcome.Channels);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task WebSocket_Publish_ReturnsAck()
    {
        using WebSocket ws = await ConnectAsync();

        await SendAsync(ws, new HelloMessage(
            ClientId: "test-client-001",
            Channels: ["chat/pub"],
            LastSequences: new Dictionary<string, long>()));
        await ReceiveAsync(ws); // WELCOME

        VestaEvent evt = CreateEvent("chat/pub");
        await SendAsync(ws, new PublishMessage("chat/pub", evt));

        ProtocolMessage? response = await ReceiveAsync(ws);

        AckMessage ack = Assert.IsType<AckMessage>(response);
        Assert.Equal("chat/pub", ack.ChannelId);
        Assert.Equal(evt.Id, ack.EventId);
        Assert.Equal(1L, ack.Sequence);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task WebSocket_Publish_BroadcastsToSubscribers()
    {
        using WebSocket publisher = await ConnectAsync();
        using WebSocket subscriber = await ConnectAsync();

        await SendAsync(publisher, new HelloMessage(
            ClientId: "publisher-001",
            Channels: ["chat/broadcast"],
            LastSequences: new Dictionary<string, long>()));
        await ReceiveAsync(publisher); // WELCOME

        await SendAsync(subscriber, new HelloMessage(
            ClientId: "subscriber-001",
            Channels: ["chat/broadcast"],
            LastSequences: new Dictionary<string, long>()));
        await ReceiveAsync(subscriber); // WELCOME

        // Publisher sends an event
        VestaEvent evt = CreateEvent("chat/broadcast");
        await SendAsync(publisher, new PublishMessage("chat/broadcast", evt));

        // Publisher gets ACK
        ProtocolMessage? pubResponse = await ReceiveAsync(publisher);
        Assert.IsType<AckMessage>(pubResponse);

        // Subscriber gets the EVENT broadcast
        ProtocolMessage? subResponse = await ReceiveAsync(subscriber);
        EventMessage eventMsg = Assert.IsType<EventMessage>(subResponse);
        Assert.Equal("chat/broadcast", eventMsg.ChannelId);
        Assert.Equal(evt.Id, eventMsg.Event.Id);
        Assert.Equal(1L, eventMsg.Sequence);

        await publisher.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await subscriber.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task WebSocket_Subscribe_WithCatchUp_ReceivesMissedEvents()
    {
        using WebSocket publisher = await ConnectAsync();

        await SendAsync(publisher, new HelloMessage(
            ClientId: "publisher-001",
            Channels: ["chat/catchup"],
            LastSequences: new Dictionary<string, long>()));
        await ReceiveAsync(publisher); // WELCOME

        // Publish two events
        await SendAsync(publisher, new PublishMessage("chat/catchup", CreateEvent("chat/catchup", "msg.first")));
        await ReceiveAsync(publisher); // ACK
        await SendAsync(publisher, new PublishMessage("chat/catchup", CreateEvent("chat/catchup", "msg.second")));
        await ReceiveAsync(publisher); // ACK

        // New subscriber connects with lastSequences indicating they've seen nothing (0)
        using WebSocket latecomer = await ConnectAsync();
        await SendAsync(latecomer, new HelloMessage(
            ClientId: "latecomer-001",
            Channels: ["chat/catchup"],
            LastSequences: new Dictionary<string, long> { ["chat/catchup"] = 0 }));

        // WELCOME
        ProtocolMessage? welcome = await ReceiveAsync(latecomer);
        Assert.IsType<WelcomeMessage>(welcome);

        // Catch-up batch
        ProtocolMessage? batch = await ReceiveAsync(latecomer);
        EventsBatchMessage batchMsg = Assert.IsType<EventsBatchMessage>(batch);
        Assert.Equal(2, batchMsg.Events.Count);
        Assert.Equal(1L, batchMsg.Events[0].Sequence);
        Assert.Equal(2L, batchMsg.Events[1].Sequence);

        await publisher.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await latecomer.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task WebSocket_Fetch_ReturnsEventsBatch()
    {
        using WebSocket ws = await ConnectAsync();

        await SendAsync(ws, new HelloMessage("client-001", ["chat/fetch"], new Dictionary<string, long>()));
        await ReceiveAsync(ws); // WELCOME

        // Publish 3 events
        for (int i = 0; i < 3; i++)
        {
            await SendAsync(ws, new PublishMessage("chat/fetch", CreateEvent("chat/fetch")));
            await ReceiveAsync(ws); // ACK
        }

        // Fetch from sequence 2
        await SendAsync(ws, new FetchMessage("chat/fetch", FromSequence: 2));
        ProtocolMessage? response = await ReceiveAsync(ws);

        EventsBatchMessage batch = Assert.IsType<EventsBatchMessage>(response);
        Assert.Equal(2, batch.Events.Count);
        Assert.Equal(2L, batch.Events[0].Sequence);
        Assert.Equal(3L, batch.Events[1].Sequence);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task WebSocket_Unsubscribe_StopsReceivingEvents()
    {
        using WebSocket ws1 = await ConnectAsync();
        using WebSocket ws2 = await ConnectAsync();

        await SendAsync(ws1, new HelloMessage("client-1", ["chat/unsub"], new Dictionary<string, long>()));
        await ReceiveAsync(ws1); // WELCOME

        await SendAsync(ws2, new HelloMessage("client-2", ["chat/unsub"], new Dictionary<string, long>()));
        await ReceiveAsync(ws2); // WELCOME

        // ws2 unsubscribes
        await SendAsync(ws2, new UnsubscribeMessage("chat/unsub"));

        // Small delay to allow the server to process the unsubscribe
        await Task.Delay(50);

        // ws1 publishes
        await SendAsync(ws1, new PublishMessage("chat/unsub", CreateEvent("chat/unsub")));
        await ReceiveAsync(ws1); // ACK

        // ws2 should NOT receive the event — timeout-based check
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(300));
        try
        {
            await ReceiveAsync(ws2, cts.Token);
            Assert.Fail("Should not have received a message after unsubscribe");
        }
        catch (OperationCanceledException)
        {
            // Expected — no message received within timeout
        }

        await ws1.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await ws2.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task WebSocket_InvalidChannel_ReturnsError()
    {
        using WebSocket ws = await ConnectAsync();

        await SendAsync(ws, new HelloMessage("client-1", ["INVALID!"], new Dictionary<string, long>()));

        // First message: ERROR for the invalid channel
        ProtocolMessage? first = await ReceiveAsync(ws);
        ErrorMessage error = Assert.IsType<ErrorMessage>(first);
        Assert.Equal("INVALID_CHANNEL", error.Code);

        // Second message: WELCOME with empty channels list
        ProtocolMessage? second = await ReceiveAsync(ws);
        WelcomeMessage welcome = Assert.IsType<WelcomeMessage>(second);
        Assert.DoesNotContain("INVALID!", welcome.Channels);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task WebSocket_MultipleChannels_IsolatedSequences()
    {
        using WebSocket ws = await ConnectAsync();

        await SendAsync(ws, new HelloMessage("client-1", ["iso-a", "iso-b"], new Dictionary<string, long>()));
        await ReceiveAsync(ws); // WELCOME

        await SendAsync(ws, new PublishMessage("iso-a", CreateEvent("iso-a")));
        AckMessage ack1 = Assert.IsType<AckMessage>(await ReceiveAsync(ws));
        Assert.Equal(1L, ack1.Sequence);

        await SendAsync(ws, new PublishMessage("iso-b", CreateEvent("iso-b")));
        AckMessage ack2 = Assert.IsType<AckMessage>(await ReceiveAsync(ws));
        Assert.Equal(1L, ack2.Sequence); // iso-b also starts at 1

        await SendAsync(ws, new PublishMessage("iso-a", CreateEvent("iso-a")));
        AckMessage ack3 = Assert.IsType<AckMessage>(await ReceiveAsync(ws));
        Assert.Equal(2L, ack3.Sequence); // iso-a is now at 2

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

    private static VestaEvent CreateEvent(string channelId, string eventType = "test.message")
    {
        JsonElement payload = JsonDocument.Parse("""{"text":"hello"}""").RootElement;
        return new VestaEvent(
            Id: Guid.NewGuid(),
            ChannelId: channelId,
            Timestamp: DateTimeOffset.UtcNow,
            ClientId: "test-client-id-123456",
            EventType: eventType,
            Payload: payload
        );
    }
}
