using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vesta;

namespace VestaClient.Tests;

/// <summary>One real relay (Kestrel, in-memory store, signed events required) shared by the flow tests.</summary>
public sealed class KestrelRelayFixture : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public KestrelRelayFixture()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("UseInMemoryStore", "true");
            b.UseSetting("Protocol:RequireSignedEvents", "true");
            b.UseSetting("urls", "http://127.0.0.1:0");
        });
        _factory.UseKestrel();
        _factory.StartServer();
        Microsoft.AspNetCore.Hosting.Server.IServer server = (Microsoft.AspNetCore.Hosting.Server.IServer)_factory.Services.GetService(typeof(Microsoft.AspNetCore.Hosting.Server.IServer))!;
        Uri http = new(server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First());
        RelayUrl = new Uri($"ws://{http.Host}:{http.Port}/ws");
    }

    public Uri RelayUrl { get; }

    public void Dispose() => _factory.Dispose();
}

/// <summary>
/// End-to-end #33 phase C through the real relay (in-memory store, Kestrel): an admin creates an encrypted
/// channel, a member joins and gets keys, both publish and read plaintext, the admin removes the member and
/// rotates, and the removed member can neither read new events nor publish.
/// </summary>
public class EncryptedChannelFlowTests(KestrelRelayFixture relay) : IClassFixture<KestrelRelayFixture>
{
    private Uri RelayUrl => relay.RelayUrl;

    [Fact]
    public async Task Admin_Member_Publish_Rotate_Revoke_EndToEnd()
    {
        string channelId = $"enc{Guid.NewGuid():N}"[..12] + "/room";
        using VestaIdentity adminIdentity = VestaIdentity.Generate();
        using VestaIdentity memberIdentity = VestaIdentity.Generate();
        using VestaIdentity strangerIdentity = VestaIdentity.Generate();
        VestaAppConfig config = new(channelId.Split('/')[0], adminIdentity.PublicKey, [RelayUrl]);

        await using VestaConnection admin = await ConnectAsync(adminIdentity, config);
        await using VestaConnection member = await ConnectAsync(memberIdentity, config);
        await using VestaConnection stranger = await ConnectAsync(strangerIdentity, config);

        // Admin creates the channel and holds epoch 1.
        await using EncryptedChannel adminChannel = await admin.CreateEncryptedChannelAsync(channelId, [memberIdentity.ClientId]);
        Assert.True(adminChannel.IsAdmin);
        Assert.Equal(1, adminChannel.CurrentEpoch);

        // Admin publishes before the member has joined; the relay stores only ciphertext.
        List<EncryptedChannel.DecryptedEvent> adminSeen = [];
        adminChannel.OnEvent += adminSeen.Add;
        await adminChannel.PublishAsync("app.note", JsonDocument.Parse("""{"text":"first"}""").RootElement);

        // Member opens, requests keys, and receives the history plus live events in plaintext.
        List<EncryptedChannel.DecryptedEvent> memberSeen = [];
        await using EncryptedChannel memberChannel = await member.OpenEncryptedChannelAsync(channelId, new EncryptedChannelOptions
        {
            AdminPublicKeys = [adminIdentity.PublicKey],
        });
        memberChannel.OnEvent += memberSeen.Add;
        await memberChannel.WaitForKeyAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, memberChannel.CurrentEpoch);

        await WaitUntilAsync(() => memberSeen.Count >= 1);
        Assert.Equal("first", memberSeen[0].Event.Event.Payload.GetProperty("text").GetString());
        Assert.True(EncryptedPayload.IsEnvelope(memberSeen[0].Sealed.Event.Payload));

        await memberChannel.PublishAsync("app.note", JsonDocument.Parse("""{"text":"from member"}""").RootElement);
        await WaitUntilAsync(() => adminSeen.Any(e => e.Event.Event.Payload.GetProperty("text").GetString() == "from member"));

