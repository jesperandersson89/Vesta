namespace VestaCore.Projections;

/// <summary>
/// Thrown by <see cref="EventReducer{TState}.Snapshot"/> or
/// <see cref="EventReducer{TState}.Restore"/> when the reducer does not implement
/// snapshotting. Built-in reducers (<see cref="AppendOnlyLog{T}"/>, <see cref="LwwRegister{T}"/>,
/// <see cref="LwwMap{TKey, TValue}"/>) all support it; user-defined reducers must override
/// the two methods themselves.
/// </summary>
public sealed class SnapshotNotSupportedException : NotSupportedException
{
  public SnapshotNotSupportedException(Type reducerType)
    : base($"Reducer {reducerType.Name} does not support snapshotting. Override Snapshot() and Restore() to opt in.")
  {
  }
}
