using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Vesta;

namespace VestaClient.Tests;

/// <summary>
/// <see cref="VestaConnection.FetchAllAsync"/> against a scripted relay that clamps page size the way
/// <c>Protocol:MaxFetchLimit</c> does (a real <see cref="ClientWebSocket"/> is always opened).
/// </summary>
public class FetchAllTests
{
    private const string Channel = "game/world";
    private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;

    [Fact]
    public async Task FetchAllAsync_PagesPastTheRelaysClamp_AndReturnsEverythingInOrder()
    {
        await using ScriptedRelay relay = await ScriptedRelay.StartAsync(eventCount: 10, maxPage: 3);
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
        await using ScriptedRelay relay = await ScriptedRelay.StartAsync(eventCount: 10, maxPage: 3);
        await using VestaConnection connection = await ConnectAsync(relay);

        IReadOnlyList<SequencedEvent> events = await connection.FetchAllAsync(Channel, fromSequence: 3, toSequence: 7);

        Assert.Equal([3L, 4L, 5L, 6L, 7L], events.Select(e => e.Sequence));
    }

    [Fact]
    public async Task FetchAllAsync_EmptyChannel_ReturnsEmpty()
    {
        await using ScriptedRelay relay = await ScriptedRelay.StartAsync(eventCount: 0, maxPage: 3);
        await using VestaConnection connection = await ConnectAsync(relay);

        Assert.Empty(await connection.FetchAllAsync(Channel));
        Assert.Single(relay.Fetches);
    }

    [Fact]
    public async Task FetchAllAsync_RelayRefusesTheFetch_ThrowsWithTheErrorCode()
    {
        await using ScriptedRelay relay = await ScriptedRelay.StartAsync(eventCount: 5, maxPage: 3, refuse: true);
        await using VestaConnection connection = await ConnectAsync(relay);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.FetchAllAsync(Channel));

        Assert.StartsWith("ACCESS_DENIED", error.Message);
    }

    [Fact]
    public async Task FetchAllAsync_RelayNeverAnswers_TimesOut()
    {
        await using ScriptedRelay relay = await ScriptedRelay.StartAsync(eventCount: 5, maxPage: 3, silent: true);
        await using VestaConnection connection = await ConnectAsync(relay);

        await Assert.ThrowsAsync<TimeoutException>(
            () => connection.FetchAllAsync(Channel, pageTimeout: TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task FetchAllAsync_InvalidPageSize_Throws()
    {
        await using ScriptedRelay relay = await ScriptedRelay.StartAsync(eventCount: 1, maxPage: 3);
        await using VestaConnection connection = await ConnectAsync(relay);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => connection.FetchAllAsync(Channel, pageSize: 0));
    }

    private static async Task<VestaConnection> ConnectAsync(ScriptedRelay relay)
    {
        VestaIdentity identity = VestaIdentity.Generate();
        VestaAppConfig config = new("game", identity.PublicKey, [relay.Url]);
        VestaConnection connection = new(identity, config, relayDirectory: new RelayDirectory(config));
        await connection.ConnectAsync(relay.Url, [Channel]);
        return connection;
    }

    private sealed class ScriptedRelay : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public Uri Url { get; }
        public ConcurrentQueue<FetchMessage> Fetches { get; }

        private ScriptedRelay(WebApplication app, ConcurrentQueue<FetchMessage> fetches, Uri url)
        {
            _app = app;
            Fetches = fetches;
            Url = url;
        }

        public static async Task<ScriptedRelay> StartAsync(int eventCount, int maxPage, bool refuse = false, bool silent = false)
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            WebApplication app = builder.Build();
            app.UseWebSockets();

            ConcurrentQueue<FetchMessage> fetches = new();
            List<SequencedEvent> stored = Enumerable.Range(1, eventCount).Select(Stored).ToList();

            app.Map("/ws", async (HttpContext context) =>
            {
                using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
                await ReceiveAsync(socket); // HELLO
                await SendAsync(socket, new WelcomeMessage("scripted-relay", [Channel]));

                while (socket.State == WebSocketState.Open && await ReceiveAsync(socket) is { } message)
                {
                    if (message is not FetchMessage fetch)
                        continue;

                    fetches.Enqueue(fetch);
                    if (silent)
                        continue;

                    if (refuse)
                    {
                        await SendAsync(socket, new ErrorMessage("ACCESS_DENIED", "Not authorized", null, fetch.ChannelId));
                        continue;
                    }

                    int limit = Math.Clamp(fetch.Limit ?? 100, 1, maxPage);
                    List<SequencedEvent> page = stored
                        .Where(e => e.Sequence >= fetch.FromSequence)
                        .Take(limit)
                        .Where(e => fetch.ToSequence is null || e.Sequence <= fetch.ToSequence)
                        .ToList();
                    await SendAsync(socket, new EventsBatchMessage(fetch.ChannelId, page));
                }
            });

            await app.StartAsync();
            Uri http = new(app.Urls.First());
            return new ScriptedRelay(app, fetches, new Uri($"ws://{http.Host}:{http.Port}/ws"));
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private static SequencedEvent Stored(int sequence) => new(
            new VestaEvent(
                Id: Guid.NewGuid(),
                ChannelId: Channel,
                Timestamp: DateTimeOffset.UtcNow,
                ClientId: "someone",
                EventType: "test.message",
                Payload: JsonDocument.Parse("""{"n":1}""").RootElement),
            sequence,
            DateTimeOffset.UtcNow);

        private static async Task<ProtocolMessage?> ReceiveAsync(WebSocket socket)
        {
            byte[] buffer = new byte[16384];
            using MemoryStream stream = new();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            return JsonSerializer.Deserialize<ProtocolMessage>(stream.ToArray(), JsonOptions);
        }

        private static Task SendAsync(WebSocket socket, ProtocolMessage message) =>
            socket.SendAsync(
                JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
    }
}
