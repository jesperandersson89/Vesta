namespace VestaServer.Data.Entities;

/// <summary>
/// EF Core entity for the "app_usage" table — durable per-app, per-calendar-month
/// message-count rollup backing <c>max_messages_per_month</c> enforcement.
/// </summary>
public sealed class AppUsageEntity
{
  public string AppId { get; set; } = string.Empty;
  public DateOnly PeriodStart { get; set; }
  public long Messages { get; set; }
  public DateTimeOffset UpdatedAt { get; set; }
}
