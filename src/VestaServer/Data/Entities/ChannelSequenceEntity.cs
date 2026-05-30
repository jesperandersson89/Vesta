namespace VestaServer.Data.Entities;

/// <summary>
/// EF Core entity mapping for the "channel_sequences" table.
/// Provides atomic per-channel sequence generation (Option B from PLANNING.md).
/// </summary>
public sealed class ChannelSequenceEntity
{
    public string ChannelId { get; set; } = string.Empty; // PK, references channels(id)
    public long NextSeq { get; set; } = 1;
}
