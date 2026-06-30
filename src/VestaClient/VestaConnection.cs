using System.Net.WebSockets;
using System.Text.Json;
using VestaClient.Relay;
using VestaClient.Storage;
using VestaCore.Events;
using VestaCore.Identity;
using VestaCore.Protocol;
using VestaCore.Relay;
using VestaCore.Serialization;
using VestaCore.Utilities;

namespace VestaClient;

/// <summary>
/// C# client for connecting to a Vesta server via WebSocket.
/// Handles the protocol handshake, publishing events, subscribing to channels,
/// and receiving real-time event broadcasts.
/// Supports reconnection — call ReconnectAsync() to establish a new connection
/// after a disconnect, or enable AutoReconnect for automatic reconnection with
/// exponential backoff.
/// </summary>
public sealed class VestaConnection : IAsyncDisposable
{
    private ClientWebSocket? _socket;
    private readonly JsonSerializerOptions _jsonOptions = VestaJsonOptions.Default;
    private readonly string _clientId;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly IClientEventStore? _localStore;
    private readonly VestaIdentity? _identity;
    private readonly VestaAppConfig _appConfig;
    private readonly Dictionary<Guid, VestaEvent> _pendingPublishes = new();
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoop;
    private IReadOnlyList<Uri> _relayCandidates = [];
    private int _activeRelayIndex;
    private RelayDirectory? _relayDirectory;
    private IReadOnlyList<string>? _channels;

    /// <summary>
    /// When true, the connection will automatically attempt to reconnect with
    /// exponential backoff when the connection is lost. Default: false.
    /// </summary>
    public bool AutoReconnect { get; set; }

    /// <summary>
    /// Maximum delay between auto-reconnect attempts. Default: 30 seconds.
    /// </summary>
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initial delay for the first auto-reconnect attempt. Default: 1 second.
    /// Each subsequent attempt doubles the delay up to MaxReconnectDelay.
    /// </summary>
    public TimeSpan InitialReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Fired when a real-time event is received from the server.
    /// </summary>
    public event Action<EventMessage>? OnEvent;

    /// <summary>
    /// Fired when a batch of events is received (catch-up or fetch response).
    /// </summary>
    public event Action<EventsBatchMessage>? OnEventsBatch;

    /// <summary>
    /// Fired when a publish is acknowledged by the server.
    /// </summary>
    public event Action<AckMessage>? OnAck;

    /// <summary>
    /// Fired when the server sends an error.
    /// </summary>
    public event Action<ErrorMessage>? OnError;

    /// <summary>
    /// Fired when the relay limits or refuses the app — quota exhausted, publish rate exceeded,
    /// app not registered, or access denied. This is the client's semantic interpretation of the
    /// underlying <see cref="ErrorMessage"/>, so apps can react (back off, prompt an upgrade) without
    /// string-matching error codes. <see cref="OnError"/> still fires for every error, limit or not.
    /// </summary>
    public event Action<VestaLimitNotice>? OnLimited;

    /// <summary>
    /// Fired when the connection is closed.
    /// </summary>
    public event Action<string>? OnDisconnected;

    /// <summary>
    /// Fired when a reconnection succeeds.
    /// </summary>
    public event Action? OnReconnected;

    /// <summary>
    /// Fired when a newer, validly-signed relay manifest is accepted. Relay self-reconfiguration is
    /// on by default — every connection carries a <see cref="RelayDirectory"/> — so this fires whenever
    /// the app owner steers the swarm to new relays. The candidate list has already been refreshed
    /// when this fires.
    /// </summary>
    public event Action<RelayManifest>? OnManifestApplied;

    /// <summary>
    /// Fired when the active relay changes — either on first connect or when a
    /// failover/migration moves the connection to a different relay in the
    /// candidate list. The argument is the newly-active relay URI.
    /// </summary>
    public event Action<Uri>? OnRelaySwitched;

    /// <summary>
    /// The server ID returned in the WELCOME message.
    /// </summary>
    public string? ServerId { get; private set; }

