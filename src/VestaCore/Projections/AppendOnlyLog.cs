using System.Text.Json;
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
  private readonly JsonSerializerOptions? _serializerOptions;
  private readonly List<T> _items = [];
  private readonly HashSet<Guid> _seenIds = [];

  /// <summary>
  /// Create a new append-only log.
  /// </summary>
  /// <param name="project">
  /// Function that inspects an event and either returns the item to append, or
  /// <c>null</c> if the event is not relevant to this log.
  /// </param>
  /// <param name="serializerOptions">Optional STJ options used by <see cref="EventReducer{TState}.Snapshot"/> / <see cref="EventReducer{TState}.Restore"/>.</param>
  public AppendOnlyLog(Func<VestaEvent, T?> project, JsonSerializerOptions? serializerOptions = null)
  {
    ArgumentNullException.ThrowIfNull(project);
    _project = project;
    _serializerOptions = serializerOptions;
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

  /// <inheritdoc />
  public override ProjectionSnapshot Snapshot()
  {
    lock (SyncRoot)
    {
      SnapshotPayload payload = new(_items.ToArray(), _seenIds.ToArray());
      string json = JsonSerializer.Serialize(payload, _serializerOptions);
      return new ProjectionSnapshot(LastSequence, json);
    }
  }

  /// <inheritdoc />
  protected override void RestoreState(string stateJson)
  {
    SnapshotPayload? payload = JsonSerializer.Deserialize<SnapshotPayload>(stateJson, _serializerOptions)
      ?? throw new InvalidOperationException("AppendOnlyLog snapshot deserialized to null.");
    _items.Clear();
    _items.AddRange(payload.Items);
    _seenIds.Clear();
    foreach (Guid id in payload.SeenIds)
    {
      _seenIds.Add(id);
    }
  }

  private sealed record SnapshotPayload(T[] Items, Guid[] SeenIds);
}
