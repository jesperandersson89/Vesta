using System.Text.Json;
using VestaCore.Events;
using VestaCore.Storage;

namespace VestaCore.Tests.Storage;

/// <summary>
/// Idempotent-append regression tests for <see cref="InMemoryEventStore"/>.
/// Part of TODO #11 (reconnect / outbox correctness): a client that retries
/// a publish after a dropped ACK must get the original sequence back, not a
/// fresh one — otherwise the server stores duplicates and the broadcast feed
/// is corrupted with phantom sequences.
/// </summary>
public class InMemoryEventStoreDedupTests
{
  [Fact]
  public async Task AppendAsync_DuplicateEventId_ReturnsOriginalSequence()
  {
    InMemoryEventStore store = new();
    VestaEvent evt = CreateEvent("test/dedup");

    SequencedEvent first = await store.AppendAsync(evt);
    SequencedEvent second = await store.AppendAsync(evt);

    Assert.Equal(first.Sequence, second.Sequence);
    Assert.Equal(1L, first.Sequence);
    Assert.Equal(1L, await store.GetLatestSequenceAsync("test/dedup"));
  }

  [Fact]
  public async Task AppendAsync_DuplicateAfterOther_DoesNotShiftSequences()
  {
    InMemoryEventStore store = new();
    VestaEvent a = CreateEvent("test/dedup2");
    VestaEvent b = CreateEvent("test/dedup2");

    SequencedEvent r1 = await store.AppendAsync(a);
    SequencedEvent r2 = await store.AppendAsync(b);
    // Retry a — must not bump the sequence forward to 3.
    SequencedEvent r1Retry = await store.AppendAsync(a);

    Assert.Equal(1L, r1.Sequence);
    Assert.Equal(2L, r2.Sequence);
    Assert.Equal(1L, r1Retry.Sequence);
    Assert.Equal(2L, await store.GetLatestSequenceAsync("test/dedup2"));

    IReadOnlyList<SequencedEvent> all = await store.GetEventsAsync("test/dedup2", fromSequence: 1);
    Assert.Equal(2, all.Count);
  }

  private static VestaEvent CreateEvent(string channelId)
      => new(
          Id: Guid.NewGuid(),
          ChannelId: channelId,
          Timestamp: DateTimeOffset.UtcNow,
          ClientId: "test",
          EventType: "x",
          Payload: JsonDocument.Parse("""{"x":1}""").RootElement);
}
