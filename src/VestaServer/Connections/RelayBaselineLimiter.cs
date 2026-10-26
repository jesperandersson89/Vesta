using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace VestaServer.Connections;

/// <summary>
/// Relay-wide baseline limits applied when an app sets no quota of its own (see
/// <c>Protocol:Defaults</c>). A value of 0 disables the limit.
/// </summary>
public sealed class ProtocolLimitDefaults
{
    /// <summary>Publishes per minute per (app, client) when the app has no <c>publish_rate_per_minute</c> quota.</summary>
    public int PublishRatePerMinute { get; set; } = 600;

    /// <summary>Channels one client may publish to per app (in-memory, per relay process).</summary>
    public int MaxChannelsPerClientPerApp { get; set; } = 100;

    /// <summary>Channels one connection may subscribe to.</summary>
    public int MaxSubscriptionsPerConnection { get; set; } = 100;

    /// <summary>
    /// Concurrent WebSocket connections per remote IP. Off by default: behind a proxy every client
    /// shares the proxy's IP unless <c>ForwardedHeaders:Enabled</c> is set.
    /// </summary>
    public int MaxConnectionsPerIp { get; set; }

    /// <summary>WebSocket connection attempts per minute per remote IP. Off by default, same caveat as <see cref="MaxConnectionsPerIp"/>.</summary>
    public int ConnectionAttemptsPerMinutePerIp { get; set; }
}

/// <summary>
/// In-memory, per-process tracker behind the <c>Protocol:Defaults</c> limits that need state beyond
/// a single connection: per-IP connection counts / attempt rate and per-client channel fan-out.
/// </summary>
public sealed class RelayBaselineLimiter(IOptions<ProtocolOptions> options, TimeProvider clock)
{
    private static readonly TimeSpan IdleEviction = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, IpState> _ips = new();
    private readonly ConcurrentDictionary<(string AppId, string ClientId), ClientChannels> _clients = new();
    private int _calls;

    private ProtocolLimitDefaults Defaults => options.Value.Defaults;

    /// <summary>
    /// Admits a new WebSocket connection from <paramref name="ip"/>. Every <c>true</c> result must be
    /// paired with <see cref="ExitConnection"/>.
    /// </summary>
    public bool TryEnterConnection(string ip)
    {
        int maxActive = Defaults.MaxConnectionsPerIp;
        int attemptsPerMinute = Defaults.ConnectionAttemptsPerMinutePerIp;
        if (maxActive <= 0 && attemptsPerMinute <= 0)
            return true;

        DateTimeOffset now = clock.GetUtcNow();
        MaybeSweep(now);

        IpState state = _ips.GetOrAdd(ip, _ => new IpState(attemptsPerMinute, now));
        lock (state)
        {
            if (attemptsPerMinute > 0)
            {
                double elapsed = (now - state.LastRefill).TotalSeconds;
                if (elapsed > 0)
                {
                    state.Tokens = Math.Min(attemptsPerMinute, state.Tokens + elapsed * (attemptsPerMinute / 60.0));
                    state.LastRefill = now;
                }
            }

            if (maxActive > 0 && state.Active >= maxActive)
                return false;

            if (attemptsPerMinute > 0)
            {
                if (state.Tokens < 1.0)
                    return false;
                state.Tokens -= 1.0;
            }

            state.Active++;
            state.LastSeen = now;
            return true;
        }
    }

    public void ExitConnection(string ip)
    {
        if (!_ips.TryGetValue(ip, out IpState? state))
            return;

        lock (state)
        {
            if (state.Active > 0)
                state.Active--;
            state.LastSeen = clock.GetUtcNow();
        }
    }

    /// <summary>
    /// Records that <paramref name="clientId"/> publishes to <paramref name="channelId"/> under
    /// <paramref name="appId"/>. Returns false when that would exceed
    /// <see cref="ProtocolLimitDefaults.MaxChannelsPerClientPerApp"/>.
    /// </summary>
    public bool TryTrackChannel(string appId, string clientId, string channelId)
    {
        int max = Defaults.MaxChannelsPerClientPerApp;
        if (max <= 0)
            return true;

        DateTimeOffset now = clock.GetUtcNow();
        MaybeSweep(now);

        ClientChannels channels = _clients.GetOrAdd((appId, clientId), _ => new ClientChannels());
        lock (channels)
        {
            channels.LastSeen = now;
            if (channels.Ids.Contains(channelId))
                return true;
            if (channels.Ids.Count >= max)
                return false;

            channels.Ids.Add(channelId);
            return true;
        }
    }

    // Opportunistic cleanup so identity/IP churn cannot grow the maps without bound.
    private void MaybeSweep(DateTimeOffset now)
    {
        if (Interlocked.Increment(ref _calls) % 1024 != 0)
            return;

        foreach (KeyValuePair<string, IpState> entry in _ips)
        {
            lock (entry.Value)
            {
                if (entry.Value.Active == 0 && now - entry.Value.LastSeen > IdleEviction)
                    _ips.TryRemove(entry);
            }
        }

        foreach (KeyValuePair<(string AppId, string ClientId), ClientChannels> entry in _clients)
        {
            lock (entry.Value)
            {
                if (now - entry.Value.LastSeen > IdleEviction)
                    _clients.TryRemove(entry);
            }
        }
    }

    private sealed class IpState(int attemptsPerMinute, DateTimeOffset now)
    {
        public int Active;
        public double Tokens = attemptsPerMinute;
        public DateTimeOffset LastRefill = now;
        public DateTimeOffset LastSeen = now;
    }

    private sealed class ClientChannels
    {
        public readonly HashSet<string> Ids = [];
        public DateTimeOffset LastSeen;
    }
}