    /// <summary>
    /// The channels confirmed by the server in the WELCOME message.
    /// </summary>
    public IReadOnlyList<string> Channels { get; private set; } = [];

    /// <summary>
    /// The relay the connection is currently using (or last attempted). Null
    /// before the first <see cref="ConnectAsync(Uri, IReadOnlyList{string}, IReadOnlyDictionary{string, long}, CancellationToken)"/> call.
    /// </summary>
    public Uri? ActiveRelay { get; private set; }

    /// <summary>
    /// The ordered list of candidate relays the connection will try, in priority
    /// order. Populated by <c>ConnectAsync</c>; on failover the connection walks
    /// this list starting from the active relay.
    /// </summary>
    public IReadOnlyList<Uri> Relays => _relayCandidates;

    /// <summary>
    /// The app configuration (relay-independence trust anchor + default relays) this
    /// connection was created with.
    /// </summary>
    public VestaAppConfig AppConfig => _appConfig;

    /// <summary>
    /// The relay directory powering self-reconfiguration: default-relay bootstrap, owner-signed
    /// manifest adoption (anti-rollback), and the user's local relay override. Always present —
    /// relay independence is on by default.
    /// </summary>
    public RelayDirectory RelayDirectory => _relayDirectory!;

    /// <summary>
    /// Whether the connection is currently open.
    /// </summary>
    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary>
    /// Create a connection for an app. The <paramref name="appConfig"/> is required: it carries the
    /// relay-independence trust anchor (the app owner's public key) and the compiled-in default
    /// relays, so relay self-reconfiguration — owner-signed manifest discovery, anti-rollback, and
    /// multi-relay failover — is ON by default and the app never silently becomes abandonware.
    /// A file-backed <see cref="RelayDirectory"/> (cached under <c>~/.vesta/relays/</c>) is created
    /// automatically; pass a custom <paramref name="relayDirectory"/> to change persistence or to
    /// supply in-memory stores.
    /// </summary>
    public VestaConnection(
        string clientId,
        VestaAppConfig appConfig,
        IClientEventStore? localStore = null,
        VestaIdentity? identity = null,
        RelayDirectory? relayDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(appConfig);

        _clientId = clientId;
        _appConfig = appConfig;
        _localStore = localStore;
        _identity = identity;

        AttachRelayDirectory(relayDirectory ?? RelayDirectory.CreateDefault(appConfig));
    }

