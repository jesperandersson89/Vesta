namespace VestaServer.Data.Entities;

/// <summary>
/// EF Core entity mapping for the "events" table.
/// Used only for schema migrations — the hot path uses raw Npgsql.
/// </summary>
public sealed class EventEntity
{
    public Guid Id { get; set; }
    public string ChannelId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty; // JSONB stored as string
    public Guid? ParentId { get; set; }
    public string? Signature { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>
    /// Optional expiry time. When set, the event is filtered out of catch-up reads
    /// after this moment and is eligible for deletion by the cleanup job.
    /// Computed at PUBLISH from <c>metadata.ttlSeconds</c>.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
