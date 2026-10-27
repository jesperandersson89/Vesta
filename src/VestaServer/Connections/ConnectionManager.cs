using System.Collections.Concurrent;
using Vesta;

namespace VestaServer.Connections;

/// <summary>
/// Manages all active WebSocket connections and channel subscriptions.
/// Responsible for broadcasting events to subscribed clients.
/// </summary>
public sealed class ConnectionManager
{
    private readonly ConcurrentDictionary<string, ClientConnection> _connections = new();
    private readonly ConcurrentDictionary<string, int> _peakJoined = new();

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

    /// <summary>Open connections that have joined the app (see <see cref="ClientConnection.JoinedApps"/>).</summary>
    public int CountJoined(string appId)
    {
        int count = 0;
        foreach (ClientConnection connection in _connections.Values)
        {
            if (connection.IsOpen && connection.JoinedApps.ContainsKey(appId))
                count++;
        }
        return count;
    }

    /// <summary>
    /// Records the app's joined-connection count after a connection joins, so a spike between two
    /// <see cref="TakePeakJoined"/> samples is not missed. Only called for apps with a <c>max_connections</c> quota.
    /// </summary>
    public void NoteJoined(string appId)
    {
        int current = CountJoined(appId);
        _peakJoined.AddOrUpdate(appId, current, (_, peak) => Math.Max(peak, current));
    }

    /// <summary>
    /// Highest joined-connection count seen since the previous call (never below the current count), then
    /// restarts the window at the current count. Used by the quota alert sweep.
    /// </summary>
    public int TakePeakJoined(string appId)
    {
        int current = CountJoined(appId);
        int peak = _peakJoined.TryGetValue(appId, out int recorded) ? Math.Max(recorded, current) : current;
        _peakJoined[appId] = current;
        return peak;
    }

    /// <summary>
    /// Open connections that proved ownership of <paramref name="clientId"/>, excluding one connection.
    /// Unproven connections are never counted, so nobody can fill a victim's slots by claiming their id.
    /// </summary>
    public int CountAuthenticated(string clientId, string excludeConnectionId)
    {
        int count = 0;
        foreach (ClientConnection connection in _connections.Values)
        {
            if (connection.IsOpen && connection.Authenticated &&
                connection.ConnectionId != excludeConnectionId &&
                string.Equals(connection.ClientId, clientId, StringComparison.Ordinal))
                count++;
        }
        return count;
    }

    /// <summary>
    /// Get the count of active connections (for diagnostics).
    /// </summary>
    public int ActiveCount => _connections.Count;

    /// <summary>
    /// Open connections currently subscribed to at least one channel of the app. Best-effort
    /// snapshot for the admin dashboard: <c>Subscriptions</c> is not synchronized, so a connection
    /// mutating its set during the scan is skipped.
    /// </summary>
    public int CountByApp(string appId)
    {
        int count = 0;
        foreach (ClientConnection connection in _connections.Values)
        {
            if (connection.IsOpen && IsSubscribedToApp(connection, appId))
                count++;
        }
        return count;
    }

    /// <summary>
    /// Disconnects every open connection subscribed to a channel of the app, after sending it
    /// <paramref name="notice"/>. Clients are expected to be single-app (the SDK binds one app per
    /// connection), so closing the whole socket is the intended semantics. Returns how many
    /// connections were disconnected. Callers should delete the app's channels first so a racing
    /// SUBSCRIBE is rejected.
    /// </summary>
    public async Task<int> DisconnectAppAsync(string appId, ProtocolMessage notice, string reason, TimeSpan grace)
    {
        List<Task> tasks = [];
        foreach (ClientConnection connection in _connections.Values)
        {
            if (connection.IsOpen && IsSubscribedToApp(connection, appId))
                tasks.Add(connection.DisconnectAsync(notice, reason, grace));
        }

        await Task.WhenAll(tasks);
        return tasks.Count;
    }

    // Subscriptions is not synchronized with the connection's own receive loop; retry on a torn read.
    private static bool IsSubscribedToApp(ClientConnection connection, string appId)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return connection.Subscriptions.Any(channel => AppId.ExtractFromChannelId(channel) == appId);
            }
            catch (InvalidOperationException)
            {
            }
        }
        return false;
    }
}
