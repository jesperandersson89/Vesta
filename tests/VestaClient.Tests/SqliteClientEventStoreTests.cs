using System.Text.Json;
using VestaClient.Storage;
using VestaCore.Events;

namespace VestaClient.Tests;

/// <summary>
/// Tests for <see cref="SqliteClientEventStore"/> — local event cache and outbox.
/// Uses in-memory SQLite for isolation and speed.
/// </summary>
public sealed class SqliteClientEventStoreTests : IDisposable
{
    private readonly SqliteClientEventStore _store = SqliteClientEventStore.CreateInMemory();

    public void Dispose() => _store.Dispose();

    // --- Event Cache Tests ---

    [Fact]
    public async Task StoreEventAsync_StoresAndRetrieves()
    {
        SequencedEvent evt = CreateSequencedEvent("test/cache", sequence: 1);

        await _store.StoreEventAsync(evt);

        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test/cache", fromSequence: 1);
        Assert.Single(events);
        Assert.Equal(evt.Event.Id, events[0].Event.Id);
        Assert.Equal(1L, events[0].Sequence);
    }

    [Fact]
    public async Task StoreEventAsync_IsIdempotent_OnDuplicate()
    {
        SequencedEvent evt = CreateSequencedEvent("test/idem", sequence: 1);

        await _store.StoreEventAsync(evt);
        await _store.StoreEventAsync(evt); // duplicate — should not throw

        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test/idem", fromSequence: 1);
        Assert.Single(events);
    }

    [Fact]
    public async Task StoreEventsAsync_BatchInsert()
    {
        List<SequencedEvent> batch =
        [
            CreateSequencedEvent("test/batch", sequence: 1),
            CreateSequencedEvent("test/batch", sequence: 2),
            CreateSequencedEvent("test/batch", sequence: 3)
        ];

        await _store.StoreEventsAsync(batch);

        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test/batch", fromSequence: 1);
        Assert.Equal(3, events.Count);
        Assert.Equal(1L, events[0].Sequence);
        Assert.Equal(2L, events[1].Sequence);
        Assert.Equal(3L, events[2].Sequence);
    }

    [Fact]
    public async Task GetEventsAsync_FiltersFromSequence()
    {
        for (int i = 1; i <= 5; i++)
        {
            await _store.StoreEventAsync(CreateSequencedEvent("test/filter", sequence: i));
        }

        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test/filter", fromSequence: 3);
        Assert.Equal(3, events.Count);
        Assert.Equal(3L, events[0].Sequence);
        Assert.Equal(4L, events[1].Sequence);
        Assert.Equal(5L, events[2].Sequence);
    }

    [Fact]
    public async Task GetEventsAsync_RespectsLimit()
    {
        for (int i = 1; i <= 10; i++)
        {
            await _store.StoreEventAsync(CreateSequencedEvent("test/limit", sequence: i));
        }

        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test/limit", fromSequence: 1, limit: 3);
        Assert.Equal(3, events.Count);
    }

    [Fact]
    public async Task GetEventsAsync_ReturnsEmpty_WhenNoEvents()
    {
        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test/empty", fromSequence: 1);
        Assert.Empty(events);
    }

    [Fact]
    public async Task GetLatestSequenceAsync_ReturnsZero_WhenEmpty()
    {
        long seq = await _store.GetLatestSequenceAsync("test/noseq");
        Assert.Equal(0L, seq);
    }

    [Fact]
    public async Task GetLatestSequenceAsync_ReturnsMax()
    {
        await _store.StoreEventAsync(CreateSequencedEvent("test/maxseq", sequence: 5));
        await _store.StoreEventAsync(CreateSequencedEvent("test/maxseq", sequence: 10));
        await _store.StoreEventAsync(CreateSequencedEvent("test/maxseq", sequence: 7));

        long seq = await _store.GetLatestSequenceAsync("test/maxseq");
        Assert.Equal(10L, seq);
    }

    [Fact]
    public async Task GetChannelPositionsAsync_ReturnsMaxPerChannel()
    {
        await _store.StoreEventAsync(CreateSequencedEvent("channel-a", sequence: 3));
        await _store.StoreEventAsync(CreateSequencedEvent("channel-a", sequence: 5));
        await _store.StoreEventAsync(CreateSequencedEvent("channel-b", sequence: 2));

        IReadOnlyDictionary<string, long> positions = await _store.GetChannelPositionsAsync();

        Assert.Equal(2, positions.Count);
        Assert.Equal(5L, positions["channel-a"]);
        Assert.Equal(2L, positions["channel-b"]);
    }

    [Fact]
    public async Task StoreEventAsync_PreservesPayload()
    {
        JsonElement payload = JsonDocument.Parse("""{"key": "value", "num": 42}""").RootElement.Clone();
        VestaEvent evt = new(Guid.NewGuid(), "test/payload", DateTimeOffset.UtcNow, "client-1", "test.event", payload);
        SequencedEvent sequenced = new(evt, 1, DateTimeOffset.UtcNow);

        await _store.StoreEventAsync(sequenced);

        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test/payload", fromSequence: 1);
        Assert.Equal("value", events[0].Event.Payload.GetProperty("key").GetString());
        Assert.Equal(42, events[0].Event.Payload.GetProperty("num").GetInt32());
    }

    [Fact]
    public async Task StoreEventAsync_PreservesOptionalFields()
    {
        Guid parentId = Guid.NewGuid();
        VestaEvent evt = new(Guid.NewGuid(), "test/optional", DateTimeOffset.UtcNow, "client-1", "test.event",
            JsonDocument.Parse("{}").RootElement.Clone(), parentId, "sig==");
        SequencedEvent sequenced = new(evt, 1, DateTimeOffset.UtcNow);

        await _store.StoreEventAsync(sequenced);

        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test/optional", fromSequence: 1);
        Assert.Equal(parentId, events[0].Event.ParentId);
        Assert.Equal("sig==", events[0].Event.Signature);
    }

