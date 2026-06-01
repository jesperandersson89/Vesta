using VestaCore.Events;

namespace VestaCore.Projections;

/// <summary>
/// Append-only ordered list reducer.
///
/// Every event for which the supplied projector returns a non-null value is appended to
/// the log. Events are deduplicated by <see cref="VestaEvent.Id"/> so that an event seen
/// both via <see cref="EventReducer{T}.ApplyLocal"/> and later via the server's confirmed
/// sequence does not appear twice.
/// </summary>
/// <typeparam name="T">The projected item type (e.g. ChatMessage, AuditEntry).</typeparam>
public sealed class AppendOnlyLog<T> : EventReducer<IReadOnlyList<T>>
{
  private readonly Func<VestaEvent, T?> _project;
  private readonly List<T> _items = [];
  private readonly HashSet<Guid> _seenIds = [];

  /// <summary>
  /// Create a new append-only log.
  /// </summary>
  /// <param name="project">
  /// Function that inspects an event and either returns the item to append, or
  /// <c>null</c> if the event is not relevant to this log.
  /// </param>
  public AppendOnlyLog(Func<VestaEvent, T?> project)
  {
    ArgumentNullException.ThrowIfNull(project);
    _project = project;
  }

  /// <inheritdoc />
  public override IReadOnlyList<T> State
  {
    get
    {
      lock (SyncRoot)
      {
        return _items.ToArray();
      }
    }
  }

  /// <summary>The number of items currently in the log.</summary>
  public int Count
  {
    get
    {
      lock (SyncRoot)
      {
        return _items.Count;
      }
    }
  }

  /// <inheritdoc />
  protected override void Reduce(VestaEvent evt)
  {
    if (!_seenIds.Add(evt.Id))
    {
      return;
    }

    T? projected = _project(evt);
    if (projected is null)
    {
      // Roll back the dedup so a later relevant event with the same id (shouldn't happen, but defensive) could still apply.
      _seenIds.Remove(evt.Id);
      return;
    }

    _items.Add(projected);
  }
}
