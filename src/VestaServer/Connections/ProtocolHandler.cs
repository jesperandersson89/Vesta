using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vesta;
using VestaServer.Storage;

namespace VestaServer.Connections;

/// <summary>
/// Options that control protocol-level security policy.
/// </summary>
public sealed class ProtocolOptions
{
    /// <summary>
    /// When true, HELLO must include a PublicKey and every PUBLISH must carry a valid Ed25519
    /// signature over the event. When false (default), unsigned events are accepted from
    /// connections that did not register a public key — but events that ARE signed (because
    /// a key was registered) are still verified.
    /// </summary>
    public bool RequireSignedEvents { get; set; }

    /// <summary>
    /// When true, the first slug segment of every channel ID referenced by PUBLISH,
    /// SUBSCRIBE, CREATE_CHANNEL, FETCH, or GRANT_ACCESS must match a registered app
    /// (see <see cref="RegisterAppMessage"/>). When false (default), channels are open
    /// and any namespace may be used implicitly.
    /// </summary>
    public bool RequireAppRegistration { get; set; }

    /// <summary>
    /// An optional operator allow-list of app IDs the server has acknowledged. When non-empty,
    /// only these app namespaces may be used or registered — every other PUBLISH / SUBSCRIBE /
    /// FETCH / CREATE_CHANNEL / REGISTER_APP is rejected with <c>APP_NOT_ALLOWED</c>. This is the
    /// simple "flip a flag in appsettings" admission gate for self-hosters who don't want any app
    /// connecting to their relay without prior acknowledgement; it applies independently of
    /// <see cref="RequireAppRegistration"/>. When empty (default), no allow-list gating is applied
    /// and the relay stays open. Protocol-reserved (<c>vesta/*</c>) channels are never gated.
    /// </summary>
    public IReadOnlyList<string> AllowedApps { get; set; } = [];

    /// <summary>
    /// Upper bound on events returned per <c>EVENTS_BATCH</c>. <c>FETCH</c> limits are clamped to this
    /// value, and HELLO / SUBSCRIBE catch-up is sent as consecutive batches of at most this size until
    /// the client is caught up. Values below 1 fall back to the default (1000).
    /// </summary>
    public int MaxFetchLimit { get; set; } = 1000;

    /// <summary>
    /// When true, every connection must complete the HELLO key-possession proof (CHALLENGE / PROVE)
    /// before it may do anything else; clients that cannot prove get <c>PROOF_REQUIRED</c>. Off by
    /// default so legacy SDKs keep working. Turn it on in production: without it a client id is only
    /// a claim, so private-channel reads and the per-client connection cap can be spoofed.
    /// </summary>
    public bool RequireHelloProof { get; set; }

    /// <summary>
    /// Relay-wide baseline limits that apply when an app sets no quota of its own; app quotas
    /// always take precedence. See <see cref="ProtocolLimitDefaults"/>.
    /// </summary>
    public ProtocolLimitDefaults Defaults { get; set; } = new();
}

