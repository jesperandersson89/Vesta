using VestaCore.Events;

namespace Presence.CLI;

// ─── Event Schema ────────────────────────────────────────────────────────────
//
// Channel: "presence/{app-name}"
//
// Event types:
//   app.presence.heartbeat  { username: string, status: "online", ttlSeconds: int }
//   app.presence.bye        { username: string }
//
// Conflict resolution: LWW per clientId — latest heartbeat wins.
// A user is considered offline if:
//   - A "bye" event is received from them, OR
//   - No heartbeat has been received within their declared ttlSeconds
//
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Represents the current presence state for a single user.
/// </summary>
public sealed class PresenceEntry
{
    public required string ClientId { get; init; }
    public required string Username { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public int TtlSeconds { get; set; }
    public bool Online { get; set; }

    /// <summary>
    /// True if the TTL has elapsed since the last heartbeat.
    /// </summary>
    public bool IsExpired(DateTimeOffset now) =>
        Online && (now - LastSeen).TotalSeconds > TtlSeconds;
}

/// <summary>
/// Projection that maintains a live view of which users are currently online.
/// Thread-safe: all mutations are lock-protected.
/// </summary>
public sealed class PresenceState
{
    private readonly Dictionary<string, PresenceEntry> _users = new();
    private readonly object _lock = new();

    /// <summary>
    /// All known users, online first then by last seen descending.
    /// </summary>
    public IReadOnlyList<PresenceEntry> AllUsers
    {
        get
        {
            lock (_lock)
            {
                return _users.Values
                    .OrderByDescending(e => e.Online)
                    .ThenByDescending(e => e.LastSeen)
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Apply a heartbeat or bye event received from the server.
    /// </summary>
    public void Apply(VestaEvent evt)
    {
        switch (evt.EventType)
        {
            case "app.presence.heartbeat":
                ApplyHeartbeat(evt);
                break;
            case "app.presence.bye":
                ApplyBye(evt);
                break;
        }
    }

    /// <summary>
    /// Mark any users whose TTL has expired as offline.
    /// Returns true if any state changed (so caller can redraw).
    /// </summary>
    public bool ExpireStaleUsers(DateTimeOffset now)
    {
        bool changed = false;
        lock (_lock)
        {
            foreach (PresenceEntry entry in _users.Values)
            {
                if (entry.IsExpired(now))
                {
                    entry.Online = false;
                    changed = true;
                }
            }
        }
        return changed;
    }

    private void ApplyHeartbeat(VestaEvent evt)
    {
        string username = evt.Payload.TryGetProperty("username", out System.Text.Json.JsonElement u)
            ? u.GetString() ?? evt.ClientId[..8]
            : evt.ClientId[..8];

        int ttl = evt.Metadata is System.Text.Json.JsonElement md
            && md.ValueKind == System.Text.Json.JsonValueKind.Object
            && md.TryGetProperty("ttlSeconds", out System.Text.Json.JsonElement t)
            && t.ValueKind == System.Text.Json.JsonValueKind.Number
            ? t.GetInt32()
            : 30;

        lock (_lock)
        {
            if (!_users.TryGetValue(evt.ClientId, out PresenceEntry? entry))
            {
                entry = new PresenceEntry { ClientId = evt.ClientId, Username = username, TtlSeconds = ttl, Online = true, LastSeen = evt.Timestamp };
                _users[evt.ClientId] = entry;
            }

            // Only apply if this is newer than what we have
            if (evt.Timestamp >= entry.LastSeen)
            {
                entry.Username = username;
                entry.TtlSeconds = ttl;
                entry.LastSeen = evt.Timestamp;
                entry.Online = true;
            }
        }
    }

    private void ApplyBye(VestaEvent evt)
    {
        lock (_lock)
        {
            if (_users.TryGetValue(evt.ClientId, out PresenceEntry? entry))
            {
                entry.Online = false;
            }
        }
    }
}
