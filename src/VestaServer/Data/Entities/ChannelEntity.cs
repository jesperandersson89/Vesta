namespace VestaServer.Data.Entities;

/// <summary>
/// EF Core entity mapping for the "channels" table.
/// </summary>
public sealed class ChannelEntity
{
    public string Id { get; set; } = string.Empty; // Human-readable slug
    public DateTimeOffset CreatedAt { get; set; }
    public string Visibility { get; set; } = "public"; // "public" | "private" | "broadcast"
    public bool Encrypted { get; set; } // End-to-end encrypted: relay enforces the envelope shape, never holds a key
    public string? Metadata { get; set; } // JSONB stored as string
    public DateTimeOffset? DeletedAt { get; set; } // Tombstone; null when active
}
