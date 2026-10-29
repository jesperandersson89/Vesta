using System.Collections.Concurrent;

namespace VestaServer.Storage;

/// <summary>
/// In-memory implementation of <see cref="IChannelAccessStore"/>.
/// Used in tests and the in-memory dev mode.
/// </summary>
public sealed class InMemoryChannelAccessStore : IChannelAccessStore
{
  private readonly ConcurrentDictionary<string, ChannelInfo> _channels = new();

  public Task<ChannelVisibility?> GetVisibilityAsync(string channelId, CancellationToken cancellationToken = default)
  {
    if (_channels.TryGetValue(channelId, out ChannelInfo? info))
    {
      return Task.FromResult<ChannelVisibility?>(info.Visibility);
    }
    return Task.FromResult<ChannelVisibility?>(null);
  }

  public Task<bool> IsEncryptedAsync(string channelId, CancellationToken cancellationToken = default)
  {
    return Task.FromResult(_channels.TryGetValue(channelId, out ChannelInfo? info) && info.Encrypted);
  }

  public Task<bool> CanAccessAsync(string channelId, string? clientId, CancellationToken cancellationToken = default)
  {
    if (!_channels.TryGetValue(channelId, out ChannelInfo? info))
    {
      // Implicit-create: unknown channel will become public on first append.
      return Task.FromResult(true);
    }
    if (info.Visibility is ChannelVisibility.Public or ChannelVisibility.Broadcast)
    {
      return Task.FromResult(true);
    }
    if (clientId is null)
    {
      return Task.FromResult(false);
    }
    return Task.FromResult(info.Members.ContainsKey(clientId));
  }

  public Task<bool> CanPublishAsync(string channelId, string? clientId, CancellationToken cancellationToken = default)
  {
    if (!_channels.TryGetValue(channelId, out ChannelInfo? info) || info.Visibility == ChannelVisibility.Public)
    {
      return Task.FromResult(true);
    }
    if (clientId is null)
    {
      return Task.FromResult(false);
    }
    return Task.FromResult(info.Members.TryGetValue(clientId, out string? role) && ChannelRoles.CanWrite(role));
  }

  public Task<bool> IsAdminAsync(string channelId, string clientId, CancellationToken cancellationToken = default)
  {
    if (_channels.TryGetValue(channelId, out ChannelInfo? info) &&
        info.Members.TryGetValue(clientId, out string? role))
    {
      return Task.FromResult(role == ChannelRoles.Admin);
    }
    return Task.FromResult(false);
  }

  public Task CreateChannelAsync(
      string channelId,
      ChannelVisibility visibility,
      string adminClientId,
      IReadOnlyList<string> memberClientIds,
      bool encrypted = false,
      CancellationToken cancellationToken = default)
  {
    ChannelInfo info = new(visibility, encrypted);
    info.Members[adminClientId] = ChannelRoles.Admin;
    foreach (string member in memberClientIds)
    {
      if (member == adminClientId) continue;
      info.Members.TryAdd(member, ChannelRoles.Member);
    }

    if (!_channels.TryAdd(channelId, info))
    {
      throw new ChannelAlreadyExistsException(channelId);
    }
    return Task.CompletedTask;
  }

  public Task GrantAccessAsync(
      string channelId,
      string clientId,
      string role,
      CancellationToken cancellationToken = default)
  {
    ChannelInfo info = _channels.GetOrAdd(channelId, _ => new ChannelInfo(ChannelVisibility.Private));
    info.Members[clientId] = role;
    return Task.CompletedTask;
  }

  public Task<bool> RevokeAccessAsync(
      string channelId,
      string clientId,
      CancellationToken cancellationToken = default)
  {
    bool removed = _channels.TryGetValue(channelId, out ChannelInfo? info) && info.Members.TryRemove(clientId, out _);
    return Task.FromResult(removed);
  }

  public Task<int> CountChannelsByAppAsync(string appId, CancellationToken cancellationToken = default)
  {
    string prefix = appId + "/";
    int count = 0;
    foreach (string id in _channels.Keys)
    {
      if (id == appId || id.StartsWith(prefix, StringComparison.Ordinal))
        count++;
    }
    return Task.FromResult(count);
  }

