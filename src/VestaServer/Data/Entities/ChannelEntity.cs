namespace VestaServer.Data.Entities;

/// <summary>
/// EF Core entity mapping for the "channels" table.
/// </summary>
public sealed class ChannelEntity
{
    public string Id { get; set; } = string.Empty; // Human-readable slug
    public DateTimeOffset CreatedAt { get; set; }
    public string? Metadata { get; set; } // JSONB stored as string
}
