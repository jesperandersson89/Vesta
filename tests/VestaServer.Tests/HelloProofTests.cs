using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vesta;

namespace VestaServer.Tests;

public class HelloProofTests(WebApplicationFactory<Program> baseFactory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;

    [Fact]
    public async Task Hello_WithValidProof_CompletesHandshake_AndGrantsPrivateAccess()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using VestaIdentity owner = VestaIdentity.Generate();
        using WebSocket ws = await ConnectAsync(factory);
        await ProvenHelloAsync(ws, owner, []);

        await SendAsync(ws, new CreateChannelMessage("proof/room", "private", []));
        Assert.IsType<AckMessage>(await ReceiveAsync(ws));
    }

    [Fact]
    public async Task Hello_WithWrongKeySignature_IsRejected_AndLeavesNoIdentity()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using VestaIdentity victim = VestaIdentity.Generate();
        using VestaIdentity attacker = VestaIdentity.Generate();
        using WebSocket ws = await ConnectAsync(factory);

        ChallengeMessage challenge = await StartHelloAsync(ws, victim, []);
        await SendAsync(ws, new ProveMessage(HelloProof.Sign(attacker, challenge.Nonce)));

        Assert.Equal("PROOF_INVALID", Assert.IsType<ErrorMessage>(await ReceiveAsync(ws)).Code);
        await SendAsync(ws, new RegisterAppMessage("stolen"));
        Assert.Equal("HELLO_REQUIRED", Assert.IsType<ErrorMessage>(await ReceiveAsync(ws)).Code);
    }

    [Fact]
    public async Task Prove_IsSingleUse()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using VestaIdentity identity = VestaIdentity.Generate();
        using WebSocket ws = await ConnectAsync(factory);
        ChallengeMessage challenge = await StartHelloAsync(ws, identity, []);
        string proof = HelloProof.Sign(identity, challenge.Nonce);
        await SendAsync(ws, new ProveMessage(proof));
        Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));

        await SendAsync(ws, new ProveMessage(proof));

        Assert.Equal("UNEXPECTED_PROOF", Assert.IsType<ErrorMessage>(await ReceiveAsync(ws)).Code);
    }

    [Fact]
    public async Task Prove_WithoutHello_IsRejected()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using WebSocket ws = await ConnectAsync(factory);

        await SendAsync(ws, new ProveMessage("AAAA"));

        Assert.Equal("UNEXPECTED_PROOF", Assert.IsType<ErrorMessage>(await ReceiveAsync(ws)).Code);
    }

    [Fact]
    public async Task Hello_WithoutProofSupport_StillWorks_ByDefault()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using VestaIdentity identity = VestaIdentity.Generate();
        using WebSocket ws = await ConnectAsync(factory);

        await SendAsync(ws, new HelloMessage(identity.ClientId, [], new Dictionary<string, long>(), Base64Url.Encode(identity.PublicKey)));

        Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));
    }

    [Fact]
    public async Task RequireHelloProof_RejectsClientsThatCannotProve()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(("Protocol:RequireHelloProof", "true"));
        using VestaIdentity identity = VestaIdentity.Generate();
        using WebSocket legacy = await ConnectAsync(factory);
        await SendAsync(legacy, new HelloMessage(identity.ClientId, [], new Dictionary<string, long>(), Base64Url.Encode(identity.PublicKey)));
        Assert.Equal("PROOF_REQUIRED", Assert.IsType<ErrorMessage>(await ReceiveAsync(legacy)).Code);

        using WebSocket keyless = await ConnectAsync(factory);
        await SendAsync(keyless, new HelloMessage("no-key", [], new Dictionary<string, long>(), ProofSupported: true));
        Assert.Equal("PROOF_REQUIRED", Assert.IsType<ErrorMessage>(await ReceiveAsync(keyless)).Code);
    }

    [Fact]
    public async Task RequireHelloProof_BlocksEverythingUntilProven()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(("Protocol:RequireHelloProof", "true"));
        using WebSocket ws = await ConnectAsync(factory);

        await SendAsync(ws, new SubscribeMessage("any/thing"));

        Assert.Equal("PROOF_REQUIRED", Assert.IsType<ErrorMessage>(await ReceiveAsync(ws)).Code);
    }

    [Fact]
    public async Task RequireHelloProof_ImpersonatingAMember_CannotReadTheirPrivateChannel()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(("Protocol:RequireHelloProof", "true"));
        using VestaIdentity member = VestaIdentity.Generate();
        using VestaIdentity attacker = VestaIdentity.Generate();
        using WebSocket memberWs = await ConnectAsync(factory);
        await ProvenHelloAsync(memberWs, member, []);
        await SendAsync(memberWs, new CreateChannelMessage("secret/room", "private", []));
        Assert.IsType<AckMessage>(await ReceiveAsync(memberWs));

        // The attacker knows the member's client id and public key (both are visible in event streams).
        using WebSocket attackerWs = await ConnectAsync(factory);
        ChallengeMessage challenge = await StartHelloAsync(attackerWs, member, ["secret/room"]);
        await SendAsync(attackerWs, new ProveMessage(HelloProof.Sign(attacker, challenge.Nonce)));
        Assert.Equal("PROOF_INVALID", Assert.IsType<ErrorMessage>(await ReceiveAsync(attackerWs)).Code);

        await SendAsync(attackerWs, new FetchMessage("secret/room", FromSequence: 1));
        Assert.Equal("PROOF_REQUIRED", Assert.IsType<ErrorMessage>(await ReceiveAsync(attackerWs)).Code);
    }

    [Fact]
    public async Task AnnouncingAnAdminKey_WithoutProof_DoesNotMakeYouAdmin()
    {
        using VestaIdentity admin = VestaIdentity.Generate();
        await using WebApplicationFactory<Program> factory = CreateFactory(
            ("Admin:BootstrapPublicKeys:0", Base64Url.Encode(admin.PublicKey)));
        using WebSocket ws = await ConnectAsync(factory);
        await SendAsync(ws, new HelloMessage(admin.ClientId, [], new Dictionary<string, long>(), Base64Url.Encode(admin.PublicKey)));
        Assert.IsType<WelcomeMessage>(await ReceiveAsync(ws));

        await SendAsync(ws, new DeleteChannelMessage("any/thing"));

        Assert.Equal("NOT_ADMIN", Assert.IsType<ErrorMessage>(await ReceiveAsync(ws)).Code);
    }

    [Fact]
    public async Task MaxConnectionsPerClientId_CountsOnlyProvenConnections()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(("Protocol:Defaults:MaxConnectionsPerClientId", "2"));
        using VestaIdentity player = VestaIdentity.Generate();

        // Anyone can claim the player's client id without proof; that must not use up the player's slots.
        List<WebSocket> squatters = [];
        for (int i = 0; i < 3; i++)
        {
            WebSocket squatter = await ConnectAsync(factory);
            await SendAsync(squatter, new HelloMessage(player.ClientId, [], new Dictionary<string, long>()));
            Assert.IsType<WelcomeMessage>(await ReceiveAsync(squatter));
            squatters.Add(squatter);
        }

        using WebSocket first = await ConnectAsync(factory);
        await ProvenHelloAsync(first, player, []);
        using WebSocket second = await ConnectAsync(factory);
        await ProvenHelloAsync(second, player, []);

        using WebSocket third = await ConnectAsync(factory);
        ChallengeMessage challenge = await StartHelloAsync(third, player, []);
        await SendAsync(third, new ProveMessage(HelloProof.Sign(player, challenge.Nonce)));
        ErrorMessage limited = Assert.IsType<ErrorMessage>(await ReceiveAsync(third));

        Assert.Equal("CONNECTION_LIMITED", limited.Code);
        foreach (WebSocket squatter in squatters)
            squatter.Dispose();
    }

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

    private static async Task<ChallengeMessage> StartHelloAsync(WebSocket ws, VestaIdentity identity, IReadOnlyList<string> channels)
    {
        await SendAsync(ws, new HelloMessage(
            identity.ClientId, channels, new Dictionary<string, long>(), Base64Url.Encode(identity.PublicKey), ProofSupported: true));
        return Assert.IsType<ChallengeMessage>(await ReceiveAsync(ws));
    }

    private static async Task ProvenHelloAsync(WebSocket ws, VestaIdentity identity, IReadOnlyList<string> channels)
    {
        ChallengeMessage challenge = await StartHelloAsync(ws, identity, channels);
        await SendAsync(ws, new ProveMessage(HelloProof.Sign(identity, challenge.Nonce)));
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
}
