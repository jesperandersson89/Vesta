using System.Text.Json;
using VestaCore.Events;
using VestaCore.Projections;

namespace VestaCore.Tests.Projections;

public class LwwMapTests
{
  private const string ClientId = "test-client-id-12345";

  private static VestaEvent Evt(string eventType, object payload, DateTimeOffset ts) =>
      new(Id: Guid.NewGuid(),
          ChannelId: "test",
          Timestamp: ts,
          ClientId: ClientId,
          EventType: eventType,
          Payload: JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement);

  private static SequencedEvent Seq(VestaEvent evt, long seq) => new(evt, seq, DateTimeOffset.UtcNow);

  private static LwwMap<string, int> CreateScoreMap() => new(evt => evt.EventType switch
  {
    "set" => LwwMapUpdate<string, int>.Set(
          evt.Payload.GetProperty("key").GetString()!,
          evt.Payload.GetProperty("value").GetInt32()),
    "remove" => LwwMapUpdate<string, int>.Remove(evt.Payload.GetProperty("key").GetString()!),
    _ => null
  });

  [Fact]
  public void Apply_StoresSets()
  {
    LwwMap<string, int> map = CreateScoreMap();
    DateTimeOffset t = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    map.Apply(Seq(Evt("set", new { key = "a", value = 1 }, t), 1));
    map.Apply(Seq(Evt("set", new { key = "b", value = 2 }, t.AddSeconds(1)), 2));

    Assert.Equal(2, map.State.Count);
    Assert.Equal(1, map.State["a"]);
    Assert.Equal(2, map.State["b"]);
  }

  [Fact]
  public void Apply_LaterTimestampOverwritesEarlier()
  {
    LwwMap<string, int> map = CreateScoreMap();
    DateTimeOffset t = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    map.Apply(Seq(Evt("set", new { key = "a", value = 1 }, t), 1));
    map.Apply(Seq(Evt("set", new { key = "a", value = 99 }, t.AddSeconds(10)), 2));

    Assert.Equal(99, map.State["a"]);
  }

  [Fact]
  public void Apply_EarlierTimestampIsIgnored()
  {
    LwwMap<string, int> map = CreateScoreMap();
    DateTimeOffset t = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    map.Apply(Seq(Evt("set", new { key = "a", value = 100 }, t.AddSeconds(10)), 1));
    map.Apply(Seq(Evt("set", new { key = "a", value = 1 }, t), 2));

    Assert.Equal(100, map.State["a"]);
  }

  [Fact]
  public void Apply_RemoveTombstonesKey()
  {
    LwwMap<string, int> map = CreateScoreMap();
    DateTimeOffset t = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    map.Apply(Seq(Evt("set", new { key = "a", value = 1 }, t), 1));
    map.Apply(Seq(Evt("remove", new { key = "a" }, t.AddSeconds(1)), 2));

    Assert.False(map.State.ContainsKey("a"));
    Assert.False(map.TryGetValue("a", out _));
  }

  [Fact]
  public void Apply_StaleSetAfterRemoveIsIgnored()
  {
    LwwMap<string, int> map = CreateScoreMap();
    DateTimeOffset t = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    map.Apply(Seq(Evt("remove", new { key = "a" }, t.AddSeconds(10)), 1));
    map.Apply(Seq(Evt("set", new { key = "a", value = 1 }, t), 2)); // older than tombstone

    Assert.False(map.State.ContainsKey("a"));
  }

  [Fact]
  public void Apply_NewSetAfterRemoveReanimates()
  {
    LwwMap<string, int> map = CreateScoreMap();
    DateTimeOffset t = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    map.Apply(Seq(Evt("set", new { key = "a", value = 1 }, t), 1));
    map.Apply(Seq(Evt("remove", new { key = "a" }, t.AddSeconds(1)), 2));
    map.Apply(Seq(Evt("set", new { key = "a", value = 42 }, t.AddSeconds(2)), 3));

    Assert.Equal(42, map.State["a"]);
  }

  [Fact]
  public void TryGetValue_ReturnsFalseForMissingKey()
  {
    LwwMap<string, int> map = CreateScoreMap();
    Assert.False(map.TryGetValue("missing", out int v));
    Assert.Equal(0, v);
  }
}
