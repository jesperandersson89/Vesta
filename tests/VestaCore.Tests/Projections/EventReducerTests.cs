using System.Text.Json;
using VestaCore.Events;
using VestaCore.Projections;

namespace VestaCore.Tests.Projections;

public class EventReducerTests
{
  private sealed class CountingReducer : EventReducer<int>
  {
    private int _count;
    public override int State { get { lock (SyncRoot) { return _count; } } }
    protected override void Reduce(VestaEvent evt) => _count++;
  }

  private static SequencedEvent Seq(long seq) =>
      new(new VestaEvent(
              Id: Guid.NewGuid(),
              ChannelId: "test",
              Timestamp: DateTimeOffset.UtcNow,
              ClientId: "test-client-id-12345",
              EventType: "x",
              Payload: JsonDocument.Parse("""{}""").RootElement),
          seq,
          DateTimeOffset.UtcNow);

  [Fact]
  public void Apply_SequencedEvent_AdvancesLastSequence()
  {
    CountingReducer r = new();
    r.Apply(Seq(5));
    Assert.Equal(1, r.State);
    Assert.Equal(5L, r.LastSequence);
  }

  [Fact]
  public void Apply_OutOfOrderSequence_DoesNotRegress()
  {
    CountingReducer r = new();
    r.Apply(Seq(5));
    r.Apply(Seq(3));
    Assert.Equal(5L, r.LastSequence);
    Assert.Equal(2, r.State);
  }

  [Fact]
  public void ApplyLocal_DoesNotAdvanceLastSequence()
  {
    CountingReducer r = new();
    r.ApplyLocal(new VestaEvent(
        Id: Guid.NewGuid(),
        ChannelId: "test",
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: "test-client-id-12345",
        EventType: "x",
        Payload: JsonDocument.Parse("""{}""").RootElement));
    Assert.Equal(1, r.State);
    Assert.Equal(0L, r.LastSequence);
  }

  [Fact]
  public void Apply_BatchAppliesAll()
  {
    CountingReducer r = new();
    r.Apply([Seq(1), Seq(2), Seq(3)]);
    Assert.Equal(3, r.State);
    Assert.Equal(3L, r.LastSequence);
  }
}
