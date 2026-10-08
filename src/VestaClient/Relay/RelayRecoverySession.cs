
namespace Vesta;

/// <summary>The slice of <see cref="VestaConnection"/> a <see cref="RelayRecoverySession"/> drives.</summary>
public interface IRelayRecoveryHost
{
    RelayDirectory RelayDirectory { get; }

    IReadOnlyList<Uri> Relays { get; }

    bool AutoReconnect { get; }

    event Action<string>? OnDisconnected;

    event Action? OnReconnected;

    event Action<RelaysExhaustedInfo>? OnRelaysExhausted;

    Task<bool> ReconnectAsync(CancellationToken cancellationToken = default);

    Task<RelayAdoptResult> AdoptRelayAsync(RelayOverride relayOverride, CancellationToken cancellationToken = default);

    Task<bool> ClearUserRelayOverrideAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of moving to a relay the user picked.</summary>
/// <param name="Connected">True if the connection is up on the new relay and the relay accepted the app.</param>
/// <param name="FailureReason">Why it was not adopted (unreachable, namespace rejected, registration refused), or null.</param>
public sealed record RelayAdoptResult(bool Connected, string? FailureReason = null);

/// <summary>The per-relay inputs a user can set when moving to a relay.</summary>
/// <param name="AppId">The app namespace to use on the new relay, or null/empty to keep the app's own id.</param>
/// <param name="RegisterApp">True to register the namespace on the relay after connecting.</param>
public sealed record RelayAdoptOptions(string? AppId = null, bool RegisterApp = false);

public enum RelayRecoveryPhase
{
    Healthy,
    Degraded,
    Exhausted,
    Discovering,
    Choices,
    NothingFound,
    Adopting,
    Failed,
}

/// <summary>A relay the user can choose to move to. Always unverified as to whether it holds the app's data.</summary>
/// <param name="Url">The relay's WebSocket URL.</param>
/// <param name="RelayPublicKey">The relay's descriptor key, or null for a manually entered URL.</param>
/// <param name="HostsRequestedApp">True if the relay verifiably advertised this app under the trusted owner.</param>
/// <param name="FromCache">True if only the persisted peer cache knows this relay (not confirmed live this session).</param>
/// <param name="AcceptsUnregisteredApps">True if the relay's signed descriptor says it runs in open mode, null if it didn't say.</param>
public sealed record RelayChoice(Uri Url, string? RelayPublicKey, bool HostsRequestedApp, bool FromCache, bool? AcceptsUnregisteredApps = null);

/// <summary>Everything a view needs to render the recovery prompt. Views hold no other state.</summary>
public sealed record RelayRecoverySnapshot(
    RelayRecoveryPhase Phase,
    IReadOnlyList<RelayAttempt> TriedRelays,
    IReadOnlyList<RelayChoice> Choices,
    RelayOverride? ActiveOverride,
    bool BackgroundRetrying,
    int FailedPasses,
    Uri? Adopting,
    string? FailureReason,
    Uri? FailedRelay = null,
    RelayAdoptOptions? FailedOptions = null);

/// <summary>
/// Headless state machine behind every relay-recovery UI (console, web, and future iOS/Android
/// views). It owns the flow and the trust rules; views only render <see cref="Snapshot"/> and call
/// the input methods. A relay is never adopted without an explicit <see cref="AdoptAsync"/> or
/// <see cref="UseManualAsync"/> call, and background reconnect keeps running while prompting.
///
/// Healthy → Degraded → Exhausted → Discovering → Choices | NothingFound → Adopting → Healthy | Failed
/// </summary>
public sealed class RelayRecoverySession : IDisposable
{
    private const int MaxFederationBases = 16;

    private readonly IRelayRecoveryHost _host;
    private readonly FederationClient _federation;
    private readonly object _gate = new();
    private RelayRecoverySnapshot _snapshot;

    public RelayRecoverySession(IRelayRecoveryHost host, FederationClient? federation = null)
    {
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
        _federation = federation ?? new FederationClient(host.RelayDirectory.Config);
        _snapshot = Build(RelayRecoveryPhase.Healthy);

        _host.OnDisconnected += HandleDisconnected;
        _host.OnReconnected += HandleReconnected;
        _host.OnRelaysExhausted += HandleExhausted;
    }

    public RelayRecoverySnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>Raised after every state change, on whichever thread caused it.</summary>
    public event Action<RelayRecoverySnapshot>? OnChanged;

    public void Dispose()
    {
        _host.OnDisconnected -= HandleDisconnected;
        _host.OnReconnected -= HandleReconnected;
        _host.OnRelaysExhausted -= HandleExhausted;
    }