    [Fact]
    public async Task StoreEventAsync_HandlesNullOptionalFields()
    {
        VestaEvent evt = new(Guid.NewGuid(), "test/nulls", DateTimeOffset.UtcNow, "client-1", "test.event",
            JsonDocument.Parse("{}").RootElement.Clone(), null, null);
        SequencedEvent sequenced = new(evt, 1, DateTimeOffset.UtcNow);

        await _store.StoreEventAsync(sequenced);

        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test/nulls", fromSequence: 1);
        Assert.Null(events[0].Event.ParentId);
        Assert.Null(events[0].Event.Signature);
    }

    // --- Outbox Tests ---

    [Fact]
    public async Task EnqueueOutboxAsync_StoresEvent()
    {
        VestaEvent evt = CreateEvent("test/outbox");

        await _store.EnqueueOutboxAsync(evt);

        IReadOnlyList<OutboxEntry> pending = await _store.GetPendingOutboxAsync();
        Assert.Single(pending);
        Assert.Equal(evt.Id, pending[0].Event.Id);
        Assert.Equal(OutboxStatus.Pending, pending[0].Status);
    }

    [Fact]
    public async Task GetPendingOutboxAsync_ReturnsPendingAndSent()
    {
        // Both 'pending' and 'sent' entries must be re-flushed on reconnect —
        // server-side dedup on event id makes re-sending 'sent' entries safe and
        // recovers from a process that died between SEND and ACK.
        VestaEvent evt1 = CreateEvent("test/outbox-filter");
        VestaEvent evt2 = CreateEvent("test/outbox-filter");

        await _store.EnqueueOutboxAsync(evt1);
        await _store.EnqueueOutboxAsync(evt2);
        await _store.MarkOutboxSentAsync(evt1.Id);

        IReadOnlyList<OutboxEntry> pending = await _store.GetPendingOutboxAsync();
        Assert.Equal(2, pending.Count);
        OutboxEntry sentEntry = pending.Single(e => e.Event.Id == evt1.Id);
        OutboxEntry pendingEntry = pending.Single(e => e.Event.Id == evt2.Id);
        Assert.Equal(OutboxStatus.Sent, sentEntry.Status);
        Assert.Equal(OutboxStatus.Pending, pendingEntry.Status);
    }

    [Fact]
    public async Task MarkOutboxSentAsync_UpdatesStatus()
    {
        VestaEvent evt = CreateEvent("test/sent");
        await _store.EnqueueOutboxAsync(evt);

        await _store.MarkOutboxSentAsync(evt.Id);

        // Entry stays in the flush set with status 'sent' until confirmed,
        // so it gets re-published after a crash.
        IReadOnlyList<OutboxEntry> pending = await _store.GetPendingOutboxAsync();
        Assert.Single(pending);
        Assert.Equal(OutboxStatus.Sent, pending[0].Status);
    }

    [Fact]
    public async Task MarkOutboxConfirmedAsync_RemovesEntry()
    {
        VestaEvent evt = CreateEvent("test/confirmed");
        await _store.EnqueueOutboxAsync(evt);
        await _store.MarkOutboxSentAsync(evt.Id);

        await _store.MarkOutboxConfirmedAsync(evt.Id);

        IReadOnlyList<OutboxEntry> pending = await _store.GetPendingOutboxAsync();
        Assert.Empty(pending);
    }

    [Fact]
    public async Task OutboxPreservesPayload()
    {
        JsonElement payload = JsonDocument.Parse("""{"task": "buy milk"}""").RootElement.Clone();
        VestaEvent evt = new(Guid.NewGuid(), "test/outbox-payload", DateTimeOffset.UtcNow, "client-1", "todo.add", payload);

        await _store.EnqueueOutboxAsync(evt);

        IReadOnlyList<OutboxEntry> pending = await _store.GetPendingOutboxAsync();
        Assert.Equal("buy milk", pending[0].Event.Payload.GetProperty("task").GetString());
    }

    [Fact]
    public async Task GetPendingOutboxAsync_OrdersByCreatedAt()
    {
        VestaEvent evt1 = CreateEvent("test/order");
        VestaEvent evt2 = CreateEvent("test/order");
        VestaEvent evt3 = CreateEvent("test/order");

        await _store.EnqueueOutboxAsync(evt1);
        await _store.EnqueueOutboxAsync(evt2);
        await _store.EnqueueOutboxAsync(evt3);

        IReadOnlyList<OutboxEntry> pending = await _store.GetPendingOutboxAsync();
        Assert.Equal(3, pending.Count);
        Assert.Equal(evt1.Id, pending[0].Event.Id);
        Assert.Equal(evt2.Id, pending[1].Event.Id);
        Assert.Equal(evt3.Id, pending[2].Event.Id);
    }

    // --- Helpers ---

    private static SequencedEvent CreateSequencedEvent(string channelId, long sequence)
    {
        VestaEvent evt = CreateEvent(channelId);
        return new SequencedEvent(evt, sequence, DateTimeOffset.UtcNow);
    }

    private static VestaEvent CreateEvent(string channelId)
    {
        return new VestaEvent(
            Id: Guid.NewGuid(),
            ChannelId: channelId,
            Timestamp: DateTimeOffset.UtcNow,
            ClientId: "test-client",
            EventType: "test.event",
            Payload: JsonDocument.Parse("""{"message": "hello"}""").RootElement.Clone());
    }
}
