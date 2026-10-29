namespace VestaServer.Storage;

/// <summary>
/// Visibility/access model for a channel as stored in the access layer.
/// </summary>
public enum ChannelVisibility
{
  /// <summary>Public channel — implicitly created on first PUBLISH/SUBSCRIBE; anyone may read/write.</summary>
  Public,

  /// <summary>Private channel — only entries in <c>channel_access</c> may read; members and admins may write.</summary>
  Private,

  /// <summary>
  /// Broadcast channel — anyone may subscribe/fetch, but only <c>channel_access</c> entries with a
  /// writing role (member/admin) may publish. Never implicit: it is created with CREATE_CHANNEL.
  /// </summary>
  Broadcast,
}

/// <summary>Wire names for <see cref="ChannelVisibility"/> (the <c>channels.visibility</c> text column).</summary>
public static class ChannelVisibilityNames
{
  public static ChannelVisibility Parse(string? value) => value?.ToLowerInvariant() switch
  {
    "private" => ChannelVisibility.Private,
    "broadcast" => ChannelVisibility.Broadcast,
    _ => ChannelVisibility.Public,
  };

  public static string Format(ChannelVisibility visibility) => visibility switch
  {
    ChannelVisibility.Private => "private",
    ChannelVisibility.Broadcast => "broadcast",
    _ => "public",
  };
}

/// <summary>Roles stored in <c>channel_access.role</c>.</summary>
public static class ChannelRoles
{
  public const string Admin = "admin";
  public const string Member = "member";

  /// <summary>May read a private channel but never publish to it.</summary>
  public const string Reader = "reader";

  /// <summary>Maps a wire role to a stored role; anything unrecognised becomes <see cref="Member"/>.</summary>
  public static string Normalize(string? role) => role?.ToLowerInvariant() switch
  {
    "admin" => Admin,
    "reader" => Reader,
    _ => Member,
  };

  public static bool CanWrite(string? role) => role is Admin or Member;
}

/// <summary>Summary row used by admin listing endpoints.</summary>
public sealed record ChannelSummary(
    string Id,
    ChannelVisibility Visibility,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeletedAt,
    bool Encrypted = false);