        // A stranger who fetches the channel is refused by the relay (private); even the raw store holds envelopes.
        TaskCompletionSource<ErrorMessage> denied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        stranger.OnError += e => { if (e.ChannelId == channelId) denied.TrySetResult(e); };
        await stranger.FetchAsync(channelId, 0);
        Assert.Equal("ACCESS_DENIED", (await denied.Task.WaitAsync(TimeSpan.FromSeconds(10))).Code);

        // Admin removes the member: revoke on the relay + rotate to epoch 2.
        await adminChannel.RemoveMemberAsync(memberIdentity.ClientId);
        Assert.Equal(2, adminChannel.CurrentEpoch);

        await adminChannel.PublishAsync("app.note", JsonDocument.Parse("""{"text":"after rotation"}""").RootElement);
        await Task.Delay(300);
        Assert.DoesNotContain(memberSeen, e => e.Event.Event.Payload.GetProperty("text").GetString() == "after rotation");
        Assert.Equal(1, memberChannel.CurrentEpoch);

        TaskCompletionSource<ErrorMessage> memberDenied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        member.OnError += e => { if (e.ChannelId == channelId) memberDenied.TrySetResult(e); };
        await memberChannel.PublishAsync("app.note", JsonDocument.Parse("""{"text":"still here?"}""").RootElement);
        Assert.Equal("ACCESS_DENIED", (await memberDenied.Task.WaitAsync(TimeSpan.FromSeconds(10))).Code);
    }

    [Fact]
    public async Task Member_OpeningLater_ReceivesEveryEpoch_AndReadsOldHistory()
    {
        string channelId = $"enc{Guid.NewGuid():N}"[..12] + "/late";
        using VestaIdentity adminIdentity = VestaIdentity.Generate();
        using VestaIdentity memberIdentity = VestaIdentity.Generate();
        VestaAppConfig config = new(channelId.Split('/')[0], adminIdentity.PublicKey, [RelayUrl]);

        await using VestaConnection admin = await ConnectAsync(adminIdentity, config);
        await using EncryptedChannel adminChannel = await admin.CreateEncryptedChannelAsync(channelId);
        await adminChannel.PublishAsync("app.note", JsonDocument.Parse("""{"n":1}""").RootElement);
        await adminChannel.RotateKeyAsync();
        await adminChannel.PublishAsync("app.note", JsonDocument.Parse("""{"n":2}""").RootElement);
        await admin.GrantAccessAsync(channelId, memberIdentity.ClientId);
        await Task.Delay(200);

        await using VestaConnection member = await ConnectAsync(memberIdentity, config);
        List<int> seen = [];
        await using EncryptedChannel memberChannel = await member.OpenEncryptedChannelAsync(channelId, new EncryptedChannelOptions
        {
            AdminPublicKeys = [adminIdentity.PublicKey],
        });
        memberChannel.OnEvent += e => { lock (seen) seen.Add(e.Event.Event.Payload.GetProperty("n").GetInt32()); };

        await memberChannel.WaitForKeyAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => { lock (seen) return seen.Count == 2; });
        lock (seen) Assert.Equal([1, 2], seen);
        Assert.Equal(2, memberChannel.CurrentEpoch);
    }

    [Fact]
    public async Task CreateEncryptedChannel_WhenTheRelayRefuses_Throws()
    {
        string channelId = $"enc{Guid.NewGuid():N}"[..12] + "/dup";
        using VestaIdentity adminIdentity = VestaIdentity.Generate();
        VestaAppConfig config = new(channelId.Split('/')[0], adminIdentity.PublicKey, [RelayUrl]);
        await using VestaConnection admin = await ConnectAsync(adminIdentity, config);

        await using EncryptedChannel first = await admin.CreateEncryptedChannelAsync(channelId);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => admin.CreateEncryptedChannelAsync(channelId));
        Assert.Contains("CHANNEL_EXISTS", error.Message);
    }

    private static async Task<VestaConnection> ConnectAsync(VestaIdentity identity, VestaAppConfig config)
    {
        VestaConnection connection = new(identity, config, relayDirectory: new RelayDirectory(config));
        await connection.ConnectAsync(config.DefaultRelays[0], []);
        return connection;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(25);
        }
    }
}
