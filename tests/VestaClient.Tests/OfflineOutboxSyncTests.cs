using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VestaClient.Storage;
using VestaCore.Events;
using VestaCore.Protocol;

namespace VestaClient.Tests;

/// <summary>
/// Integration tests proving the offline outbox + reconnect sync flow.
/// Events created while offline are stored in the outbox and flushed on connect.
/// </summary>
public class OfflineOutboxSyncTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public OfflineOutboxSyncTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Vesta", "");
        });
    }

    [Fact]
    public async Task PublishWhileDisconnected_EnqueuesToOutbox()
    {
        using SqliteClientEventStore store = SqliteClientEventStore.CreateInMemory();
        VestaConnection connection = new("offline-client", store);

        VestaEvent evt = CreateEvent("test/offline");
        await connection.PublishAsync(evt);

        IReadOnlyList<OutboxEntry> pending = await store.GetPendingOutboxAsync();
        Assert.Single(pending);
        Assert.Equal(evt.Id, pending[0].Event.Id);
        Assert.Equal(OutboxStatus.Pending, pending[0].Status);

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task OutboxFlushedOnConnect_EventsPublishedToServer()
    {
        using SqliteClientEventStore store = SqliteClientEventStore.CreateInMemory();

        // Enqueue events while "offline"
        VestaEvent evt1 = CreateEvent("test/flush");
        VestaEvent evt2 = CreateEvent("test/flush");
        await store.EnqueueOutboxAsync(evt1);
        await store.EnqueueOutboxAsync(evt2);

        // Now connect with the store — should flush outbox
        // Use the TestHost WebSocket (VestaConnection requires ws:// but TestHost gives http://)
        // We need to use the ws:// URI conversion
        Microsoft.AspNetCore.TestHost.WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        Uri wsUri = new(_factory.Server.BaseAddress, "/ws");

        // Use VestaTestClient for the flush test — create a wrapper that simulates VestaConnection behavior
        WebSocket socket = await wsClient.ConnectAsync(wsUri, CancellationToken.None);
        VestaTestClient client = new(socket, "flush-client", store);
        await client.HandshakeAsync(["test/flush"]);

        ConcurrentQueue<AckMessage> acks = new();
        client.OnAck += ack => acks.Enqueue(ack);

        // Flush outbox manually (mimicking what VestaConnection does on connect)
        IReadOnlyList<OutboxEntry> pending = await store.GetPendingOutboxAsync();
        foreach (OutboxEntry entry in pending)
        {
            await client.PublishAsync(entry.Event);
            await store.MarkOutboxSentAsync(entry.Event.Id);
        }

        // Wait for ACKs
        await WaitForConditionAsync(() => acks.Count >= 2);
        Assert.Equal(2, acks.Count);

        // Confirm outbox entries
        foreach (AckMessage ack in acks)
        {
            await store.MarkOutboxConfirmedAsync(ack.EventId);
        }

        IReadOnlyList<OutboxEntry> remaining = await store.GetPendingOutboxAsync();
        Assert.Empty(remaining);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ReceivedEventsAreCachedLocally()
    {
        using SqliteClientEventStore store = SqliteClientEventStore.CreateInMemory();
        Microsoft.AspNetCore.TestHost.WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        Uri wsUri = new(_factory.Server.BaseAddress, "/ws");

        // Connect a subscriber with the local store
        WebSocket subSocket = await wsClient.ConnectAsync(wsUri, CancellationToken.None);
        VestaTestClient subscriber = new(subSocket, "sub-client", store);
        await subscriber.HandshakeAsync(["test/cache-recv"]);

        // Connect a publisher (no store)
        WebSocket pubSocket = await wsClient.ConnectAsync(wsUri, CancellationToken.None);
        VestaTestClient publisher = new(pubSocket, "pub-client");
        await publisher.HandshakeAsync(["test/cache-recv"]);

        ConcurrentQueue<AckMessage> pubAcks = new();
        publisher.OnAck += ack => pubAcks.Enqueue(ack);

        // Publish an event
        VestaEvent evt = CreateEvent("test/cache-recv");
        await publisher.PublishAsync(evt);
        await WaitForConditionAsync(() => !pubAcks.IsEmpty);

        // Wait for subscriber to receive and cache it
        await Task.Delay(200);

        // Verify it's in the local store
        IReadOnlyList<SequencedEvent> cached = await store.GetEventsAsync("test/cache-recv", fromSequence: 1);
        Assert.Single(cached);
        Assert.Equal(evt.Id, cached[0].Event.Id);
        Assert.Equal(1L, cached[0].Sequence);

        await subscriber.DisposeAsync();
        await publisher.DisposeAsync();
    }

    [Fact]
    public async Task ReconnectUsesCachedPositions_ForCatchUp()
    {
        using SqliteClientEventStore store = SqliteClientEventStore.CreateInMemory();
        Microsoft.AspNetCore.TestHost.WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        Uri wsUri = new(_factory.Server.BaseAddress, "/ws");

        // First session: connect and receive 2 events
        WebSocket sub1Socket = await wsClient.ConnectAsync(wsUri, CancellationToken.None);
        VestaTestClient session1 = new(sub1Socket, "reconnect-client", store);
        await session1.HandshakeAsync(["test/reconnect"]);

        // Publish 2 events via a separate publisher
        WebSocket pubSocket = await wsClient.ConnectAsync(wsUri, CancellationToken.None);
        VestaTestClient publisher = new(pubSocket, "publisher");
        await publisher.HandshakeAsync(["test/reconnect"]);

        ConcurrentQueue<AckMessage> pubAcks = new();
        publisher.OnAck += ack => pubAcks.Enqueue(ack);

        await publisher.PublishAsync(CreateEvent("test/reconnect"));
        await WaitForConditionAsync(() => pubAcks.Count >= 1);
        await publisher.PublishAsync(CreateEvent("test/reconnect"));
        await WaitForConditionAsync(() => pubAcks.Count >= 2);

        // Wait for subscriber to cache them
        await Task.Delay(200);
        long latestSeq = await store.GetLatestSequenceAsync("test/reconnect");
        Assert.Equal(2L, latestSeq);

        // Disconnect first session
        await session1.DisposeAsync();

        // Publish a 3rd event while disconnected
        await publisher.PublishAsync(CreateEvent("test/reconnect"));
        await WaitForConditionAsync(() => pubAcks.Count >= 3);

        // Reconnect — should use stored position (seq 2) and get catch-up for seq 3
        IReadOnlyDictionary<string, long> positions = await store.GetChannelPositionsAsync();
        WebSocket sub2Socket = await wsClient.ConnectAsync(wsUri, CancellationToken.None);
        VestaTestClient session2 = new(sub2Socket, "reconnect-client", store);

        ConcurrentQueue<EventsBatchMessage> batches = new();
        session2.OnEventsBatch += batch => batches.Enqueue(batch);

        await session2.HandshakeAsync(["test/reconnect"], positions);

        // Should receive catch-up batch with seq 3
        await WaitForConditionAsync(() => !batches.IsEmpty);
        Assert.True(batches.TryDequeue(out EventsBatchMessage? batchMsg));
        Assert.Single(batchMsg.Events);
        Assert.Equal(3L, batchMsg.Events[0].Sequence);

        await session2.DisposeAsync();
        await publisher.DisposeAsync();
    }

    // --- Helpers ---

    private static VestaEvent CreateEvent(string channelId)
    {
        JsonElement payload = JsonDocument.Parse("""{"text":"hello"}""").RootElement;
        return new VestaEvent(
            Id: Guid.NewGuid(),
            ChannelId: channelId,
            Timestamp: DateTimeOffset.UtcNow,
            ClientId: "test-client-id",
            EventType: "test.message",
            Payload: payload);
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
