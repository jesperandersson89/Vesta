using VestaCore.Events;

namespace VestaCore.Projections;

/// <summary>
/// Base class for projections that fold a channel's event stream into a typed state object.
///
/// Concrete reducers implement <see cref="Reduce"/> to mutate their internal storage and
/// expose a snapshot via <see cref="State"/>. Applying a <see cref="SequencedEvent"/>
/// advances <see cref="LastSequence"/>; applying a bare <see cref="VestaEvent"/> via
/// <see cref="ApplyLocal"/> does not (used for optimistic local updates before the
/// server has assigned a sequence).
///
/// Thread-safe: all mutations are guarded by <see cref="SyncRoot"/>.
/// </summary>
public abstract class EventReducer<TState>
{
  /// <summary>Lock object guarding all mutations. Derived classes should also lock on this when reading internal state for snapshots.</summary>
  protected readonly object SyncRoot = new();

  /// <summary>The highest server-assigned sequence number this reducer has observed.</summary>
  public long LastSequence { get; private set; }

  /// <summary>Snapshot of the current projected state. Implementations should return an immutable view.</summary>
  public abstract TState State { get; }

  /// <summary>Apply a server-confirmed event. Advances <see cref="LastSequence"/>.</summary>
  public void Apply(SequencedEvent sequenced)
  {
    ArgumentNullException.ThrowIfNull(sequenced);
    lock (SyncRoot)
    {
      Reduce(sequenced.Event);
      if (sequenced.Sequence > LastSequence)
      {
        LastSequence = sequenced.Sequence;
      }
    }
  }

  /// <summary>Apply a batch of server-confirmed events.</summary>
  public void Apply(IEnumerable<SequencedEvent> events)
  {
    ArgumentNullException.ThrowIfNull(events);
    foreach (SequencedEvent sequenced in events)
    {
      Apply(sequenced);
    }
  }

  /// <summary>
  /// Apply a locally-authored event that has not yet been sequenced by the server,
  /// for optimistic UI updates. Does not advance <see cref="LastSequence"/>.
  /// </summary>
  public void ApplyLocal(VestaEvent evt)
  {
    ArgumentNullException.ThrowIfNull(evt);
    lock (SyncRoot)
    {
      Reduce(evt);
    }
  }

  /// <summary>Implementations mutate their internal state in response to the event.</summary>
  protected abstract void Reduce(VestaEvent evt);
}
