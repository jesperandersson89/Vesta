using System.Net.WebSockets;
using System.Text.Json;
using VestaClient.Storage;
using VestaCore.Events;
using VestaCore.Identity;
using VestaCore.Protocol;
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
    private readonly Dictionary<Guid, VestaEvent> _pendingPublishes = new();
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoop;
    private Uri? _serverUri;
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
    /// Fired when the connection is closed.
    /// </summary>
    public event Action<string>? OnDisconnected;

    /// <summary>
    /// Fired when a reconnection succeeds.
    /// </summary>
    public event Action? OnReconnected;

    /// <summary>
    /// The server ID returned in the WELCOME message.
    /// </summary>
    public string? ServerId { get; private set; }

    /// <summary>
    /// The channels confirmed by the server in the WELCOME message.
    /// </summary>
    public IReadOnlyList<string> Channels { get; private set; } = [];

    /// <summary>
    /// Whether the connection is currently open.
    /// </summary>
    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public VestaConnection(string clientId, IClientEventStore? localStore = null, VestaIdentity? identity = null)
    {
        _clientId = clientId;
        _localStore = localStore;
        _identity = identity;
    }

    /// <summary>
    /// Connect to a Vesta server and perform the HELLO/WELCOME handshake.
    /// </summary>
    public async Task ConnectAsync(
        Uri serverUri,
        IReadOnlyList<string> channels,
        IReadOnlyDictionary<string, long>? lastSequences = null,
        CancellationToken cancellationToken = default)
    {
        // Store for reconnection
        _serverUri = serverUri;
        _channels = channels;

        await ConnectInternalAsync(lastSequences, cancellationToken);
    }

    /// <summary>
    /// Reconnect to the server using the same URI and channels from the original ConnectAsync call.
    /// Uses stored channel positions from the local store for smart catch-up.
    /// Returns true if reconnection succeeded, false if it failed.
    /// </summary>
    public async Task<bool> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_serverUri is null || _channels is null)
        {
            throw new InvalidOperationException("Cannot reconnect — ConnectAsync has not been called yet.");
        }

        // Clean up old socket and receive loop
        await CleanupAsync();

        try
        {
            await ConnectInternalAsync(lastSequences: null, cancellationToken);
            OnReconnected?.Invoke();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ConnectInternalAsync(
        IReadOnlyDictionary<string, long>? lastSequences,
        CancellationToken cancellationToken)
    {
        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(_serverUri!, cancellationToken);

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
                OnError?.Invoke(error);
            }
            else if (response is null)
            {
                throw new InvalidOperationException("Connection closed during handshake");
            }
        }

        // Start the background receive loop
        _receiveCts = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));

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
                OnError?.Invoke(error);
                break;
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
