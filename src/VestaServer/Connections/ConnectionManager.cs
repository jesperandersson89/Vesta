using System.Collections.Concurrent;
using VestaCore.Protocol;

namespace VestaServer.Connections;

/// <summary>
/// Manages all active WebSocket connections and channel subscriptions.
/// Responsible for broadcasting events to subscribed clients.
/// </summary>
public sealed class ConnectionManager
{
    private readonly ConcurrentDictionary<string, ClientConnection> _connections = new();

    /// <summary>
    /// Register a new client connection.
    /// </summary>
    public void Add(ClientConnection connection)
    {
        _connections[connection.ConnectionId] = connection;
    }

    /// <summary>
    /// Remove a client connection (on disconnect).
    /// </summary>
    public void Remove(ClientConnection connection)
    {
        _connections.TryRemove(connection.ConnectionId, out _);
    }

    /// <summary>
    /// Broadcast a protocol message to all clients subscribed to the given channel,
    /// optionally excluding one connection (the sender).
    /// </summary>
    public async Task BroadcastToChannelAsync(
        string channelId,
        ProtocolMessage message,
        string? excludeConnectionId = null,
        CancellationToken cancellationToken = default)
    {
        List<Task> sendTasks = [];

        foreach (ClientConnection connection in _connections.Values)
        {
            if (!connection.IsOpen)
                continue;

            if (connection.ConnectionId == excludeConnectionId)
                continue;

            if (!connection.Subscriptions.Contains(channelId))
                continue;

            sendTasks.Add(connection.SendAsync(message, cancellationToken));
        }

        await Task.WhenAll(sendTasks);
    }

    /// <summary>
    /// Get the count of active connections (for diagnostics).
    /// </summary>
    public int ActiveCount => _connections.Count;
}
