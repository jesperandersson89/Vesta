namespace VestaServer.Storage;

/// <summary>
/// Optional per-app quotas and rate limits. <c>null</c> on any field means "no limit".
/// The server does not enforce these until they are set; <see cref="InMemoryAppStore"/>
/// and <see cref="NpgsqlAppStore"/> persist the columns and return them inside <see cref="AppInfo"/>.
/// </summary>
/// <param name="MaxPayloadBytes">Maximum size of <c>event.payload</c> + <c>event.metadata</c> at PUBLISH-time, in bytes.</param>
/// <param name="PublishRatePerMinute">Maximum events any single client may PUBLISH to channels in this app per rolling minute.</param>
/// <param name="MaxChannels">Maximum number of channels (rows in <c>channels</c>) the app may own. Checked at CREATE_CHANNEL.</param>
/// <param name="MaxEventsPerChannel">Maximum events to retain per channel under the app — enforced by <see cref="AppQuotaPrunerService"/>.</param>
/// <param name="RetentionDays">Events older than this (by <c>received_at</c>) are deleted by <see cref="AppQuotaPrunerService"/>.</param>
/// <param name="TotalStorageBytes">Hard ceiling on summed <c>pg_column_size(payload)</c> across the app namespace. Checked at PUBLISH against an in-process cached rollup maintained by <see cref="IAppStorageAccountant"/>.</param>
public sealed record AppQuotas(
    int? MaxPayloadBytes = null,
    int? PublishRatePerMinute = null,
    int? MaxChannels = null,
    int? MaxEventsPerChannel = null,
    int? RetentionDays = null,
    long? TotalStorageBytes = null)
{
  public static AppQuotas None { get; } = new();
}

/// <summary>
/// A registered app namespace and its owner.
/// </summary>
/// <param name="Id">The app namespace — also the first slug segment of any channel ID owned by the app.</param>
/// <param name="OwnerClientId">The client that registered the app.</param>
/// <param name="CreatedAt">When the app was registered.</param>
/// <param name="Quotas">Per-app limits (see <see cref="AppQuotas"/>).</param>
/// <param name="Discoverable">Whether the app owner has opted this app into server-to-server discovery (federation). When true and the host relay has discovery enabled, the relay advertises this app in its signed <c>ServerDescriptor</c>.</param>
public sealed record AppInfo(
    string Id,
    string OwnerClientId,
    DateTimeOffset CreatedAt,
    AppQuotas Quotas,
    bool Discoverable = false);

/// <summary>
/// Server-side abstraction for app namespace registration.
/// Apps own the first slug segment of channel IDs; when the server is configured
/// with <c>Protocol:RequireAppRegistration=true</c>, every channel-creating
/// operation must target an app namespace that exists in this store.
/// </summary>
public interface IAppStore
{
  /// <summary>
  /// Returns the app, or <c>null</c> if it is not registered.
  /// </summary>
  Task<AppInfo?> GetAsync(string appId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Returns true if the app exists.
  /// </summary>
  Task<bool> ExistsAsync(string appId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Registers a new app namespace owned by <paramref name="ownerClientId"/>.
  /// Throws <see cref="AppAlreadyRegisteredException"/> if the app already exists.
  /// </summary>
  /// <param name="discoverable">Whether the owner opts this app into server-to-server discovery at registration time.</param>
  Task RegisterAsync(string appId, string ownerClientId, bool discoverable = false, CancellationToken cancellationToken = default);

  /// <summary>
  /// Set the discoverability flag on an existing app (owner opt-in for federation).
  /// Returns false if the app does not exist.
  /// </summary>
  Task<bool> SetDiscoverableAsync(string appId, bool discoverable, CancellationToken cancellationToken = default);

  /// <summary>
  /// Update the quotas attached to an existing app. Any <c>null</c> field clears that limit.
  /// Returns false if the app does not exist.
  /// </summary>
  Task<bool> SetQuotasAsync(string appId, AppQuotas quotas, CancellationToken cancellationToken = default);

  /// <summary>
  /// List all registered apps. Used by background quota enforcement (pruner,
  /// accounting). Order is unspecified.
  /// </summary>
  Task<IReadOnlyList<AppInfo>> ListAsync(CancellationToken cancellationToken = default);
}

public sealed class AppAlreadyRegisteredException(string appId)
    : InvalidOperationException($"App '{appId}' is already registered.")
{
  public string AppId { get; } = appId;
}
