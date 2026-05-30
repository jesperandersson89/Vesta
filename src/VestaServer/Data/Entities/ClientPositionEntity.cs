namespace VestaServer.Data.Entities;

/// <summary>
/// EF Core entity mapping for the "client_positions" table.
/// Tracks each client's last-seen sequence per channel.
/// </summary>
public sealed class ClientPositionEntity
{
    public string ClientId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public long LastSequence { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
