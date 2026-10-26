using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Vesta;
using VestaServer.Connections;

namespace VestaServer.Tests;

public class BaselineLimitsTests(WebApplicationFactory<Program> baseFactory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;

    [Fact]
    public async Task Publish_WithoutAppQuota_UsesDefaultPublishRate()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(("Protocol:Defaults:PublishRatePerMinute", "2"));
        using WebSocket ws = await ConnectAsync(factory);
        await HelloAsync(ws, "rate-client", "rate/room");

        for (int i = 0; i < 2; i++)
        {
            await SendAsync(ws, new PublishMessage("rate/room", CreateEvent("rate/room", "rate-client")));
            Assert.IsType<AckMessage>(await ReceiveAsync(ws));
        }

        await SendAsync(ws, new PublishMessage("rate/room", CreateEvent("rate/room", "rate-client")));
        ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
        Assert.Equal("RATE_LIMITED", error.Code);
    }

    [Fact]
    public async Task Publish_DefaultRateZero_DisablesLimit()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(("Protocol:Defaults:PublishRatePerMinute", "0"));
        using WebSocket ws = await ConnectAsync(factory);
        await HelloAsync(ws, "free-client", "free/room");

        for (int i = 0; i < 5; i++)
        {
            await SendAsync(ws, new PublishMessage("free/room", CreateEvent("free/room", "free-client")));
            Assert.IsType<AckMessage>(await ReceiveAsync(ws));
        }
    }

    [Fact]
    public async Task Hello_BeyondSubscriptionCap_RejectsExtraChannels()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(("Protocol:Defaults:MaxSubscriptionsPerConnection", "2"));
        using WebSocket ws = await ConnectAsync(factory);

        await SendAsync(ws, new HelloMessage("sub-client", ["sub/a", "sub/b", "sub/c"], new Dictionary<string, long>()));

        ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
        Assert.Equal("CONNECTION_LIMITED", error.Code);
        Assert.Equal("sub/c", error.ChannelId);
        WelcomeMessage welcome = Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));
        Assert.Equal(["sub/a", "sub/b"], welcome.Channels.Order());
    }

    [Fact]
    public async Task Subscribe_BeyondSubscriptionCap_IsRejected_ButResubscribeIsAllowed()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(("Protocol:Defaults:MaxSubscriptionsPerConnection", "1"));
        using WebSocket ws = await ConnectAsync(factory);
        await HelloAsync(ws, "sub-client", "subcap/a");

        await SendAsync(ws, new SubscribeMessage("subcap/b"));
        ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
        Assert.Equal("CONNECTION_LIMITED", error.Code);

        // Already subscribed: not a new subscription, so no error — a publish round-trip proves it.
        await SendAsync(ws, new SubscribeMessage("subcap/a"));
        await SendAsync(ws, new PublishMessage("subcap/a", CreateEvent("subcap/a", "sub-client")));
        Assert.IsType<AckMessage>(await ReceiveAsync(ws));
    }

    [Fact]
    public async Task Publish_BeyondChannelCapPerClient_IsRejected()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(("Protocol:Defaults:MaxChannelsPerClientPerApp", "1"));
        using WebSocket ws = await ConnectAsync(factory);
        await HelloAsync(ws, "chan-client", "fan/one");

        await SendAsync(ws, new PublishMessage("fan/one", CreateEvent("fan/one", "chan-client")));
        Assert.IsType<AckMessage>(await ReceiveAsync(ws));

        // Same channel again is fine.
        await SendAsync(ws, new PublishMessage("fan/one", CreateEvent("fan/one", "chan-client")));
        Assert.IsType<AckMessage>(await ReceiveAsync(ws));

        await SendAsync(ws, new PublishMessage("fan/two", CreateEvent("fan/two", "chan-client")));
        ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(ws));
        Assert.Equal("QUOTA_EXCEEDED", error.Code);
        Assert.Equal("fan/two", error.ChannelId);
    }

    [Fact]
    public async Task Connect_BeyondPerIpLimit_IsRejectedBeforeUpgrade()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(("Protocol:Defaults:MaxConnectionsPerIp", "1"));
        using WebSocket first = await ConnectAsync(factory);

        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(factory));
    }

    [Fact]
    public void TryEnterConnection_ConnectionsPerIp_FreesSlotOnExit()
    {
        RelayBaselineLimiter limiter = CreateLimiter(new ProtocolLimitDefaults { MaxConnectionsPerIp = 2 }, new ManualTimeProvider());

        Assert.True(limiter.TryEnterConnection("10.0.0.1"));
        Assert.True(limiter.TryEnterConnection("10.0.0.1"));
        Assert.False(limiter.TryEnterConnection("10.0.0.1"));
        Assert.True(limiter.TryEnterConnection("10.0.0.2"));

        limiter.ExitConnection("10.0.0.1");

        Assert.True(limiter.TryEnterConnection("10.0.0.1"));
    }

    [Fact]
    public void TryEnterConnection_AttemptRate_RefillsOverTime()
    {
        ManualTimeProvider clock = new();
        RelayBaselineLimiter limiter = CreateLimiter(
            new ProtocolLimitDefaults { ConnectionAttemptsPerMinutePerIp = 2 }, clock);

        Assert.True(limiter.TryEnterConnection("10.0.0.1"));
        Assert.True(limiter.TryEnterConnection("10.0.0.1"));
        Assert.False(limiter.TryEnterConnection("10.0.0.1"));

        clock.Advance(TimeSpan.FromSeconds(30)); // refill rate is 2/min => one token per 30 s

        Assert.True(limiter.TryEnterConnection("10.0.0.1"));
        Assert.False(limiter.TryEnterConnection("10.0.0.1"));
    }

    [Fact]
    public void TryEnterConnection_BothLimitsDisabled_AlwaysAdmits()
    {
        RelayBaselineLimiter limiter = CreateLimiter(new ProtocolLimitDefaults(), new ManualTimeProvider());

        for (int i = 0; i < 500; i++)
            Assert.True(limiter.TryEnterConnection("10.0.0.1"));
    }

    [Fact]
    public void TryTrackChannel_IsPerClientAndPerApp()
    {
        RelayBaselineLimiter limiter = CreateLimiter(
            new ProtocolLimitDefaults { MaxChannelsPerClientPerApp = 2 }, new ManualTimeProvider());

        Assert.True(limiter.TryTrackChannel("app", "alice", "app/a"));
        Assert.True(limiter.TryTrackChannel("app", "alice", "app/b"));
        Assert.False(limiter.TryTrackChannel("app", "alice", "app/c"));
        Assert.True(limiter.TryTrackChannel("app", "alice", "app/a"));

        Assert.True(limiter.TryTrackChannel("app", "bob", "app/c"));
        Assert.True(limiter.TryTrackChannel("other", "alice", "other/a"));
    }

    private static RelayBaselineLimiter CreateLimiter(ProtocolLimitDefaults defaults, TimeProvider clock) =>
        new(Options.Create(new ProtocolOptions { Defaults = defaults }), clock);

    private WebApplicationFactory<Program> CreateFactory(params (string Key, string Value)[] settings) =>
        baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("UseInMemoryStore", "true");
            foreach ((string key, string value) in settings)
                builder.UseSetting(key, value);
        });

    private static async Task<WebSocket> ConnectAsync(WebApplicationFactory<Program> factory)
    {
        Microsoft.AspNetCore.TestHost.WebSocketClient client = factory.Server.CreateWebSocketClient();
        return await client.ConnectAsync(new Uri(factory.Server.BaseAddress, "/ws"), CancellationToken.None);
    }

    private static async Task HelloAsync(WebSocket ws, string clientId, string channel)
    {
        await SendAsync(ws, new HelloMessage(clientId, [channel], new Dictionary<string, long>()));
        Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));
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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
