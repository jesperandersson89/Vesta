using System.Collections.Concurrent;

namespace VestaServer.Storage;

/// <summary>
/// In-process per-app storage accountant used to enforce <c>total_storage_bytes</c>
/// without a per-PUBLISH SQL aggregate. Cached values are seeded and refreshed by
/// <see cref="AppQuotaPrunerService"/> (SUM over <c>events</c>) and incremented
/// in-line on each successful PUBLISH. Drift between sweeps is bounded by the
/// pruner interval; the value is intentionally approximate.
/// </summary>
public interface IAppStorageAccountant
{
  /// <summary>
  /// Returns the cached storage size in bytes, or <c>null</c> if the app has not
  /// been measured yet (cold cache — callers should allow the PUBLISH).
  /// </summary>
  long? Get(string appId);

  /// <summary>
  /// Replaces the cached value (called by the pruner after a SUM).
  /// </summary>
  void Set(string appId, long bytes);

  /// <summary>
  /// Atomically adds <paramref name="bytes"/> to the cached value. No-op if the
  /// app has no cached value yet.
  /// </summary>
  void Add(string appId, long bytes);
}

/// <summary>
/// Default in-memory implementation. Singleton-scoped.
/// </summary>
public sealed class InMemoryAppStorageAccountant : IAppStorageAccountant
{
  private readonly ConcurrentDictionary<string, long> _bytes = new();

  public long? Get(string appId)
      => _bytes.TryGetValue(appId, out long value) ? value : null;

  public void Set(string appId, long bytes)
      => _bytes[appId] = bytes;

  public void Add(string appId, long bytes)
      => _bytes.AddOrUpdate(appId, _ => bytes, (_, existing) => existing + bytes);
}
