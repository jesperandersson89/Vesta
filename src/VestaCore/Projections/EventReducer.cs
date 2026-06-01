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

  /// <summary>
  /// Capture the current state as a <see cref="ProjectionSnapshot"/>. Override in
  /// subclasses that want snapshot support; the default throws
  /// <see cref="SnapshotNotSupportedException"/>.
  ///
  /// Implementations should serialize whatever internal state is needed to fully
  /// reconstruct the reducer (including per-entry timestamps, tombstones, dedup sets,
  /// etc — not just the public <see cref="State"/> view).
  /// </summary>
  public virtual ProjectionSnapshot Snapshot()
  {
    throw new SnapshotNotSupportedException(GetType());
  }

  /// <summary>
  /// Restore the reducer from a previously captured snapshot. Replaces all internal state
  /// and sets <see cref="LastSequence"/> to <see cref="ProjectionSnapshot.LastSequence"/>.
  ///
  /// Typical use: load snapshot from <c>IProjectionStore</c> on startup, restore the
  /// reducer, then call <c>FETCH { fromSequence = reducer.LastSequence + 1 }</c> to catch up
  /// incrementally instead of replaying the whole channel.
  /// </summary>
  public virtual void Restore(ProjectionSnapshot snapshot)
  {
    ArgumentNullException.ThrowIfNull(snapshot);
    lock (SyncRoot)
    {
      RestoreState(snapshot.StateJson);
      LastSequence = snapshot.LastSequence;
    }
  }

  /// <summary>
  /// Hook for <see cref="Restore"/> — implementations replace their internal state from the
  /// JSON produced by their own <see cref="Snapshot"/>. Called under <see cref="SyncRoot"/>.
  /// </summary>
  protected virtual void RestoreState(string stateJson)
  {
    throw new SnapshotNotSupportedException(GetType());
  }
}
