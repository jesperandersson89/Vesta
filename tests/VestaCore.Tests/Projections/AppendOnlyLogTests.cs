using System.Text.Json;
using VestaCore.Events;
using VestaCore.Projections;

namespace VestaCore.Tests.Projections;

public class AppendOnlyLogTests
{
  private const string ClientId = "test-client-id-12345";
  private const string Channel = "test";

  private static SequencedEvent Seq(long seq, string eventType, object payload, DateTimeOffset? ts = null) =>
      new(new VestaEvent(
              Id: Guid.NewGuid(),
              ChannelId: Channel,
              Timestamp: ts ?? DateTimeOffset.UtcNow,
              ClientId: ClientId,
              EventType: eventType,
              Payload: JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement),
          seq,
          DateTimeOffset.UtcNow);

  [Fact]
  public void Apply_AppendsProjectedItems_InOrder()
  {
    AppendOnlyLog<string> log = new(evt =>
        evt.EventType == "msg" ? evt.Payload.GetProperty("text").GetString() : null);

    log.Apply(Seq(1, "msg", new { text = "hello" }));
    log.Apply(Seq(2, "msg", new { text = "world" }));

    Assert.Equal(new[] { "hello", "world" }, log.State);
    Assert.Equal(2L, log.LastSequence);
  }

  [Fact]
  public void Apply_SkipsEventsTheProjectorRejects()
  {
    AppendOnlyLog<string> log = new(evt =>
        evt.EventType == "msg" ? evt.Payload.GetProperty("text").GetString() : null);

    log.Apply(Seq(1, "msg", new { text = "a" }));
    log.Apply(Seq(2, "other", new { text = "b" }));
    log.Apply(Seq(3, "msg", new { text = "c" }));

    Assert.Equal(new[] { "a", "c" }, log.State);
    Assert.Equal(3L, log.LastSequence);
  }

  [Fact]
  public void Apply_DeduplicatesByEventId_AcrossLocalAndSequenced()
  {
    AppendOnlyLog<string> log = new(evt => evt.Payload.GetProperty("text").GetString());

    VestaEvent local = new(
        Id: Guid.NewGuid(),
        ChannelId: Channel,
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: ClientId,
        EventType: "msg",
        Payload: JsonDocument.Parse("""{"text":"once"}""").RootElement);

    log.ApplyLocal(local);
    log.Apply(new SequencedEvent(local, Sequence: 1, ReceivedAt: DateTimeOffset.UtcNow));

    Assert.Single(log.State);
    Assert.Equal("once", log.State[0]);
    Assert.Equal(1L, log.LastSequence);
  }

  [Fact]
  public void ApplyLocal_DoesNotAdvanceLastSequence()
  {
    AppendOnlyLog<string> log = new(evt => evt.Payload.GetProperty("text").GetString());
    VestaEvent local = new(
        Id: Guid.NewGuid(),
        ChannelId: Channel,
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: ClientId,
        EventType: "msg",
        Payload: JsonDocument.Parse("""{"text":"x"}""").RootElement);

    log.ApplyLocal(local);

    Assert.Equal(0L, log.LastSequence);
    Assert.Single(log.State);
  }
}