    /// <summary>
    /// Connect using the relays resolved from the app config — the user override, then the latest
    /// owner-signed manifest, then the compiled-in defaults, in that precedence. This is the default
    /// connect path: relay selection, manifest adoption, and failover are handled for you. Use the
    /// <see cref="ConnectAsync(IReadOnlyList{Uri}, IReadOnlyList{string}, IReadOnlyDictionary{string, long}, CancellationToken)"/>
    /// overload only to force an explicit relay list (e.g. in tests).
    /// </summary>
    public Task ConnectAsync(
        IReadOnlyList<string> channels,
        IReadOnlyDictionary<string, long>? lastSequences = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Uri> candidates = _relayDirectory!.ResolveCandidates();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "No relays available — the app config declared no default relays and no manifest or override is set.");
        }
        return ConnectAsync(candidates, channels, lastSequences, cancellationToken);
    }

    /// <summary>
    /// Connect to a Vesta server and perform the HELLO/WELCOME handshake.
    /// </summary>
    public Task ConnectAsync(
        Uri serverUri,
        IReadOnlyList<string> channels,
        IReadOnlyDictionary<string, long>? lastSequences = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverUri);
        return ConnectAsync([serverUri], channels, lastSequences, cancellationToken);
    }

    /// <summary>
    /// Connect to the first reachable relay in an ordered candidate list, performing
    /// the HELLO/WELCOME handshake. Candidates are tried in order; if the active
    /// relay later drops, reconnection walks the same list (failover). The list is
    /// the resolved priority order — e.g. user override, then signed-manifest relays,
    /// then app defaults.
    /// </summary>
    public async Task ConnectAsync(
        IReadOnlyList<Uri> relays,
        IReadOnlyList<string> channels,
        IReadOnlyDictionary<string, long>? lastSequences = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relays);
        if (relays.Count == 0)
        {
            throw new ArgumentException("At least one relay URI is required.", nameof(relays));
        }

        // Store for reconnection / failover
        _relayCandidates = relays;
        _channels = channels;
        _activeRelayIndex = 0;

        if (!await TryConnectCandidatesAsync(lastSequences, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Could not connect to any of the {relays.Count} configured relay(s).");
        }
    }

    /// <summary>
    /// Reconnect using the relay candidate list and channels from the original
    /// ConnectAsync call. Walks the candidate list starting from the active relay,
    /// so a dead primary fails over to the next relay. Uses stored channel positions
    /// from the local store for smart catch-up. Returns true if reconnection succeeded.
    /// </summary>
    public async Task<bool> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_relayCandidates.Count == 0 || _channels is null)
        {
            throw new InvalidOperationException("Cannot reconnect — ConnectAsync has not been called yet.");
        }

        // Clean up old socket and receive loop
        await CleanupAsync();

        bool success = await TryConnectCandidatesAsync(lastSequences: null, cancellationToken);
        if (success)
        {
            OnReconnected?.Invoke();
        }
        return success;
    }

    /// <summary>
    /// Attach a <see cref="RelayDirectory"/> so the connection automatically discovers, verifies,
    /// and adopts owner-signed relay manifests. Call this BEFORE <c>ConnectAsync</c>: on connect the
    /// connection subscribes to the manifest channel, and accepted manifests refresh the candidate
    /// list (firing <see cref="OnManifestApplied"/>). The new relays take effect on the next failover,
    /// or call <see cref="ReconnectAsync"/> / <see cref="SwitchRelayAsync"/> to switch immediately.
    /// </summary>
    public void AttachRelayDirectory(RelayDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (_relayDirectory is not null)
        {
            OnEvent -= HandleManifestEvent;
            OnEventsBatch -= HandleManifestBatch;
        }

        _relayDirectory = directory;
        OnEvent += HandleManifestEvent;
        OnEventsBatch += HandleManifestBatch;
    }

    private void HandleManifestEvent(EventMessage message)
    {
        if (_relayDirectory is null || message.Event.ChannelId != _relayDirectory.ManifestChannel)
        {
            return;
        }
        ApplyManifestFromEvent(message.Event);
    }

    private void HandleManifestBatch(EventsBatchMessage batch)
    {
        if (_relayDirectory is null || batch.ChannelId != _relayDirectory.ManifestChannel)
        {
            return;
        }
        foreach (SequencedEvent sequenced in batch.Events)
        {
            ApplyManifestFromEvent(sequenced.Event);
        }
    }

    private void ApplyManifestFromEvent(VestaEvent evt)
    {
        if (evt.EventType != RelayManifest.EventType)
        {
            return;
        }

        RelayManifest? manifest;
        try
        {
            manifest = evt.Payload.Deserialize<RelayManifest>(_jsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (manifest is null || !_relayDirectory!.TryApplyManifest(manifest))
        {
            return;
        }

        UpdateRelayCandidates(_relayDirectory.ResolveCandidates());
        OnManifestApplied?.Invoke(manifest);
    }

    /// <summary>
    /// Replace the relay candidate list — e.g. after a newer owner-signed manifest is applied.
    /// Keeps the currently-active relay as the starting point if it is still present in the new
    /// list; otherwise resets to the top. Does not reconnect: failover adopts the new list on the
    /// next drop, or call <see cref="ReconnectAsync"/> to switch immediately.
    /// </summary>
    public void UpdateRelayCandidates(IReadOnlyList<Uri> relays)
    {
        ArgumentNullException.ThrowIfNull(relays);
        if (relays.Count == 0)
        {
            throw new ArgumentException("At least one relay URI is required.", nameof(relays));
        }

        _relayCandidates = relays;

        int activeIndex = -1;
        if (ActiveRelay is not null)
        {
            for (int i = 0; i < relays.Count; i++)
            {
                if (relays[i] == ActiveRelay)
                {
                    activeIndex = i;
                    break;
                }
            }
        }
        _activeRelayIndex = activeIndex >= 0 ? activeIndex : 0;
    }

    /// <summary>
    /// Switch to a specific relay and reconnect immediately. The relay must be one of the
    /// current <see cref="Relays"/> candidates. Returns true if the switch connected.
    /// </summary>
    public async Task<bool> SwitchRelayAsync(Uri relay, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relay);

        int index = -1;
        for (int i = 0; i < _relayCandidates.Count; i++)
        {
            if (_relayCandidates[i] == relay)
            {
                index = i;
                break;
            }
        }
        if (index < 0)
        {
            throw new ArgumentException($"Relay '{relay}' is not in the current candidate list.", nameof(relay));
        }

        _activeRelayIndex = index;
        return await ReconnectAsync(cancellationToken);
    }

    /// <summary>
    /// Set the user's local relay override — the individual escape hatch that always wins locally —
    /// persist it via the relay directory, and switch to it immediately. This is how an end-user
    /// keeps an app alive on a relay of their choosing regardless of what the app ships with.
    /// Returns true if the switch connected.
    /// </summary>
    public async Task<bool> SetUserRelayOverrideAsync(Uri relay, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relay);

        _relayDirectory!.SetUserOverride(relay);
        UpdateRelayCandidates(_relayDirectory.ResolveCandidates());
        return await SwitchRelayAsync(relay, cancellationToken);
    }

    /// <summary>
    /// Clear the user's local relay override, reverting to owner-manifest / default resolution, and
    /// reconnect using the freshly resolved candidate list. Returns true if reconnection succeeded.
    /// </summary>
    public async Task<bool> ClearUserRelayOverrideAsync(CancellationToken cancellationToken = default)
    {
        _relayDirectory!.ClearUserOverride();
        UpdateRelayCandidates(_relayDirectory.ResolveCandidates());
        return await ReconnectAsync(cancellationToken);
    }

    /// <summary>
    /// Walk the candidate list once, starting at the active relay, attempting each
    /// until one connects. On success, updates <see cref="ActiveRelay"/> and fires
    /// <see cref="OnRelaySwitched"/> if the relay changed. Returns false if every
    /// candidate failed.
    /// </summary>
    private async Task<bool> TryConnectCandidatesAsync(
        IReadOnlyDictionary<string, long>? lastSequences,
        CancellationToken cancellationToken)
    {
        int count = _relayCandidates.Count;
        for (int offset = 0; offset < count; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int index = (_activeRelayIndex + offset) % count;
            Uri candidate = _relayCandidates[index];
            try
            {
                await ConnectInternalAsync(candidate, lastSequences, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // This candidate is unreachable — clean up and try the next one.
                await CleanupAsync();
                continue;
            }

            bool switched = ActiveRelay != candidate;
            _activeRelayIndex = index;
            ActiveRelay = candidate;
            if (switched)
            {
                OnRelaySwitched?.Invoke(candidate);
            }
            return true;
        }

        return false;
    }

    private async Task ConnectInternalAsync(
        Uri relay,
        IReadOnlyDictionary<string, long>? lastSequences,
        CancellationToken cancellationToken)
    {
        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(relay, cancellationToken);

        // Build lastSequences for catch-up: include ALL subscribed channels.
        // Channels with no local position get 0 (meaning "send me everything").
        Dictionary<string, long> sequences = new();
        if (lastSequences is not null)
        {
            foreach (KeyValuePair<string, long> kvp in lastSequences)
            {
                sequences[kvp.Key] = kvp.Value;
            }
        }
        else if (_localStore is not null)
        {
            IReadOnlyDictionary<string, long> positions = await _localStore.GetChannelPositionsAsync(cancellationToken);
            foreach (KeyValuePair<string, long> kvp in positions)
            {
                sequences[kvp.Key] = kvp.Value;
            }
        }

        // Ensure every subscribed channel has an entry (default to 0 = full catch-up)
        foreach (string ch in _channels!)
        {
            sequences.TryAdd(ch, 0);
        }

        // Send HELLO
        string? publicKeyBase64Url = _identity is not null
            ? Base64Url.Encode(_identity.PublicKey)
            : null;

        HelloMessage hello = new(
            ClientId: _clientId,
            Channels: _channels!.ToList(),
            LastSequences: sequences,
            PublicKey: publicKeyBase64Url);

        await SendAsync(hello, cancellationToken);

        // Receive WELCOME (and possibly error messages before it)
        while (true)
        {
            ProtocolMessage? response = await ReceiveOneAsync(cancellationToken);

            if (response is WelcomeMessage welcome)
            {
                ServerId = welcome.ServerId;
                Channels = welcome.Channels;
                break;
            }
            else if (response is ErrorMessage error)
            {
                HandleErrorMessage(error);
            }
            else if (response is null)
            {
                throw new InvalidOperationException("Connection closed during handshake");
            }
        }

        // Start the background receive loop
        _receiveCts = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));

        // If a relay directory is attached, make sure we are subscribed to the manifest channel
        // so we discover owner-signed relay changes even if the app did not list it explicitly.
        if (_relayDirectory is not null && !_channels!.Contains(_relayDirectory.ManifestChannel))
        {
            await SubscribeAsync(_relayDirectory.ManifestChannel, fromSequence: 0, cancellationToken);
        }

        // Flush any pending outbox events
        if (_localStore is not null)
        {
            await FlushOutboxAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Publish an event to a channel.
    /// If connected, sends immediately. If disconnected and a local store is configured,
    /// enqueues to the outbox for sync on reconnect.
    /// Events are automatically signed if an identity is configured.
    /// </summary>
    public async Task PublishAsync(VestaEvent evt, CancellationToken cancellationToken = default)
    {
        // Auto-sign if identity is present and event is not already signed
        VestaEvent eventToPublish = (_identity is not null && evt.Signature is null)
            ? EventSigner.SignEvent(evt, _identity)
            : evt;

        if (IsConnected)
        {
            _pendingPublishes[eventToPublish.Id] = eventToPublish;
            PublishMessage message = new(eventToPublish.ChannelId, eventToPublish);
            await SendAsync(message, cancellationToken);
        }
        else if (_localStore is not null)
        {
            await _localStore.EnqueueOutboxAsync(eventToPublish, cancellationToken);
        }
        else
        {
            throw new InvalidOperationException("Not connected and no local store configured for offline publishing");
        }
    }

    /// <summary>
    /// Subscribe to a channel, optionally requesting catch-up from a sequence.
    /// </summary>
    public async Task SubscribeAsync(string channelId, long? fromSequence = null, CancellationToken cancellationToken = default)
    {
        SubscribeMessage message = new(channelId, fromSequence);
        await SendAsync(message, cancellationToken);
    }

    /// <summary>
    /// Unsubscribe from a channel.
    /// </summary>
    public async Task UnsubscribeAsync(string channelId, CancellationToken cancellationToken = default)
    {
        UnsubscribeMessage message = new(channelId);
        await SendAsync(message, cancellationToken);
    }

    /// <summary>
    /// Fetch historical events from a channel.
    /// </summary>
    public async Task FetchAsync(string channelId, long fromSequence, long? toSequence = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        FetchMessage message = new(channelId, fromSequence, toSequence, limit);
        await SendAsync(message, cancellationToken);
    }

    /// <summary>
    /// Create a channel with explicit visibility and initial members.
    /// For private channels, only the caller (admin) and listed members can publish/subscribe.
    /// The caller is auto-subscribed.
    /// </summary>
    public async Task CreateChannelAsync(
        string channelId,
        string visibility = "private",
        IReadOnlyList<string>? initialMembers = null,
        CancellationToken cancellationToken = default)
    {
        CreateChannelMessage message = new(channelId, visibility, initialMembers ?? []);
        await SendAsync(message, cancellationToken);
    }

    /// <summary>
    /// Grant a client access to a private channel. Caller must be the channel admin.
    /// </summary>
    public async Task GrantAccessAsync(
        string channelId,
        string clientId,
        string role = "member",
        CancellationToken cancellationToken = default)
    {
        GrantAccessMessage message = new(channelId, clientId, role);
        await SendAsync(message, cancellationToken);
    }

    /// <summary>
    /// Register an app namespace. The first slug segment of every channel ID belongs to an app.
    /// When the server is configured with <c>Vesta:RequireAppRegistration=true</c>, the app must
    /// be registered before publishing or subscribing on any channel in its namespace.
    /// The connecting client becomes the owner.
    /// </summary>
    public async Task RegisterAppAsync(string appId, CancellationToken cancellationToken = default)
    {
        RegisterAppMessage message = new(appId);
        await SendAsync(message, cancellationToken);
    }

    /// <summary>
    /// Soft-delete a channel. Requires the connection to have been promoted to
    /// server admin during HELLO (its public key must be in the server's
    /// <c>Admin:BootstrapPublicKeys</c> allow-list). Subsequent operations on
    /// the channel are rejected with <c>CHANNEL_DELETED</c>. Existing events
    /// remain in storage until a future hard-delete sweep.
    /// </summary>
    public async Task DeleteChannelAsync(string channelId, CancellationToken cancellationToken = default)
    {
        DeleteChannelMessage message = new(channelId);
        await SendAsync(message, cancellationToken);
    }

    /// <summary>
    /// Create a new device group with this connection's identity as the founder, and
    /// publish a <c>vesta.identity.announce</c> event on the group's identity channel.
    /// Returns the freshly generated <c>groupId</c>.
    /// <para>
    /// Requires the connection to have been opened with a <see cref="VestaIdentity"/>.
    /// The founder is self-trusted; subsequent devices must be vouched for via
    /// <see cref="LinkDeviceAsync"/>.
    /// </para>
    /// </summary>
    public async Task<string> CreateDeviceGroupAsync(
        string? deviceName = null,
        CancellationToken cancellationToken = default)
    {
        VestaIdentity identity = RequireIdentity(nameof(CreateDeviceGroupAsync));
        string groupId = IdentityLinkBuilder.GenerateGroupId();
        VestaEvent announce = IdentityLinkBuilder.BuildAnnounce(identity, groupId, deviceName);
        await PublishAsync(announce, cancellationToken);
        return groupId;
    }

    /// <summary>
    /// Vouch for another device as a member of the given group. Publishes a
    /// <c>vesta.identity.link</c> event signed by this connection's identity.
    /// <para>
    /// For other clients to honor the link, this connection's identity must already
    /// be a trusted member of the group (typically because it created the group or
    /// was itself linked by an existing member).
    /// </para>
    /// </summary>
    /// <param name="groupId">The group ID returned by <see cref="CreateDeviceGroupAsync"/>.</param>
    /// <param name="targetPublicKey">The 32-byte Ed25519 public key of the device being linked.</param>
    /// <param name="reason">Optional human-readable hint (e.g. <c>"device-pairing"</c>).</param>
    public async Task LinkDeviceAsync(
        string groupId,
        byte[] targetPublicKey,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        VestaIdentity identity = RequireIdentity(nameof(LinkDeviceAsync));
        VestaEvent link = IdentityLinkBuilder.BuildLink(identity, groupId, targetPublicKey, reason);
        await PublishAsync(link, cancellationToken);
    }

    /// <summary>
    /// Announce this connection's identity as joining an existing group. Publishes a
    /// <c>vesta.identity.announce</c> event signed by this connection's identity.
    /// <para>
    /// The announce alone is not enough to make this device a trusted member — an
    /// existing trusted member must also publish a <see cref="LinkDeviceAsync"/>
    /// event vouching for this device's public key.
    /// </para>
    /// </summary>
    public async Task JoinDeviceGroupAsync(
        string groupId,
        string? deviceName = null,
        CancellationToken cancellationToken = default)
    {
        VestaIdentity identity = RequireIdentity(nameof(JoinDeviceGroupAsync));
        VestaEvent announce = IdentityLinkBuilder.BuildAnnounce(identity, groupId, deviceName);
        await PublishAsync(announce, cancellationToken);
    }

    /// <summary>
    /// Subscribe to the given group's identity channel, fetch the full history, replay it
    /// into a <see cref="DeviceGroupProjection"/>, and return the materialized membership.
    /// <para>
    /// This is a one-shot convenience method intended for occasional inspection (e.g. "show
    /// me my linked devices"). For continuous tracking, subscribe to the channel directly
    /// and feed events into your own <see cref="DeviceGroupProjection"/>.
    /// </para>
    /// </summary>
    /// <param name="groupId">The group ID to query.</param>
    /// <param name="timeout">How long to wait for the catch-up batch. Defaults to 5 seconds.</param>
    public async Task<DeviceGroup> GetDeviceGroupMembersAsync(
        string groupId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        string channelId = DeviceGroupChannel.For(groupId);
        DeviceGroupProjection projection = new(groupId);

        TaskCompletionSource<bool> batchReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnBatch(EventsBatchMessage batch)
        {
            if (batch.ChannelId != channelId)
                return;
            projection.Apply(batch.Events);
            batchReceived.TrySetResult(true);
        }

        void OnEvt(EventMessage evt)
        {
            if (evt.Event.ChannelId != channelId)
                return;
            projection.Apply(new SequencedEvent(evt.Event, evt.Sequence, evt.ReceivedAt));
        }

        OnEventsBatch += OnBatch;
        OnEvent += OnEvt;
        try
        {
            await SubscribeAsync(channelId, fromSequence: 0, cancellationToken);

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
            try
            {
                await batchReceived.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // No batch arrived within the timeout — the channel may be empty.
                // Return whatever the projection has (likely an empty group).
            }
        }
        finally
        {
            OnEventsBatch -= OnBatch;
            OnEvent -= OnEvt;
        }

        return projection.State;
    }

    private VestaIdentity RequireIdentity(string methodName)
    {
        if (_identity is null)
        {
            throw new InvalidOperationException(
                $"{methodName} requires the VestaConnection to be constructed with a VestaIdentity. " +
                "Device-group operations need to sign events with the connection's identity.");
        }
        return _identity;
    }

    /// <summary>
    /// Gracefully disconnect from the server.
    /// Sends a close frame and waits for the receive loop to finish naturally.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_socket?.State == WebSocketState.Open)
        {
            // Send close frame without waiting for the response here —
            // the receive loop will see the server's close response and exit.
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", cancellationToken);
        }

        // Wait for the receive loop to finish (it will exit when it sees the close frame)
        if (_receiveLoop is not null)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await _receiveLoop.WaitAsync(timeout.Token); }
            catch (OperationCanceledException) { /* timed out waiting — force it */ }
        }

        _receiveCts?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync();
        _sendLock.Dispose();
    }

    private async Task CleanupAsync()
    {
        if (_socket is not null)
        {
            if (_socket.State == WebSocketState.Open)
            {
                try
                {
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None);
                }
                catch { /* best effort */ }
            }

            // Give the receive loop a moment to exit cleanly
            if (_receiveLoop is not null)
            {
                try { await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { /* best effort */ }
            }

            _receiveCts?.Cancel();
            _receiveCts?.Dispose();
            _receiveCts = null;
            _receiveLoop = null;
            _socket.Dispose();
            _socket = null;
        }
    }

    private async Task SendAsync(ProtocolMessage message, CancellationToken cancellationToken)
    {
        if (_socket is null)
            throw new InvalidOperationException("Not connected");

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes<ProtocolMessage>(message, _jsonOptions);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<ProtocolMessage?> ReceiveOneAsync(CancellationToken cancellationToken)
    {
        if (_socket is null)
            return null;

        byte[] buffer = new byte[16384];
        using MemoryStream stream = new();

        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        stream.Position = 0;
        return await JsonSerializer.DeserializeAsync<ProtocolMessage>(stream, _jsonOptions, cancellationToken);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket?.State == WebSocketState.Open)
            {
                ProtocolMessage? message = await ReceiveOneAsync(cancellationToken);

                if (message is null)
                    break;

                DispatchMessage(message);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (WebSocketException) { /* connection lost */ }
        finally
        {
            OnDisconnected?.Invoke("Connection closed");

            if (AutoReconnect && !cancellationToken.IsCancellationRequested)
            {
                _ = AutoReconnectLoopAsync();
            }
        }
    }

    private async Task AutoReconnectLoopAsync()
    {
        TimeSpan delay = InitialReconnectDelay;

        while (AutoReconnect)
        {
            await Task.Delay(delay);

            bool success = await ReconnectAsync();
            if (success)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxReconnectDelay.Ticks));
        }
    }

    private void DispatchMessage(ProtocolMessage message)
    {
        switch (message)
        {
            case EventMessage evt:
                CacheEventLocally(evt);
                OnEvent?.Invoke(evt);
                break;
            case EventsBatchMessage batch:
                CacheBatchLocally(batch);
                OnEventsBatch?.Invoke(batch);
                break;
            case AckMessage ack:
                CacheEventOnAck(ack);
                OnAck?.Invoke(ack);
                break;
            case ErrorMessage error:
                HandleErrorMessage(error);
                break;
        }
    }

    private void HandleErrorMessage(ErrorMessage error)
    {
        // Always surface the raw error.
        OnError?.Invoke(error);

        VestaErrorCodes.Classification classification = VestaErrorCodes.Classify(error.Code);

        // Dead-letter a doomed publish so the offline outbox never re-sends it on reconnect.
        // Transient limits (e.g. RATE_LIMITED) are left in place to retry later.
        if (error.EventId is Guid eventId && classification.IsEventFatal)
        {
            _pendingPublishes.Remove(eventId);
            if (_localStore is not null)
                _ = _localStore.MarkOutboxRejectedAsync(eventId, error.Code);
        }

        // Surface a typed limit signal so apps can react without string-matching codes.
        if (classification.IsLimit)
        {
            OnLimited?.Invoke(new VestaLimitNotice(
                error.Code,
                error.Message,
                error.ChannelId,
                error.EventId,
                classification.IsTransient));
        }
    }

    private void CacheEventLocally(EventMessage eventMessage)
    {
        if (_localStore is null) return;
        SequencedEvent sequenced = new(eventMessage.Event, eventMessage.Sequence, DateTimeOffset.UtcNow);
        _ = _localStore.StoreEventAsync(sequenced);
    }

    private void CacheBatchLocally(EventsBatchMessage batch)
    {
        if (_localStore is null) return;
        _ = _localStore.StoreEventsAsync(batch.Events);
    }

    private void CacheEventOnAck(AckMessage ack)
    {
        if (_localStore is null) return;

        // If we have the published event in flight, cache it locally with the server-assigned sequence
        if (_pendingPublishes.Remove(ack.EventId, out VestaEvent? publishedEvent))
        {
            SequencedEvent sequenced = new(publishedEvent, ack.Sequence, DateTimeOffset.UtcNow);
            _ = _localStore.StoreEventAsync(sequenced);
        }

        // Also confirm outbox entry if it came from there
        _ = _localStore.MarkOutboxConfirmedAsync(ack.EventId);
    }

    private async Task FlushOutboxAsync(CancellationToken cancellationToken)
    {
        if (_localStore is null) return;

        IReadOnlyList<OutboxEntry> pending = await _localStore.GetPendingOutboxAsync(cancellationToken);
        foreach (OutboxEntry entry in pending)
        {
            _pendingPublishes[entry.Event.Id] = entry.Event;
            PublishMessage message = new(entry.Event.ChannelId, entry.Event);
            await SendAsync(message, cancellationToken);
            await _localStore.MarkOutboxSentAsync(entry.Event.Id, cancellationToken);
        }
    }
}
