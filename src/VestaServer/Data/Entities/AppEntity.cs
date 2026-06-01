namespace VestaServer.Data.Entities;

/// <summary>
/// EF Core entity mapping for the "apps" table.
/// An app owns a namespace — the first slug segment of every channel ID it uses.
/// Quota columns are reserved for TODO #9b and currently always null (no limits enforced).
/// </summary>
public sealed class AppEntity
{
  public string Id { get; set; } = string.Empty;
  public string OwnerClientId { get; set; } = string.Empty;
  public DateTimeOffset CreatedAt { get; set; }

  // === Reserved for TODO #9b (per-app quotas & rate limits) ===
  // Nullable means "no limit". Server does not enforce these yet.
  public int? MaxChannels { get; set; }
  public int? MaxEventsPerChannel { get; set; }
  public int? MaxPayloadBytes { get; set; }
  public int? PublishRatePerMinute { get; set; }
  public int? RetentionDays { get; set; }
  public long? TotalStorageBytes { get; set; }
}
