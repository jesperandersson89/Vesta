using System.Net;
using VestaClient.Federation;
using VestaClient.Relay;
using VestaCore.Identity;

namespace VestaClient.Tests;

/// <summary>Tests for the built-in loopback relay-picker page and the namespace remap it drives.</summary>
public sealed class WebRelayPickerTests : IDisposable
{
    private static readonly Uri DeadRelay = new("wss://dead.example/ws");

    private readonly VestaIdentity _owner = VestaIdentity.Generate();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }
        _owner.Dispose();
    }

    private VestaAppConfig Config() => new("chess", _owner.PublicKey, [DeadRelay]);

    private sealed class StubOverrideStore : IRelayOverrideStore
    {
        private RelayOverride? _override;

        public RelayOverride? GetOverride() => _override;

        public void SetOverride(RelayOverride relayOverride) => _override = relayOverride;

        public void ClearOverride() => _override = null;
    }

    // Fails instantly so discovery settles without touching DNS/network (slow on CI runners).
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline");
    }

    private sealed class StubHost(RelayDirectory directory) : IRelayRecoveryHost
    {
        public RelayDirectory RelayDirectory { get; } = directory;

        public IReadOnlyList<Uri> Relays { get; } = [DeadRelay];

        public bool AutoReconnect => true;

        public List<RelayOverride> Adopted { get; } = [];

        public event Action<string>? OnDisconnected
        {
            add { }
            remove { }
        }

        public event Action? OnReconnected
        {
            add { }
            remove { }
        }

        public event Action<RelaysExhaustedInfo>? OnRelaysExhausted
        {
            add { }
            remove { }
        }

        public Task<bool> ReconnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<RelayAdoptResult> AdoptRelayAsync(RelayOverride relayOverride, CancellationToken cancellationToken = default)
        {
            Adopted.Add(relayOverride);
            return Task.FromResult(new RelayAdoptResult(true));
        }

        public Task<bool> ClearUserRelayOverrideAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private (WebRelayPicker Picker, StubHost Host, List<Uri> Launched, HttpClient Http) Start()
    {
        VestaAppConfig config = Config();
        StubHost host = new(new RelayDirectory(config, new StubOverrideStore()));
        List<Uri> launched = [];
        HttpClient offline = new(new OfflineHandler());
        _disposables.Add(offline);
        RelayRecoverySession session = new(host, new FederationClient(config, offline));
        WebRelayPicker picker = new(host, config, url =>
        {
            launched.Add(url);
            return true;
        }, session);
        HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false });
        _disposables.Add(picker);
        _disposables.Add(http);

        Assert.True(picker.Show(new RelaysExhaustedInfo([new RelayAttempt(DeadRelay, "refused <b>")], Passes: 2)));
        return (picker, host, launched, http);
    }

    // Show() kicks off a best-effort discovery; wait until the page leaves the transient phase.
    private static async Task<string> SettledPageAsync(HttpClient http, Uri? url)
    {
        string html = string.Empty;
        for (int i = 0; i < 50; i++)
        {
            html = await http.GetStringAsync(url);
            if (!html.Contains("Looking for relays"))
            {
                break;
            }
            await Task.Delay(100);
        }
        return html;
    }

    [Fact]
    public async Task Show_LaunchesOncePerOutage_AndServesPageWithEscapedContent()
    {
        (WebRelayPicker picker, _, List<Uri> launched, HttpClient http) = Start();

        picker.Show(new RelaysExhaustedInfo([new RelayAttempt(DeadRelay, "again")], Passes: 3));

        Assert.Single(launched);
        Assert.Equal("127.0.0.1", picker.Url!.Host);

        string html = await SettledPageAsync(http, picker.Url);
        Assert.Contains("Can't reach any relay", html);
        Assert.Contains("refused &lt;b&gt;", html);
        Assert.DoesNotContain("<b>", html);
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public async Task Request_WithWrongToken_IsNotFound()
    {
        (WebRelayPicker picker, _, _, HttpClient http) = Start();
        Uri wrong = new($"http://127.0.0.1:{picker.Url!.Port}/not-the-token/");

        // The listener only owns the tokenised prefix, so anything else is refused by the OS layer.
        HttpResponseMessage response = await http.GetAsync(wrong);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Request_WithForeignHostHeader_IsRejected()
    {
        (WebRelayPicker picker, _, _, HttpClient http) = Start();
        using HttpRequestMessage request = new(HttpMethod.Get, picker.Url);
        request.Headers.Host = "evil.example";

        HttpResponseMessage response = await http.SendAsync(request);

        // Windows (http.sys) reaches our handler and answers 400; Linux's managed listener matches
        // prefixes on Host and answers 404. Either way the page is not served.
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"Unexpected {response.StatusCode}");
    }

    [Fact]
    public async Task PostAdopt_WithManualUrlAndNamespace_AdoptsWithOptions()
    {
        (WebRelayPicker picker, StubHost host, _, HttpClient http) = Start();
        FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["relay"] = "manual",
            ["manual"] = "https://relay.example/",
            ["appId"] = "chess-copy",
            ["register"] = "1",
        });

        HttpResponseMessage response = await http.PostAsync(new Uri(picker.Url!, "adopt"), form);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        RelayOverride adopted = Assert.Single(host.Adopted);
        Assert.Equal(new Uri("wss://relay.example/"), adopted.Relay);
        Assert.Equal("chess-copy", adopted.AppId);
        Assert.True(adopted.RegisterApp);
    }

    [Fact]
    public async Task PostAdopt_WithInvalidNamespace_ReopensPageWithErrorAndInputs()
    {
        (WebRelayPicker picker, StubHost host, _, HttpClient http) = Start();
        FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["relay"] = "manual",
            ["manual"] = "wss://relay.example/ws",
            ["appId"] = "Bad Name!",
        });

        await http.PostAsync(new Uri(picker.Url!, "adopt"), form);
        string html = await http.GetStringAsync(picker.Url);

        Assert.Empty(host.Adopted);
        Assert.Contains("wss://relay.example/ws", html);
        Assert.Contains("Bad Name!", html);
        Assert.Contains("alert err", html);
    }

    [Fact]
    public async Task PostDismiss_MovesOutOfExhaustedPrompt()
    {
        (WebRelayPicker picker, _, _, HttpClient http) = Start();

        await http.PostAsync(new Uri(picker.Url!, "dismiss"), new FormUrlEncodedContent([]));
        string html = await http.GetStringAsync(picker.Url);

        Assert.Contains("Connection lost", html);
    }

    [Fact]
    public async Task Post_WithNonFormContentType_IsRejected()
    {
        (WebRelayPicker picker, StubHost host, _, HttpClient http) = Start();
        StringContent body = new("{\"relay\":\"x\"}", System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response = await http.PostAsync(new Uri(picker.Url!, "adopt"), body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Adopted);
    }

    [Fact]
    public void Render_HealthyWithOverride_OffersSwitchBack()
    {
        RelayRecoverySnapshot snapshot = new(
            RelayRecoveryPhase.Healthy, [], [], new RelayOverride(new Uri("wss://r.example/ws"), "chess-x"),
            BackgroundRetrying: false, FailedPasses: 0, Adopting: null, FailureReason: null, FailedRelay: null, FailedOptions: null);

        string html = RelayPickerPage.Render(snapshot, Config(), "/tok", new Uri("wss://r.example/ws"));

        Assert.Contains("Connected", html);
        Assert.Contains("/tok/clear", html);
    }

    [Fact]
    public void Render_Choices_ShowsBadgesAndWarnsWhenAppNotHosted()
    {
        RelayChoice hosted = new(new Uri("wss://a.example/ws"), "k", HostsRequestedApp: true, FromCache: false, AcceptsUnregisteredApps: true);
        RelayChoice other = new(new Uri("wss://b.example/ws"), "k", HostsRequestedApp: false, FromCache: true, AcceptsUnregisteredApps: false);
        RelayRecoverySnapshot snapshot = new(
            RelayRecoveryPhase.Choices, [], [hosted, other], null,
            BackgroundRetrying: true, FailedPasses: 1, Adopting: null, FailureReason: null, FailedRelay: null, FailedOptions: null);

        string html = RelayPickerPage.Render(snapshot, Config(), "/tok", null);

        Assert.Contains("hosts this app", html);
        Assert.Contains("Open relay", html);
        Assert.Contains("does not advertise this app", html);
        Assert.Contains("registered apps only", html);
        Assert.Contains("remembered, not confirmed", html);
    }

    [Theory]
    [InlineData("chess/todo", "chess", "chess-x", "chess-x/todo")]
    [InlineData("chess/vesta/relays", "chess", "chess-x", "chess-x/vesta/relays")]
    [InlineData("vesta/relays", "chess", "chess-x", "vesta/relays")]
    [InlineData("other/todo", "chess", "chess-x", "other/todo")]
    [InlineData("chess/todo", "chess", null, "chess/todo")]
    public void RemapChannel_RewritesOnlyTheAppNamespace(string channel, string from, string? to, string expected)
    {
        Assert.Equal(expected, VestaConnection.RemapChannel(channel, from, to));
    }
}
