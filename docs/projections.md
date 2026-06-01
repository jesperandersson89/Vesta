# Projections & conflict resolution

Vesta's job is to give every client the **same ordered stream of events** on each channel. Turning that stream into application state (the "current value" of a counter, the live list of online users, the contents of a todo list) is the app's responsibility. That transformation is called a **projection**.

The SDK provides four reusable building blocks so you don't have to hand-roll the boring parts. Pick the one whose conflict semantics match your data, give it a projector function, and feed it events.

**Available in all three client languages** with identical semantics:

| Language   | Namespace / module                 |
| ---------- | ---------------------------------- |
| C#         | `VestaCore.Projections`            |
| TypeScript | `vesta-client` (top-level exports) |
| Python     | `vesta_client` (top-level exports) |

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

## Snapshotting

Replaying the full event log on every cold start gets expensive fast. The C# SDK persists projection state via `IProjectionStore` (default impl: `SqliteProjectionStore`) so a client can boot, restore, and only fetch events newer than the saved sequence.

The three built-in reducers (`AppendOnlyLog<T>`, `LwwRegister<T>`, `LwwMap<TKey,TValue>`) implement `Snapshot()` / `Restore(ProjectionSnapshot)` out of the box. User-defined reducers can opt in by overriding both methods (default throws `SnapshotNotSupportedException`).

```csharp
using SqliteProjectionStore store = new("Data Source=projections.db");
LwwMap<string, Heartbeat> presence = new(Project);

// Cold start: restore if we have a snapshot, otherwise start from scratch.
await store.RestoreAsync("presence/myapp", "presence", presence);

// After connecting, fetch only events newer than what the snapshot already covers.
await connection.FetchAsync("presence/myapp", fromSequence: presence.LastSequence + 1);

// Periodically (or at shutdown), persist.
await store.SaveAsync("presence/myapp", "presence", presence);
```

The store keys snapshots by `(channelId, projectionId)` — a single channel may have multiple projections (e.g. chat history _and_ a presence map) saved independently. The `state_json` blob is opaque to the store; each reducer owns its own serialization format (including timestamps, tombstones, dedup sets — not just the public `State` view).

`ProjectionCheckpoint(string ChannelId, long LastSequence)` remains available as a smaller record if you want to track sequence positions without state — but for the common case, `Snapshot()` carries both.

> **TS / Python:** Snapshot APIs are not yet ported. Tracked as a follow-up to TODO #10 in [PLANNING.md](../PLANNING.md).

## Thread safety

The C# and Python implementations are thread-safe via an internal lock (`EventReducer.SyncRoot` / `_lock`). The TypeScript implementation has no lock — JavaScript is single-threaded so it isn't needed. `State` returns a defensive snapshot in C# / Python; in TypeScript it returns a `ReadonlyMap` view backed by the internal map (treat as read-only).

## Cross-language parity

The three implementations share identical semantics: `applyLocal` never advances `lastSequence`; `apply` advances only on strict-greater sequence; ties on timestamp preserve the existing value; `LwwMap` tombstones survive stale `set`s. The same channel projected by a C#, TS, and Python client converges to the same state. Worked examples:

- C# — [examples/Presence.CLI/PresenceState.cs](../examples/Presence.CLI/PresenceState.cs) uses `LwwMap<string, Heartbeat>` for presence.
- TypeScript — [examples/clipboard-ts/src/main.ts](../examples/clipboard-ts/src/main.ts) uses `LwwMap<string, ClipboardEntry>` for per-author latest paste.
- Python — see [clients/vesta-client-py/tests/test_projections.py](../clients/vesta-client-py/tests/test_projections.py) for the full primitive surface in use.

## See also

- [events.md](events.md) — what an event looks like before you project it
- [PLANNING.md §Conflict Resolution Strategy](../PLANNING.md) — design rationale and the broader pattern taxonomy
