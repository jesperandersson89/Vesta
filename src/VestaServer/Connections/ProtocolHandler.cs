using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VestaCore.Channels;
using VestaCore.Events;
using VestaCore.Identity;
using VestaCore.Protocol;
using VestaCore.Storage;
using VestaCore.Utilities;
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
    IOptions<ProtocolOptions>? protocolOptions = null,
    IAdminStore? adminStore = null)
{
    private static readonly string _serverId = Environment.MachineName + "-" + Guid.NewGuid().ToString("N")[..8];
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
        switch (message)
        {
            case HelloMessage hello:
                await HandleHelloAsync(connection, hello, cancellationToken);
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
        connection.ClientId = hello.ClientId;

        // If a public key is provided, validate it matches the clientId and store it
        if (!string.IsNullOrEmpty(hello.PublicKey))
        {
            byte[] publicKeyBytes = Base64Url.Decode(hello.PublicKey);
            string derivedClientId = VestaIdentity.DeriveClientId(publicKeyBytes);

            if (derivedClientId != hello.ClientId)
            {
                await connection.SendAsync(
                    new ErrorMessage("IDENTITY_MISMATCH", "PublicKey does not match clientId"),
                    cancellationToken);
                return;
            }

            connection.PublicKey = publicKeyBytes;
        }
        else if (_options.RequireSignedEvents)
        {
            await connection.SendAsync(
                new ErrorMessage("PUBLIC_KEY_REQUIRED", "HELLO must include a PublicKey on this server"),
                cancellationToken);
            return;
        }

        // Promote to server admin if the verified public key is in the admin allow-list.
        // The check runs only after PublicKey validation above, so an unauthenticated or
        // mismatched client can never receive admin status.
        if (adminStore is not null && connection.PublicKey is not null &&
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

            connection.Subscriptions.Add(channelId);
        }

        // Send WELCOME
        await connection.SendAsync(
            new WelcomeMessage(_serverId, connection.Subscriptions.ToList()),
            cancellationToken);

        // Catch-up: send missed events for channels with lastSequences
        foreach (KeyValuePair<string, long> entry in hello.LastSequences)
        {
            if (!connection.Subscriptions.Contains(entry.Key))
                continue;

            long fromSequence = entry.Value + 1; // Client has seen up to entry.Value
            IReadOnlyList<SequencedEvent> missedEvents = await eventStore.GetEventsAsync(
                entry.Key, fromSequence, cancellationToken: cancellationToken);

            if (missedEvents.Count > 0)
            {
                await connection.SendAsync(
                    new EventsBatchMessage(entry.Key, missedEvents),
                    cancellationToken);
            }
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

        if (!await accessStore.CanAccessAsync(publish.ChannelId, connection.ClientId, cancellationToken))
        {
            await connection.SendAsync(
                new ErrorMessage("ACCESS_DENIED", $"Not authorized to publish to '{publish.ChannelId}'",
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

        connection.Subscriptions.Add(subscribe.ChannelId);

        // If fromSequence is specified, send catch-up events
        if (subscribe.FromSequence.HasValue)
        {
            IReadOnlyList<SequencedEvent> events = await eventStore.GetEventsAsync(
                subscribe.ChannelId,
                subscribe.FromSequence.Value,
                cancellationToken: cancellationToken);

            if (events.Count > 0)
            {
                await connection.SendAsync(
                    new EventsBatchMessage(subscribe.ChannelId, events),
                    cancellationToken);
            }
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
                new ErrorMessage("INVALID_CHANNEL", $"Invalid channel ID: '{fetch.ChannelId}'"),
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
                new ErrorMessage("ACCESS_DENIED", $"Not authorized to fetch from '{fetch.ChannelId}'"),
                cancellationToken);
            return;
        }

        int limit = fetch.Limit ?? 100;
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

        // max_channels quota — checked before attempting to create the row.
        if (!await EnforceMaxChannelsAsync(connection, create.ChannelId, cancellationToken))
            return;

        ChannelVisibility visibility = string.Equals(create.Visibility, "private", StringComparison.OrdinalIgnoreCase)
            ? ChannelVisibility.Private
            : ChannelVisibility.Public;

        try
        {
            await accessStore.CreateChannelAsync(
                create.ChannelId,
                visibility,
                adminClientId: connection.ClientId,
                memberClientIds: create.InitialMembers,
                cancellationToken);
        }
        catch (ChannelAlreadyExistsException)
        {
            await connection.SendAsync(
                new ErrorMessage("CHANNEL_EXISTS", $"Channel '{create.ChannelId}' already exists"),
                cancellationToken);
            return;
        }

        // Auto-subscribe the creator
        connection.Subscriptions.Add(create.ChannelId);

        await connection.SendAsync(
            new AckMessage(create.ChannelId, Guid.Empty, 0),
            cancellationToken);

        logger.LogInformation(
            "Channel created: {ChannelId} ({Visibility}) by {ClientId} with {MemberCount} initial members",
            create.ChannelId, visibility, connection.ClientId, create.InitialMembers.Count);
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

        string role = string.Equals(grant.Role, "admin", StringComparison.OrdinalIgnoreCase) ? "admin" : "member";
        await accessStore.GrantAccessAsync(grant.ChannelId, grant.ClientId, role, cancellationToken);

        await connection.SendAsync(
            new AckMessage(grant.ChannelId, Guid.Empty, 0),
            cancellationToken);
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
            await appStore.RegisterAsync(register.AppId, connection.ClientId, cancellationToken);
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
                new ErrorMessage("CHANNEL_DELETED", $"Channel '{channelId}' has been deleted"),
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
                        $"App '{allowedAppId ?? channelId}' is not on this server's allow-list"),
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
                new ErrorMessage("UNKNOWN_APP", $"Channel '{channelId}' has no valid app namespace"),
                cancellationToken);
            return false;
        }

        if (!await appStore.ExistsAsync(appId, cancellationToken))
        {
            await connection.SendAsync(
                new ErrorMessage("UNKNOWN_APP", $"App '{appId}' is not registered"),
                cancellationToken);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Enforces the publish-time per-app quotas: <c>max_payload_bytes</c> (estimated as the
    /// UTF-8 byte length of <c>payload</c> + <c>metadata</c>) and <c>publish_rate_per_minute</c>
    /// (token bucket keyed by <c>(appId, clientId)</c>). No-op when the channel's app is
    /// unregistered or has no quotas set. Sends an ERROR frame and returns false on rejection.
    /// </summary>
    private async Task<bool> EnforcePublishQuotasAsync(
        ClientConnection connection,
        PublishMessage publish,
        CancellationToken cancellationToken)
    {
        if (appStore is null)
            return true;

        string? appId = AppId.ExtractFromChannelId(publish.ChannelId);
        if (appId is null)
            return true;

        AppInfo? app = await appStore.GetAsync(appId, cancellationToken);
        if (app is null)
            return true; // unknown apps are either rejected upstream or grandfathered

        AppQuotas quotas = app.Quotas;

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

        if (rateLimiter is not null &&
            quotas.PublishRatePerMinute is int rate &&
            !string.IsNullOrEmpty(connection.ClientId))
        {
            if (!rateLimiter.TryAcquire(appId, connection.ClientId, rate))
            {
                await connection.SendAsync(
                    new ErrorMessage(
                        "RATE_LIMITED",
                        $"Publish rate exceeded ({rate}/min) for client on app '{appId}'",
                        publish.Event.Id,
                        publish.ChannelId),
                    cancellationToken);
                return false;
            }
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
        if (app is null || app.Quotas.MaxChannels is not int max)
            return true;

        int current = await accessStore.CountChannelsByAppAsync(appId, cancellationToken);
        if (current >= max)
        {
            await connection.SendAsync(
                new ErrorMessage(
                    "QUOTA_EXCEEDED",
                    $"App '{appId}' already owns {current} channels (max_channels = {max})"),
                cancellationToken);
            return false;
        }

        return true;
    }

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
