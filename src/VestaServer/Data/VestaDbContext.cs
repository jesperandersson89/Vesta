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

            // Reserved for TODO #9b — all nullable, server does not enforce yet.
            entity.Property(e => e.MaxChannels).HasColumnName("max_channels");
            entity.Property(e => e.MaxEventsPerChannel).HasColumnName("max_events_per_channel");
            entity.Property(e => e.MaxPayloadBytes).HasColumnName("max_payload_bytes");
            entity.Property(e => e.PublishRatePerMinute).HasColumnName("publish_rate_per_minute");
            entity.Property(e => e.RetentionDays).HasColumnName("retention_days");
            entity.Property(e => e.TotalStorageBytes).HasColumnName("total_storage_bytes");

            entity.HasIndex(e => e.OwnerClientId).HasDatabaseName("IX_apps_owner_client_id");
        });
    }
}
