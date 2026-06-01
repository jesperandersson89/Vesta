namespace VestaServer.Storage;

/// <summary>
/// Visibility/access model for a channel as stored in the access layer.
/// </summary>
public enum ChannelVisibility
{
  /// <summary>Public channel — implicitly created on first PUBLISH/SUBSCRIBE; anyone may read/write.</summary>
  Public,

  /// <summary>Private channel — only entries in <c>channel_access</c> may read/write.</summary>
  Private,
}

/// <summary>
/// Server-side abstraction for channel visibility and per-client access control.
/// Public channels do not need explicit access rows.
/// </summary>
public interface IChannelAccessStore
{
  /// <summary>
  /// Returns the visibility of a channel, or <c>null</c> if the channel does not exist.
  /// </summary>
  Task<ChannelVisibility?> GetVisibilityAsync(string channelId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Returns true if the given client is allowed to read or write to the channel.
  /// Public channels always allow. For private channels, the client must have an entry in <c>channel_access</c>.
  /// Implicit-create semantics: if the channel does not yet exist, returns true (it will be created as public).
  /// </summary>
  Task<bool> CanAccessAsync(string channelId, string? clientId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Returns true if the given client has the "admin" role on the channel.
  /// </summary>
  Task<bool> IsAdminAsync(string channelId, string clientId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Create a new channel with the specified visibility and members.
  /// The <paramref name="adminClientId"/> is recorded as admin; <paramref name="memberClientIds"/> are recorded as members.
  /// Throws <see cref="ChannelAlreadyExistsException"/> if the channel already exists.
  /// </summary>
  Task CreateChannelAsync(
      string channelId,
      ChannelVisibility visibility,
      string adminClientId,
      IReadOnlyList<string> memberClientIds,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Upsert an access grant. The caller is expected to have already verified admin rights.
  /// </summary>
  Task GrantAccessAsync(
      string channelId,
      string clientId,
      string role,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Count channels belonging to the given app namespace. A channel belongs to the app
  /// when its ID equals <paramref name="appId"/> or starts with <c>"{appId}/"</c>.
  /// Used by per-app quota enforcement (max_channels).
  /// </summary>
  Task<int> CountChannelsByAppAsync(string appId, CancellationToken cancellationToken = default);
}

public sealed class ChannelAlreadyExistsException(string channelId)
    : InvalidOperationException($"Channel '{channelId}' already exists.")
{
  public string ChannelId { get; } = channelId;
}
