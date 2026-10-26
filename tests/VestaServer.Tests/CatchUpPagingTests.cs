using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vesta;

namespace VestaServer.Tests;

public class CatchUpPagingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const int PageSize = 3;
    private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;
    private readonly WebApplicationFactory<Program> _factory;

    public CatchUpPagingTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("UseInMemoryStore", "true");
            builder.UseSetting("Protocol:MaxFetchLimit", PageSize.ToString());
        });
    }

    [Fact]
    public async Task Hello_CatchUp_WithBacklogLargerThanPage_DeliversAllEventsInPages()
    {
        const string channel = "chat/paged-hello";
        using WebSocket publisher = await ConnectAsync();
        await PublishManyAsync(publisher, channel, count: 7);

        using WebSocket latecomer = await ConnectAsync();
        await SendAsync(latecomer, new HelloMessage(
            "latecomer-001", [channel], new Dictionary<string, long> { [channel] = 0 }));
        Assert.IsType<WelcomeMessage>(await ReceiveAsync(latecomer));

        List<long> sequences = await ReadBatchesAsync(latecomer, expectedEvents: 7);

        Assert.Equal(Enumerable.Range(1, 7).Select(i => (long)i), sequences);
    }

    [Fact]
    public async Task Subscribe_FromSequence_WithBacklogLargerThanPage_DeliversAllEventsInPages()
    {
        const string channel = "chat/paged-subscribe";
        using WebSocket publisher = await ConnectAsync();
        await PublishManyAsync(publisher, channel, count: 7);

        using WebSocket reader = await ConnectAsync();
        await SendAsync(reader, new HelloMessage("reader-001", [], new Dictionary<string, long>()));
        Assert.IsType<WelcomeMessage>(await ReceiveAsync(reader));
        await SendAsync(reader, new SubscribeMessage(channel, FromSequence: 1));

        List<long> sequences = await ReadBatchesAsync(reader, expectedEvents: 7);

        Assert.Equal(Enumerable.Range(1, 7).Select(i => (long)i), sequences);
    }

    [Fact]
    public async Task Fetch_LimitAboveMax_IsClampedToMaxFetchLimit()
    {
        const string channel = "chat/paged-fetch";
        using WebSocket ws = await ConnectAsync();
        await PublishManyAsync(ws, channel, count: 7);

        await SendAsync(ws, new FetchMessage(channel, FromSequence: 1, Limit: 1000));
        EventsBatchMessage batch = Assert.IsType<EventsBatchMessage>(await ReceiveAsync(ws));

        Assert.Equal(PageSize, batch.Events.Count);
        Assert.Equal(1L, batch.Events[0].Sequence);
    }

    private async Task PublishManyAsync(WebSocket ws, string channel, int count)
    {
        await SendAsync(ws, new HelloMessage("publisher-001", [channel], new Dictionary<string, long>()));
        Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));

        for (int i = 0; i < count; i++)
        {
            await SendAsync(ws, new PublishMessage(channel, CreateEvent(channel, "publisher-001")));
            Assert.IsType<AckMessage>(await ReceiveAsync(ws));
        }
    }

    private static async Task<List<long>> ReadBatchesAsync(WebSocket ws, int expectedEvents)
    {
        List<long> sequences = [];
        while (sequences.Count < expectedEvents)
        {
            EventsBatchMessage batch = Assert.IsType<EventsBatchMessage>(await ReceiveAsync(ws));
            Assert.InRange(batch.Events.Count, 1, PageSize);
            sequences.AddRange(batch.Events.Select(e => e.Sequence));
        }

        return sequences;
    }

    private async Task<WebSocket> ConnectAsync()
    {
        Microsoft.AspNetCore.TestHost.WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        return await wsClient.ConnectAsync(new Uri(_factory.Server.BaseAddress, "/ws"), CancellationToken.None);
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
