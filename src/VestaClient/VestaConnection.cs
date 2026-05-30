using System.Net.WebSockets;
using System.Text.Json;
using VestaCore.Events;
using VestaCore.Protocol;
using VestaCore.Serialization;

namespace VestaClient;

/// <summary>
/// C# client for connecting to a Vesta server via WebSocket.
/// Handles the protocol handshake, publishing events, subscribing to channels,
/// and receiving real-time event broadcasts.
/// </summary>
public sealed class VestaConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly JsonSerializerOptions _jsonOptions = VestaJsonOptions.Default;
    private readonly string _clientId;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoop;

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
    public bool IsConnected => _socket.State == WebSocketState.Open;

    public VestaConnection(string clientId)
    {
        _clientId = clientId;
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
        await _socket.ConnectAsync(serverUri, cancellationToken);

        // Send HELLO
        HelloMessage hello = new(
            ClientId: _clientId,
            Channels: channels.ToList(),
            LastSequences: lastSequences ?? new Dictionary<string, long>());

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
    }

    /// <summary>
    /// Publish an event to a channel.
    /// </summary>
    public async Task PublishAsync(VestaEvent evt, CancellationToken cancellationToken = default)
    {
        PublishMessage message = new(evt.ChannelId, evt);
        await SendAsync(message, cancellationToken);
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
    /// Gracefully disconnect from the server.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _receiveCts?.Cancel();

        if (_socket.State == WebSocketState.Open)
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", cancellationToken);
        }

        if (_receiveLoop is not null)
        {
            try { await _receiveLoop; } catch (OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _receiveCts?.Cancel();

        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None);
            }
            catch { /* best effort */ }
        }

        if (_receiveLoop is not null)
        {
            try { await _receiveLoop; } catch { /* best effort */ }
        }

        _receiveCts?.Dispose();
        _sendLock.Dispose();
        _socket.Dispose();
    }

    private async Task SendAsync(ProtocolMessage message, CancellationToken cancellationToken)
    {
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
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
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
        }
    }

    private void DispatchMessage(ProtocolMessage message)
    {
        switch (message)
        {
            case EventMessage evt:
                OnEvent?.Invoke(evt);
                break;
            case EventsBatchMessage batch:
                OnEventsBatch?.Invoke(batch);
                break;
            case AckMessage ack:
                OnAck?.Invoke(ack);
                break;
            case ErrorMessage error:
                OnError?.Invoke(error);
                break;
        }
    }
}
