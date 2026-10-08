using Microsoft.EntityFrameworkCore;
using VestaServer.Data.Entities;

namespace VestaServer.Data;

/// <summary>
/// EF Core context used for schema migrations and metadata queries.
/// The event hot path (append/read) uses raw Npgsql for performance.
/// </summary>
public sealed class VestaDbContext(DbContextOptions<VestaDbContext> options) : DbContext(options)
{
    public DbSet<EventEntity> Events => Set<EventEntity>();
    public DbSet<ChannelEntity> Channels => Set<ChannelEntity>();
    public DbSet<ClientPositionEntity> ClientPositions => Set<ClientPositionEntity>();
    public DbSet<ChannelSequenceEntity> ChannelSequences => Set<ChannelSequenceEntity>();
    public DbSet<ChannelAccessEntity> ChannelAccess => Set<ChannelAccessEntity>();
    public DbSet<AppEntity> Apps => Set<AppEntity>();
    public DbSet<AppUsageEntity> AppUsage => Set<AppUsageEntity>();
    public DbSet<AdminAuditEntity> AdminAudit => Set<AdminAuditEntity>();
    public DbSet<AppAlertEntity> AppAlerts => Set<AppAlertEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // === channels ===
        modelBuilder.Entity<ChannelEntity>(entity =>
        {
            entity.ToTable("channels");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            entity.Property(e => e.Visibility).HasColumnName("visibility").HasDefaultValue("public");
            entity.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.HasIndex(e => e.DeletedAt).HasDatabaseName("IX_channels_deleted_at");
        });

        // === events ===
        modelBuilder.Entity<EventEntity>(entity =>
        {
            entity.ToTable("events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ChannelId).HasColumnName("channel_id").IsRequired();
            entity.Property(e => e.Sequence).HasColumnName("sequence").IsRequired();
            entity.Property(e => e.Timestamp).HasColumnName("timestamp").IsRequired();
            entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
            entity.Property(e => e.EventType).HasColumnName("event_type").IsRequired();
            entity.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.ParentId).HasColumnName("parent_id");
            entity.Property(e => e.Signature).HasColumnName("signature");
            entity.Property(e => e.ReceivedAt).HasColumnName("received_at").HasDefaultValueSql("now()");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");

            entity.HasIndex(e => new { e.ChannelId, e.Sequence }).IsUnique();
            entity.HasIndex(e => new { e.ChannelId, e.Timestamp });
            entity.HasIndex(e => e.ExpiresAt).HasDatabaseName("IX_events_expires_at");
        });

        // === client_positions ===
        modelBuilder.Entity<ClientPositionEntity>(entity =>
        {
            entity.ToTable("client_positions");
            entity.HasKey(e => new { e.ClientId, e.ChannelId });
            entity.Property(e => e.ClientId).HasColumnName("client_id");
            entity.Property(e => e.ChannelId).HasColumnName("channel_id");
            entity.Property(e => e.LastSequence).HasColumnName("last_sequence").HasDefaultValue(0L);
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        });

        // === channel_sequences ===
        modelBuilder.Entity<ChannelSequenceEntity>(entity =>
        {
            entity.ToTable("channel_sequences");
            entity.HasKey(e => e.ChannelId);
            entity.Property(e => e.ChannelId).HasColumnName("channel_id");
            entity.Property(e => e.NextSeq).HasColumnName("next_seq").HasDefaultValue(1L);
        });

        // === channel_access ===
        modelBuilder.Entity<ChannelAccessEntity>(entity =>
        {
            entity.ToTable("channel_access");
            entity.HasKey(e => new { e.ChannelId, e.ClientId });
            entity.Property(e => e.ChannelId).HasColumnName("channel_id");
            entity.Property(e => e.ClientId).HasColumnName("client_id");
            entity.Property(e => e.Role).HasColumnName("role").HasDefaultValue("member");
            entity.Property(e => e.GrantedAt).HasColumnName("granted_at").HasDefaultValueSql("now()");
        });

