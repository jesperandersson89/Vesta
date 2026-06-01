namespace VestaCore.Projections;

/// <summary>
/// Tracks how far a projection has been processed within a channel, so it can be persisted
/// and used to resume catch-up reads on reconnect without re-applying events from scratch.
/// </summary>
/// <param name="ChannelId">The channel this checkpoint belongs to.</param>
/// <param name="LastSequence">The highest server-assigned sequence number that has been applied.</param>
public sealed record ProjectionCheckpoint(string ChannelId, long LastSequence);
