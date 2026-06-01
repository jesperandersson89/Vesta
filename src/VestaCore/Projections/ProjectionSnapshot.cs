namespace VestaCore.Projections;

/// <summary>
/// A captured snapshot of a projection's state at a specific server-assigned sequence.
///
/// Persist alongside your projection so that on cold start the client can load the snapshot,
/// restore the reducer, and then catch up from <see cref="LastSequence"/> + 1 instead of
/// replaying the entire channel log.
/// </summary>
/// <param name="LastSequence">The highest server-assigned sequence the snapshot reflects.</param>
/// <param name="StateJson">Reducer-defined serialized state. Opaque to <see cref="IProjectionStore"/> implementations.</param>
public sealed record ProjectionSnapshot(long LastSequence, string StateJson);