  public Task<int> CountEncryptedChannelsByAppAsync(string appId, CancellationToken cancellationToken = default)
  {
    string prefix = appId + "/";
    int count = 0;
    foreach (KeyValuePair<string, ChannelInfo> kv in _channels)
    {
      if (kv.Value.Encrypted && (kv.Key == appId || kv.Key.StartsWith(prefix, StringComparison.Ordinal)))
        count++;
    }
    return Task.FromResult(count);
  }

  public Task<bool> IsDeletedAsync(string channelId, CancellationToken cancellationToken = default)
  {
    if (_channels.TryGetValue(channelId, out ChannelInfo? info))
      return Task.FromResult(info.DeletedAt is not null);
    return Task.FromResult(false);
  }

  public Task<bool> DeleteChannelAsync(string channelId, CancellationToken cancellationToken = default)
  {
    if (!_channels.TryGetValue(channelId, out ChannelInfo? info))
      return Task.FromResult(false);
    info.DeletedAt ??= DateTimeOffset.UtcNow;
    return Task.FromResult(true);
  }

  public Task RecordImplicitChannelAsync(string channelId, CancellationToken cancellationToken = default)
  {
    RecordImplicitChannel(channelId);
    return Task.CompletedTask;
  }

  public Task<IReadOnlyList<ChannelSummary>> ListChannelsAsync(
      string? appPrefix,
      bool includeDeleted,
      CancellationToken cancellationToken = default)
  {
    string? prefix = appPrefix is null ? null : appPrefix + "/";
    List<ChannelSummary> result = [];
    foreach (KeyValuePair<string, ChannelInfo> kv in _channels)
    {
      if (!includeDeleted && kv.Value.DeletedAt is not null) continue;
      if (appPrefix is not null && kv.Key != appPrefix && !kv.Key.StartsWith(prefix!, StringComparison.Ordinal)) continue;
      result.Add(new ChannelSummary(kv.Key, kv.Value.Visibility, kv.Value.CreatedAt, kv.Value.DeletedAt, kv.Value.Encrypted));
    }
    return Task.FromResult<IReadOnlyList<ChannelSummary>>(result);
  }

  public Task<IReadOnlyList<ChannelMember>> ListMembersAsync(
      string channelId,
      CancellationToken cancellationToken = default)
  {
    if (!_channels.TryGetValue(channelId, out ChannelInfo? info))
      return Task.FromResult<IReadOnlyList<ChannelMember>>([]);
    List<ChannelMember> members = [];
    foreach (KeyValuePair<string, string> kv in info.Members)
      members.Add(new ChannelMember(kv.Key, kv.Value));
    return Task.FromResult<IReadOnlyList<ChannelMember>>(members);
  }

  public Task<int> DeleteChannelsByAppAsync(string appId, DateTimeOffset deletedAt, CancellationToken cancellationToken = default)
  {
    string prefix = appId + "/";
    int count = 0;
    foreach (KeyValuePair<string, ChannelInfo> kv in _channels)
    {
      if (kv.Key != appId && !kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
      if (kv.Value.DeletedAt is not null) continue;
      kv.Value.DeletedAt = deletedAt;
      count++;
    }
    return Task.FromResult(count);
  }

  public Task<int> RestoreChannelsByAppAsync(string appId, DateTimeOffset deletedAt, CancellationToken cancellationToken = default)
  {
    string prefix = appId + "/";
    int count = 0;
    foreach (KeyValuePair<string, ChannelInfo> kv in _channels)
    {
      if (kv.Key != appId && !kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
      if (kv.Value.DeletedAt != deletedAt) continue;
      kv.Value.DeletedAt = null;
      count++;
    }
    return Task.FromResult(count);
  }

  /// <summary>
  /// Records an implicit channel creation (public) when an event is appended to a previously unknown channel.
  /// Idempotent.
  /// </summary>
  public void RecordImplicitChannel(string channelId)
  {
    _channels.TryAdd(channelId, new ChannelInfo(ChannelVisibility.Public));
  }

  private sealed class ChannelInfo(ChannelVisibility visibility, bool encrypted = false)
  {
    public ChannelVisibility Visibility { get; } = visibility;
    public bool Encrypted { get; } = encrypted;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public ConcurrentDictionary<string, string> Members { get; } = new();
    public DateTimeOffset? DeletedAt { get; set; }
  }
}
