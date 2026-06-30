using System.Collections.Concurrent;
using VestaCore.Relay;

namespace VestaServer.Federation;

/// <summary>
/// Holds this relay's view of the discovery mesh — the set of signed
/// <see cref="ServerDescriptor"/> records learned from peers via gossip. Descriptors are keyed by
/// <see cref="ServerDescriptor.RelayPublicKey"/>; a newer <see cref="ServerDescriptor.IssuedAt"/>
/// supersedes an older one for the same key. Expired descriptors are evicted on read/merge.
/// </summary>
public interface IServerDirectory
{
    /// <summary>
    /// Merge a descriptor learned from a peer. The descriptor's signature must already be verified by
    /// the caller. Returns true if it was accepted (new or newer than what we held).
    /// </summary>
    bool Merge(ServerDescriptor descriptor);

    /// <summary>All currently-known, non-expired descriptors.</summary>
    IReadOnlyList<ServerDescriptor> All();

    /// <summary>Known, non-expired descriptors that advertise <paramref name="appId"/>.</summary>
    IReadOnlyList<ServerDescriptor> ForApp(string appId);
}

/// <summary>
/// In-memory <see cref="IServerDirectory"/> with last-write-wins by issuance time, TTL eviction,
/// and a <see cref="DiscoveryOptions.MaxPeers"/> cap. Sufficient for v1 — the directory is soft
/// state that the gossip service continuously refreshes, so it need not survive restarts.
/// </summary>
public sealed class InMemoryServerDirectory(TimeProvider timeProvider, int maxPeers) : IServerDirectory
{
    private readonly ConcurrentDictionary<string, ServerDescriptor> _descriptors = new(StringComparer.Ordinal);

    public bool Merge(ServerDescriptor descriptor)
    {
        if (string.IsNullOrEmpty(descriptor.RelayPublicKey))
        {
            return false;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (descriptor.IsExpired(now))
        {
            return false;
        }

        bool accepted = false;
        _descriptors.AddOrUpdate(
            descriptor.RelayPublicKey,
            _ =>
            {
                accepted = true;
                return descriptor;
            },
            (_, existing) =>
            {
                if (descriptor.IssuedAt > existing.IssuedAt)
                {
                    accepted = true;
                    return descriptor;
                }
                return existing;
            });

        EvictExpiredAndOverflow(now);
        return accepted;
    }

    public IReadOnlyList<ServerDescriptor> All()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        return [.. _descriptors.Values.Where(d => !d.IsExpired(now))];
    }

    public IReadOnlyList<ServerDescriptor> ForApp(string appId)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        return [.. _descriptors.Values
            .Where(d => !d.IsExpired(now) && d.Apps.Any(a => string.Equals(a.AppId, appId, StringComparison.Ordinal)))];
    }

    private void EvictExpiredAndOverflow(DateTimeOffset now)
    {
        foreach (KeyValuePair<string, ServerDescriptor> entry in _descriptors)
        {
            if (entry.Value.IsExpired(now))
            {
                _descriptors.TryRemove(entry);
            }
        }

        // Cap the directory: when over the limit, drop the oldest descriptors first.
        int overflow = _descriptors.Count - maxPeers;
        if (overflow > 0)
        {
            foreach (KeyValuePair<string, ServerDescriptor> entry in _descriptors
                .OrderBy(e => e.Value.IssuedAt)
                .Take(overflow))
            {
                _descriptors.TryRemove(entry);
            }
        }
    }
}
