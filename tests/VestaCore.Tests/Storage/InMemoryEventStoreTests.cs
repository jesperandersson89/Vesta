using System.Text.Json;
using VestaCore.Events;
using VestaCore.Storage;

namespace VestaCore.Tests.Storage;

public class InMemoryEventStoreTests
{
    private readonly InMemoryEventStore _store = new();

    private static VestaEvent CreateEvent(string channelId = "test-channel", string eventType = "test.event")
    {
        JsonElement payload = JsonDocument.Parse("""{"key":"value"}""").RootElement;

        return new VestaEvent(
            Id: Guid.NewGuid(),
            ChannelId: channelId,
            Timestamp: DateTimeOffset.UtcNow,
            ClientId: "test-client-id-123456",
            EventType: eventType,
            Payload: payload
        );
    }

    [Fact]
    public async Task AppendAsync_ReturnsSequencedEvent_WithSequence1()
    {
        VestaEvent evt = CreateEvent();

        SequencedEvent result = await _store.AppendAsync(evt);

        Assert.Equal(1L, result.Sequence);
        Assert.Equal(evt.Id, result.Event.Id);
        Assert.Equal(evt.ChannelId, result.Event.ChannelId);
        Assert.Equal(evt.EventType, result.Event.EventType);
    }

    [Fact]
    public async Task AppendAsync_AssignsMonotonicSequences()
    {
        VestaEvent evt1 = CreateEvent();
        VestaEvent evt2 = CreateEvent();
        VestaEvent evt3 = CreateEvent();

        SequencedEvent result1 = await _store.AppendAsync(evt1);
        SequencedEvent result2 = await _store.AppendAsync(evt2);
        SequencedEvent result3 = await _store.AppendAsync(evt3);

        Assert.Equal(1L, result1.Sequence);
        Assert.Equal(2L, result2.Sequence);
        Assert.Equal(3L, result3.Sequence);
    }

    [Fact]
    public async Task AppendAsync_SequencesArePerChannel()
    {
        VestaEvent evtA = CreateEvent("channel-a");
        VestaEvent evtB = CreateEvent("channel-b");
        VestaEvent evtA2 = CreateEvent("channel-a");

        SequencedEvent resultA1 = await _store.AppendAsync(evtA);
        SequencedEvent resultB1 = await _store.AppendAsync(evtB);
        SequencedEvent resultA2 = await _store.AppendAsync(evtA2);

        Assert.Equal(1L, resultA1.Sequence);
        Assert.Equal(1L, resultB1.Sequence);
        Assert.Equal(2L, resultA2.Sequence);
    }

    [Fact]
    public async Task AppendAsync_PreservesOriginalEvent()
    {
        VestaEvent evt = CreateEvent();

        SequencedEvent result = await _store.AppendAsync(evt);

        Assert.Equal(evt.Id, result.Event.Id);
        Assert.Equal(evt.ChannelId, result.Event.ChannelId);
        Assert.Equal(evt.Timestamp, result.Event.Timestamp);
        Assert.Equal(evt.ClientId, result.Event.ClientId);
        Assert.Equal(evt.EventType, result.Event.EventType);
        Assert.Equal(evt.Payload.GetRawText(), result.Event.Payload.GetRawText());
        Assert.Equal(evt.ParentId, result.Event.ParentId);
        Assert.Equal(evt.Signature, result.Event.Signature);
    }

    [Fact]
    public async Task AppendAsync_SetsReceivedAt()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        VestaEvent evt = CreateEvent();

        SequencedEvent result = await _store.AppendAsync(evt);

