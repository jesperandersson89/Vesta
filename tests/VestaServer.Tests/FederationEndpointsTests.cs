using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VestaCore.Protocol;
using VestaCore.Relay;
using VestaCore.Serialization;

namespace VestaServer.Tests;

/// <summary>
/// Integration tests for the federation HTTP surface (<c>/federation/*</c>), exercised against a
/// real host with <c>Discovery:Enabled=true</c> and the in-memory store. Covers the relay's own
/// descriptor, the dual opt-in (only owner-flagged apps are advertised), and the app lookup.
/// </summary>
public class FederationEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly JsonSerializerOptions JsonOptions = VestaJsonOptions.Default;
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    public FederationEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("UseInMemoryStore", "true");
            builder.UseSetting("Discovery:Enabled", "true");
            builder.UseSetting("Discovery:PublicUrls:0", "wss://test-relay.example/ws");
        });
    }

    [Fact]
    public async Task Descriptor_IsSignedAndAdvertisesPublicUrls()
    {
        using HttpClient client = _factory.CreateClient();

        ServerDescriptor? descriptor =
            await client.GetFromJsonAsync<ServerDescriptor>("/federation/descriptor", WebOptions);

        Assert.NotNull(descriptor);
        Assert.True(DescriptorSigner.Verify(descriptor!));
        Assert.Contains("wss://test-relay.example/ws", descriptor!.Urls);
    }

    [Fact]
    public async Task Descriptor_ExcludesAppsThatDidNotOptIn()
    {
        // Register an app WITHOUT the discoverable flag — it must not appear in the descriptor.
        await RegisterAppAsync("plainapp", "client-plain", discoverable: false);

        using HttpClient client = _factory.CreateClient();
        ServerDescriptor descriptor =
            (await client.GetFromJsonAsync<ServerDescriptor>("/federation/descriptor", WebOptions))!;

        Assert.DoesNotContain(descriptor.Apps, a => a.AppId == "plainapp");
    }

    [Fact]
    public async Task Descriptor_IncludesDiscoverableApp()
    {
        await RegisterAppAsync("openapp", "client-open", discoverable: true);

        using HttpClient client = _factory.CreateClient();
        ServerDescriptor descriptor =
            (await client.GetFromJsonAsync<ServerDescriptor>("/federation/descriptor", WebOptions))!;

        DiscoverableApp app = Assert.Single(descriptor.Apps, a => a.AppId == "openapp");
        Assert.False(string.IsNullOrEmpty(app.OwnerClientId));
    }

    [Fact]
    public async Task AppsEndpoint_ReturnsRelayForDiscoverableApp()
    {
        await RegisterAppAsync("lookupapp", "client-lookup", discoverable: true);

        using HttpClient client = _factory.CreateClient();
        List<ServerDescriptor>? descriptors =
            await client.GetFromJsonAsync<List<ServerDescriptor>>("/federation/apps/lookupapp", WebOptions);

        Assert.NotNull(descriptors);
        ServerDescriptor match = Assert.Single(descriptors!);
        Assert.True(DescriptorSigner.Verify(match));
        Assert.Contains(match.Apps, a => a.AppId == "lookupapp");
    }

    [Fact]
    public async Task AppsEndpoint_EmptyForUnknownApp()
    {
        using HttpClient client = _factory.CreateClient();
        List<ServerDescriptor>? descriptors =
            await client.GetFromJsonAsync<List<ServerDescriptor>>("/federation/apps/ghostapp", WebOptions);

        Assert.NotNull(descriptors);
        Assert.Empty(descriptors!);
    }

    [Fact]
    public async Task AppsEndpoint_RejectsInvalidAppId()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/federation/apps/Invalid_App");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Peers_IncludesOwnDescriptor()
    {
        using HttpClient client = _factory.CreateClient();
        List<ServerDescriptor>? peers =
            await client.GetFromJsonAsync<List<ServerDescriptor>>("/federation/peers", WebOptions);

        Assert.NotNull(peers);
        Assert.Contains(peers!, DescriptorSigner.Verify);
    }

    private async Task RegisterAppAsync(string appId, string clientId, bool discoverable)
    {
        using WebSocket ws = await ConnectAsync();
        await SendAsync(ws, new HelloMessage(clientId, [], new Dictionary<string, long>()));
        await ReceiveAsync(ws); // WELCOME

        await SendAsync(ws, new RegisterAppMessage(appId, discoverable));
        ProtocolMessage? ack = await ReceiveAsync(ws);
        Assert.IsType<AckMessage>(ack);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    private async Task<WebSocket> ConnectAsync()
    {
        Microsoft.AspNetCore.TestHost.WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        return await wsClient.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, "/ws"),
            CancellationToken.None);
    }

    private static async Task SendAsync(WebSocket ws, ProtocolMessage message)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes<ProtocolMessage>(message, JsonOptions);
        await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<ProtocolMessage?> ReceiveAsync(WebSocket ws, CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[16384];
        using MemoryStream stream = new();

        ValueWebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        stream.Position = 0;
        return await JsonSerializer.DeserializeAsync<ProtocolMessage>(stream, JsonOptions, cancellationToken);
    }
}