    /// <summary>Open the prompt for an outage the session didn't witness, e.g. the <see cref="RelaysExhaustedException"/> from a failed first connect.</summary>
    public void ReportExhausted(RelaysExhaustedInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        HandleExhausted(info);
    }

    /// <summary>Try the current candidates again right now.</summary>
    public async Task RetryAsync(CancellationToken cancellationToken = default)
    {
        if (await _host.ReconnectAsync(cancellationToken))
        {
            Update(Build(RelayRecoveryPhase.Healthy));
        }
    }

    /// <summary>Ask reachable relays (cache, seeds, known candidates) which relays host this app.</summary>
    public async Task DiscoverAsync(CancellationToken cancellationToken = default)
    {
        Update(Snapshot with { Phase = RelayRecoveryPhase.Discovering, Choices = [], FailureReason = null });

        IReadOnlyList<RelayChoice> choices = await CollectChoicesAsync(cancellationToken);
        Update(Snapshot with
        {
            Phase = choices.Count > 0 ? RelayRecoveryPhase.Choices : RelayRecoveryPhase.NothingFound,
            Choices = choices,
        });
    }

    /// <summary>Move to a relay the user picked from <see cref="RelayRecoverySnapshot.Choices"/>.</summary>
    public Task AdoptAsync(RelayChoice choice, RelayAdoptOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return AdoptCoreAsync(choice.Url, options, cancellationToken);
    }

    /// <summary>Move to a relay URL the user typed or scanned. Accepts ws/wss (http/https are mapped).</summary>
    public Task UseManualAsync(string url, RelayAdoptOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!TryNormalize(url, out Uri? relay))
        {
            Update(Snapshot with { Phase = RelayRecoveryPhase.Failed, FailureReason = "Not a valid relay URL (expected ws://, wss://, http:// or https://)." });
            return Task.CompletedTask;
        }
        return AdoptCoreAsync(relay!, options, cancellationToken);
    }

    /// <summary>Drop the user's override and go back to manifest/default relays.</summary>
    public async Task ClearOverrideAsync(CancellationToken cancellationToken = default)
    {
        bool connected = await _host.ClearUserRelayOverrideAsync(cancellationToken);
        Update(connected ? Build(RelayRecoveryPhase.Healthy) : Snapshot with { ActiveOverride = null, FailedRelay = null, FailedOptions = null });
    }

    /// <summary>Hide the prompt. Background reconnect continues; the prompt returns on the next exhaustion.</summary>
    public void Dismiss()
    {
        Update(Snapshot with { Phase = RelayRecoveryPhase.Degraded, Choices = [], FailureReason = null, FailedRelay = null, FailedOptions = null });
    }

