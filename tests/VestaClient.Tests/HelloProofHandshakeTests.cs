using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Vesta;

namespace VestaClient.Tests;

/// <summary>
/// The SDK side of the HELLO key-possession proof, against a scripted relay on a real loopback socket
/// (<see cref="VestaConnection"/> always opens its own <see cref="ClientWebSocket"/>).
/// </summary>
public class HelloProofHandshakeTests
{
    private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;

    [Fact]
    public async Task Connect_WithIdentity_AdvertisesProofSupport_AndAnswersTheChallenge()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        await using ScriptedRelay relay = await ScriptedRelay.StartAsync(challenge: "nonce-123");
        await using VestaConnection connection = CreateConnection(identity, relay.Url);

        await connection.ConnectAsync(relay.Url, ["game/farm"]);

        HelloMessage hello = Assert.IsType<HelloMessage>(relay.Received[0]);
        Assert.True(hello.ProofSupported);
        Assert.Equal(Base64Url.Encode(identity.PublicKey), hello.PublicKey);
        ProveMessage prove = Assert.IsType<ProveMessage>(relay.Received[1]);
        Assert.True(HelloProof.Verify(identity.PublicKey, "nonce-123", identity.ClientId, prove.Signature));
        Assert.Equal("scripted-relay", connection.ServerId);
    }

    [Fact]
    public async Task Connect_ToARelayThatDoesNotChallenge_StillConnects()
    {
        using VestaIdentity identity = VestaIdentity.Generate();
        await using ScriptedRelay relay = await ScriptedRelay.StartAsync(challenge: null);
        await using VestaConnection connection = CreateConnection(identity, relay.Url);

        await connection.ConnectAsync(relay.Url, ["game/farm"]);

        Assert.IsType<HelloMessage>(relay.Received[0]);
        Assert.DoesNotContain(relay.Received, m => m is ProveMessage);
        Assert.Equal("scripted-relay", connection.ServerId);
    }

    private static VestaConnection CreateConnection(VestaIdentity identity, Uri relay)
    {
        VestaAppConfig config = new("game", identity.PublicKey, [relay]);
        return new VestaConnection(identity, config, relayDirectory: new RelayDirectory(config));
    }

    private sealed class ScriptedRelay : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<ProtocolMessage> _messages;

        public Uri Url { get; }
        public IReadOnlyList<ProtocolMessage> Received => _messages.ToArray();

        private ScriptedRelay(WebApplication app, ConcurrentQueue<ProtocolMessage> messages, Uri url)
        {
            _app = app;
            _messages = messages;
            Url = url;
        }

        public static async Task<ScriptedRelay> StartAsync(string? challenge)
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            WebApplication app = builder.Build();
            app.UseWebSockets();

            ConcurrentQueue<ProtocolMessage> messages = new();
            app.Map("/ws", async (HttpContext context) =>
            {
                using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
                await ReceiveAsync(socket, messages);
                if (challenge is not null)
                {
                    await SendAsync(socket, new ChallengeMessage(challenge));
                    await ReceiveAsync(socket, messages);
                }

                await SendAsync(socket, new WelcomeMessage("scripted-relay", ["game/farm"]));
                while (socket.State == WebSocketState.Open && await ReceiveAsync(socket, messages)) { }
            });

            await app.StartAsync();
            Uri http = new(app.Urls.First());
            return new ScriptedRelay(app, messages, new Uri($"ws://{http.Host}:{http.Port}/ws"));
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private static async Task<bool> ReceiveAsync(WebSocket socket, ConcurrentQueue<ProtocolMessage> messages)
        {
            byte[] buffer = new byte[16384];
            using MemoryStream stream = new();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                    return false;
                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            messages.Enqueue(JsonSerializer.Deserialize<ProtocolMessage>(stream.ToArray(), JsonOptions)!);
            return true;
        }

        private static Task SendAsync(WebSocket socket, ProtocolMessage message) =>
            socket.SendAsync(
                JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
    }
}
