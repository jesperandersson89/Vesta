using VestaCore.Events;

namespace VestaCore.Projections;

/// <summary>
/// Describes a single update to an <see cref="LwwMap{TKey, TValue}"/>: either set a key
/// to a value, or remove (tombstone) the key.
/// </summary>
public readonly struct LwwMapUpdate<TKey, TValue>
    where TKey : notnull
{
  /// <summary>The key being affected.</summary>
  public TKey Key { get; }
  /// <summary>The new value (only meaningful when <see cref="IsRemove"/> is false).</summary>
  public TValue Value { get; }
  /// <summary>True if this update tombstones the key.</summary>
  public bool IsRemove { get; }

  private LwwMapUpdate(TKey key, TValue value, bool isRemove)
  {
    Key = key;
    Value = value;
    IsRemove = isRemove;
  }

  /// <summary>Create an update that assigns <paramref name="value"/> to <paramref name="key"/>.</summary>
  public static LwwMapUpdate<TKey, TValue> Set(TKey key, TValue value) => new(key, value, isRemove: false);
  /// <summary>Create an update that tombstones <paramref name="key"/>.</summary>
  public static LwwMapUpdate<TKey, TValue> Remove(TKey key) => new(key, default!, isRemove: true);
}

/// <summary>
/// Key-value map where each key independently follows last-writer-wins by event timestamp.
///
/// The supplied projector inspects each event and either returns an
/// <see cref="LwwMapUpdate{TKey, TValue}"/> (Set or Remove) or <c>null</c> when the event
/// does not affect the map. A Set with a timestamp older than the current entry is
/// ignored; a Remove tombstones the key, and a later Set whose timestamp is older than
/// the tombstone is also ignored (preventing zombies).
/// </summary>
public sealed class LwwMap<TKey, TValue> : EventReducer<IReadOnlyDictionary<TKey, TValue>>
    where TKey : notnull
{
  private readonly Func<VestaEvent, LwwMapUpdate<TKey, TValue>?> _project;
  private readonly Dictionary<TKey, Entry> _entries = [];

  /// <summary>Create a new LWW map.</summary>
  /// <param name="project">Function returning the update an event implies, or <c>null</c> if the event is irrelevant.</param>
  public LwwMap(Func<VestaEvent, LwwMapUpdate<TKey, TValue>?> project)
  {
    ArgumentNullException.ThrowIfNull(project);
    _project = project;
  }

  /// <inheritdoc />
  public override IReadOnlyDictionary<TKey, TValue> State
  {
    get
    {
      lock (SyncRoot)
      {
        Dictionary<TKey, TValue> snapshot = new(_entries.Count);
        foreach (KeyValuePair<TKey, Entry> kv in _entries)
        {
          if (!kv.Value.Tombstoned)
          {
            snapshot[kv.Key] = kv.Value.Value!;
          }
        }
        return snapshot;
      }
    }
  }

  /// <summary>Try to read the live (non-tombstoned) value for a key.</summary>
  public bool TryGetValue(TKey key, out TValue value)
  {
    lock (SyncRoot)
    {
      if (_entries.TryGetValue(key, out Entry entry) && !entry.Tombstoned)
      {
        value = entry.Value!;
        return true;
      }
    }
    value = default!;
    return false;
  }

  /// <inheritdoc />
  protected override void Reduce(VestaEvent evt)
  {
    LwwMapUpdate<TKey, TValue>? update = _project(evt);
    if (update is not LwwMapUpdate<TKey, TValue> u)
    {
      return;
    }

    if (_entries.TryGetValue(u.Key, out Entry existing) && evt.Timestamp <= existing.Timestamp)
    {
      return; // stale update — keep existing
    }

    _entries[u.Key] = new Entry(u.IsRemove ? default! : u.Value, evt.Timestamp, u.IsRemove);
  }

  private readonly record struct Entry(TValue? Value, DateTimeOffset Timestamp, bool Tombstoned);
}