        Assert.True(result.ReceivedAt >= before);
        Assert.True(result.ReceivedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task GetEventsAsync_EmptyChannel_ReturnsEmpty()
    {
        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("nonexistent", 1);

        Assert.Empty(events);
    }

    [Fact]
    public async Task GetEventsAsync_FromSequence1_ReturnsAll()
    {
        await _store.AppendAsync(CreateEvent());
        await _store.AppendAsync(CreateEvent());
        await _store.AppendAsync(CreateEvent());

        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test-channel", 1);

        Assert.Equal(3, events.Count);
        Assert.Equal(1L, events[0].Sequence);
        Assert.Equal(2L, events[1].Sequence);
        Assert.Equal(3L, events[2].Sequence);
    }

    [Fact]
    public async Task GetEventsAsync_FromMiddle_ReturnsSubset()
    {
        await _store.AppendAsync(CreateEvent());
        await _store.AppendAsync(CreateEvent());
        await _store.AppendAsync(CreateEvent());

        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test-channel", 2);

        Assert.Equal(2, events.Count);
        Assert.Equal(2L, events[0].Sequence);
        Assert.Equal(3L, events[1].Sequence);
    }

    [Fact]
    public async Task GetEventsAsync_FromBeyondEnd_ReturnsEmpty()
    {
        await _store.AppendAsync(CreateEvent());

        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test-channel", 99);

        Assert.Empty(events);
    }

    [Fact]
    public async Task GetEventsAsync_WithLimit_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
        {
            await _store.AppendAsync(CreateEvent());
        }

        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test-channel", 1, limit: 3);

        Assert.Equal(3, events.Count);
        Assert.Equal(1L, events[0].Sequence);
        Assert.Equal(3L, events[2].Sequence);
    }

    [Fact]
    public async Task GetEventsAsync_LimitExceedsAvailable_ReturnsAll()
    {
        await _store.AppendAsync(CreateEvent());
        await _store.AppendAsync(CreateEvent());

        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test-channel", 1, limit: 1000);

        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task GetEventsAsync_OnlyReturnsEventsForRequestedChannel()
    {
        await _store.AppendAsync(CreateEvent("channel-a"));
        await _store.AppendAsync(CreateEvent("channel-b"));
        await _store.AppendAsync(CreateEvent("channel-a"));

        IReadOnlyList<SequencedEvent> eventsA = await _store.GetEventsAsync("channel-a", 1);
        IReadOnlyList<SequencedEvent> eventsB = await _store.GetEventsAsync("channel-b", 1);

        Assert.Equal(2, eventsA.Count);
        Assert.Single(eventsB);
    }

    [Fact]
    public async Task GetLatestSequenceAsync_EmptyChannel_ReturnsZero()
    {
        long sequence = await _store.GetLatestSequenceAsync("nonexistent");

        Assert.Equal(0L, sequence);
    }

    [Fact]
    public async Task GetLatestSequenceAsync_AfterAppends_ReturnsLatest()
    {
        await _store.AppendAsync(CreateEvent());
        await _store.AppendAsync(CreateEvent());
        await _store.AppendAsync(CreateEvent());

        long sequence = await _store.GetLatestSequenceAsync("test-channel");

        Assert.Equal(3L, sequence);
    }

    [Fact]
    public async Task GetLatestSequenceAsync_PerChannel()
    {
        await _store.AppendAsync(CreateEvent("channel-a"));
        await _store.AppendAsync(CreateEvent("channel-a"));
        await _store.AppendAsync(CreateEvent("channel-b"));

        long seqA = await _store.GetLatestSequenceAsync("channel-a");
        long seqB = await _store.GetLatestSequenceAsync("channel-b");

        Assert.Equal(2L, seqA);
        Assert.Equal(1L, seqB);
    }

    [Fact]
    public async Task ConcurrentAppends_AssignUniqueSequences()
    {
        // Verify thread safety by appending concurrently
        int concurrency = 100;
        Task<SequencedEvent>[] tasks = new Task<SequencedEvent>[concurrency];

        for (int i = 0; i < concurrency; i++)
        {
            tasks[i] = _store.AppendAsync(CreateEvent());
        }

        SequencedEvent[] results = await Task.WhenAll(tasks);

        // All sequences should be unique and in range [1, concurrency]
        long[] sequences = results.Select(r => r.Sequence).OrderBy(s => s).ToArray();
        Assert.Equal(concurrency, sequences.Distinct().Count());
        Assert.Equal(1L, sequences.First());
        Assert.Equal((long)concurrency, sequences.Last());
    }
}