    private async Task AdoptCoreAsync(Uri relay, RelayAdoptOptions? options, CancellationToken cancellationToken)
    {
        string? requested = string.IsNullOrWhiteSpace(options?.AppId) ? null : options!.AppId!.Trim();
        if (requested is not null && !AppId.IsValid(requested))
        {
            Update(Snapshot with
            {
                Phase = RelayRecoveryPhase.Failed,
                FailedRelay = relay,
                FailedOptions = options,
                FailureReason = "Invalid app namespace: use lowercase letters, digits and single hyphens (max 64 characters).",
            });
            return;
        }

        // Using the app's own id needs no remap.
        string? appId = requested == _host.RelayDirectory.Config.AppId ? null : requested;
        bool register = options?.RegisterApp ?? false;
        RelayAdoptOptions effective = new(requested, register);

        Update(Snapshot with { Phase = RelayRecoveryPhase.Adopting, Adopting = relay, FailureReason = null, FailedRelay = null, FailedOptions = null });

        RelayAdoptResult result;
        try
        {
            result = await _host.AdoptRelayAsync(new RelayOverride(relay, appId, register), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new RelayAdoptResult(false, ex.Message);
        }

        Update(result.Connected
            ? Build(RelayRecoveryPhase.Healthy)
            : Snapshot with
            {
                Phase = RelayRecoveryPhase.Failed,
                Adopting = null,
                ActiveOverride = _host.RelayDirectory.ActiveOverride,
                FailureReason = result.FailureReason ?? $"Could not connect to {relay}.",
                FailedRelay = relay,
                FailedOptions = effective,
            });
    }

    private async Task<IReadOnlyList<RelayChoice>> CollectChoicesAsync(CancellationToken cancellationToken)
    {
        VestaAppConfig config = _host.RelayDirectory.Config;
        IReadOnlyList<DiscoveredRelay> cached = _host.RelayDirectory.PeerCache?.Load() ?? [];

        List<Uri> seeds = [];
        foreach (DiscoveredRelay peer in cached)
        {
            seeds.AddRange(peer.Urls);
        }
        seeds.AddRange(config.DiscoverySeeds ?? []);
        seeds.AddRange(_host.Relays);
        seeds.AddRange(config.DefaultRelays);

        List<Uri> bases = [];
        foreach (Uri seed in seeds)
        {
            if (FederationClient.ToFederationBaseUrl(seed, out Uri? baseUrl) && !bases.Contains(baseUrl!))
            {
                bases.Add(baseUrl!);
            }
        }

        IReadOnlyList<DiscoveredRelay>[] live = await Task.WhenAll(
            bases.Take(MaxFederationBases).Select(b => QueryAsync(b, cancellationToken)));

        Dictionary<Uri, RelayChoice> merged = [];
        foreach (DiscoveredRelay peer in cached)
        {
            foreach (Uri url in peer.Urls)
            {
                merged[url] = new RelayChoice(url, peer.RelayPublicKey, peer.HostsRequestedApp, FromCache: true, peer.AcceptsUnregisteredApps);
            }
        }
        foreach (IReadOnlyList<DiscoveredRelay> relays in live)
        {
            foreach (DiscoveredRelay relay in relays)
            {
                foreach (Uri url in relay.Urls)
                {
                    bool hosts = relay.HostsRequestedApp || (merged.TryGetValue(url, out RelayChoice? prior) && !prior.FromCache && prior.HostsRequestedApp);
                    merged[url] = new RelayChoice(url, relay.RelayPublicKey, hosts, FromCache: false, relay.AcceptsUnregisteredApps);
                }
            }
        }

        // Relays that just failed us are not useful choices.
        HashSet<Uri> failed = [.. Snapshot.TriedRelays.Select(a => a.Relay)];
        return [.. merged.Values
            .Where(c => !failed.Contains(c.Url))
            .OrderByDescending(c => c.HostsRequestedApp)
            .ThenBy(c => c.FromCache)];
    }

    private async Task<IReadOnlyList<DiscoveredRelay>> QueryAsync(Uri baseUrl, CancellationToken cancellationToken)
    {
        IReadOnlyList<DiscoveredRelay> hosting = await _federation.DiscoverRelaysForAppAsync(baseUrl, cancellationToken);
        IReadOnlyList<DiscoveredRelay> all = await _federation.ListAllRelaysAsync(baseUrl, cancellationToken);
        return [.. hosting, .. all];
    }

    private static bool TryNormalize(string? url, out Uri? relay)
    {
        relay = null;
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        string? scheme = parsed.Scheme switch
        {
            "ws" or "wss" => parsed.Scheme,
            "http" => "ws",
            "https" => "wss",
            _ => null,
        };
        if (scheme is null)
        {
            return false;
        }

        UriBuilder builder = new(parsed) { Scheme = scheme, Port = parsed.IsDefaultPort ? -1 : parsed.Port };
        relay = builder.Uri;
        return true;
    }

    private void HandleDisconnected(string reason)
    {
        if (Snapshot.Phase == RelayRecoveryPhase.Healthy)
        {
            Update(Snapshot with { Phase = RelayRecoveryPhase.Degraded });
        }
    }

    private void HandleReconnected() => Update(Build(RelayRecoveryPhase.Healthy));

    private void HandleExhausted(RelaysExhaustedInfo info)
    {
        Update(Snapshot with
        {
            Phase = RelayRecoveryPhase.Exhausted,
            TriedRelays = info.Attempts,
            FailedPasses = info.Passes,
            Choices = [],
            FailureReason = null,
        });
    }

    private RelayRecoverySnapshot Build(RelayRecoveryPhase phase) => new(
        phase,
        TriedRelays: [],
        Choices: [],
        ActiveOverride: _host.RelayDirectory.ActiveOverride,
        BackgroundRetrying: false,
        FailedPasses: 0,
        Adopting: null,
        FailureReason: null,
        FailedRelay: null,
        FailedOptions: null);

    private void Update(RelayRecoverySnapshot next)
    {
        bool retrying = next.Phase is not (RelayRecoveryPhase.Healthy or RelayRecoveryPhase.Adopting) && _host.AutoReconnect;
        RelayRecoverySnapshot final = next with { BackgroundRetrying = retrying };

        lock (_gate)
        {
            _snapshot = final;
        }
        OnChanged?.Invoke(final);
    }
}
