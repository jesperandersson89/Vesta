namespace VestaServer.Data.Entities;

/// <summary>EF Core entity for "app_alerts" — quota-threshold crossings raised by the quota pruner.</summary>
public sealed class AppAlertEntity
{
  public long Id { get; set; }
  public string AppId { get; set; } = string.Empty;

  /// <summary>One of <c>messages</c>, <c>storage</c>, <c>channels</c>.</summary>
  public string Metric { get; set; } = string.Empty;

  public int ThresholdPercent { get; set; }
  public long Observed { get; set; }
  public long LimitValue { get; set; }

  /// <summary>Usage period the alert belongs to (messages only); the alert auto-resolves when the period rolls over.</summary>
  public DateOnly? PeriodStart { get; set; }

  public DateTimeOffset RaisedAt { get; set; }
  public DateTimeOffset? ResolvedAt { get; set; }
  public DateTimeOffset? AcknowledgedAt { get; set; }
}
