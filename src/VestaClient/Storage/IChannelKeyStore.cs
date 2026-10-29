namespace Vesta;

/// <summary>
/// Where a client keeps the channel keys it has received or created, plus (for admins) the X25519 public
/// keys of the members it has seen, so it can seal new epochs to them after a rotation. Keys are stored
/// in the clear: the device already holds them to read the channel, and local snapshots are plaintext too.
/// Pluggable like <see cref="IProjectionStore"/>: SQLite by default on .NET, Keychain / Keystore on mobile.
/// </summary>
public interface IChannelKeyStore
{
    /// <summary>Every epoch key known for the channel, any order.</summary>
    Task<IReadOnlyList<ChannelKey>> LoadKeysAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>Save (or overwrite) one epoch key.</summary>
    Task SaveKeyAsync(string channelId, ChannelKey key, CancellationToken cancellationToken = default);

    /// <summary>Members the admin has seen on the channel: client id → X25519 public key.</summary>
    Task<IReadOnlyDictionary<string, byte[]>> LoadMembersAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>Remember (or update) a member's X25519 public key.</summary>
    Task SaveMemberAsync(string channelId, string clientId, byte[] x25519PublicKey, CancellationToken cancellationToken = default);

    /// <summary>Forget a member (after revocation) so the next rotation is not sealed to it.</summary>
    Task RemoveMemberAsync(string channelId, string clientId, CancellationToken cancellationToken = default);
}

/// <summary>Volatile <see cref="IChannelKeyStore"/> for tests and throwaway sessions; keys are lost on exit.</summary>
public sealed class InMemoryChannelKeyStore : IChannelKeyStore
{
    private readonly Dictionary<string, Dictionary<int, ChannelKey>> _keys = [];
    private readonly Dictionary<string, Dictionary<string, byte[]>> _members = [];
    private readonly object _lock = new();

    public Task<IReadOnlyList<ChannelKey>> LoadKeysAsync(string channelId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<ChannelKey> keys = _keys.TryGetValue(channelId, out Dictionary<int, ChannelKey>? byEpoch)
                ? byEpoch.Values.ToList()
                : [];
            return Task.FromResult(keys);
        }
    }

    public Task SaveKeyAsync(string channelId, ChannelKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_lock)
        {
            if (!_keys.TryGetValue(channelId, out Dictionary<int, ChannelKey>? byEpoch))
                _keys[channelId] = byEpoch = [];
            byEpoch[key.Epoch] = key;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, byte[]>> LoadMembersAsync(string channelId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyDictionary<string, byte[]> members = _members.TryGetValue(channelId, out Dictionary<string, byte[]>? byClient)
                ? new Dictionary<string, byte[]>(byClient)
                : new Dictionary<string, byte[]>();
            return Task.FromResult(members);
        }
    }

    public Task SaveMemberAsync(string channelId, string clientId, byte[] x25519PublicKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(x25519PublicKey);
        lock (_lock)
        {
            if (!_members.TryGetValue(channelId, out Dictionary<string, byte[]>? byClient))
                _members[channelId] = byClient = [];
            byClient[clientId] = x25519PublicKey;
        }
        return Task.CompletedTask;
    }

    public Task RemoveMemberAsync(string channelId, string clientId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_members.TryGetValue(channelId, out Dictionary<string, byte[]>? byClient))
                byClient.Remove(clientId);
        }
        return Task.CompletedTask;
    }
}
