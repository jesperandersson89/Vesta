using VestaCore.Events;

namespace VestaCore.Projections;

/// <summary>
/// Single-value last-writer-wins register.
///
/// Each event is run through the supplied projector. If it produces a non-null value
/// and the event's timestamp is strictly newer than the timestamp of the currently
/// stored value, the register is updated. Ties on timestamp preserve the existing
/// value (deterministic and easy to reason about).
/// </summary>
/// <typeparam name="T">The stored value type (e.g. string for clipboard content).</typeparam>
public sealed class LwwRegister<T> : EventReducer<T?>
{
  private readonly Func<VestaEvent, T?> _project;
  private T? _value;
  private DateTimeOffset _valueTimestamp = DateTimeOffset.MinValue;

  /// <summary>
  /// Create a new LWW register.
  /// </summary>
  /// <param name="project">Function returning the new value carried by an event, or <c>null</c> if the event does not affect this register.</param>
  public LwwRegister(Func<VestaEvent, T?> project)
  {
    ArgumentNullException.ThrowIfNull(project);
    _project = project;
  }

  /// <inheritdoc />
  public override T? State
  {
    get
    {
      lock (SyncRoot)
      {
        return _value;
      }
    }
  }

  /// <summary>The timestamp of the event that produced the current value, or <see cref="DateTimeOffset.MinValue"/> if empty.</summary>
  public DateTimeOffset LastModified
  {
    get
    {
      lock (SyncRoot)
      {
        return _valueTimestamp;
      }
    }
  }

  /// <inheritdoc />
  protected override void Reduce(VestaEvent evt)
  {
    T? projected = _project(evt);
    if (projected is null)
    {
      return;
    }

    if (evt.Timestamp > _valueTimestamp)
    {
      _value = projected;
      _valueTimestamp = evt.Timestamp;
    }
  }
}
