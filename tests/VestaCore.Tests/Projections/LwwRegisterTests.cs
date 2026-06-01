using System.Text.Json;
using VestaCore.Events;
using VestaCore.Projections;

namespace VestaCore.Tests.Projections;

public class LwwRegisterTests
{
  private const string ClientId = "test-client-id-12345";

  private static VestaEvent MakeEvent(string text, DateTimeOffset ts, string eventType = "set") =>
      new(Id: Guid.NewGuid(),
          ChannelId: "test",
          Timestamp: ts,
          ClientId: ClientId,
          EventType: eventType,
          Payload: JsonDocument.Parse(JsonSerializer.Serialize(new { text })).RootElement);

  private static SequencedEvent Seq(VestaEvent evt, long seq) => new(evt, seq, DateTimeOffset.UtcNow);

  [Fact]
  public void Apply_KeepsLatestByTimestamp()
  {
    LwwRegister<string> reg = new(evt =>
        evt.EventType == "set" ? evt.Payload.GetProperty("text").GetString() : null);

    DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    reg.Apply(Seq(MakeEvent("first", t0), 1));
    reg.Apply(Seq(MakeEvent("second", t0.AddSeconds(10)), 2));

    Assert.Equal("second", reg.State);
    Assert.Equal(t0.AddSeconds(10), reg.LastModified);
  }

  [Fact]
  public void Apply_IgnoresOlderTimestamp()
  {
    LwwRegister<string> reg = new(evt => evt.Payload.GetProperty("text").GetString());

    DateTimeOffset t = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    reg.Apply(Seq(MakeEvent("newer", t.AddSeconds(10)), 1));
    reg.Apply(Seq(MakeEvent("older", t), 2));

    Assert.Equal("newer", reg.State);
  }

  [Fact]
  public void Apply_IgnoresIrrelevantEvents()
  {
    LwwRegister<string> reg = new(evt =>
        evt.EventType == "set" ? evt.Payload.GetProperty("text").GetString() : null);

    DateTimeOffset t = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    reg.Apply(Seq(MakeEvent("hi", t, "set"), 1));
    reg.Apply(Seq(MakeEvent("ignored", t.AddSeconds(10), "other"), 2));

    Assert.Equal("hi", reg.State);
  }

  [Fact]
  public void State_IsDefaultWhenNothingApplied()
  {
    LwwRegister<string> reg = new(_ => null);
    Assert.Null(reg.State);
    Assert.Equal(DateTimeOffset.MinValue, reg.LastModified);
  }
}
