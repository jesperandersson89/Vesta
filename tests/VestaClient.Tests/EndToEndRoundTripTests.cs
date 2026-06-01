using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VestaClient;
using VestaCore.Events;
using VestaCore.Protocol;

namespace VestaClient.Tests;

/// <summary>
/// End-to-end tests proving the full Client → Server → Client event flow.
/// Uses VestaTestClient against a real server via WebApplicationFactory.
/// </summary>
public class EndToEndRoundTripTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public EndToEndRoundTripTests(WebApplicationFactory<Program> factory)
    {
        // Use in-memory store for integration tests
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("UseInMemoryStore", "true");
        });
    }

    [Fact]
    public async Task Client_ConnectsAndReceivesWelcome()
    {
        await using VestaTestClient client = await CreateClientAsync("client-001", ["chat/e2e"]);

        Assert.NotNull(client.ServerId);
        Assert.Contains("chat/e2e", client.Channels);
    }

    [Fact]
    public async Task Client_PublishesAndReceivesAck()
    {
        await using VestaTestClient client = await CreateClientAsync("client-001", ["chat/ack"]);

        ConcurrentQueue<AckMessage> acks = new();
        client.OnAck += ack => acks.Enqueue(ack);

        VestaEvent evt = CreateEvent("chat/ack");
        await client.PublishAsync(evt);

        // Wait for ACK
        await WaitForConditionAsync(() => !acks.IsEmpty);

        Assert.True(acks.TryDequeue(out AckMessage? received));
        Assert.Equal("chat/ack", received.ChannelId);
        Assert.Equal(evt.Id, received.EventId);
        Assert.Equal(1L, received.Sequence);
    }

    [Fact]
    public async Task TwoClients_PublishAndReceive_FullRoundTrip()
    {
        await using VestaTestClient alice = await CreateClientAsync("alice", ["chat/roundtrip"]);
        await using VestaTestClient bob = await CreateClientAsync("bob", ["chat/roundtrip"]);

        ConcurrentQueue<EventMessage> bobEvents = new();
        bob.OnEvent += evt => bobEvents.Enqueue(evt);

        ConcurrentQueue<AckMessage> aliceAcks = new();
        alice.OnAck += ack => aliceAcks.Enqueue(ack);

        // Alice publishes
        VestaEvent evt = CreateEvent("chat/roundtrip", "app.chat.message");
        await alice.PublishAsync(evt);

        // Alice gets ACK
        await WaitForConditionAsync(() => !aliceAcks.IsEmpty);
        Assert.True(aliceAcks.TryDequeue(out AckMessage? ack));
        Assert.Equal(1L, ack.Sequence);

        // Bob receives the event
        await WaitForConditionAsync(() => !bobEvents.IsEmpty);
        Assert.True(bobEvents.TryDequeue(out EventMessage? received));
        Assert.Equal("chat/roundtrip", received.ChannelId);
        Assert.Equal(evt.Id, received.Event.Id);
        Assert.Equal("app.chat.message", received.Event.EventType);
        Assert.Equal(1L, received.Sequence);
    }

    [Fact]
    public async Task Client_ReceivesCatchUpEvents_OnConnect()
    {
        // First client publishes some events
        await using VestaTestClient publisher = await CreateClientAsync("publisher", ["chat/catchup2"]);

        ConcurrentQueue<AckMessage> acks = new();
        publisher.OnAck += ack => acks.Enqueue(ack);

        await publisher.PublishAsync(CreateEvent("chat/catchup2", "msg.1"));
        await WaitForConditionAsync(() => acks.Count >= 1);
        await publisher.PublishAsync(CreateEvent("chat/catchup2", "msg.2"));
        await WaitForConditionAsync(() => acks.Count >= 2);
        await publisher.PublishAsync(CreateEvent("chat/catchup2", "msg.3"));
        await WaitForConditionAsync(() => acks.Count >= 3);

        // Second client connects with lastSequences = { channel: 1 } (has seen seq 1)
        await using VestaTestClient latecomer = await CreateClientAsync(
            "latecomer",
            ["chat/catchup2"],
            new Dictionary<string, long> { ["chat/catchup2"] = 1 });

        ConcurrentQueue<EventsBatchMessage> batches = new();
        latecomer.OnEventsBatch += batch => batches.Enqueue(batch);

        // Wait for the catch-up batch
        await WaitForConditionAsync(() => !batches.IsEmpty);

        Assert.True(batches.TryDequeue(out EventsBatchMessage? batchMsg));
        Assert.Equal(2, batchMsg.Events.Count); // seq 2 and 3
        Assert.Equal(2L, batchMsg.Events[0].Sequence);
        Assert.Equal(3L, batchMsg.Events[1].Sequence);
    }

    [Fact]
    public async Task Client_Fetch_ReturnsHistoricalEvents()
    {
        await using VestaTestClient client = await CreateClientAsync("client-001", ["chat/fetch2"]);

        ConcurrentQueue<AckMessage> acks = new();
        client.OnAck += ack => acks.Enqueue(ack);

        // Publish 5 events
        for (int i = 0; i < 5; i++)
        {
            await client.PublishAsync(CreateEvent("chat/fetch2"));
            await WaitForConditionAsync(() => acks.Count >= i + 1);
        }

        ConcurrentQueue<EventsBatchMessage> batches = new();
        client.OnEventsBatch += batch => batches.Enqueue(batch);

        // Fetch from sequence 3
        await client.FetchAsync("chat/fetch2", fromSequence: 3);

        await WaitForConditionAsync(() => !batches.IsEmpty);

        Assert.True(batches.TryDequeue(out EventsBatchMessage? batchMsg));
        Assert.Equal(3, batchMsg.Events.Count); // seq 3, 4, 5
        Assert.Equal(3L, batchMsg.Events[0].Sequence);
        Assert.Equal(5L, batchMsg.Events[2].Sequence);
    }

    [Fact]
    public async Task MultipleClients_BidirectionalFlow()
    {
        await using VestaTestClient alice = await CreateClientAsync("alice", ["chat/bidir"]);
        await using VestaTestClient bob = await CreateClientAsync("bob", ["chat/bidir"]);

        ConcurrentQueue<EventMessage> aliceEvents = new();
        ConcurrentQueue<EventMessage> bobEvents = new();
        alice.OnEvent += evt => aliceEvents.Enqueue(evt);
        bob.OnEvent += evt => bobEvents.Enqueue(evt);

        ConcurrentQueue<AckMessage> aliceAcks = new();
        ConcurrentQueue<AckMessage> bobAcks = new();
        alice.OnAck += ack => aliceAcks.Enqueue(ack);
        bob.OnAck += ack => bobAcks.Enqueue(ack);

        // Alice sends
        await alice.PublishAsync(CreateEvent("chat/bidir", "alice.msg"));
        await WaitForConditionAsync(() => !aliceAcks.IsEmpty);

        // Bob receives Alice's message
        await WaitForConditionAsync(() => !bobEvents.IsEmpty);
        Assert.True(bobEvents.TryDequeue(out EventMessage? fromAlice));
        Assert.Equal("alice.msg", fromAlice.Event.EventType);

        // Bob sends back
        await bob.PublishAsync(CreateEvent("chat/bidir", "bob.reply"));
        await WaitForConditionAsync(() => !bobAcks.IsEmpty);

        // Alice receives Bob's reply
        await WaitForConditionAsync(() => !aliceEvents.IsEmpty);
        Assert.True(aliceEvents.TryDequeue(out EventMessage? fromBob));
        Assert.Equal("bob.reply", fromBob.Event.EventType);
    }

    [Fact]
    public async Task Client_SequencesAreMonotonic()
    {
        await using VestaTestClient client = await CreateClientAsync("client-001", ["chat/mono"]);

        ConcurrentQueue<AckMessage> acks = new();
        client.OnAck += ack => acks.Enqueue(ack);

        for (int i = 0; i < 10; i++)
        {
            await client.PublishAsync(CreateEvent("chat/mono"));
        }

        await WaitForConditionAsync(() => acks.Count >= 10);

        long[] sequences = acks.Select(a => a.Sequence).ToArray();
        for (int i = 0; i < sequences.Length; i++)
        {
            Assert.Equal(i + 1L, sequences[i]);
        }
    }

    [Fact]
    public async Task Client_DisconnectsGracefully()
    {
        await using VestaTestClient client = await CreateClientAsync("client-001", ["chat/disc"]);

        bool disconnected = false;
        client.OnDisconnected += _ => disconnected = true;

        await client.DisconnectAsync();

        await WaitForConditionAsync(() => disconnected, timeoutMs: 2000);
        Assert.True(disconnected);
    }

    // --- Helpers ---

    private async Task<VestaTestClient> CreateClientAsync(
        string clientId,
        IReadOnlyList<string> channels,
        IReadOnlyDictionary<string, long>? lastSequences = null)
    {
        Microsoft.AspNetCore.TestHost.WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        WebSocket socket = await wsClient.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, "/ws"),
            CancellationToken.None);

        VestaTestClient client = new(socket, clientId);
        await client.HandshakeAsync(channels, lastSequences);
        return client;
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

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        if (!condition())
        {
            throw new TimeoutException($"Condition not met within {timeoutMs}ms");
        }
    }
}
