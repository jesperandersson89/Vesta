using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vesta;
using VestaServer.Storage;

namespace VestaServer.Tests;

/// <summary>
/// Relay side of per-channel encryption (#33 phase A): the <c>encrypted</c> flag on CREATE_CHANNEL,
/// the WELCOME <c>features</c> list, the envelope shape check on PUBLISH, REVOKE_ACCESS and the
/// <c>max_encrypted_channels</c> quota. The relay never sees a key; these tests only use envelopes.
/// </summary>
public class EncryptedChannelTests(WebApplicationFactory<Program> baseFactory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;
    private readonly WebApplicationFactory<Program> _factory = baseFactory.WithWebHostBuilder(b => b.UseSetting("UseInMemoryStore", "true"));

    private IAppStore Apps => _factory.Services.GetRequiredService<IAppStore>();
    private IChannelAccessStore Access => _factory.Services.GetRequiredService<IChannelAccessStore>();

    private static readonly JsonElement Envelope = JsonDocument.Parse(
        """{"enc":1,"kid":1,"n":"AAAAAAAAAAAAAAAA","ct":"c2VjcmV0"}""").RootElement;

    [Fact]
    public async Task Welcome_AdvertisesEncryptedChannelsFeature()
    {
        Microsoft.AspNetCore.TestHost.WebSocketClient client = _factory.Server.CreateWebSocketClient();
        using WebSocket ws = await client.ConnectAsync(new Uri(_factory.Server.BaseAddress, "/ws"), CancellationToken.None);
        await SendAsync(ws, new HelloMessage("feat-client", [], new Dictionary<string, long>()));

        WelcomeMessage welcome = Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));

        Assert.NotNull(welcome.Features);
        Assert.Contains(RelayFeatures.EncryptedChannels, welcome.Features);
    }

    [Fact]
    public async Task CreateChannel_Encrypted_MustBePrivate()
    {
        using WebSocket admin = await HelloAsync("enc-admin-1");

        await SendAsync(admin, new CreateChannelMessage("encapp/bcast", "broadcast", [], Encrypted: true));
        ErrorMessage broadcast = Assert.IsType<ErrorMessage>(await ReceiveAsync(admin));
        Assert.Equal("INVALID_CHANNEL", broadcast.Code);
        Assert.Equal("encapp/bcast", broadcast.ChannelId);

        await SendAsync(admin, new CreateChannelMessage("encapp/public", "public", [], Encrypted: true));
        Assert.Equal("INVALID_CHANNEL", Assert.IsType<ErrorMessage>(await ReceiveAsync(admin)).Code);

        await SendAsync(admin, new CreateChannelMessage("encapp/private", "private", [], Encrypted: true));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));
        Assert.True(await Access.IsEncryptedAsync("encapp/private"));
        Assert.False(await Access.IsEncryptedAsync("encapp/bcast"));
    }

    [Fact]
    public async Task Publish_ToEncryptedChannel_RequiresEnvelope_ButAllowsVestaControlEvents()
    {
        const string channel = "encapp/room";
        using WebSocket admin = await HelloAsync("enc-admin-2");
        await SendAsync(admin, new CreateChannelMessage(channel, "private", [], Encrypted: true));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));

        VestaEvent plaintext = CreateEvent(channel, "enc-admin-2", "app.note", """{"text":"hello"}""");
        await SendAsync(admin, new PublishMessage(channel, plaintext));
        ErrorMessage rejected = Assert.IsType<ErrorMessage>(await ReceiveAsync(admin));
        Assert.Equal("ENCRYPTION_REQUIRED", rejected.Code);
        Assert.Equal(plaintext.Id, rejected.EventId);
        Assert.Equal(channel, rejected.ChannelId);

        VestaEvent almost = CreateEvent(channel, "enc-admin-2", "app.note", """{"enc":1,"kid":1,"n":"AAAAAAAAAAAAAAAA","ct":"c2VjcmV0","extra":1}""");
        await SendAsync(admin, new PublishMessage(channel, almost));
        Assert.Equal("ENCRYPTION_REQUIRED", Assert.IsType<ErrorMessage>(await ReceiveAsync(admin)).Code);

        VestaEvent sealedEvent = CreateEvent(channel, "enc-admin-2", "app.note") with { Payload = Envelope };
        await SendAsync(admin, new PublishMessage(channel, sealedEvent));
        AckMessage ack = Assert.IsType<AckMessage>(await ReceiveAsync(admin));
        Assert.Equal(1, ack.Sequence);

        VestaEvent keyRequest = CreateEvent(channel, "enc-admin-2", "vesta.channel-key-request", """{"x25519PublicKey":"abc"}""");
        await SendAsync(admin, new PublishMessage(channel, keyRequest));
        Assert.Equal(2, Assert.IsType<AckMessage>(await ReceiveAsync(admin)).Sequence);
    }

    [Fact]
    public async Task Publish_ToPlainChannel_IsUnaffected()
    {
        const string channel = "plainapp/room";
        using WebSocket admin = await HelloAsync("plain-admin");
        await SendAsync(admin, new CreateChannelMessage(channel, "private", []));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));

        await SendAsync(admin, new PublishMessage(channel, CreateEvent(channel, "plain-admin", "app.note", """{"text":"hello"}""")));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));
    }

    [Fact]
    public async Task RevokeAccess_RemovesMember_IsIdempotent_AndAdminCannotRevokeItself()
    {
        const string channel = "revoke/room";
        using WebSocket admin = await HelloAsync("rev-admin");
        await SendAsync(admin, new CreateChannelMessage(channel, "private", ["rev-member"]));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));

        using WebSocket member = await HelloAsync("rev-member", channel);
        await SendAsync(member, new PublishMessage(channel, CreateEvent(channel, "rev-member")));
        Assert.IsType<AckMessage>(await ReceiveAsync(member));
        Assert.IsType<EventMessage>(await ReceiveAsync(admin));

        await SendAsync(admin, new RevokeAccessMessage(channel, "rev-member"));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));

        await SendAsync(member, new PublishMessage(channel, CreateEvent(channel, "rev-member")));
        Assert.Equal("ACCESS_DENIED", Assert.IsType<ErrorMessage>(await ReceiveAsync(member)).Code);
        Assert.False(await Access.CanAccessAsync(channel, "rev-member"));

        await SendAsync(admin, new RevokeAccessMessage(channel, "rev-member"));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));

        await SendAsync(admin, new RevokeAccessMessage(channel, "rev-admin"));
        ErrorMessage self = Assert.IsType<ErrorMessage>(await ReceiveAsync(admin));
        Assert.Equal("ACCESS_DENIED", self.Code);
        Assert.True(await Access.IsAdminAsync(channel, "rev-admin"));
    }

    [Fact]
    public async Task RevokeAccess_ByNonAdmin_IsDenied()
    {
        const string channel = "revoke/denied";
        using WebSocket admin = await HelloAsync("rev2-admin");
        await SendAsync(admin, new CreateChannelMessage(channel, "private", ["rev2-a", "rev2-b"]));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));

        using WebSocket memberA = await HelloAsync("rev2-a");
        await SendAsync(memberA, new RevokeAccessMessage(channel, "rev2-b"));

        Assert.Equal("ACCESS_DENIED", Assert.IsType<ErrorMessage>(await ReceiveAsync(memberA)).Code);
        Assert.True(await Access.CanAccessAsync(channel, "rev2-b"));
    }

    [Fact]
    public async Task CreateChannel_OverMaxEncryptedChannels_RejectedWithQuotaExceeded_PlainChannelsStillAllowed()
    {
        await Apps.RegisterAsync("encquota", "encquota-owner");
        await Apps.SetQuotasAsync("encquota", new AppQuotas(MaxEncryptedChannels: 1));

        using WebSocket admin = await HelloAsync("encquota-owner");
        await SendAsync(admin, new CreateChannelMessage("encquota/one", "private", [], Encrypted: true));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));

        await SendAsync(admin, new CreateChannelMessage("encquota/two", "private", [], Encrypted: true));
        ErrorMessage error = Assert.IsType<ErrorMessage>(await ReceiveAsync(admin));
        Assert.Equal("QUOTA_EXCEEDED", error.Code);
        Assert.Contains("max_encrypted_channels", error.Message);
        Assert.Equal("encquota/two", error.ChannelId);

        await SendAsync(admin, new CreateChannelMessage("encquota/plain", "private", []));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));

        Assert.Equal(1, await Access.CountEncryptedChannelsByAppAsync("encquota"));
        Assert.Equal(2, await Access.CountChannelsByAppAsync("encquota"));
    }

    [Fact]
    public async Task ListChannels_ReportsEncryptedFlag()
    {
        using WebSocket admin = await HelloAsync("list-admin");
        await SendAsync(admin, new CreateChannelMessage("listenc/secret", "private", [], Encrypted: true));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));
        await SendAsync(admin, new CreateChannelMessage("listenc/open", "private", []));
        Assert.IsType<AckMessage>(await ReceiveAsync(admin));

        IReadOnlyList<ChannelSummary> rows = await Access.ListChannelsAsync("listenc", includeDeleted: true);

        Assert.True(rows.Single(r => r.Id == "listenc/secret").Encrypted);
        Assert.False(rows.Single(r => r.Id == "listenc/open").Encrypted);
    }

    private async Task<WebSocket> HelloAsync(string clientId, params string[] channels)
    {
        Microsoft.AspNetCore.TestHost.WebSocketClient client = _factory.Server.CreateWebSocketClient();
        WebSocket ws = await client.ConnectAsync(new Uri(_factory.Server.BaseAddress, "/ws"), CancellationToken.None);
        await SendAsync(ws, new HelloMessage(clientId, channels, new Dictionary<string, long>()));
        Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));
        return ws;
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

    private static VestaEvent CreateEvent(string channelId, string clientId, string eventType = "test.message", string payload = """{"text":"hello"}""") => new(
        Id: Guid.NewGuid(),
        ChannelId: channelId,
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: clientId,
        EventType: eventType,
        Payload: JsonDocument.Parse(payload).RootElement);
}
