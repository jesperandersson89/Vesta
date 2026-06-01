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

  public Task<bool> CanAccessAsync(string channelId, string? clientId, CancellationToken cancellationToken = default)
  {
    if (!_channels.TryGetValue(channelId, out ChannelInfo? info))
    {
      // Implicit-create: unknown channel will become public on first append.
      return Task.FromResult(true);
    }
    if (info.Visibility == ChannelVisibility.Public)
    {
      return Task.FromResult(true);
    }
    if (clientId is null)
    {
      return Task.FromResult(false);
    }
    return Task.FromResult(info.Members.ContainsKey(clientId));
  }

  public Task<bool> IsAdminAsync(string channelId, string clientId, CancellationToken cancellationToken = default)
  {
    if (_channels.TryGetValue(channelId, out ChannelInfo? info) &&
        info.Members.TryGetValue(clientId, out string? role))
    {
      return Task.FromResult(role == "admin");
    }
    return Task.FromResult(false);
  }

  public Task CreateChannelAsync(
      string channelId,
      ChannelVisibility visibility,
      string adminClientId,
      IReadOnlyList<string> memberClientIds,
      CancellationToken cancellationToken = default)
  {
    ChannelInfo info = new(visibility);
    info.Members[adminClientId] = "admin";
    foreach (string member in memberClientIds)
    {
      if (member == adminClientId) continue;
      info.Members.TryAdd(member, "member");
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

  /// <summary>
  /// Records an implicit channel creation (public) when an event is appended to a previously unknown channel.
  /// Idempotent.
  /// </summary>
  public void RecordImplicitChannel(string channelId)
  {
    _channels.TryAdd(channelId, new ChannelInfo(ChannelVisibility.Public));
  }

  private sealed class ChannelInfo(ChannelVisibility visibility)
  {
    public ChannelVisibility Visibility { get; } = visibility;
    public ConcurrentDictionary<string, string> Members { get; } = new();
  }
}
