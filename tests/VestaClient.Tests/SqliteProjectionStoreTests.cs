using System.Text.Json;
using VestaClient.Storage;
using VestaCore.Events;
using VestaCore.Projections;

namespace VestaClient.Tests;

public sealed class SqliteProjectionStoreTests : IDisposable
{
  private readonly SqliteProjectionStore _store = SqliteProjectionStore.CreateInMemory();

  public void Dispose() => _store.Dispose();

  [Fact]
  public async Task Load_NoSnapshot_ReturnsNull()
  {
    ProjectionSnapshot? loaded = await _store.LoadAsync("missing", "proj");
    Assert.Null(loaded);
  }

  [Fact]
  public async Task SaveAndLoad_Roundtrip()
  {
    ProjectionSnapshot snap = new(42, """{"items":[1,2,3]}""");
    await _store.SaveAsync("chess/lobby", "presence", snap);

    ProjectionSnapshot? loaded = await _store.LoadAsync("chess/lobby", "presence");
    Assert.NotNull(loaded);
    Assert.Equal(42, loaded!.LastSequence);
    Assert.Equal("""{"items":[1,2,3]}""", loaded.StateJson);
  }

  [Fact]
  public async Task Save_Overwrites_ExistingSnapshot()
  {
    await _store.SaveAsync("c", "p", new ProjectionSnapshot(1, "{}"));
    await _store.SaveAsync("c", "p", new ProjectionSnapshot(2, """{"v":1}"""));

    ProjectionSnapshot? loaded = await _store.LoadAsync("c", "p");
    Assert.Equal(2, loaded!.LastSequence);
    Assert.Equal("""{"v":1}""", loaded.StateJson);
  }

  [Fact]
  public async Task Delete_RemovesSnapshot()
  {
    await _store.SaveAsync("c", "p", new ProjectionSnapshot(1, "{}"));
    await _store.DeleteAsync("c", "p");
    Assert.Null(await _store.LoadAsync("c", "p"));
  }

  [Fact]
  public async Task ChannelAndProjectionIdsAreIndependentKeys()
  {
    await _store.SaveAsync("c1", "p1", new ProjectionSnapshot(1, """{"a":1}"""));
    await _store.SaveAsync("c1", "p2", new ProjectionSnapshot(2, """{"b":2}"""));
    await _store.SaveAsync("c2", "p1", new ProjectionSnapshot(3, """{"c":3}"""));

    Assert.Equal(1, (await _store.LoadAsync("c1", "p1"))!.LastSequence);
    Assert.Equal(2, (await _store.LoadAsync("c1", "p2"))!.LastSequence);
    Assert.Equal(3, (await _store.LoadAsync("c2", "p1"))!.LastSequence);
  }

  [Fact]
  public async Task ReducerRoundtrip_AppendOnlyLog()
  {
    AppendOnlyLog<string> log = new(e => e.EventType == "msg" ? e.Payload.GetProperty("text").GetString() : null);
    log.Apply(new SequencedEvent(
        new VestaEvent(Guid.NewGuid(), "ch", DateTimeOffset.UtcNow, "client", "msg",
            JsonSerializer.SerializeToElement(new { text = "hello" })),
        1, DateTimeOffset.UtcNow));

    await _store.SaveAsync("ch", "chat", log);

    AppendOnlyLog<string> restored = new(e => e.EventType == "msg" ? e.Payload.GetProperty("text").GetString() : null);
    bool found = await _store.RestoreAsync("ch", "chat", restored);

    Assert.True(found);
    Assert.Equal(new[] { "hello" }, restored.State);
    Assert.Equal(1, restored.LastSequence);
  }

  [Fact]
  public async Task RestoreAsync_NoSnapshot_ReturnsFalse()
  {
    AppendOnlyLog<string> reducer = new(_ => null);
    bool found = await _store.RestoreAsync("missing", "x", reducer);
    Assert.False(found);
    Assert.Equal(0, reducer.LastSequence);
  }
}