/// <summary>A (clientId, role) pair on a private channel.</summary>
public sealed record ChannelMember(string ClientId, string Role);

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
  /// Returns true when the channel exists and was created with <c>encrypted: true</c>. Channels that
  /// do not exist (and implicitly created ones) are never encrypted.
  /// </summary>
  Task<bool> IsEncryptedAsync(string channelId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Returns true if the given client is allowed to read (subscribe / fetch) the channel.
  /// Public and broadcast channels always allow. For private channels, the client must have an entry
  /// (any role, including <c>reader</c>) in <c>channel_access</c>.
  /// Implicit-create semantics: if the channel does not yet exist, returns true (it will be created as public).
  /// </summary>
  Task<bool> CanAccessAsync(string channelId, string? clientId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Returns true if the given client is allowed to publish to the channel. Public channels (and
  /// channels that do not exist yet) allow everyone. Private and broadcast channels require an entry
  /// with a writing role (<c>member</c> or <c>admin</c>); <c>reader</c> entries may not publish.
  /// </summary>
  Task<bool> CanPublishAsync(string channelId, string? clientId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Returns true if the given client has the "admin" role on the channel.
  /// </summary>
  Task<bool> IsAdminAsync(string channelId, string clientId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Create a new channel with the specified visibility and members.
  /// The <paramref name="adminClientId"/> is recorded as admin; <paramref name="memberClientIds"/> are recorded as members.
  /// <paramref name="encrypted"/> marks the channel end-to-end encrypted (the caller has already required <c>private</c>).
  /// Throws <see cref="ChannelAlreadyExistsException"/> if the channel already exists.
  /// </summary>
  Task CreateChannelAsync(
      string channelId,
      ChannelVisibility visibility,
      string adminClientId,
      IReadOnlyList<string> memberClientIds,
      bool encrypted = false,
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
  /// Remove a client's access entry. Idempotent; returns true when an entry was removed.
  /// The caller is expected to have already verified admin rights and that the target is not the caller.
  /// </summary>
  Task<bool> RevokeAccessAsync(
      string channelId,
      string clientId,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Count channels belonging to the given app namespace. A channel belongs to the app
  /// when its ID equals <paramref name="appId"/> or starts with <c>"{appId}/"</c>.
  /// Used by per-app quota enforcement (max_channels).
  /// </summary>
  Task<int> CountChannelsByAppAsync(string appId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Count encrypted channels belonging to the given app namespace (same membership rule as
  /// <see cref="CountChannelsByAppAsync"/>). Used by <c>max_encrypted_channels</c> and the admin API.
  /// </summary>
  Task<int> CountEncryptedChannelsByAppAsync(string appId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Returns true if the channel exists and has been soft-deleted (tombstoned).
  /// Soft-deleted channels reject PUBLISH, SUBSCRIBE, FETCH, and CREATE_CHANNEL
  /// with the <c>CHANNEL_DELETED</c> error. Returns false for channels that
  /// do not exist at all or are still active.
  /// </summary>
  Task<bool> IsDeletedAsync(string channelId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Soft-delete a channel by stamping a deletion tombstone. Idempotent: deleting
  /// an already-deleted channel succeeds without changing the original
  /// <c>deleted_at</c> timestamp. Returns false if the channel does not exist
  /// (and was therefore not deleted). Existing events are retained until a
  /// future hard-delete sweep (see TODO #12b).
  /// </summary>
  Task<bool> DeleteChannelAsync(string channelId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Records an implicit-create for a channel that was just touched by a PUBLISH
  /// or SUBSCRIBE. Idempotent. The Postgres impl is a no-op because the event
  /// store creates the <c>channels</c> row in the same transaction as the
  /// event insert; the in-memory impl needs the explicit hook so the channel
  /// is visible to <see cref="IsDeletedAsync"/>, <see cref="DeleteChannelAsync"/>,
  /// and <see cref="CountChannelsByAppAsync"/>.
  /// </summary>
  Task RecordImplicitChannelAsync(string channelId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Admin-only: list channels. When <paramref name="appPrefix"/> is non-null,
  /// only channels whose id equals it or starts with <c>"{prefix}/"</c> are
  /// returned. When <paramref name="includeDeleted"/> is false, soft-deleted
  /// channels are filtered out. Order is unspecified; callers sort as needed.
  /// </summary>
  Task<IReadOnlyList<ChannelSummary>> ListChannelsAsync(
      string? appPrefix,
      bool includeDeleted,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Admin-only: list every (clientId, role) entry for the channel. Empty for
  /// public channels with no explicit grants and for channels that do not exist.
  /// </summary>
  Task<IReadOnlyList<ChannelMember>> ListMembersAsync(
      string channelId,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Admin-only: soft-delete every still-active channel belonging to the app, stamping them with
  /// <paramref name="deletedAt"/> (the app's own tombstone) so <see cref="RestoreChannelsByAppAsync"/>
  /// can undo exactly this cascade. Returns the number of channels newly deleted.
  /// </summary>
  Task<int> DeleteChannelsByAppAsync(string appId, DateTimeOffset deletedAt, CancellationToken cancellationToken = default);

  /// <summary>
  /// Admin-only: clear the tombstone on channels of the app whose <c>deleted_at</c> equals
  /// <paramref name="deletedAt"/>; channels deleted independently (different timestamp) stay deleted.
  /// Returns the number of channels restored.
  /// </summary>
  Task<int> RestoreChannelsByAppAsync(string appId, DateTimeOffset deletedAt, CancellationToken cancellationToken = default);
}

public sealed class ChannelAlreadyExistsException(string channelId)
    : InvalidOperationException($"Channel '{channelId}' already exists.")
{
  public string ChannelId { get; } = channelId;
}
