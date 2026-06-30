using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VestaClient.Federation;
using VestaClient.Relay;
using VestaCore.Identity;
using VestaCore.Relay;

namespace VestaClient.Tests;

/// <summary>
/// Tests for <see cref="FederationClient"/> — descriptor parsing, signature verification, and the
/// owner-key cross-check that makes discovered relays trustworthy enough to *show* (but never to
/// auto-adopt). A stub <see cref="HttpMessageHandler"/> serves canned <c>/federation/*</c> JSON.
/// </summary>
public sealed class FederationClientTests : IDisposable
{
    private static readonly Uri FederationBase = new("https://relay.example/");
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private readonly VestaIdentity _owner = VestaIdentity.Generate();

    public void Dispose() => _owner.Dispose();

    private VestaAppConfig Config() => new("chess", _owner.PublicKey, [new Uri("wss://default.example/ws")]);

    private string OwnerClientId => VestaIdentity.DeriveClientId(_owner.PublicKey);

    private sealed class StubHandler(Func<string, (HttpStatusCode Status, string Json)> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            (HttpStatusCode status, string json) = responder(request.RequestUri!.AbsolutePath);
            HttpResponseMessage response = new(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private static ServerDescriptor SignDescriptor(VestaIdentity relay, params DiscoverableApp[] apps)
    {
        ServerDescriptor descriptor = new()
        {
            RelayPublicKey = string.Empty,
            Urls = ["wss://relay.example/ws"],
            Apps = apps,
            IssuedAt = DateTimeOffset.UtcNow,
            TtlSeconds = 300
        };
        return DescriptorSigner.Sign(descriptor, relay);
    }

    private FederationClient ClientReturning(params ServerDescriptor[] descriptors)
    {
        string json = JsonSerializer.Serialize(descriptors, WebOptions);
        StubHandler handler = new(_ => (HttpStatusCode.OK, json));
        return new FederationClient(Config(), new HttpClient(handler));
    }

    [Fact]
    public async Task DiscoverRelaysForApp_ReturnsRelayWithMatchingOwner()
    {
        using VestaIdentity relay = VestaIdentity.Generate();
        ServerDescriptor descriptor = SignDescriptor(relay, new DiscoverableApp("chess", OwnerClientId));

        FederationClient federation = ClientReturning(descriptor);
        IReadOnlyList<DiscoveredRelay> found = await federation.DiscoverRelaysForAppAsync(FederationBase);

        DiscoveredRelay relayResult = Assert.Single(found);
        Assert.True(relayResult.HostsRequestedApp);
        Assert.Equal("wss://relay.example/ws", relayResult.Urls[0].ToString());
    }

    [Fact]
    public async Task DiscoverRelaysForApp_DropsRelayAdvertisingWrongOwner()
    {
        // Relay claims to host "chess" but under a DIFFERENT owner client id — must be rejected
        // even though its signature is valid (a relay can lie about which apps it carries).
        using VestaIdentity relay = VestaIdentity.Generate();
        ServerDescriptor descriptor = SignDescriptor(relay, new DiscoverableApp("chess", "some-other-owner-id"));

        FederationClient federation = ClientReturning(descriptor);
        IReadOnlyList<DiscoveredRelay> found = await federation.DiscoverRelaysForAppAsync(FederationBase);

        Assert.Empty(found);
    }

    [Fact]
    public async Task DiscoverRelaysForApp_DropsTamperedDescriptor()
    {
        using VestaIdentity relay = VestaIdentity.Generate();
        ServerDescriptor signed = SignDescriptor(relay, new DiscoverableApp("chess", OwnerClientId));
        // Tamper after signing: the signature no longer matches.
        ServerDescriptor tampered = signed with { Urls = ["wss://evil.example/ws"] };

        FederationClient federation = ClientReturning(tampered);
        IReadOnlyList<DiscoveredRelay> found = await federation.DiscoverRelaysForAppAsync(FederationBase);

        Assert.Empty(found);
    }

    [Fact]
    public async Task DiscoverRelaysForApp_DropsUnsignedDescriptor()
    {
        ServerDescriptor unsigned = new()
        {
            RelayPublicKey = "not-a-real-key",
            Urls = ["wss://relay.example/ws"],
            Apps = [new DiscoverableApp("chess", OwnerClientId)],
            IssuedAt = DateTimeOffset.UtcNow,
            TtlSeconds = 300
        };

        FederationClient federation = ClientReturning(unsigned);
        IReadOnlyList<DiscoveredRelay> found = await federation.DiscoverRelaysForAppAsync(FederationBase);

        Assert.Empty(found);
    }

    [Fact]
    public async Task ListAllRelays_FlagsAppHostingRelays()
    {
        using VestaIdentity hostingRelay = VestaIdentity.Generate();
        using VestaIdentity otherRelay = VestaIdentity.Generate();
        ServerDescriptor hosting = SignDescriptor(hostingRelay, new DiscoverableApp("chess", OwnerClientId));
        ServerDescriptor other = SignDescriptor(otherRelay, new DiscoverableApp("chat", "another-owner"));

        FederationClient federation = ClientReturning(hosting, other);
        IReadOnlyList<DiscoveredRelay> found = await federation.ListAllRelaysAsync(FederationBase);

        Assert.Equal(2, found.Count);
        Assert.Single(found, r => r.HostsRequestedApp);
        Assert.Single(found, r => !r.HostsRequestedApp);
    }

    [Fact]
    public async Task DiscoverRelaysForApp_ReturnsEmptyOnHttpError()
    {
        StubHandler handler = new(_ => (HttpStatusCode.InternalServerError, "{}"));
        FederationClient federation = new(Config(), new HttpClient(handler));

        IReadOnlyList<DiscoveredRelay> found = await federation.DiscoverRelaysForAppAsync(FederationBase);

        Assert.Empty(found);
    }

    [Theory]
    [InlineData("wss://r.example/ws", "https://r.example/")]
    [InlineData("ws://r.example:8080/ws", "http://r.example:8080/")]
    [InlineData("https://r.example/federation", "https://r.example/")]
    public void ToFederationBaseUrl_MapsSchemeAndStripsPath(string input, string expected)
    {
        bool ok = FederationClient.ToFederationBaseUrl(new Uri(input), out Uri? result);

        Assert.True(ok);
        Assert.Equal(expected, result!.ToString());
    }

    [Fact]
    public void ToFederationBaseUrl_RejectsUnsupportedScheme()
    {
        Assert.False(FederationClient.ToFederationBaseUrl(new Uri("ftp://r.example/x"), out Uri? result));
        Assert.Null(result);
    }
}
