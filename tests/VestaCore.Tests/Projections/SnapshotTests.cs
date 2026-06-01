using System.Text.Json;
using VestaCore.Events;
using VestaCore.Projections;

namespace VestaCore.Tests.Projections;

public class SnapshotTests
{
  private static VestaEvent Evt(string type, object payload, DateTimeOffset? ts = null, Guid? id = null) =>
      new(
          Id: id ?? Guid.NewGuid(),
          ChannelId: "test",
          Timestamp: ts ?? DateTimeOffset.UtcNow,
          ClientId: "test-client-id-12345",
          EventType: type,
          Payload: JsonSerializer.SerializeToElement(payload));

  private static SequencedEvent Seq(VestaEvent evt, long sequence) =>
      new(evt, sequence, DateTimeOffset.UtcNow);

  // ── AppendOnlyLog ──────────────────────────────────────────────────────────

  [Fact]
  public void AppendOnlyLog_SnapshotRestore_PreservesItemsAndDedup()
  {
    AppendOnlyLog<string> original = new(e => e.EventType == "msg" ? e.Payload.GetProperty("text").GetString() : null);
    VestaEvent e1 = Evt("msg", new { text = "hello" });
    VestaEvent e2 = Evt("msg", new { text = "world" });
    original.Apply(Seq(e1, 1));
    original.Apply(Seq(e2, 2));

    ProjectionSnapshot snap = original.Snapshot();
    Assert.Equal(2, snap.LastSequence);

    AppendOnlyLog<string> restored = new(e => e.EventType == "msg" ? e.Payload.GetProperty("text").GetString() : null);
    restored.Restore(snap);

    Assert.Equal(new[] { "hello", "world" }, restored.State);
    Assert.Equal(2, restored.LastSequence);

    // Replaying the same events must not duplicate.
    restored.Apply(Seq(e1, 1));
    restored.Apply(Seq(e2, 2));
    Assert.Equal(new[] { "hello", "world" }, restored.State);
  }

  // ── LwwRegister ────────────────────────────────────────────────────────────

  [Fact]
  public void LwwRegister_SnapshotRestore_PreservesValueAndTimestamp()
  {
    LwwRegister<string> original = new(e => e.Payload.GetProperty("text").GetString());
    DateTimeOffset t1 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    DateTimeOffset t2 = DateTimeOffset.Parse("2026-01-01T00:00:10Z");
    original.Apply(Seq(Evt("set", new { text = "first" }, t1), 1));
    original.Apply(Seq(Evt("set", new { text = "second" }, t2), 2));

    ProjectionSnapshot snap = original.Snapshot();

    LwwRegister<string> restored = new(e => e.Payload.GetProperty("text").GetString());
    restored.Restore(snap);

    Assert.Equal("second", restored.State);
    Assert.Equal(t2, restored.LastModified);
    Assert.Equal(2, restored.LastSequence);

    // A stale event after restore must be rejected.
    restored.Apply(Seq(Evt("set", new { text = "stale" }, t1), 3));
    Assert.Equal("second", restored.State);
  }

  // ── LwwMap ─────────────────────────────────────────────────────────────────

  private static LwwMap<string, int> ScoreMap() => new(e =>
  {
    return e.EventType switch
    {
      "set" => LwwMapUpdate<string, int>.Set(e.Payload.GetProperty("key").GetString()!, e.Payload.GetProperty("value").GetInt32()),
      "remove" => LwwMapUpdate<string, int>.Remove(e.Payload.GetProperty("key").GetString()!),
      _ => null,
    };
  });

  [Fact]
  public void LwwMap_SnapshotRestore_PreservesEntriesAndTombstones()
  {
    LwwMap<string, int> original = ScoreMap();
    DateTimeOffset t1 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    DateTimeOffset t2 = DateTimeOffset.Parse("2026-01-01T00:00:10Z");
    DateTimeOffset t3 = DateTimeOffset.Parse("2026-01-01T00:00:20Z");
    original.Apply(Seq(Evt("set", new { key = "a", value = 1 }, t1), 1));
    original.Apply(Seq(Evt("set", new { key = "b", value = 42 }, t2), 2));
    original.Apply(Seq(Evt("remove", new { key = "a" }, t3), 3));

    ProjectionSnapshot snap = original.Snapshot();

    LwwMap<string, int> restored = ScoreMap();
    restored.Restore(snap);

    Assert.Single(restored.State);
    Assert.Equal(42, restored.State["b"]);
    Assert.False(restored.TryGetValue("a", out _));
    Assert.Equal(3, restored.LastSequence);

    // A stale set on the tombstoned key must remain ignored after restore.
    restored.Apply(Seq(Evt("set", new { key = "a", value = 999 }, t1), 4));
    Assert.False(restored.TryGetValue("a", out _));
  }

  // ── Base contract ──────────────────────────────────────────────────────────

  private sealed class CustomReducer : EventReducer<int>
  {
    private int _count;
    public override int State { get { lock (SyncRoot) { return _count; } } }
    protected override void Reduce(VestaEvent evt) => _count++;
  }

  [Fact]
  public void EventReducer_DefaultSnapshot_ThrowsSnapshotNotSupported()
  {
    CustomReducer r = new();
    Assert.Throws<SnapshotNotSupportedException>(() => r.Snapshot());
    Assert.Throws<SnapshotNotSupportedException>(() => r.Restore(new ProjectionSnapshot(0, "{}")));
  }
}
