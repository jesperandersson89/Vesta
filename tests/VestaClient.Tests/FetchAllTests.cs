using System.Net.WebSockets;
using Vesta;

namespace VestaClient.Tests;

/// <summary>
/// <see cref="VestaConnection.FetchAllAsync"/> against a scripted relay that clamps page size the way
/// <c>Protocol:MaxFetchLimit</c> does (a real <see cref="ClientWebSocket"/> is always opened).
/// </summary>
public class FetchAllTests
{
    private const string Channel = ScriptedFetchRelay.Channel;

    [Fact]
    public async Task FetchAllAsync_PagesPastTheRelaysClamp_AndReturnsEverythingInOrder()
    {
        await using ScriptedFetchRelay relay = await ScriptedFetchRelay.StartAsync(eventCount: 10, maxPage: 3);
        await using VestaConnection connection = await ConnectAsync(relay);

        IReadOnlyList<SequencedEvent> events = await connection.FetchAllAsync(Channel, pageSize: 100);

        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), events.Select(e => e.Sequence));
        // 4 pages of 3, 3, 3, 1 and one empty page that ends the loop.
        Assert.Equal(5, relay.Fetches.Count);
        Assert.Equal([0L, 4L, 7L, 10L, 11L], relay.Fetches.Select(f => f.FromSequence));
    }

    [Fact]
    public async Task FetchAllAsync_WithToSequence_StopsAtTheUpperBound()
    {
        await using ScriptedFetchRelay relay = await ScriptedFetchRelay.StartAsync(eventCount: 10, maxPage: 3);
        await using VestaConnection connection = await ConnectAsync(relay);

        IReadOnlyList<SequencedEvent> events = await connection.FetchAllAsync(Channel, fromSequence: 3, toSequence: 7);

        Assert.Equal([3L, 4L, 5L, 6L, 7L], events.Select(e => e.Sequence));
    }

    [Fact]
    public async Task FetchAllAsync_EmptyChannel_ReturnsEmpty()
    {
        await using ScriptedFetchRelay relay = await ScriptedFetchRelay.StartAsync(eventCount: 0, maxPage: 3);
        await using VestaConnection connection = await ConnectAsync(relay);

        Assert.Empty(await connection.FetchAllAsync(Channel));
        Assert.Single(relay.Fetches);
    }

    [Fact]
    public async Task FetchAllAsync_RelayRefusesTheFetch_ThrowsWithTheErrorCode()
    {
        await using ScriptedFetchRelay relay = await ScriptedFetchRelay.StartAsync(eventCount: 5, maxPage: 3, refuse: true);
        await using VestaConnection connection = await ConnectAsync(relay);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.FetchAllAsync(Channel));

        Assert.StartsWith("ACCESS_DENIED", error.Message);
    }

    [Fact]
    public async Task FetchAllAsync_RelayNeverAnswers_TimesOut()
    {
        await using ScriptedFetchRelay relay = await ScriptedFetchRelay.StartAsync(eventCount: 5, maxPage: 3, silent: true);
        await using VestaConnection connection = await ConnectAsync(relay);

        await Assert.ThrowsAsync<TimeoutException>(
            () => connection.FetchAllAsync(Channel, pageTimeout: TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task FetchAllAsync_InvalidPageSize_Throws()
    {
        await using ScriptedFetchRelay relay = await ScriptedFetchRelay.StartAsync(eventCount: 1, maxPage: 3);
        await using VestaConnection connection = await ConnectAsync(relay);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => connection.FetchAllAsync(Channel, pageSize: 0));
    }

    private static async Task<VestaConnection> ConnectAsync(ScriptedFetchRelay relay)
    {
        VestaIdentity identity = VestaIdentity.Generate();
        VestaAppConfig config = new("game", identity.PublicKey, [relay.Url]);
        VestaConnection connection = new(identity, config, relayDirectory: new RelayDirectory(config));
        await connection.ConnectAsync(relay.Url, [Channel]);
        return connection;
    }
}
