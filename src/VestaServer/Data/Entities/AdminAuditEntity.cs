namespace VestaServer.Data.Entities;

/// <summary>EF Core entity for "admin_audit" — durable record of every mutating admin action.</summary>
public sealed class AdminAuditEntity
{
  public long Id { get; set; }
  public DateTimeOffset At { get; set; }

  /// <summary>Hex public key of the acting admin, or <c>system</c> for background jobs.</summary>
  public string AdminPublicKey { get; set; } = string.Empty;

  public string Action { get; set; } = string.Empty;
  public string? Target { get; set; }

  /// <summary>Optional JSON object with action-specific context.</summary>
  public string? Details { get; set; }
}