/// <summary>
/// Handles the protocol message loop for a single connected client.
/// Processes incoming messages, interacts with the event store,
/// and coordinates broadcasts through the connection manager.
/// </summary>
public sealed class ProtocolHandler(
    IEventStore eventStore,
    IChannelAccessStore accessStore,
    ConnectionManager connectionManager,
    ILogger<ProtocolHandler> logger,
    IAppStore? appStore = null,
    AppRateLimiter? rateLimiter = null,
    IAppStorageAccountant? storageAccountant = null,
    IAppUsageAccountant? usageAccountant = null,
    IOptions<ProtocolOptions>? protocolOptions = null,
    IAdminStore? adminStore = null,
    RelayBaselineLimiter? baselineLimiter = null)
{
    private static readonly string _serverId = Environment.MachineName + "-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Optional capabilities this relay advertises in WELCOME (see <see cref="RelayFeatures"/>).</summary>
    private static readonly IReadOnlyList<string> SupportedFeatures = [RelayFeatures.EncryptedChannels];

    // Real client ids are base64url, so "*" can never collide with a per-client bucket.
    private const string AppThrottleBucketKey = "*throttle";
    private static readonly TimeSpan HelloProofWindow = TimeSpan.FromSeconds(30);
    private readonly ProtocolOptions _options = protocolOptions?.Value ?? new ProtocolOptions();

    /// <summary>
    /// Runs the full message loop for a client connection until disconnect.
    /// </summary>
    public async Task HandleConnectionAsync(ClientConnection connection, CancellationToken cancellationToken)
    {
        connectionManager.Add(connection);
        logger.LogInformation("Client connected: {ConnectionId}", connection.ConnectionId);

        try
        {
            while (connection.IsOpen && !cancellationToken.IsCancellationRequested)
            {
                ProtocolMessage? message = await connection.ReceiveAsync(cancellationToken);

                if (message is null)
                    break;

                await HandleMessageAsync(connection, message, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (WebSocketException)
        {
            // Client disconnected without completing close handshake — not an error
            logger.LogDebug("Client {ConnectionId} disconnected abruptly", connection.ConnectionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling client {ConnectionId}", connection.ConnectionId);
        }
        finally
        {
            connectionManager.Remove(connection);
            logger.LogInformation("Client disconnected: {ConnectionId} (ClientId: {ClientId})",
                connection.ConnectionId, connection.ClientId ?? "unknown");
        }
    }

    private async Task HandleMessageAsync(
        ClientConnection connection,
        ProtocolMessage message,
        CancellationToken cancellationToken)
    {
        if (_options.RequireHelloProof && !connection.Authenticated && message is not (HelloMessage or ProveMessage))
        {
            await connection.SendAsync(
                new ErrorMessage("PROOF_REQUIRED", "This relay requires HELLO with a key-possession proof first"),
                cancellationToken);
            return;
        }

        switch (message)
        {
            case HelloMessage hello:
                await HandleHelloAsync(connection, hello, cancellationToken);
                break;

            case ProveMessage prove:
                await HandleProveAsync(connection, prove, cancellationToken);
                break;

            case PublishMessage publish:
                await HandlePublishAsync(connection, publish, cancellationToken);
                break;

            case SubscribeMessage subscribe:
                await HandleSubscribeAsync(connection, subscribe, cancellationToken);
                break;

            case UnsubscribeMessage unsubscribe:
                HandleUnsubscribe(connection, unsubscribe);
                break;

            case FetchMessage fetch:
                await HandleFetchAsync(connection, fetch, cancellationToken);
                break;

            case CreateChannelMessage create:
                await HandleCreateChannelAsync(connection, create, cancellationToken);
                break;

            case GrantAccessMessage grant:
                await HandleGrantAccessAsync(connection, grant, cancellationToken);
                break;

            case RevokeAccessMessage revoke:
                await HandleRevokeAccessAsync(connection, revoke, cancellationToken);
                break;

            case RegisterAppMessage register:
                await HandleRegisterAppAsync(connection, register, cancellationToken);
                break;

            case DeleteChannelMessage delete:
                await HandleDeleteChannelAsync(connection, delete, cancellationToken);
                break;

            default:
                await connection.SendAsync(
                    new ErrorMessage("UNKNOWN_MESSAGE", "Unrecognized message type"),
                    cancellationToken);
                break;
        }
    }

    private async Task HandleHelloAsync(
        ClientConnection connection,
        HelloMessage hello,
        CancellationToken cancellationToken)
    {
        // A new HELLO abandons any earlier handshake and drops the identity it established.
        connection.PendingHello = null;
        connection.Authenticated = false;
        connection.IsAdmin = false;
        connection.ClientId = null;
        connection.PublicKey = null;
        connection.Subscriptions.Clear();

        byte[]? publicKeyBytes = null;

        // If a public key is provided, validate it matches the clientId
        if (!string.IsNullOrEmpty(hello.PublicKey))
        {
            publicKeyBytes = Base64Url.Decode(hello.PublicKey);
            string derivedClientId = VestaIdentity.DeriveClientId(publicKeyBytes);

            if (derivedClientId != hello.ClientId)
            {
                await connection.SendAsync(
                    new ErrorMessage("IDENTITY_MISMATCH", "PublicKey does not match clientId"),
                    cancellationToken);
                return;
            }
        }
        else if (_options.RequireSignedEvents)
        {
            await connection.SendAsync(
                new ErrorMessage("PUBLIC_KEY_REQUIRED", "HELLO must include a PublicKey on this server"),
                cancellationToken);
            return;
        }

        // Proof-capable clients that announced a key are challenged to sign a nonce, which is the only
        // thing that proves they hold the key behind the clientId (HELLO alone does not).
        bool canProve = hello.ProofSupported == true && publicKeyBytes is not null;
        if (_options.RequireHelloProof && !canProve)
        {
            await connection.SendAsync(
                new ErrorMessage("PROOF_REQUIRED", "This relay requires HELLO with a PublicKey and proofSupported"),
                cancellationToken);
            return;
        }

        if (canProve)
        {
            string nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
            connection.PendingHello = new PendingHello(hello, publicKeyBytes!, nonce, DateTimeOffset.UtcNow + HelloProofWindow);
            await connection.SendAsync(new ChallengeMessage(nonce), cancellationToken);
            return;
        }

        await CompleteHelloAsync(connection, hello, publicKeyBytes, authenticated: false, cancellationToken);
    }

    private async Task HandleProveAsync(
        ClientConnection connection,
        ProveMessage prove,
        CancellationToken cancellationToken)
    {
        PendingHello? pending = connection.PendingHello;
        connection.PendingHello = null; // single use, pass or fail

        if (pending is null)
        {
            await connection.SendAsync(
                new ErrorMessage("UNEXPECTED_PROOF", "No HELLO is waiting for a proof"),
                cancellationToken);
            return;
        }

        if (DateTimeOffset.UtcNow > pending.ExpiresAt)
        {
            await connection.SendAsync(
                new ErrorMessage("PROOF_EXPIRED", "The challenge expired; send HELLO again"),
                cancellationToken);
            return;
        }

        if (!HelloProof.Verify(pending.PublicKey, pending.Nonce, pending.Hello.ClientId, prove.Signature))
        {
            await connection.SendAsync(
                new ErrorMessage("PROOF_INVALID", "Proof signature verification failed"),
                cancellationToken);
            return;
        }

        await CompleteHelloAsync(connection, pending.Hello, pending.PublicKey, authenticated: true, cancellationToken);
    }

    private async Task CompleteHelloAsync(
        ClientConnection connection,
        HelloMessage hello,
        byte[]? publicKeyBytes,
        bool authenticated,
        CancellationToken cancellationToken)
    {
        // Only proven identities are counted, so a client id cannot be used to lock its owner out.
        int maxPerClient = _options.Defaults.MaxConnectionsPerClientId;
        if (authenticated && maxPerClient > 0 &&
            connectionManager.CountAuthenticated(hello.ClientId, connection.ConnectionId) >= maxPerClient)
        {
            await connection.SendAsync(
                new ErrorMessage(
                    "CONNECTION_LIMITED",
                    $"Client already has {maxPerClient} open connections (Protocol:Defaults:MaxConnectionsPerClientId)"),
                cancellationToken);
            return;
        }

        connection.ClientId = hello.ClientId;
        connection.PublicKey = publicKeyBytes;
        connection.Authenticated = authenticated;

        // Promote to server admin only for a proven key in the admin allow-list: the operator's public
        // key is public, so announcing it in HELLO must never be enough.
        if (authenticated && adminStore is not null && connection.PublicKey is not null &&
            await adminStore.IsAdminAsync(connection.PublicKey, cancellationToken))
        {
            connection.IsAdmin = true;
            logger.LogInformation("Client {ClientId} promoted to server admin", connection.ClientId);
        }

        // Subscribe to requested channels (ACL-gated)
        List<string> rejected = [];
        foreach (string channelId in hello.Channels)
        {
            if (!ChannelId.IsValid(channelId))
            {
                await connection.SendAsync(
                    new ErrorMessage("INVALID_CHANNEL", $"Invalid channel ID: '{channelId}'"),
                    cancellationToken);
                continue;
            }

            if (!await EnsureAppRegisteredAsync(connection, channelId, cancellationToken))
            {
                rejected.Add(channelId);
                continue;
            }

            if (!await accessStore.CanAccessAsync(channelId, connection.ClientId, cancellationToken))
            {
                await connection.SendAsync(
                    new ErrorMessage("ACCESS_DENIED", $"Not authorized for channel '{channelId}'"),
                    cancellationToken);
                rejected.Add(channelId);
                continue;
            }

            if (SubscriptionCapReached(connection, channelId))
            {
                await connection.SendAsync(SubscriptionLimitError(channelId), cancellationToken);
                rejected.Add(channelId);
                continue;
            }

            if (!await JoinAppAsync(connection, channelId, eventId: null, cancellationToken))
            {
                rejected.Add(channelId);
                continue;
            }

            connection.Subscriptions.Add(channelId);
        }

        // Send WELCOME
        await connection.SendAsync(
            new WelcomeMessage(_serverId, connection.Subscriptions.ToList(), SupportedFeatures),
            cancellationToken);

        // Catch-up: send missed events for channels with lastSequences
        foreach (KeyValuePair<string, long> entry in hello.LastSequences)
        {
            if (!connection.Subscriptions.Contains(entry.Key))
                continue;

            // Client has seen up to entry.Value
            await SendCatchUpAsync(connection, entry.Key, entry.Value + 1, cancellationToken);
        }

        logger.LogInformation("Client {ClientId} said HELLO, subscribed to {ChannelCount} channels",
            hello.ClientId, connection.Subscriptions.Count);
    }

    private async Task HandlePublishAsync(
        ClientConnection connection,
        PublishMessage publish,
        CancellationToken cancellationToken)
    {
        if (!ChannelId.IsValid(publish.ChannelId))
        {
            await connection.SendAsync(
                new ErrorMessage("INVALID_CHANNEL", $"Invalid channel ID: '{publish.ChannelId}'",
                    publish.Event.Id, publish.ChannelId),
                cancellationToken);
            return;
        }

        // Reserved-namespace enforcement: any channel under "vesta/" may only carry events
        // whose type also begins with "vesta." — this prevents apps from co-opting the
        // protocol-reserved channel space (e.g. injecting app data into identity channels).
        if (ChannelId.IsProtocolChannel(publish.ChannelId) &&
            !publish.Event.EventType.StartsWith("vesta.", StringComparison.Ordinal))
        {
            await connection.SendAsync(
                new ErrorMessage(
                    "PROTOCOL_NAMESPACE_RESERVED",
                    $"Channel '{publish.ChannelId}' is in the reserved 'vesta/' namespace; only events with type 'vesta.*' may be published there.",
                    publish.Event.Id,
                    publish.ChannelId),
                cancellationToken);
            return;
        }

        if (!await EnsureAppRegisteredAsync(connection, publish.ChannelId, cancellationToken))
            return;

        if (!await EnsureChannelNotDeletedAsync(connection, publish.ChannelId, cancellationToken))
            return;

        if (!await accessStore.CanPublishAsync(publish.ChannelId, connection.ClientId, cancellationToken))
        {
            await connection.SendAsync(
                new ErrorMessage("ACCESS_DENIED", $"Not authorized to publish to '{publish.ChannelId}'",
                    publish.Event.Id, publish.ChannelId),
                cancellationToken);
            return;
        }

        if (!await EnsureManifestPublisherIsOwnerAsync(connection, publish, cancellationToken))
            return;

        // Encrypted channels: every app event must be an EncryptedPayload envelope. The relay checks
        // the shape only (it holds no key); vesta.* control events (key distribution) stay in the clear.
        if (!publish.Event.EventType.StartsWith("vesta.", StringComparison.Ordinal) &&
            !EncryptedPayload.IsEnvelope(publish.Event.Payload) &&
            await accessStore.IsEncryptedAsync(publish.ChannelId, cancellationToken))
        {
            await connection.SendAsync(
                new ErrorMessage("ENCRYPTION_REQUIRED",
                    $"Channel '{publish.ChannelId}' is encrypted; payload must be an encrypted envelope",
                    publish.Event.Id, publish.ChannelId),
                cancellationToken);
            return;
        }

        // Anti-impersonation: the event's clientId must match this connection's clientId.
        // Without this check, a connection could publish events claiming to be from another
        // client (the signature check below would still pass because it verifies against the
        // connection's own registered key — so a malicious client could otherwise sign-as-self
        // but stamp the event with someone else's clientId).
        if (!string.IsNullOrEmpty(connection.ClientId) &&
            !string.Equals(publish.Event.ClientId, connection.ClientId, StringComparison.Ordinal))
        {
            await connection.SendAsync(
                new ErrorMessage("CLIENT_ID_MISMATCH", "Event clientId does not match connection clientId",
                    publish.Event.Id, publish.ChannelId),
                cancellationToken);
            return;
        }

        // Verify signature if public key is known
        if (connection.PublicKey is not null)
        {
            if (string.IsNullOrEmpty(publish.Event.Signature))
            {
                await connection.SendAsync(
                    new ErrorMessage("SIGNATURE_REQUIRED", "Events must be signed when public key is registered",
                        publish.Event.Id, publish.ChannelId),
                    cancellationToken);
                return;
            }

            if (!EventSigner.VerifyEvent(publish.Event, connection.PublicKey))
            {
                await connection.SendAsync(
                    new ErrorMessage("INVALID_SIGNATURE", "Event signature verification failed",
                        publish.Event.Id, publish.ChannelId),
                    cancellationToken);
                return;
            }
        }
        else if (_options.RequireSignedEvents)
        {
            // Should be unreachable because HELLO already enforces PublicKey,
            // but guard defensively in case state is ever inconsistent.
            await connection.SendAsync(
                new ErrorMessage("SIGNATURE_REQUIRED", "Server requires signed events"),
                cancellationToken);
            return;
        }

        // Per-app quota enforcement (TODO #9b: max_payload_bytes + publish rate).
        if (!await EnforcePublishQuotasAsync(connection, publish, cancellationToken))
            return;

        // If volatile, skip DB storage — just relay to current subscribers
        if (publish.Event.Volatile is true)
        {
            EventMessage volatileMessage = new(
                publish.ChannelId,
                publish.Event,
                0, // Sequence 0 indicates non-sequenced volatile event
                ReceivedAt: DateTimeOffset.UtcNow);

            await connectionManager.BroadcastToChannelAsync(
                publish.ChannelId,
                volatileMessage,
                excludeConnectionId: connection.ConnectionId,
                cancellationToken: cancellationToken);

            return;
        }

        // Store the event
        SequencedEvent sequenced = await eventStore.AppendAsync(publish.Event, cancellationToken);

        // Record the implicit-create so the access store knows about this channel
        // (needed for IsDeletedAsync / DeleteChannelAsync / CountChannelsByAppAsync
        // in the in-memory backend; no-op for Postgres).
        await accessStore.RecordImplicitChannelAsync(publish.ChannelId, cancellationToken);

        // Update the cached storage rollup so total_storage_bytes enforcement stays current
        // between pruner sweeps. No-op for apps with a cold cache or no quota.
        if (storageAccountant is not null)
        {
            string? appId = AppId.ExtractFromChannelId(publish.ChannelId);
            if (appId is not null)
                storageAccountant.Add(appId, EstimatePayloadBytes(publish.Event));
        }

        // Update the cached message-count rollup so max_messages_per_month enforcement
        // stays current between pruner sweeps.
        if (usageAccountant is not null)
        {
            string? appId = AppId.ExtractFromChannelId(publish.ChannelId);
            if (appId is not null)
                usageAccountant.IncrementMessages(appId);
        }

        // ACK back to the publisher
        await connection.SendAsync(
            new AckMessage(publish.ChannelId, sequenced.Event.Id, sequenced.Sequence),
            cancellationToken);

        // Broadcast to other subscribers
        EventMessage eventMessage = new(
            publish.ChannelId,
            sequenced.Event,
            sequenced.Sequence,
            sequenced.ReceivedAt);

        await connectionManager.BroadcastToChannelAsync(
            publish.ChannelId,
            eventMessage,
            excludeConnectionId: connection.ConnectionId,
            cancellationToken: cancellationToken);
    }

    private async Task HandleSubscribeAsync(
        ClientConnection connection,
        SubscribeMessage subscribe,
        CancellationToken cancellationToken)
    {
        if (!ChannelId.IsValid(subscribe.ChannelId))
        {
            await connection.SendAsync(
                new ErrorMessage("INVALID_CHANNEL", $"Invalid channel ID: '{subscribe.ChannelId}'"),
                cancellationToken);
            return;
        }

        if (!await EnsureAppRegisteredAsync(connection, subscribe.ChannelId, cancellationToken))
            return;

        if (!await EnsureChannelNotDeletedAsync(connection, subscribe.ChannelId, cancellationToken))
            return;

        if (!await accessStore.CanAccessAsync(subscribe.ChannelId, connection.ClientId, cancellationToken))
        {
            await connection.SendAsync(
                new ErrorMessage("ACCESS_DENIED", $"Not authorized for channel '{subscribe.ChannelId}'"),
                cancellationToken);
            return;
        }

        if (SubscriptionCapReached(connection, subscribe.ChannelId))
        {
            await connection.SendAsync(SubscriptionLimitError(subscribe.ChannelId), cancellationToken);
            return;
        }

        if (!await JoinAppAsync(connection, subscribe.ChannelId, eventId: null, cancellationToken))
            return;

        connection.Subscriptions.Add(subscribe.ChannelId);

        // If fromSequence is specified, send catch-up events
        if (subscribe.FromSequence.HasValue)
        {
            await SendCatchUpAsync(
                connection, subscribe.ChannelId, subscribe.FromSequence.Value, cancellationToken);
        }
    }

    /// <summary>
    /// Only the registered app owner may publish to <c>{appId}/vesta/relays</c> (the relay manifest
    /// channel); anyone can still read it. Apps the relay does not know are left open.
    /// </summary>
    private async Task<bool> EnsureManifestPublisherIsOwnerAsync(
        ClientConnection connection,
        PublishMessage publish,
        CancellationToken cancellationToken)
    {
        string? appId = AppId.ExtractFromChannelId(publish.ChannelId);
        if (appStore is null || appId is null || publish.ChannelId != RelayManifest.ChannelFor(appId))
            return true;

        AppInfo? app = await appStore.GetAsync(appId, cancellationToken);
        if (app is null || string.Equals(app.OwnerClientId, connection.ClientId, StringComparison.Ordinal))
            return true;

        await connection.SendAsync(
            new ErrorMessage("ACCESS_DENIED", $"Only the owner of app '{appId}' may publish its relay manifest",
                publish.Event.Id, publish.ChannelId),
            cancellationToken);
        return false;
    }

    private int EffectiveMaxFetchLimit => _options.MaxFetchLimit < 1 ? 1000 : _options.MaxFetchLimit;

    private async Task<bool> JoinAppAsync(
        ClientConnection connection,
        string channelId,
        Guid? eventId,
        CancellationToken cancellationToken)
    {
        string? appId = AppId.ExtractFromChannelId(channelId);
        if (appId is null || connection.JoinedApps.ContainsKey(appId))
            return true;

        AppInfo? app = appStore is null ? null : await appStore.GetAsync(appId, cancellationToken);
        return await JoinAppAsync(connection, appId, app, channelId, eventId, cancellationToken);
    }

    // The first SUBSCRIBE / CREATE_CHANNEL / PUBLISH in an app's namespace makes the connection count
    // against max_connections until it disconnects; over the cap answers CONNECTION_LIMITED.
    private async Task<bool> JoinAppAsync(
        ClientConnection connection,
        string appId,
        AppInfo? app,
        string channelId,
        Guid? eventId,
        CancellationToken cancellationToken)
    {
        if (connection.JoinedApps.ContainsKey(appId))
            return true;

        if (app?.Quotas.MaxConnections is int max && connectionManager.CountJoined(appId) >= max)
        {
            await connection.SendAsync(
                new ErrorMessage(
                    "CONNECTION_LIMITED",
                    $"App '{appId}' already has {max} connections (max_connections)",
                    eventId,
                    channelId),
                cancellationToken);
            return false;
        }

        connection.JoinedApps[appId] = 0;
        if (app?.Quotas.MaxConnections is not null)
            connectionManager.NoteJoined(appId);
        return true;
    }

    private bool SubscriptionCapReached(ClientConnection connection, string channelId)
    {
        int max = _options.Defaults.MaxSubscriptionsPerConnection;
        return max > 0 && connection.Subscriptions.Count >= max && !connection.Subscriptions.Contains(channelId);
    }

    private ErrorMessage SubscriptionLimitError(string channelId) => new(
        "CONNECTION_LIMITED",
        $"Connection already has {_options.Defaults.MaxSubscriptionsPerConnection} subscriptions (Protocol:Defaults:MaxSubscriptionsPerConnection)",
        null,
        channelId);

    // Pages through the channel so a backlog larger than one batch is delivered in full, not truncated.
    private async Task SendCatchUpAsync(
        ClientConnection connection,
        string channelId,
        long fromSequence,
        CancellationToken cancellationToken)
    {
        int pageSize = EffectiveMaxFetchLimit;
        long next = fromSequence;

        while (connection.IsOpen)
        {
            IReadOnlyList<SequencedEvent> page = await eventStore.GetEventsAsync(
                channelId, next, pageSize, cancellationToken);

            if (page.Count == 0)
                return;

            await connection.SendAsync(new EventsBatchMessage(channelId, page), cancellationToken);

            if (page.Count < pageSize)
                return;

            next = page[^1].Sequence + 1;
        }
    }

    private void HandleUnsubscribe(ClientConnection connection, UnsubscribeMessage unsubscribe)
    {
        connection.Subscriptions.Remove(unsubscribe.ChannelId);
    }

    private async Task HandleFetchAsync(
        ClientConnection connection,
        FetchMessage fetch,
        CancellationToken cancellationToken)
    {
        if (!ChannelId.IsValid(fetch.ChannelId))
        {
            await connection.SendAsync(
                new ErrorMessage("INVALID_CHANNEL", $"Invalid channel ID: '{fetch.ChannelId}'", null, fetch.ChannelId),
                cancellationToken);
            return;
        }

        if (!await EnsureAppRegisteredAsync(connection, fetch.ChannelId, cancellationToken))
            return;

        if (!await EnsureChannelNotDeletedAsync(connection, fetch.ChannelId, cancellationToken))
            return;

        if (!await accessStore.CanAccessAsync(fetch.ChannelId, connection.ClientId, cancellationToken))
        {
            await connection.SendAsync(
                new ErrorMessage("ACCESS_DENIED", $"Not authorized to fetch from '{fetch.ChannelId}'", null, fetch.ChannelId),
                cancellationToken);
            return;
        }

        int limit = Math.Clamp(fetch.Limit ?? 100, 1, EffectiveMaxFetchLimit);
        IReadOnlyList<SequencedEvent> events = await eventStore.GetEventsAsync(
            fetch.ChannelId,
            fetch.FromSequence,
            limit,
            cancellationToken);

        // If toSequence is specified, filter further
        if (fetch.ToSequence.HasValue)
        {
            events = events.Where(e => e.Sequence <= fetch.ToSequence.Value).ToList();
        }

        await connection.SendAsync(
            new EventsBatchMessage(fetch.ChannelId, events),
            cancellationToken);
    }

    private async Task HandleCreateChannelAsync(
        ClientConnection connection,
        CreateChannelMessage create,
        CancellationToken cancellationToken)
    {
        if (connection.ClientId is null)
        {
            await connection.SendAsync(
                new ErrorMessage("HELLO_REQUIRED", "Must send HELLO before CREATE_CHANNEL"),
                cancellationToken);
            return;
        }

        if (!ChannelId.IsValid(create.ChannelId))
        {
            await connection.SendAsync(
                new ErrorMessage("INVALID_CHANNEL", $"Invalid channel ID: '{create.ChannelId}'"),
                cancellationToken);
            return;
        }

        // Apps must not explicitly create channels in the reserved 'vesta/' namespace —
        // protocol channels (e.g. vesta/identity/{group}) are auto-created on first PUBLISH.
        if (ChannelId.IsProtocolChannel(create.ChannelId))
        {
            await connection.SendAsync(
                new ErrorMessage(
                    "PROTOCOL_NAMESPACE_RESERVED",
                    $"Channel '{create.ChannelId}' is in the reserved 'vesta/' namespace; protocol channels are auto-created on first PUBLISH."),
                cancellationToken);
            return;
        }

        if (!await EnsureAppRegisteredAsync(connection, create.ChannelId, cancellationToken))
            return;

        if (!await EnsureChannelNotDeletedAsync(connection, create.ChannelId, cancellationToken))
            return;

        if (appStore is not null &&
            AppId.ExtractFromChannelId(create.ChannelId) is string createAppId &&
            IsBlockedByOwnerOnlyCreation(await appStore.GetAsync(createAppId, cancellationToken), connection))
        {
            await connection.SendAsync(OwnerOnlyCreationError(createAppId, null, create.ChannelId), cancellationToken);
            return;
        }

        // max_channels quota — checked before attempting to create the row.
        if (!await EnforceMaxChannelsAsync(connection, create.ChannelId, cancellationToken))
            return;

        bool encrypted = create.Encrypted is true;
        ChannelVisibility visibility = ChannelVisibilityNames.Parse(create.Visibility);

        // Keys on an encrypted channel are distributed by membership, so it must be private.
        if (encrypted && visibility != ChannelVisibility.Private)
        {
            await connection.SendAsync(
                new ErrorMessage("INVALID_CHANNEL",
                    $"Encrypted channel '{create.ChannelId}' must have visibility 'private'", null, create.ChannelId),
                cancellationToken);
            return;
        }

        if (encrypted && !await EnforceMaxEncryptedChannelsAsync(connection, create.ChannelId, cancellationToken))
            return;

        if (!await JoinAppAsync(connection, create.ChannelId, eventId: null, cancellationToken))
            return;

        try
        {
            await accessStore.CreateChannelAsync(
                create.ChannelId,
                visibility,
                adminClientId: connection.ClientId,
                memberClientIds: create.InitialMembers,
                encrypted,
                cancellationToken);
        }
        catch (ChannelAlreadyExistsException)
        {
            await connection.SendAsync(
                new ErrorMessage("CHANNEL_EXISTS", $"Channel '{create.ChannelId}' already exists", null, create.ChannelId),
                cancellationToken);
            return;
        }

        // Auto-subscribe the creator
        connection.Subscriptions.Add(create.ChannelId);

        await connection.SendAsync(
            new AckMessage(create.ChannelId, Guid.Empty, 0),
            cancellationToken);

        logger.LogInformation(
            "Channel created: {ChannelId} ({Visibility}{Encrypted}) by {ClientId} with {MemberCount} initial members",
            create.ChannelId, visibility, encrypted ? ", encrypted" : "", connection.ClientId, create.InitialMembers.Count);
    }

    private async Task HandleGrantAccessAsync(
        ClientConnection connection,
        GrantAccessMessage grant,
        CancellationToken cancellationToken)
    {
        if (connection.ClientId is null)
        {
            await connection.SendAsync(
                new ErrorMessage("HELLO_REQUIRED", "Must send HELLO before GRANT_ACCESS"),
                cancellationToken);
            return;
        }

        if (!ChannelId.IsValid(grant.ChannelId))
        {
            await connection.SendAsync(
                new ErrorMessage("INVALID_CHANNEL", $"Invalid channel ID: '{grant.ChannelId}'"),
                cancellationToken);
            return;
        }

        if (!await accessStore.IsAdminAsync(grant.ChannelId, connection.ClientId, cancellationToken))
        {
            await connection.SendAsync(
                new ErrorMessage("ACCESS_DENIED", "Only channel admins can grant access"),
                cancellationToken);
            return;
        }

        string role = ChannelRoles.Normalize(grant.Role);
        await accessStore.GrantAccessAsync(grant.ChannelId, grant.ClientId, role, cancellationToken);

        await connection.SendAsync(
            new AckMessage(grant.ChannelId, Guid.Empty, 0),
            cancellationToken);
    }

    private async Task HandleRevokeAccessAsync(
        ClientConnection connection,
        RevokeAccessMessage revoke,
        CancellationToken cancellationToken)
    {
        if (connection.ClientId is null)
        {
            await connection.SendAsync(
                new ErrorMessage("HELLO_REQUIRED", "Must send HELLO before REVOKE_ACCESS"),
                cancellationToken);
            return;
        }

        if (!ChannelId.IsValid(revoke.ChannelId))
        {
            await connection.SendAsync(
                new ErrorMessage("INVALID_CHANNEL", $"Invalid channel ID: '{revoke.ChannelId}'"),
                cancellationToken);
            return;
        }

        if (!await accessStore.IsAdminAsync(revoke.ChannelId, connection.ClientId, cancellationToken))
        {
            await connection.SendAsync(
                new ErrorMessage("ACCESS_DENIED", "Only channel admins can revoke access", null, revoke.ChannelId),
                cancellationToken);
            return;
        }

        // A channel must never lose its last admin; an admin steps down by having another admin revoke it.
        if (string.Equals(revoke.ClientId, connection.ClientId, StringComparison.Ordinal))
        {
            await connection.SendAsync(
                new ErrorMessage("ACCESS_DENIED", "An admin cannot revoke its own access", null, revoke.ChannelId),
                cancellationToken);
            return;
        }

        await accessStore.RevokeAccessAsync(revoke.ChannelId, revoke.ClientId, cancellationToken);

        await connection.SendAsync(
            new AckMessage(revoke.ChannelId, Guid.Empty, 0),
            cancellationToken);

        logger.LogInformation(
            "Access revoked on {ChannelId}: {Target} by admin {ClientId}",
            revoke.ChannelId, revoke.ClientId, connection.ClientId);
    }

    private async Task HandleRegisterAppAsync(
        ClientConnection connection,
        RegisterAppMessage register,
        CancellationToken cancellationToken)
    {
        if (connection.ClientId is null)
        {
            await connection.SendAsync(
                new ErrorMessage("HELLO_REQUIRED", "Must send HELLO before REGISTER_APP"),
                cancellationToken);
            return;
        }

        if (!AppId.IsValid(register.AppId))
        {
            await connection.SendAsync(
                new ErrorMessage("INVALID_APP", $"Invalid app ID: '{register.AppId}'"),
                cancellationToken);
            return;
        }

        // Operator allow-list gate: a self-hoster who pinned AllowedApps must have acknowledged
        // this app id before it can be registered/claimed.
        if (_options.AllowedApps.Count > 0 &&
            !_options.AllowedApps.Contains(register.AppId, StringComparer.Ordinal))
        {
            await connection.SendAsync(
                new ErrorMessage("APP_NOT_ALLOWED", $"App '{register.AppId}' is not on this server's allow-list"),
                cancellationToken);
            return;
        }

        if (appStore is null)
        {
            await connection.SendAsync(
                new ErrorMessage("APPS_NOT_SUPPORTED", "Server does not have an app store configured"),
                cancellationToken);
            return;
        }

        try
        {
            await appStore.RegisterAsync(register.AppId, connection.ClientId, register.Discoverable, cancellationToken);
        }
        catch (AppAlreadyRegisteredException)
        {
            await connection.SendAsync(
                new ErrorMessage("DUPLICATE_APP", $"App '{register.AppId}' is already registered"),
                cancellationToken);
            return;
        }

        await connection.SendAsync(
            new AckMessage(register.AppId, Guid.Empty, 0),
            cancellationToken);

        logger.LogInformation(
            "App registered: {AppId} owned by {ClientId}",
            register.AppId, connection.ClientId);
    }

    private async Task HandleDeleteChannelAsync(
        ClientConnection connection,
        DeleteChannelMessage delete,
        CancellationToken cancellationToken)
    {
        if (connection.ClientId is null)
        {
            await connection.SendAsync(
                new ErrorMessage("HELLO_REQUIRED", "Must send HELLO before DELETE_CHANNEL"),
                cancellationToken);
            return;
        }

        if (!connection.IsAdmin)
        {
            await connection.SendAsync(
                new ErrorMessage("NOT_ADMIN", "DELETE_CHANNEL is reserved for server admins"),
                cancellationToken);
            return;
        }

        if (!ChannelId.IsValid(delete.ChannelId))
        {
            await connection.SendAsync(
                new ErrorMessage("INVALID_CHANNEL", $"Invalid channel ID: '{delete.ChannelId}'"),
                cancellationToken);
            return;
        }

        bool existed = await accessStore.DeleteChannelAsync(delete.ChannelId, cancellationToken);
        if (!existed)
        {
            await connection.SendAsync(
                new ErrorMessage("CHANNEL_NOT_FOUND", $"Channel '{delete.ChannelId}' does not exist"),
                cancellationToken);
            return;
        }

        await connection.SendAsync(
            new AckMessage(delete.ChannelId, Guid.Empty, 0),
            cancellationToken);

        logger.LogInformation(
            "Channel {ChannelId} soft-deleted by admin {ClientId}",
            delete.ChannelId, connection.ClientId);
    }

    /// <summary>
    /// Returns false (and sends a <c>CHANNEL_DELETED</c> ERROR frame) if the channel has
    /// been soft-deleted. Returns true otherwise (including for channels that don't exist
    /// yet \u2014 implicit creation is still allowed for active namespaces).
    /// </summary>
    private async Task<bool> EnsureChannelNotDeletedAsync(
        ClientConnection connection,
        string channelId,
        CancellationToken cancellationToken)
    {
        if (await accessStore.IsDeletedAsync(channelId, cancellationToken))
        {
            await connection.SendAsync(
                new ErrorMessage("CHANNEL_DELETED", $"Channel '{channelId}' has been deleted", null, channelId),
                cancellationToken);
            return false;
        }
        return true;
    }

    /// <summary>
    /// When app registration is required, returns false (and sends an ERROR frame) if the
    /// channel's app namespace is not registered. When not required, always returns true.
    /// </summary>
    private async Task<bool> EnsureAppRegisteredAsync(
        ClientConnection connection,
        string channelId,
        CancellationToken cancellationToken)
    {
        // Protocol-reserved channels (vesta/*) are not an "app" — they are part of the SDK
        // surface itself (e.g. vesta/identity/{group}). Skip both gates so device-group
        // bootstrap works on servers that otherwise restrict apps.
        if (ChannelId.IsProtocolChannel(channelId))
            return true;

        // Operator allow-list gate (acknowledgement). When configured, only listed app
        // namespaces may be used at all — independent of RequireAppRegistration / appStore.
        if (_options.AllowedApps.Count > 0)
        {
            string? allowedAppId = AppId.ExtractFromChannelId(channelId);
            if (allowedAppId is null || !_options.AllowedApps.Contains(allowedAppId, StringComparer.Ordinal))
            {
                await connection.SendAsync(
                    new ErrorMessage(
                        "APP_NOT_ALLOWED",
                        $"App '{allowedAppId ?? channelId}' is not on this server's allow-list",
                        null,
                        channelId),
                    cancellationToken);
                return false;
            }
        }

        if (!_options.RequireAppRegistration || appStore is null)
            return true;

        string? appId = AppId.ExtractFromChannelId(channelId);
        if (appId is null || !AppId.IsValid(appId))
        {
            await connection.SendAsync(
                new ErrorMessage("UNKNOWN_APP", $"Channel '{channelId}' has no valid app namespace", null, channelId),
                cancellationToken);
            return false;
        }

        if (!await appStore.ExistsAsync(appId, cancellationToken))
        {
            await connection.SendAsync(
                new ErrorMessage("UNKNOWN_APP", $"App '{appId}' is not registered", null, channelId),
                cancellationToken);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Enforces the publish-time per-app quotas: <c>max_payload_bytes</c> (estimated as the
    /// UTF-8 byte length of <c>payload</c> + <c>metadata</c>) and <c>publish_rate_per_minute</c>
    /// (token bucket keyed by <c>(appId, clientId)</c>), falling back to <c>Protocol:Defaults</c> when the
    /// app is unregistered or sets no rate. Sends an ERROR frame and returns false on rejection.
    /// </summary>
    private async Task<bool> EnforcePublishQuotasAsync(
        ClientConnection connection,
        PublishMessage publish,
        CancellationToken cancellationToken)
    {
        string? appId = AppId.ExtractFromChannelId(publish.ChannelId);
        if (appId is null)
            return true;

        AppInfo? app = appStore is null ? null : await appStore.GetAsync(appId, cancellationToken);

        if (app?.PausedAt is not null)
        {
            await connection.SendAsync(
                new ErrorMessage(
                    "APP_PAUSED",
                    $"App '{appId}' is paused by the operator",
                    publish.Event.Id,
                    publish.ChannelId),
                cancellationToken);
            return false;
        }

        // Owner-only apps: a stranger's first PUBLISH to an unknown channel would implicitly create it.
        if (IsBlockedByOwnerOnlyCreation(app, connection) &&
            await accessStore.GetVisibilityAsync(publish.ChannelId, cancellationToken) is null)
        {
            await connection.SendAsync(OwnerOnlyCreationError(appId, publish.Event.Id, publish.ChannelId), cancellationToken);
            return false;
        }

        AppQuotas quotas = app?.Quotas ?? AppQuotas.None;

        if (!await JoinAppAsync(connection, appId, app, publish.ChannelId, publish.Event.Id, cancellationToken))
            return false;

        int? estimatedSize = null;

        if (quotas.MaxPayloadBytes is int maxBytes)
        {
            int size = EstimatePayloadBytes(publish.Event);
            estimatedSize = size;
            if (size > maxBytes)
            {
                await connection.SendAsync(
                    new ErrorMessage(
                        "QUOTA_EXCEEDED",
                        $"Event payload ({size} B) exceeds max_payload_bytes ({maxBytes} B) for app '{appId}'",
                        publish.Event.Id,
                        publish.ChannelId),
                    cancellationToken);
                return false;
            }
        }

        if (quotas.TotalStorageBytes is long maxStorage && storageAccountant is not null)
        {
            long? cached = storageAccountant.Get(appId);
            if (cached is long current)
            {
                int size = estimatedSize ?? EstimatePayloadBytes(publish.Event);
                if (current + size > maxStorage)
                {
                    await connection.SendAsync(
                        new ErrorMessage(
                            "QUOTA_EXCEEDED",
                            $"App '{appId}' would exceed total_storage_bytes ({maxStorage} B; currently {current} B)",
                            publish.Event.Id,
                            publish.ChannelId),
                        cancellationToken);
                    return false;
                }
            }
            // Cold cache: allow — the next pruner sweep will populate it.
        }

        if (quotas.MaxMessagesPerMonth is long maxMessages && usageAccountant is not null)
        {
            long? cachedMessages = usageAccountant.GetMessages(appId);
            if (cachedMessages is long currentMessages && currentMessages + 1 > maxMessages)
            {
                await connection.SendAsync(
                    new ErrorMessage(
                        "MESSAGE_QUOTA_EXCEEDED",
                        $"App '{appId}' would exceed max_messages_per_month ({maxMessages}; currently {currentMessages})",
                        publish.Event.Id,
                        publish.ChannelId),
                    cancellationToken);
                return false;
            }
            // Cold cache: allow — the next pruner sweep will populate it.
        }

        if (rateLimiter is not null && app?.ThrottlePerMinute is int throttle &&
            !rateLimiter.TryAcquire(appId, AppThrottleBucketKey, throttle))
        {
            await connection.SendAsync(
                new ErrorMessage(
                    "RATE_LIMITED",
                    $"App '{appId}' is throttled by the operator ({throttle}/min across all clients)",
                    publish.Event.Id,
                    publish.ChannelId),
                cancellationToken);
            return false;
        }

        if (rateLimiter is not null && !string.IsNullOrEmpty(connection.ClientId))
        {
            // An app that sets volatile_rate_per_minute gets a bucket of its own for volatile events,
            // so a 20 Hz position stream no longer eats the budget for persisted events.
            bool volatileBucket = publish.Event.Volatile is true && quotas.VolatileRatePerMinute is not null;
            int rate = volatileBucket
                ? quotas.VolatileRatePerMinute!.Value
                : quotas.PublishRatePerMinute ?? _options.Defaults.PublishRatePerMinute;
            // Real client ids are base64url, so "*v:" can never collide with another client's bucket.
            string bucketClient = volatileBucket ? $"*v:{connection.ClientId}" : connection.ClientId;
            if (!rateLimiter.TryAcquire(appId, bucketClient, rate))
            {
                await connection.SendAsync(
                    new ErrorMessage(
                        "RATE_LIMITED",
                        volatileBucket
                            ? $"Volatile publish rate exceeded ({rate}/min) for client on app '{appId}'"
                            : $"Publish rate exceeded ({rate}/min) for client on app '{appId}'",
                        publish.Event.Id,
                        publish.ChannelId),
                    cancellationToken);
                return false;
            }
        }

        if (baselineLimiter is not null && !string.IsNullOrEmpty(connection.ClientId) &&
            !baselineLimiter.TryTrackChannel(appId, connection.ClientId, publish.ChannelId))
        {
            await connection.SendAsync(
                new ErrorMessage(
                    "QUOTA_EXCEEDED",
                    $"Client already publishes to {_options.Defaults.MaxChannelsPerClientPerApp} channels on app '{appId}' (Protocol:Defaults:MaxChannelsPerClientPerApp)",
                    publish.Event.Id,
                    publish.ChannelId),
                cancellationToken);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Enforces <c>max_channels</c> on CREATE_CHANNEL. Counts existing channels in the app
    /// namespace and rejects if the configured limit would be exceeded.
    /// </summary>
    private async Task<bool> EnforceMaxChannelsAsync(
        ClientConnection connection,
        string channelId,
        CancellationToken cancellationToken)
    {
        if (appStore is null)
            return true;

        string? appId = AppId.ExtractFromChannelId(channelId);
        if (appId is null)
            return true;

        AppInfo? app = await appStore.GetAsync(appId, cancellationToken);
        if (app is null)
            return true;

        if (app.PausedAt is not null)
        {
            await connection.SendAsync(
                new ErrorMessage("APP_PAUSED", $"App '{appId}' is paused by the operator", null, channelId),
                cancellationToken);
            return false;
        }

        if (app.Quotas.MaxChannels is not int max)
            return true;

        int current = await accessStore.CountChannelsByAppAsync(appId, cancellationToken);
        if (current >= max)
        {
            await connection.SendAsync(
                new ErrorMessage(
                    "QUOTA_EXCEEDED",
                    $"App '{appId}' already owns {current} channels (max_channels = {max})",
                    null, channelId),
                cancellationToken);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Enforces <c>max_encrypted_channels</c> on CREATE_CHANNEL with <c>encrypted: true</c>.
    /// Pause and app existence were already checked by <see cref="EnforceMaxChannelsAsync"/>.
    /// </summary>
    private async Task<bool> EnforceMaxEncryptedChannelsAsync(
        ClientConnection connection,
        string channelId,
        CancellationToken cancellationToken)
    {
        if (appStore is null)
            return true;

        string? appId = AppId.ExtractFromChannelId(channelId);
        if (appId is null)
            return true;

        AppInfo? app = await appStore.GetAsync(appId, cancellationToken);
        if (app?.Quotas.MaxEncryptedChannels is not int max)
            return true;

        int current = await accessStore.CountEncryptedChannelsByAppAsync(appId, cancellationToken);
        if (current >= max)
        {
            await connection.SendAsync(
                new ErrorMessage(
                    "QUOTA_EXCEEDED",
                    $"App '{appId}' already owns {current} encrypted channels (max_encrypted_channels = {max})",
                    null, channelId),
                cancellationToken);
            return false;
        }

        return true;
    }

    private static bool IsBlockedByOwnerOnlyCreation(AppInfo? app, ClientConnection connection) =>
        app is { OwnerOnlyChannelCreation: true } &&
        !string.Equals(app.OwnerClientId, connection.ClientId, StringComparison.Ordinal);

    private static ErrorMessage OwnerOnlyCreationError(string appId, Guid? eventId, string channelId) => new(
        "ACCESS_DENIED",
        $"Only the owner of app '{appId}' may create channels in its namespace",
        eventId,
        channelId);

    private static int EstimatePayloadBytes(VestaEvent evt)
    {
        // The signing/wire form of payload + metadata is what counts toward the byte budget.
        // GetRawText() is the JSON text as it sits in the message buffer.
        int total = 0;
        try { total += System.Text.Encoding.UTF8.GetByteCount(evt.Payload.GetRawText()); }
        catch (InvalidOperationException) { /* default JsonElement — counts as 0 */ }

        if (evt.Metadata is { } md)
        {
            try { total += System.Text.Encoding.UTF8.GetByteCount(md.GetRawText()); }
            catch (InvalidOperationException) { /* default — 0 */ }
        }

        return total;
    }
}
