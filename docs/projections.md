# Projections & conflict resolution

Vesta's job is to give every client the **same ordered stream of events** on each channel. Turning that stream into application state (the "current value" of a counter, the live list of online users, the contents of a todo list) is the app's responsibility. That transformation is called a **projection**.

The SDK provides four reusable building blocks in the `VestaCore.Projections` namespace so you don't have to hand-roll the boring parts. Pick the one whose conflict semantics match your data, give it a projector function, and feed it events.

> **Note:** The projection primitives are currently C#-only. The TypeScript and Python clients still hand-roll projections (e.g. `chess-web` builds its own presence map). Porting them is tracked as TODO #8 in [PLANNING.md](../PLANNING.md).

## Picking a primitive

| Primitive              | State shape                         | Conflict policy                                 | Use for                                                |
| ---------------------- | ----------------------------------- | ----------------------------------------------- | ------------------------------------------------------ |
| `AppendOnlyLog<T>`     | `IReadOnlyList<T>`                  | None — every event adds an item (dedup by `id`) | Chat messages, audit logs, todo items                  |
| `LwwRegister<T>`       | `T?`                                | Latest `timestamp` wins                         | A single shared value (selected color, document title) |
| `LwwMap<TKey, TValue>` | `IReadOnlyDictionary<TKey, TValue>` | Per-key LWW with tombstones                     | Presence, user preferences, key/value state            |
| `EventReducer<TState>` | `TState` (you define)               | You decide                                      | Custom projections (e.g. `TodoListState`)              |

All four extend `EventReducer<TState>` and share the same surface:

```csharp
void Apply(SequencedEvent sequenced);          // advances LastSequence
void Apply(IEnumerable<SequencedEvent> batch); // for catch-up
void ApplyLocal(VestaEvent evt);               // optimistic local apply, no sequence advance
long LastSequence { get; }
TState State { get; }                          // thread-safe snapshot
```

`ApplyLocal` is for **optimistic UI**: apply your own event immediately so the user sees instant feedback, then when the server echoes it back via `Apply(SequencedEvent)`, dedup logic prevents double-application.

## `AppendOnlyLog<T>`

```csharp
AppendOnlyLog<ChatMessage> chat = new(evt =>
    evt.EventType == "app.chat.message"
        ? new ChatMessage(evt.ClientId, evt.Payload.GetProperty("text").GetString()!)
        : null); // return null to skip an event

connection.OnEvent += msg => chat.Apply(new SequencedEvent(msg.Event, msg.Sequence, msg.ReceivedAt));
```

- Events whose projector returns `null` are skipped (so you can mix event types on one channel).
- Dedup by `evt.Id` means `ApplyLocal` followed by the server's echo is safe — the item only appears once.

## `LwwRegister<T>`

```csharp
LwwRegister<string> documentTitle = new(evt =>
    evt.EventType == "doc.title.set"
        ? evt.Payload.GetProperty("title").GetString()
        : null);
```

- The event with the **strictly greater** `Timestamp` wins. Ties preserve the existing value (deterministic across clients).
- Exposes `LastModified` so you can show "last edited at...".

## `LwwMap<TKey, TValue>`

```csharp
LwwMap<string, int> scores = new(evt => evt.EventType switch
{
    "score.set"    => LwwMapUpdate<string, int>.Set(
                          evt.Payload.GetProperty("player").GetString()!,
                          evt.Payload.GetProperty("points").GetInt32()),
    "score.remove" => LwwMapUpdate<string, int>.Remove(
                          evt.Payload.GetProperty("player").GetString()!),
    _              => null
});
```

- Each key has independent LWW. A stale `Set` arriving after a newer `Remove` does **not** resurrect the key (zombie prevention).
- Tombstones are kept internally; `State` and `TryGetValue` hide them.
- A genuinely-newer `Set` after a `Remove` reanimates the key with the new value.

This is the canonical pattern for presence — see [examples/Presence.CLI/PresenceState.cs](../examples/Presence.CLI/PresenceState.cs) for a real-world composition that layers TTL-based "is online" derivation on top of `LwwMap<string, Heartbeat>`.

## Custom: subclass `EventReducer<TState>`

When the four built-ins don't fit (e.g. multi-field per-item LWW like a todo list), subclass directly:

```csharp
public sealed class CounterState : EventReducer<int>
{
    private int _count;
    public override int State { get { lock (SyncRoot) { return _count; } } }

    protected override void Reduce(VestaEvent evt)
    {
        if (evt.EventType == "counter.increment") _count++;
        else if (evt.EventType == "counter.decrement") _count--;
    }
}
```

- `Reduce` is called under `SyncRoot` — your code doesn't need its own locking for the state field.
- Don't advance `LastSequence` yourself; the base class does it after `Reduce` returns successfully (and only for `Apply(SequencedEvent)`, not `ApplyLocal`).
- See [examples/TodoList.CLI/TodoListState.cs](../examples/TodoList.CLI/TodoListState.cs) for a hand-rolled projection with per-item LWW across multiple fields.

## Checkpointing

`ProjectionCheckpoint(string ChannelId, long LastSequence)` is a small record you can persist alongside your projection's state to skip a full replay on startup. On boot, load your snapshot, then `FETCH { channelId, sinceSequence: checkpoint.LastSequence }` to catch up incrementally.

The SDK does not yet provide a built-in snapshot store — pick whatever fits your app (JSON file, SQLite row, etc).

## Thread safety

All four primitives are thread-safe via an internal lock (`EventReducer.SyncRoot`). `State` returns a defensive snapshot — it's safe to enumerate without holding any lock. Mutation methods (`Apply`, `ApplyLocal`) can be called from any thread.

## See also

- [events.md](events.md) — what an event looks like before you project it
- [PLANNING.md §Conflict Resolution Strategy](../PLANNING.md) — design rationale and the broader pattern taxonomy