        // === apps ===
        modelBuilder.Entity<AppEntity>(entity =>
        {
            entity.ToTable("apps");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").HasMaxLength(64);
            entity.Property(e => e.OwnerClientId).HasColumnName("owner_client_id").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");

            entity.Property(e => e.Discoverable).HasColumnName("discoverable").HasDefaultValue(false);
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.PausedAt).HasColumnName("paused_at");
            entity.Property(e => e.ThrottlePerMinute).HasColumnName("throttle_per_minute");

            // Reserved for TODO #9b — all nullable, server does not enforce yet.
            entity.Property(e => e.MaxChannels).HasColumnName("max_channels");
            entity.Property(e => e.MaxEventsPerChannel).HasColumnName("max_events_per_channel");
            entity.Property(e => e.MaxPayloadBytes).HasColumnName("max_payload_bytes");
            entity.Property(e => e.PublishRatePerMinute).HasColumnName("publish_rate_per_minute");
            entity.Property(e => e.RetentionDays).HasColumnName("retention_days");
            entity.Property(e => e.TotalStorageBytes).HasColumnName("total_storage_bytes");
            entity.Property(e => e.MaxMessagesPerMonth).HasColumnName("max_messages_per_month");

            entity.HasIndex(e => e.OwnerClientId).HasDatabaseName("IX_apps_owner_client_id");
            entity.HasIndex(e => e.DeletedAt).HasDatabaseName("IX_apps_deleted_at");
        });

        // === admin_audit ===
        modelBuilder.Entity<AdminAuditEntity>(entity =>
        {
            entity.ToTable("admin_audit");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.At).HasColumnName("at").HasDefaultValueSql("now()");
            entity.Property(e => e.AdminPublicKey).HasColumnName("admin_public_key").IsRequired();
            entity.Property(e => e.Action).HasColumnName("action").IsRequired();
            entity.Property(e => e.Target).HasColumnName("target");
            entity.Property(e => e.Details).HasColumnName("details").HasColumnType("jsonb");
            entity.HasIndex(e => e.At).HasDatabaseName("IX_admin_audit_at");
            entity.HasIndex(e => e.Target).HasDatabaseName("IX_admin_audit_target");
        });

        // === app_alerts ===
        modelBuilder.Entity<AppAlertEntity>(entity =>
        {
            entity.ToTable("app_alerts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.AppId).HasColumnName("app_id").HasMaxLength(64).IsRequired();
            entity.Property(e => e.Metric).HasColumnName("metric").IsRequired();
            entity.Property(e => e.ThresholdPercent).HasColumnName("threshold_pct");
            entity.Property(e => e.Observed).HasColumnName("observed");
            entity.Property(e => e.LimitValue).HasColumnName("limit_value");
            entity.Property(e => e.PeriodStart).HasColumnName("period_start").HasColumnType("date");
            entity.Property(e => e.RaisedAt).HasColumnName("raised_at").HasDefaultValueSql("now()");
            entity.Property(e => e.ResolvedAt).HasColumnName("resolved_at");
            entity.Property(e => e.AcknowledgedAt).HasColumnName("acknowledged_at");
            entity.HasIndex(e => new { e.AppId, e.Metric, e.ThresholdPercent })
                .IsUnique()
                .HasFilter("resolved_at IS NULL")
                .HasDatabaseName("UX_app_alerts_open");
            entity.HasIndex(e => e.AppId).HasDatabaseName("IX_app_alerts_app_id");
        });

        // === app_usage ===
        modelBuilder.Entity<AppUsageEntity>(entity =>
        {
            entity.ToTable("app_usage");
            entity.HasKey(e => new { e.AppId, e.PeriodStart });
            entity.Property(e => e.AppId).HasColumnName("app_id");
            entity.Property(e => e.PeriodStart).HasColumnName("period_start").HasColumnType("date");
            entity.Property(e => e.Messages).HasColumnName("messages").HasDefaultValue(0L);
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        });
    }
}
