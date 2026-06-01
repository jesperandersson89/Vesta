namespace VestaServer.Data.Entities;

/// <summary>
/// EF Core entity mapping for the "channel_access" table.
/// Grants a specific client access to a private channel.
/// Public channels do not require entries here.
/// </summary>
public sealed class ChannelAccessEntity
{
  public string ChannelId { get; set; } = string.Empty;
  public string ClientId { get; set; } = string.Empty;
  public string Role { get; set; } = "member"; // "member" | "admin"
  public DateTimeOffset GrantedAt { get; set; }
}
