using System.Net.WebSockets;
using System.Text.Json;
using VestaCore.Protocol;
using VestaCore.Serialization;

namespace VestaServer.Connections;

/// <summary>
/// Represents a single connected WebSocket client.
/// Handles low-level send/receive of protocol messages over the socket.
/// </summary>
public sealed class ClientConnection : IDisposable
{
    private readonly WebSocket _socket;
    private readonly JsonSerializerOptions _jsonOptions = VestaJsonOptions.Default;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string ConnectionId { get; } = Guid.NewGuid().ToString("N")[..12];
    public string? ClientId { get; set; }
    public HashSet<string> Subscriptions { get; } = [];

    public ClientConnection(WebSocket socket)
    {
        _socket = socket;
    }

    /// <summary>
    /// Sends a protocol message to this client. Thread-safe via semaphore.
    /// </summary>
    public async Task SendAsync(ProtocolMessage message, CancellationToken cancellationToken = default)
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

    /// <summary>
    /// Receives the next protocol message from this client.
    /// Returns null if the connection is closed.
    /// </summary>
    public async Task<ProtocolMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[8192];
        using MemoryStream stream = new();

        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(
                new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Respond to the close handshake
                if (_socket.State == WebSocketState.CloseReceived)
                {
                    await _socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
                return null;
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        stream.Position = 0;
        return await JsonSerializer.DeserializeAsync<ProtocolMessage>(stream, _jsonOptions, cancellationToken);
    }

    /// <summary>
    /// Gracefully closes the WebSocket connection.
    /// </summary>
    public async Task CloseAsync(string reason = "Server closing connection", CancellationToken cancellationToken = default)
    {
        if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cancellationToken);
        }
    }

    public bool IsOpen => _socket.State == WebSocketState.Open;

    public void Dispose()
    {
        _sendLock.Dispose();
        _socket.Dispose();
    }
}
