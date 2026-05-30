using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VestaCore.Events;
using VestaServer.Data;
using VestaServer.Storage;

namespace VestaServer.Tests;

/// <summary>
/// Integration tests for NpgsqlEventStore against the local PostgreSQL instance.
/// Requires PostgreSQL running on localhost:5432 with username=postgres, password=vesta.
/// Creates and drops a unique test database per test class run.
/// </summary>
public sealed class NpgsqlEventStoreTests : IAsyncLifetime
{
    private const string BaseConnectionString = "Host=localhost;Port=5432;Username=postgres;Password=vesta";
    private readonly string _testDbName = $"vesta_test_{Guid.NewGuid():N}"[..32];

    private NpgsqlDataSource _dataSource = null!;
    private NpgsqlEventStore _store = null!;

    public async Task InitializeAsync()
    {
        // Create a unique test database
        await using NpgsqlDataSource adminSource = NpgsqlDataSource.Create($"{BaseConnectionString};Database=postgres");
        await using NpgsqlCommand createDb = adminSource.CreateCommand($"CREATE DATABASE \"{_testDbName}\"");
        await createDb.ExecuteNonQueryAsync();

        string testConnectionString = $"{BaseConnectionString};Database={_testDbName}";
        _dataSource = NpgsqlDataSource.Create(testConnectionString);

        // Apply EF Core migrations to set up schema
        DbContextOptionsBuilder<VestaDbContext> optionsBuilder = new();
        optionsBuilder.UseNpgsql(testConnectionString);
        await using VestaDbContext dbContext = new(optionsBuilder.Options);
        await dbContext.Database.MigrateAsync();

        _store = new NpgsqlEventStore(_dataSource);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();

        // Drop the test database
        await using NpgsqlDataSource adminSource = NpgsqlDataSource.Create($"{BaseConnectionString};Database=postgres");
        await using NpgsqlCommand dropDb = adminSource.CreateCommand(
            $"DROP DATABASE IF EXISTS \"{_testDbName}\" WITH (FORCE)");
        await dropDb.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task AppendAsync_AssignsSequence1_ForFirstEvent()
    {
        VestaEvent evt = CreateEvent("test/channel-a");

        SequencedEvent result = await _store.AppendAsync(evt);

        Assert.Equal(1L, result.Sequence);
        Assert.Equal(evt.Id, result.Event.Id);
        Assert.Equal("test/channel-a", result.Event.ChannelId);
    }

    [Fact]
    public async Task AppendAsync_IncrementsSequence_PerChannel()
    {
        VestaEvent evt1 = CreateEvent("test/seq");
        VestaEvent evt2 = CreateEvent("test/seq");
        VestaEvent evt3 = CreateEvent("test/seq");

        SequencedEvent r1 = await _store.AppendAsync(evt1);
        SequencedEvent r2 = await _store.AppendAsync(evt2);
        SequencedEvent r3 = await _store.AppendAsync(evt3);

        Assert.Equal(1L, r1.Sequence);
        Assert.Equal(2L, r2.Sequence);
        Assert.Equal(3L, r3.Sequence);
    }

    [Fact]
    public async Task AppendAsync_IsolatesSequences_AcrossChannels()
    {
        VestaEvent evtA = CreateEvent("test/iso-a");
        VestaEvent evtB = CreateEvent("test/iso-b");
        VestaEvent evtA2 = CreateEvent("test/iso-a");

        SequencedEvent rA1 = await _store.AppendAsync(evtA);
        SequencedEvent rB1 = await _store.AppendAsync(evtB);
        SequencedEvent rA2 = await _store.AppendAsync(evtA2);

        Assert.Equal(1L, rA1.Sequence);
        Assert.Equal(1L, rB1.Sequence);
        Assert.Equal(2L, rA2.Sequence);
    }

    [Fact]
    public async Task AppendAsync_SetsReceivedAt()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        VestaEvent evt = CreateEvent("test/time");

        SequencedEvent result = await _store.AppendAsync(evt);

        Assert.True(result.ReceivedAt >= before);
        Assert.True(result.ReceivedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task AppendAsync_PreservesPayload()
    {
        JsonElement payload = JsonDocument.Parse("""{"key": "value", "count": 42}""").RootElement.Clone();
        VestaEvent evt = new(
            Id: Guid.NewGuid(),
            ChannelId: "test/payload",
            Timestamp: DateTimeOffset.UtcNow,
            ClientId: "client-001",
            EventType: "test.payload",
            Payload: payload);

        SequencedEvent result = await _store.AppendAsync(evt);

        Assert.Equal("value", result.Event.Payload.GetProperty("key").GetString());
        Assert.Equal(42, result.Event.Payload.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task GetEventsAsync_ReturnsEventsFromSequence()
    {
        // Append 5 events
        for (int i = 0; i < 5; i++)
        {
            await _store.AppendAsync(CreateEvent("test/range"));
        }

        // Fetch from sequence 3
        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test/range", fromSequence: 3);

        Assert.Equal(3, events.Count);
        Assert.Equal(3L, events[0].Sequence);
        Assert.Equal(4L, events[1].Sequence);
        Assert.Equal(5L, events[2].Sequence);
    }

    [Fact]
    public async Task GetEventsAsync_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
        {
            await _store.AppendAsync(CreateEvent("test/limit"));
        }

        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test/limit", fromSequence: 1, limit: 3);

        Assert.Equal(3, events.Count);
        Assert.Equal(1L, events[0].Sequence);
        Assert.Equal(2L, events[1].Sequence);
        Assert.Equal(3L, events[2].Sequence);
    }

    [Fact]
    public async Task GetEventsAsync_ReturnsEmpty_WhenChannelDoesNotExist()
    {
        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test/nonexistent", fromSequence: 1);

        Assert.Empty(events);
    }

    [Fact]
    public async Task GetEventsAsync_ReturnsEmpty_WhenSequenceBeyondLatest()
    {
        await _store.AppendAsync(CreateEvent("test/beyond"));

        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test/beyond", fromSequence: 100);

        Assert.Empty(events);
    }

    [Fact]
    public async Task GetLatestSequenceAsync_ReturnsZero_WhenChannelEmpty()
    {
        long seq = await _store.GetLatestSequenceAsync("test/empty");

        Assert.Equal(0L, seq);
    }

    [Fact]
    public async Task GetLatestSequenceAsync_ReturnsLatest_AfterAppends()
    {
        await _store.AppendAsync(CreateEvent("test/latest"));
        await _store.AppendAsync(CreateEvent("test/latest"));
        await _store.AppendAsync(CreateEvent("test/latest"));

        long seq = await _store.GetLatestSequenceAsync("test/latest");

        Assert.Equal(3L, seq);
    }

    [Fact]
    public async Task AppendAsync_PreservesOptionalFields()
    {
        Guid parentId = Guid.NewGuid();
        VestaEvent evt = new(
            Id: Guid.NewGuid(),
            ChannelId: "test/optional",
            Timestamp: DateTimeOffset.UtcNow,
            ClientId: "client-001",
            EventType: "test.optional",
            Payload: JsonDocument.Parse("{}").RootElement.Clone(),
            ParentId: parentId,
            Signature: "base64sig==");

        await _store.AppendAsync(evt);

        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test/optional", fromSequence: 1);
        SequencedEvent stored = events[0];

        Assert.Equal(parentId, stored.Event.ParentId);
        Assert.Equal("base64sig==", stored.Event.Signature);
    }

    [Fact]
    public async Task AppendAsync_HandlesNullOptionalFields()
    {
        VestaEvent evt = new(
            Id: Guid.NewGuid(),
            ChannelId: "test/nulls",
            Timestamp: DateTimeOffset.UtcNow,
            ClientId: "client-001",
            EventType: "test.nulls",
            Payload: JsonDocument.Parse("{}").RootElement.Clone(),
            ParentId: null,
            Signature: null);

        await _store.AppendAsync(evt);

        IReadOnlyList<SequencedEvent> events = await _store.GetEventsAsync("test/nulls", fromSequence: 1);
        SequencedEvent stored = events[0];

        Assert.Null(stored.Event.ParentId);
        Assert.Null(stored.Event.Signature);
    }

    [Fact]
    public async Task AppendAsync_RejectsDuplicateEventId()
    {
        Guid id = Guid.NewGuid();
        VestaEvent evt1 = new(id, "test/dup", DateTimeOffset.UtcNow, "client-001", "test.dup",
            JsonDocument.Parse("{}").RootElement.Clone());
        VestaEvent evt2 = new(id, "test/dup", DateTimeOffset.UtcNow, "client-001", "test.dup",
            JsonDocument.Parse("{}").RootElement.Clone());

        await _store.AppendAsync(evt1);

        await Assert.ThrowsAnyAsync<Exception>(() => _store.AppendAsync(evt2));
    }

    [Fact]
    public async Task ConcurrentAppends_MaintainSequenceIntegrity()
    {
        // Append multiple events concurrently to the same channel
        Task<SequencedEvent>[] tasks = Enumerable.Range(0, 10)
            .Select(_ => _store.AppendAsync(CreateEvent("test/concurrent")))
            .ToArray();

        SequencedEvent[] results = await Task.WhenAll(tasks);

        // All sequences should be unique and form 1..10
        long[] sequences = results.Select(r => r.Sequence).OrderBy(s => s).ToArray();
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i).ToArray(), sequences);
    }

    [Fact]
    public async Task NotifyTrigger_FiresOnInsert()
    {
        // Verify the NOTIFY trigger is installed and fires
        await using NpgsqlConnection listener = await _dataSource.OpenConnectionAsync();
        await using NpgsqlCommand listenCmd = new("LISTEN vesta_events", listener);
        await listenCmd.ExecuteNonQueryAsync();

        // Set up notification tracking
        string? receivedPayload = null;
        TaskCompletionSource<string> tcs = new();
        listener.Notification += (_, args) => tcs.TrySetResult(args.Payload);

        // Append an event (from a different connection)
        await _store.AppendAsync(CreateEvent("test/notify"));

        // Wait for the notification (with timeout)
        await listener.WaitAsync(CancellationToken.None);
        Task<string> completed = await Task.WhenAny(tcs.Task, Task.Delay(5000).ContinueWith<string>(_ => "TIMEOUT"));
        receivedPayload = completed == tcs.Task ? await tcs.Task : null;

        Assert.NotNull(receivedPayload);
        Assert.Contains("test/notify", receivedPayload);
    }

    private static VestaEvent CreateEvent(string channelId, string eventType = "test.event")
    {
        return new VestaEvent(
            Id: Guid.NewGuid(),
            ChannelId: channelId,
            Timestamp: DateTimeOffset.UtcNow,
            ClientId: "test-client",
            EventType: eventType,
            Payload: JsonDocument.Parse("""{"message": "hello"}""").RootElement.Clone());
    }
}
