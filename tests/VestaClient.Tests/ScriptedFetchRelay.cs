using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Vesta;

namespace VestaClient.Tests;

/// <summary>
/// A scripted relay on a real loopback socket (<see cref="VestaConnection"/> always opens its own
/// <see cref="ClientWebSocket"/>) that answers <c>FETCH</c> from a fixed event list and clamps page size the way
/// <c>Protocol:MaxFetchLimit</c> does.
/// </summary>
internal sealed class ScriptedFetchRelay : IAsyncDisposable
{
    public const string Channel = "game/world";
    private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;

    private readonly WebApplication _app;

    public Uri Url { get; }
    public ConcurrentQueue<FetchMessage> Fetches { get; }

    /// <summary>Control messages the relay saw (CREATE_CHANNEL, REVOKE_ACCESS), in order.</summary>
    public ConcurrentQueue<ProtocolMessage> Controls { get; }

    private ScriptedFetchRelay(WebApplication app, ConcurrentQueue<FetchMessage> fetches, ConcurrentQueue<ProtocolMessage> controls, Uri url)
    {
        _app = app;
        Fetches = fetches;
        Controls = controls;
        Url = url;
    }

    public static Task<ScriptedFetchRelay> StartAsync(int eventCount, int maxPage, bool refuse = false, bool silent = false, IReadOnlyList<string>? features = null) =>
        StartAsync(Enumerable.Range(1, eventCount).Select(i => Stored(Channel, i)).ToList(), maxPage, refuse, silent, features);

    public static async Task<ScriptedFetchRelay> StartAsync(IReadOnlyList<SequencedEvent> stored, int maxPage, bool refuse = false, bool silent = false, IReadOnlyList<string>? features = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        app.UseWebSockets();

        ConcurrentQueue<FetchMessage> fetches = new();
        ConcurrentQueue<ProtocolMessage> controls = new();
        app.Map("/ws", async (HttpContext context) =>
        {
            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            await ReceiveAsync(socket); // HELLO
            await SendAsync(socket, new WelcomeMessage("scripted-relay", [], features));

            while (socket.State == WebSocketState.Open && await ReceiveAsync(socket) is { } message)
            {
                if (message is CreateChannelMessage create)
                {
                    controls.Enqueue(create);
                    await SendAsync(socket, new AckMessage(create.ChannelId, Guid.Empty, 0));
                    continue;
                }

                if (message is RevokeAccessMessage revoke)
                {
                    controls.Enqueue(revoke);
                    await SendAsync(socket, new AckMessage(revoke.ChannelId, Guid.Empty, 0));
                    continue;
                }

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
                    .Where(e => e.Event.ChannelId == fetch.ChannelId && e.Sequence >= fetch.FromSequence)
                    .Take(limit)
                    .Where(e => fetch.ToSequence is null || e.Sequence <= fetch.ToSequence)
                    .ToList();
                await SendAsync(socket, new EventsBatchMessage(fetch.ChannelId, page));
            }
        });

        await app.StartAsync();
        Uri http = new(app.Urls.First());
        return new ScriptedFetchRelay(app, fetches, controls, new Uri($"ws://{http.Host}:{http.Port}/ws"));
    }

    public static SequencedEvent Stored(string channel, long sequence) => new(
        new VestaEvent(
            Id: Guid.NewGuid(),
            ChannelId: channel,
            Timestamp: DateTimeOffset.UtcNow,
            ClientId: "someone",
            EventType: "test.message",
            Payload: JsonDocument.Parse("""{"n":1}""").RootElement),
        sequence,
        DateTimeOffset.UtcNow);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

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
