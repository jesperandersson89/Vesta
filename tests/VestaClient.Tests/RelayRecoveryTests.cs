using System.Net;
using System.Text.Json;
using VestaClient.Federation;
using VestaClient.Relay;
using VestaCore.Identity;
using VestaCore.Relay;

namespace VestaClient.Tests;

/// <summary>
/// Tests for the relay-recovery layer: the peer cache, the headless <see cref="RelayRecoverySession"/>
/// state machine, and the rule that nothing is adopted without an explicit user action.
/// </summary>
public sealed class RelayRecoveryTests : IDisposable
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);
    private static readonly Uri DeadRelay = new("wss://dead.example/ws");

    private readonly VestaIdentity _owner = VestaIdentity.Generate();
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        _owner.Dispose();
        foreach (string file in _tempFiles)
        {
            File.Delete(file);
        }
    }

    private string OwnerClientId => VestaIdentity.DeriveClientId(_owner.PublicKey);

    private VestaAppConfig Config(IReadOnlyList<Uri>? seeds = null)
        => new("chess", _owner.PublicKey, [DeadRelay], seeds);

    private sealed class FakeOverrideStore : IRelayOverrideStore
    {
        private Uri? _relay;

        public Uri? GetOverride() => _relay;

        public void SetOverride(Uri relay) => _relay = relay;

        public void ClearOverride() => _relay = null;
    }

    private sealed class FakeHost(RelayDirectory directory, bool connectSucceeds = true) : IRelayRecoveryHost
    {
        public RelayDirectory RelayDirectory { get; } = directory;

        public IReadOnlyList<Uri> Relays { get; } = [DeadRelay];

        public bool AutoReconnect => true;

        public List<Uri> Adopted { get; } = [];

        public bool ConnectSucceeds { get; set; } = connectSucceeds;

        public event Action<string>? OnDisconnected;

        public event Action? OnReconnected;

        public event Action<RelaysExhaustedInfo>? OnRelaysExhausted;

        public void RaiseDisconnected() => OnDisconnected?.Invoke("lost");

        public void RaiseReconnected() => OnReconnected?.Invoke();

        public void RaiseExhausted(RelaysExhaustedInfo info) => OnRelaysExhausted?.Invoke(info);

        public Task<bool> ReconnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(ConnectSucceeds);

        public Task<bool> SetUserRelayOverrideAsync(Uri relay, CancellationToken cancellationToken = default)
        {
            Adopted.Add(relay);
            return Task.FromResult(ConnectSucceeds);
        }

        public Task<bool> ClearUserRelayOverrideAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class StubHandler(Func<string, string> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(responder(request.RequestUri!.AbsolutePath), System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private ServerDescriptor Descriptor(string url, bool hostsApp)
    {
        using VestaIdentity relay = VestaIdentity.Generate();
        ServerDescriptor descriptor = new()
        {
            RelayPublicKey = string.Empty,
            Urls = [url],
            Apps = hostsApp ? [new DiscoverableApp("chess", OwnerClientId)] : [],
            IssuedAt = DateTimeOffset.UtcNow,
            TtlSeconds = 300
        };
        return DescriptorSigner.Sign(descriptor, relay);
    }

    private FederationClient Federation(VestaAppConfig config, params ServerDescriptor[] descriptors)
    {
        string json = JsonSerializer.Serialize(descriptors, WebOptions);
        return new FederationClient(config, new HttpClient(new StubHandler(_ => json)));
    }

    private static RelaysExhaustedInfo Exhausted()
        => new([new RelayAttempt(DeadRelay, "connection refused")], Passes: 3);

    [Fact]
    public void FilePeerCacheStore_RoundTripsPeers()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vesta-peers-{Guid.NewGuid():N}.json");
        _tempFiles.Add(path);
        FilePeerCacheStore store = new(path);
        DiscoveredRelay peer = new("key", [new Uri("wss://a.example/ws")], HostsRequestedApp: true, DateTimeOffset.UtcNow);

        store.Save([peer]);
        IReadOnlyList<DiscoveredRelay> loaded = new FilePeerCacheStore(path).Load();

        DiscoveredRelay result = Assert.Single(loaded);
        Assert.Equal("key", result.RelayPublicKey);
        Assert.True(result.HostsRequestedApp);
        Assert.Equal(new Uri("wss://a.example/ws"), result.Urls[0]);
    }

    [Fact]
    public void FilePeerCacheStore_CorruptFile_LoadsEmpty()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vesta-peers-{Guid.NewGuid():N}.json");
        _tempFiles.Add(path);
        File.WriteAllText(path, "{ not json");

        Assert.Empty(new FilePeerCacheStore(path).Load());
    }

    [Fact]
    public async Task ConnectAsync_AllRelaysUnreachable_ThrowsRelaysExhaustedWithReasons()
    {
        VestaAppConfig config = new("chess", _owner.PublicKey, [new Uri("ws://127.0.0.1:1/ws")]);
        await using VestaConnection connection = new("test-client", config, relayDirectory: new RelayDirectory(config));

        RelaysExhaustedException ex = await Assert.ThrowsAsync<RelaysExhaustedException>(
            () => connection.ConnectAsync(config.DefaultRelays, ["chess/room"]));

        RelayAttempt attempt = Assert.Single(ex.Info.Attempts);
        Assert.Equal(new Uri("ws://127.0.0.1:1/ws"), attempt.Relay);
        Assert.False(string.IsNullOrWhiteSpace(attempt.Reason));
    }

    [Fact]
    public void Session_ExhaustionSignal_MovesToExhaustedWithAttempts()
    {
        FakeHost host = new(new RelayDirectory(Config(), new FakeOverrideStore()));
        using RelayRecoverySession session = new(host, Federation(Config()));

        host.RaiseDisconnected();
        Assert.Equal(RelayRecoveryPhase.Degraded, session.Snapshot.Phase);

        host.RaiseExhausted(Exhausted());

        Assert.Equal(RelayRecoveryPhase.Exhausted, session.Snapshot.Phase);
        Assert.Equal("connection refused", Assert.Single(session.Snapshot.TriedRelays).Reason);
        Assert.True(session.Snapshot.BackgroundRetrying);
    }

    [Fact]
    public void Session_ReportExhausted_AfterFailedFirstConnect_OpensPrompt()
    {
        FakeHost host = new(new RelayDirectory(Config(), new FakeOverrideStore()));
        using RelayRecoverySession session = new(host, Federation(Config()));

        session.ReportExhausted(Exhausted());

        Assert.Equal(RelayRecoveryPhase.Exhausted, session.Snapshot.Phase);
        Assert.Equal("connection refused", Assert.Single(session.Snapshot.TriedRelays).Reason);
    }

    [Fact]
    public async Task Session_Discover_ListsAppHostingRelaysFirstAndNeverAdopts()
    {
        VestaAppConfig config = Config();
        FakeHost host = new(new RelayDirectory(config, new FakeOverrideStore()));
        using RelayRecoverySession session = new(host, Federation(
            config,
            Descriptor("wss://other.example/ws", hostsApp: false),
            Descriptor("wss://hosts.example/ws", hostsApp: true)));

        host.RaiseExhausted(Exhausted());
        await session.DiscoverAsync();

        Assert.Equal(RelayRecoveryPhase.Choices, session.Snapshot.Phase);
        Assert.Equal(new Uri("wss://hosts.example/ws"), session.Snapshot.Choices[0].Url);
        Assert.True(session.Snapshot.Choices[0].HostsRequestedApp);
        Assert.Contains(session.Snapshot.Choices, c => !c.HostsRequestedApp);
        Assert.Empty(host.Adopted);
    }

    [Fact]
    public async Task Session_Discover_NoRelaysReachable_ReportsNothingFound()
    {
        VestaAppConfig config = Config();
        FakeHost host = new(new RelayDirectory(config, new FakeOverrideStore()));
        using RelayRecoverySession session = new(host, Federation(config));

        host.RaiseExhausted(Exhausted());
        await session.DiscoverAsync();

        Assert.Equal(RelayRecoveryPhase.NothingFound, session.Snapshot.Phase);
    }

    [Fact]
    public async Task Session_Discover_UsesPersistedPeerCacheWhenNothingIsLive()
    {
        VestaAppConfig config = Config();
        InMemoryPeerCacheStore cache = new();
        cache.Save([new DiscoveredRelay("k", [new Uri("wss://remembered.example/ws")], HostsRequestedApp: true, DateTimeOffset.UtcNow)]);
        FakeHost host = new(new RelayDirectory(config, new FakeOverrideStore(), peerCache: cache));
        using RelayRecoverySession session = new(host, Federation(config));

        host.RaiseExhausted(Exhausted());
        await session.DiscoverAsync();

        RelayChoice choice = Assert.Single(session.Snapshot.Choices);
        Assert.True(choice.FromCache);
        Assert.Equal(new Uri("wss://remembered.example/ws"), choice.Url);
    }

    [Fact]
    public async Task Session_Adopt_OnSuccess_ReturnsToHealthy()
    {
        VestaAppConfig config = Config();
        FakeHost host = new(new RelayDirectory(config, new FakeOverrideStore()));
        using RelayRecoverySession session = new(host, Federation(config));
        RelayChoice choice = new(new Uri("wss://new.example/ws"), "k", HostsRequestedApp: true, FromCache: false);

        host.RaiseExhausted(Exhausted());
        await session.AdoptAsync(choice);

        Assert.Equal(RelayRecoveryPhase.Healthy, session.Snapshot.Phase);
        Assert.Equal(choice.Url, Assert.Single(host.Adopted));
    }

    [Fact]
    public async Task Session_Adopt_OnFailure_ReportsFailedAndKeepsRetrying()
    {
        VestaAppConfig config = Config();
        FakeHost host = new(new RelayDirectory(config, new FakeOverrideStore()), connectSucceeds: false);
        using RelayRecoverySession session = new(host, Federation(config));

        host.RaiseExhausted(Exhausted());
        await session.UseManualAsync("https://typed.example");

        Assert.Equal(RelayRecoveryPhase.Failed, session.Snapshot.Phase);
        Assert.Equal(new Uri("wss://typed.example/"), Assert.Single(host.Adopted));
        Assert.True(session.Snapshot.BackgroundRetrying);
    }

    [Fact]
    public async Task Session_UseManual_InvalidUrl_FailsWithoutAdopting()
    {
        FakeHost host = new(new RelayDirectory(Config(), new FakeOverrideStore()));
        using RelayRecoverySession session = new(host, Federation(Config()));

        await session.UseManualAsync("ftp://nope.example");

        Assert.Equal(RelayRecoveryPhase.Failed, session.Snapshot.Phase);
        Assert.Empty(host.Adopted);
    }

    [Fact]
    public void Session_Dismiss_HidesPromptButKeepsRetrying()
    {
        FakeHost host = new(new RelayDirectory(Config(), new FakeOverrideStore()));
        using RelayRecoverySession session = new(host, Federation(Config()));

        host.RaiseExhausted(Exhausted());
        session.Dismiss();

        Assert.Equal(RelayRecoveryPhase.Degraded, session.Snapshot.Phase);
        Assert.True(session.Snapshot.BackgroundRetrying);
    }

    [Fact]
    public void Session_BackgroundReconnect_ReturnsToHealthy()
    {
        FakeHost host = new(new RelayDirectory(Config(), new FakeOverrideStore()));
        using RelayRecoverySession session = new(host, Federation(Config()));

        host.RaiseExhausted(Exhausted());
        host.RaiseReconnected();

        Assert.Equal(RelayRecoveryPhase.Healthy, session.Snapshot.Phase);
        Assert.False(session.Snapshot.BackgroundRetrying);
    }

    [Fact]
    public async Task ConsolePicker_UnverifiedChoice_RequiresConfirmation()
    {
        VestaAppConfig config = Config();
        FakeHost host = new(new RelayDirectory(config, new FakeOverrideStore()));
        using RelayRecoverySession session = new(host, Federation(config, Descriptor("wss://other.example/ws", hostsApp: false)));
        host.RaiseExhausted(Exhausted());
        await session.DiscoverAsync();

        StringWriter output = new();
        bool recovered = await ConsoleRelayPicker.RunAsync(session, new StringReader("1\nn\nq\n"), output);

        Assert.False(recovered);
        Assert.Empty(host.Adopted);
        Assert.Contains("not verified", output.ToString());
    }
}
