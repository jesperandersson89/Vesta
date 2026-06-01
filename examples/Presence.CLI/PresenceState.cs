using System.Text.Json;
using VestaCore.Events;
using VestaCore.Projections;

namespace Presence.CLI;

// ─── Event Schema ────────────────────────────────────────────────────────────
//
// Channel: "presence/{app-name}"
//
// Event types:
//   app.presence.heartbeat  payload: { username }   metadata: { ttlSeconds }
//   app.presence.bye        payload: { username }
//
// Conflict resolution: LWW per clientId via VestaCore.Projections.LwwMap.
//   - heartbeat → Set(clientId, snapshot)
//   - bye       → Remove(clientId)  (user disappears from the list)
//   - "online"  → derived view: now - lastSeen <= ttlSeconds.
//
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Presence info for a single user, computed on demand from the LWW map.</summary>
public sealed record PresenceEntry(
    string ClientId,
    string Username,
    DateTimeOffset LastSeen,
    int TtlSeconds,
    bool Online);

/// <summary>
/// Projects the presence event stream into a live list of users by composing
/// <see cref="LwwMap{TKey, TValue}"/> with a time-based "is online" derivation.
/// </summary>
public sealed class PresenceState
{
    private const int DefaultTtlSeconds = 30;

    private readonly LwwMap<string, Heartbeat> _heartbeats = new(Project);
    private readonly object _displayLock = new();
    private int _lastOnlineCount;

    /// <summary>Apply a server-sequenced event.</summary>
    public void Apply(SequencedEvent sequenced) => _heartbeats.Apply(sequenced);

    /// <summary>Apply a locally-authored event (e.g. own heartbeat before round-trip).</summary>
    public void Apply(VestaEvent evt) => _heartbeats.ApplyLocal(evt);

    /// <summary>All known users, online first then by last-seen descending.</summary>
    public IReadOnlyList<PresenceEntry> AllUsers
    {
        get
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return _heartbeats.State
                .Select(kv => new PresenceEntry(
                    ClientId: kv.Key,
                    Username: kv.Value.Username,
                    LastSeen: kv.Value.LastSeen,
                    TtlSeconds: kv.Value.TtlSeconds,
                    Online: (now - kv.Value.LastSeen).TotalSeconds <= kv.Value.TtlSeconds))
                .OrderByDescending(e => e.Online)
                .ThenByDescending(e => e.LastSeen)
                .ToList();
        }
    }

    /// <summary>
    /// Returns true if the count of online users has changed since the previous call
    /// (i.e. someone just crossed their TTL boundary). Callers use the return value
    /// to decide whether to redraw the display.
    /// </summary>
    public bool ExpireStaleUsers(DateTimeOffset now)
    {
        lock (_displayLock)
        {
            int currentOnline = 0;
            foreach (KeyValuePair<string, Heartbeat> kv in _heartbeats.State)
            {
                if ((now - kv.Value.LastSeen).TotalSeconds <= kv.Value.TtlSeconds)
                {
                    currentOnline++;
                }
            }

            if (currentOnline != _lastOnlineCount)
            {
                _lastOnlineCount = currentOnline;
                return true;
            }
            return false;
        }
    }

    // ── Projection ───────────────────────────────────────────────────────────

    private sealed record Heartbeat(string Username, DateTimeOffset LastSeen, int TtlSeconds);

    private static LwwMapUpdate<string, Heartbeat>? Project(VestaEvent evt) => evt.EventType switch
    {
        "app.presence.heartbeat" => LwwMapUpdate<string, Heartbeat>.Set(
            evt.ClientId,
            new Heartbeat(
                Username: evt.Payload.TryGetProperty("username", out JsonElement u)
                    ? u.GetString() ?? evt.ClientId[..8]
                    : evt.ClientId[..8],
                LastSeen: evt.Timestamp,
                TtlSeconds: ReadTtlSeconds(evt))),
        "app.presence.bye" => LwwMapUpdate<string, Heartbeat>.Remove(evt.ClientId),
        _ => null
    };

    private static int ReadTtlSeconds(VestaEvent evt) =>
        evt.Metadata is JsonElement md
        && md.ValueKind == JsonValueKind.Object
        && md.TryGetProperty("ttlSeconds", out JsonElement t)
        && t.ValueKind == JsonValueKind.Number
            ? t.GetInt32()
            : DefaultTtlSeconds;
}
