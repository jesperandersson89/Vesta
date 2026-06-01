using VestaCore.Projections;

namespace VestaClient.Storage;

/// <summary>
/// Persistent store for <see cref="ProjectionSnapshot"/>s so projections can resume
/// from the last known sequence on cold start instead of replaying the entire
/// channel log.
///
/// Snapshots are keyed by <c>(channelId, projectionId)</c> — a single channel may have
/// multiple projections (e.g. chat history + presence map), each persisted independently.
/// </summary>
public interface IProjectionStore
{
  /// <summary>
  /// Save (or overwrite) the snapshot for a given channel + projection.
  /// </summary>
  Task SaveAsync(
      string channelId,
      string projectionId,
      ProjectionSnapshot snapshot,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Load the snapshot for a given channel + projection, or <c>null</c> if none has been saved.
  /// </summary>
  Task<ProjectionSnapshot?> LoadAsync(
      string channelId,
      string projectionId,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Delete the snapshot for a given channel + projection. No-op if not present.
  /// </summary>
  Task DeleteAsync(
      string channelId,
      string projectionId,
      CancellationToken cancellationToken = default);
}

/// <summary>
/// Convenience extensions for working with <see cref="EventReducer{TState}"/> and
/// <see cref="IProjectionStore"/>.
/// </summary>
public static class ProjectionStoreExtensions
{
  /// <summary>
  /// Capture the reducer's current state and persist it.
  /// </summary>
  public static Task SaveAsync<TState>(
      this IProjectionStore store,
      string channelId,
      string projectionId,
      EventReducer<TState> reducer,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(store);
    ArgumentNullException.ThrowIfNull(reducer);
    return store.SaveAsync(channelId, projectionId, reducer.Snapshot(), cancellationToken);
  }

  /// <summary>
  /// Load the snapshot (if any) into <paramref name="reducer"/>. Returns <c>true</c> if a
  /// snapshot was found and restored, <c>false</c> if no snapshot existed (cold start).
  /// </summary>
  public static async Task<bool> RestoreAsync<TState>(
      this IProjectionStore store,
      string channelId,
      string projectionId,
      EventReducer<TState> reducer,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(store);
    ArgumentNullException.ThrowIfNull(reducer);
    ProjectionSnapshot? snapshot = await store.LoadAsync(channelId, projectionId, cancellationToken).ConfigureAwait(false);
    if (snapshot is null)
    {
      return false;
    }
    reducer.Restore(snapshot);
    return true;
  }
}
