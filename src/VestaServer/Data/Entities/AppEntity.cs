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

  /// <summary>
  /// Whether the app owner has opted this app into server-to-server discovery (federation).
  /// When true and the host relay has discovery enabled, the relay advertises the app in its
  /// signed server descriptor. Default false (opt-in).
  /// </summary>
  public bool Discoverable { get; set; }

  // === Reserved for TODO #9b (per-app quotas & rate limits) ===
  // Nullable means "no limit". Server does not enforce these yet.
  public int? MaxChannels { get; set; }
  public int? MaxEventsPerChannel { get; set; }
  public int? MaxPayloadBytes { get; set; }
  public int? PublishRatePerMinute { get; set; }
  public int? RetentionDays { get; set; }
  public long? TotalStorageBytes { get; set; }
}
