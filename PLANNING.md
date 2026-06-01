# Vesta — Planning Document

> _The fire lives within — it doesn't come from somewhere external, it burns at the center of the home itself._

## Vision

Vesta is a protocol and runtime that lets developers build **networked applications without surrendering ownership to a central service**. The server is a commodity — like a git remote or a torrent tracker — not the source of truth. Clients hold their own state, speak a generic sync protocol, and can switch servers (or go serverless) without data loss.

The goal: if the server disappears tomorrow, the application still works locally and can reconnect to any other Vesta-compatible host.

---

## Core Principles

| #   | Principle                               | Implication                                                     |
| --- | --------------------------------------- | --------------------------------------------------------------- |
| 1   | **Data belongs to the client**          | All state is reconstructable from the client's local log        |
| 2   | **Server is a relay, not an authority** | Server stores and forwards; it doesn't interpret business logic |
| 3   | **Protocol over platform**              | Any client that speaks Vesta protocol can participate           |
| 4   | **Offline-first**                       | Operations queue locally and sync when connectivity returns     |
| 5   | **Conflict resolution is explicit**     | The protocol provides mechanisms; the app decides policy        |

---

## Architecture Overview

```
┌─────────────┐       ┌─────────────┐       ┌─────────────┐
│  Client A   │       │  Client B   │       │  Client C   │
│  (C# CLI)   │       │  (Python)   │       │  (JS/TS)    │
└──────┬──────┘       └──────┬──────┘       └──────┬──────┘
       │                     │                     │
       │      Vesta Protocol (WebSocket)           │
       │                     │                     │
       └─────────────────────┬─────────────────────┘
                             │
                      ┌──────┴──────┐
                      │ Vesta Server│
                      │  (C# host)  │
                      └──────┬──────┘
                             │
                      ┌──────┴──────┐
                      │  PostgreSQL │
                      └─────────────┘
```

### Components

| Component           | Role                                                   | Tech                                      |
| ------------------- | ------------------------------------------------------ | ----------------------------------------- |
| **VestaCore**       | Shared protocol types, serialization, conflict helpers | C# class library (net10.0)                |
| **VestaServer**     | Hosts channels, relays messages, persists log          | C# (ASP.NET Core minimal API + WebSocket) |
| **VestaClient**     | C# client library (connect, publish, subscribe)        | C# class library (net10.0)                |
| **vesta-client-ts** | TypeScript client library implementing the protocol    | TypeScript (npm package)                  |
| **vesta-client-py** | Python client library implementing the protocol        | Python (pip package)                      |

Example apps (in `examples/`) consume these libraries to prove the protocol works across languages.

---

## Protocol Design (Draft)

### Concepts

| Concept      | Description                                                                                                  |
| ------------ | ------------------------------------------------------------------------------------------------------------ |
| **Channel**  | A named scope of collaboration (like a git repo or torrent swarm)                                            |
| **Event**    | An immutable, timestamped, signed payload appended to a channel's log                                        |
| **Sequence** | A per-channel monotonic integer assigned by the server on append. This is the ordering and cursor mechanism. |
| **Snapshot** | A materialized view of state at a point in time (optimization, not source of truth)                          |

#### Sequence = Cursor (Terminology)

The term **sequence** is used everywhere — in storage, protocol, and code. It is a per-channel, monotonically increasing `BIGINT` assigned by the server when it appends an event.

A client's **position** in a channel is simply "the last sequence number I've seen." When the protocol says "give me events after X" — X is a sequence number.

We deliberately avoid using the word "cursor" as a separate concept. It's just a sequence value used as a bookmark:

```
"I've seen up to sequence 42"  →  SUBSCRIBE { channelId, fromSequence: 43 }
"What have I missed?"          →  FETCH { channelId, fromSequence: 43 }
"Here's a new event"           →  EVENT { channelId, event, sequence: 43 }
```

This avoids confusion between "cursor" (sounds abstract/opaque) and "sequence" (concrete integer). They are the same thing — the sequence number IS the cursor.

### Message Types

```
CLIENT → SERVER
  HELLO         { clientId, channels[], lastSequences{} }
  PUBLISH       { channelId, event }
  SUBSCRIBE     { channelId, fromSequence? }
  UNSUBSCRIBE   { channelId }
  FETCH         { channelId, fromSequence, toSequence?, limit? }

SERVER → CLIENT
  WELCOME       { serverId, channels[] }
  EVENT         { channelId, event, sequence, receivedAt }
  EVENTS_BATCH  { channelId, events: SequencedEvent[] }
  ACK           { channelId, eventId, sequence }
  ERROR         { code, message }
```

Every event delivered to a client is a **`SequencedEvent`** (the event + its server-assigned sequence + receivedAt). This means:

- `EVENT` is a single `SequencedEvent` pushed in real-time
- `EVENTS_BATCH` is an array of `SequencedEvent`s returned from a FETCH/catch-up
- The client can persist the exact sequence per event without any ambiguity
- `fromSequence`/`toSequence` on the batch are redundant (derivable from first/last element) but kept for convenience

### Event Model (Two Types)

Events are split into two distinct types to clearly separate client-authored data from server-assigned metadata:

**`VestaEvent`** — what the client creates and signs:

```json
{
    "id": "01961a3e-7c5d-7f8a-b1c2-d3e4f5a6b7c8",
    "channelId": "my-todo-list",
    "timestamp": "2026-05-29T12:00:00Z",
    "clientId": "Xk9mQ2pLvN3wR8tY5uA7bC",
    "type": "app.todo.item-added",
    "payload": { "title": "Buy milk", "done": false },
    "parentId": null,
    "signature": "base64url-ed25519-signature"
}
```

**`SequencedEvent`** — what the server stores and broadcasts (wraps VestaEvent + server metadata):

```json
{
    "event": {
        /* VestaEvent as above, untouched */
    },
    "sequence": 42,
    "receivedAt": "2026-05-29T12:00:00.123Z"
}
```

**C# model:**

```csharp
// Client-authored, immutable, signable
public sealed record VestaEvent(
    Guid Id,                    // UUID v7, client-generated
    string ChannelId,
    DateTimeOffset Timestamp,   // Client wall-clock
    string ClientId,            // base64url(sha256(pubkey))[:22]
    string EventType,           // e.g. "app.todo.item-added"
    JsonElement Payload,
    Guid? ParentId = null,
    string? Signature = null
);

// Server-assigned wrapper — preserves original event for signature verification
public sealed record SequencedEvent(
    VestaEvent Event,           // The original, untouched
    long Sequence,              // Server-assigned, per-channel monotonic
    DateTimeOffset ReceivedAt   // Server wall-clock
);
```

**Why composition over inheritance:**

- The original `VestaEvent` is preserved exactly as signed — signature verification doesn't need to strip fields
- Wire format is clean: `EVENT` messages send `{ event: {...}, sequence, receivedAt }`
- `IEventStore.AppendAsync` accepts `VestaEvent`, returns `SequencedEvent` — the type system enforces the flow
- Client outbox stores `VestaEvent` (no sequence yet); local event log stores `SequencedEvent` (received from server)

### Event Signing Specification

#### What Gets Signed

The signature covers **all client-authored fields except the signature itself**:

| Field        | Signed | Reason                                          |
| ------------ | ------ | ----------------------------------------------- |
| `id`         | ✅     | Prevents ID substitution                        |
| `channelId`  | ✅     | Prevents event being moved to different channel |
| `timestamp`  | ✅     | Prevents timestamp tampering                    |
| `clientId`   | ✅     | Binds event to its author                       |
| `type`       | ✅     | Prevents type substitution                      |
| `payload`    | ✅     | The actual data — obviously must be signed      |
| `parentId`   | ✅     | Prevents causal chain manipulation              |
| `signature`  | ❌     | Can't sign itself                               |
| `sequence`   | ❌     | Server-assigned, doesn't exist at signing time  |
| `receivedAt` | ❌     | Server-assigned, doesn't exist at signing time  |

#### Canonical Signing Input

The signing input is a **deterministic JSON byte string** produced using [RFC 8785 — JSON Canonicalization Scheme (JCS)](https://www.rfc-editor.org/rfc/rfc8785):

```json
{
    "channelId": "my-todo-list",
    "clientId": "Xk9mQ2pLvN3wR8tY5uA7bC",
    "id": "01961a3e-7c5d-7f8a-b1c2-d3e4f5a6b7c8",
    "parentId": null,
    "payload": { "done": false, "title": "Buy milk" },
    "timestamp": "2026-05-29T12:00:00Z",
    "type": "app.todo.item-added"
}
```

Rules (per RFC 8785):

- Keys sorted lexicographically (Unicode code point order)
- No whitespace between tokens
- Numbers use shortest representation (no trailing zeros)
- Strings use minimal escaping (`\n`, `\t`, etc. — not `\u000a`)
- `null` values are included (not omitted)
- UTF-8 encoding of the resulting string = the bytes that get signed

#### Signing Algorithm

```
signingInput = JCS_Canonicalize({
    id, channelId, timestamp, clientId, type, payload, parentId
})

signature = Base64Url(Ed25519_Sign(privateKey, UTF8_Bytes(signingInput)))
```

#### Verification

```
1. Extract event.signature, set aside
2. Construct signing input from remaining fields using JCS
3. Ed25519_Verify(publicKey, UTF8_Bytes(signingInput), Base64Url_Decode(signature))
```

The public key is derived from `clientId` — or looked up from the client's registered key. (For POC, the server can cache the mapping from clientId → publicKey established during the HELLO handshake.)

#### Why RFC 8785 (JCS)?

| Alternative                     | Problem                                                                                                                                   |
| ------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------- |
| "Just serialize in field order" | Field order varies by language/library — C# and Python won't produce same bytes                                                           |
| Custom deterministic serializer | One more thing to implement and keep in sync across 3 languages                                                                           |
| Hash the raw bytes client sent  | Server might re-serialize; can't verify from stored data alone                                                                            |
| **RFC 8785 (JCS)**              | **Standard, deterministic, implementations exist for C# (`JsonCanonicalization`), JS (`canonicalize`), Python (`json-canonicalization`)** |

#### Library References

| Language   | Package                        |
| ---------- | ------------------------------ |
| C#         | `JsonCanonicalization` (NuGet) |
| JavaScript | `canonicalize` (npm)           |
| Python     | `json-canonicalization` (PyPI) |

### Transport

- **Primary**: WebSocket (persistent, bidirectional, low latency)
- **Fallback**: HTTP long-poll or SSE for constrained environments
- **Serialization**: JSON for readability in POC; MessagePack or CBOR as future optimization

---

## Storage Model

### How GitHub / GitLab / Similar Systems Do It

Understanding how the systems we're drawing inspiration from handle storage:

**GitHub:**

- Git objects (the actual repos) stored on disk as pack files — this is their "event log"
- Metadata (issues, PRs, users, webhooks, permissions) → **MySQL** (sharded via Vitess)
- Real-time notifications → **Redis** pub/sub
- Replication of git data → custom system called Spokes (3-replica quorum writes)
- They don't use the filesystem as a database — they use it as git's native format and put everything else in proper databases

**GitLab:**

- Git objects on disk (same as GitHub)
- Metadata → **PostgreSQL** (their primary DB for everything)
- Background jobs → Sidekiq + Redis
- Real-time → ActionCable (WebSocket) backed by Redis

**EventStoreDB (the dedicated event sourcing DB):**

- Custom append-only log on disk
- Catch-up subscriptions (exactly our cursor pattern)
- Projections as first-class concept
- But: heavy, JVM-based, operational burden

**Key insight**: All of these use a **proper database for the structured/queryable data** and only use the filesystem for things that ARE files (git objects). None of them use SQLite for multi-tenant server workloads.

### Decision: PostgreSQL for the Server

**PostgreSQL** is the server-side storage engine because:

1. **LISTEN/NOTIFY** — when a client publishes an event, PG notifies all connected server processes instantly. This IS our real-time broadcast layer. No Redis needed for the POC.
2. **Concurrent writes** — MVCC handles multiple clients publishing simultaneously without blocking
3. **JSONB with indexing** — we can index into event payloads if needed, without changing schema
4. **Sequences** — native monotonic sequence generation, race-condition-free
5. **Partitioning** — if a channel gets huge, we can partition the events table by channel_id or time range
6. **Mature tooling** — migrations (DbUp or FluentMigrator), monitoring, backups, logical replication
7. **Docker one-liner for dev** — `docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=vesta postgres:18`
8. **Production-ready from day one** — no "migrate to a real DB later" tax

This doesn't violate the Vesta philosophy — the **client** still owns its data locally. The server's PostgreSQL is just a well-run relay. If it dies, clients still have their local state and can point at any new Vesta server.

### Do We Need Something Custom?

**No.** Here's why:

What we're building is essentially an **event store with pub/sub** — PostgreSQL does both natively. The event-sourcing pattern (append-only log, cursor-based reads, projections) maps directly to:

- `INSERT` → append event
- `SELECT ... WHERE sequence > $lastSeen ORDER BY sequence` → catch-up
- `LISTEN/NOTIFY` → real-time push
- `JSONB` → flexible payloads without schema migrations per app

If we outgrow PG (millions of events/sec), we could look at Kafka or a custom log — but that's a scale problem, not a design problem. The abstraction layer means we can swap later.

### Server Schema (PostgreSQL)

```sql
-- Core event log
CREATE TABLE events (
    id              UUID PRIMARY KEY,                   -- UUID v7 (time-sortable, globally unique)
    channel_id      TEXT NOT NULL REFERENCES channels(id),
    sequence        BIGINT NOT NULL,                    -- per-channel monotonic, server-assigned
    timestamp       TIMESTAMPTZ NOT NULL,
    client_id       TEXT NOT NULL,                      -- base64url(sha256(publicKey))[:22]
    event_type      TEXT NOT NULL,                      -- e.g. "app.todo.item-added"
    payload         JSONB NOT NULL,                     -- structured, indexable
    parent_id       UUID,                               -- causal link to previous event
    signature       TEXT,                               -- ed25519 sig (optional for POC)
    received_at     TIMESTAMPTZ NOT NULL DEFAULT now(),

    UNIQUE(channel_id, sequence)
);

-- Primary query pattern: "events in channel X after sequence Y"
CREATE INDEX idx_events_channel_seq ON events(channel_id, sequence);

-- Secondary: time-range queries
CREATE INDEX idx_events_channel_time ON events(channel_id, timestamp);

-- Optional: index into payload for app-specific queries
-- CREATE INDEX idx_events_payload ON events USING GIN (payload);

-- Channel metadata (id is a human-readable slug, like Kafka topics or git branches)
CREATE TABLE channels (
    id              TEXT PRIMARY KEY,                   -- e.g. "my-todo-list", "game-room-42"
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    metadata        JSONB                               -- app-specific config
);

-- Tracks each client's last-seen sequence per channel
CREATE TABLE client_positions (
    client_id       TEXT NOT NULL,
    channel_id      TEXT NOT NULL REFERENCES channels(id),
    last_sequence   BIGINT NOT NULL DEFAULT 0,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),

    PRIMARY KEY (client_id, channel_id)
);

-- Trigger: notify on new event (powers real-time push)
CREATE OR REPLACE FUNCTION notify_new_event() RETURNS trigger AS $$
BEGIN
    PERFORM pg_notify('vesta_events', json_build_object(
        'channel_id', NEW.channel_id,
        'sequence', NEW.sequence,
        'event_type', NEW.event_type
    )::text);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_notify_new_event
    AFTER INSERT ON events
    FOR EACH ROW EXECUTE FUNCTION notify_new_event();
```

### Per-Channel Sequences

One subtlety: we need a **per-channel** monotonic sequence, not a global one. Options:

```sql
-- Option A: Use a serializable transaction + MAX (simple, slight contention)
INSERT INTO events (id, channel_id, sequence, ...)
VALUES ($1, $2, (SELECT COALESCE(MAX(sequence), 0) + 1 FROM events WHERE channel_id = $2), ...);

-- Option B: Separate sequence table (better under contention)
CREATE TABLE channel_sequences (
    channel_id  TEXT PRIMARY KEY REFERENCES channels(id),
    next_seq    BIGINT NOT NULL DEFAULT 1
);

-- Atomic increment:
UPDATE channel_sequences SET next_seq = next_seq + 1 WHERE channel_id = $1 RETURNING next_seq - 1;
```

Option B is what most event stores do — it's a single-row lock per channel, which is fine because writes to the same channel should be serialized anyway (ordering matters).

### Client-Side Storage

Clients still need local storage. Here the choice is different — **SQLite** makes sense for clients:

| Client               | Storage                          | Why                               |
| -------------------- | -------------------------------- | --------------------------------- |
| **C# CLI / Desktop** | SQLite (`Microsoft.Data.Sqlite`) | Embedded, single-file, no install |
| **JS Browser**       | IndexedDB (or OPFS + `sql.js`)   | Browser-native, good capacity     |
| **JS Node**          | SQLite (`better-sqlite3`)        | Same as C# — file-based, portable |

Client-side schema:

```sql
-- Local copy of received events
CREATE TABLE events (
    id              TEXT PRIMARY KEY,
    channel_id      TEXT NOT NULL,
    sequence        INTEGER NOT NULL,
    timestamp       TEXT NOT NULL,
    client_id       TEXT NOT NULL,
    event_type      TEXT NOT NULL,
    payload         TEXT NOT NULL,           -- JSON
    parent_id       TEXT,
    signature       TEXT,
    received_at     TEXT NOT NULL,

    UNIQUE(channel_id, sequence)
);

CREATE INDEX idx_events_channel_seq ON events(channel_id, sequence);

-- Outbox: events created offline, pending sync
CREATE TABLE outbox (
    id              TEXT PRIMARY KEY,
    channel_id      TEXT NOT NULL,
    event_type      TEXT NOT NULL,
    payload         TEXT NOT NULL,
    created_at      TEXT NOT NULL,
    status          TEXT NOT NULL DEFAULT 'pending'  -- pending | sent | confirmed
);

-- Materialized state (app-specific projections)
CREATE TABLE state_snapshots (
    channel_id      TEXT NOT NULL,
    snapshot_key    TEXT NOT NULL,
    state           TEXT NOT NULL,           -- JSON
    at_sequence     INTEGER NOT NULL,
    updated_at      TEXT NOT NULL,

    PRIMARY KEY (channel_id, snapshot_key)
);
```

### Abstraction Layer

Storage is accessed through interfaces — server and client use different implementations of the same contract:

```csharp
public interface IEventStore
{
    Task<SequencedEvent> AppendAsync(VestaEvent evt);  // Input: client event → Output: sequenced
    Task<IReadOnlyList<SequencedEvent>> GetEventsAsync(string channelId, long fromSequence, int limit = 100);
    Task<long> GetLatestSequenceAsync(string channelId);
    Task UpdatePositionAsync(string clientId, string channelId, long sequence);
}

// Server implementation: NpgsqlEventStore (PostgreSQL)
// Client implementation: SqliteEventStore (local read cache — stores SequencedEvents received from server)
```

### Data Access Strategy: EF Core + Raw Npgsql

**EF Core** for the "cold" paths (schema management, metadata CRUD):

- Schema migrations (channels, client_positions, identity_links, channel_access)
- Channel CRUD, access control queries, position updates
- DI integration with ASP.NET Core
- DbContext for the relational/admin side of the server

**Raw Npgsql** for the "hot" paths (event throughput, real-time):

- Event append (`INSERT INTO events ...`) — avoids change tracker overhead
- Event range reads (`SELECT ... WHERE sequence > $lastSeen`) — simple, fast
- Bulk replay from client outbox (`COPY` or batch insert)
- `LISTEN/NOTIFY` for real-time broadcast — EF Core doesn't support this at all

```csharp
// EF Core DbContext — metadata and admin
public class VestaDbContext : DbContext
{
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<ClientPosition> ClientPositions => Set<ClientPosition>();
    public DbSet<IdentityLink> IdentityLinks => Set<IdentityLink>();
    public DbSet<ChannelAccess> ChannelAccess => Set<ChannelAccess>();
}

// Raw Npgsql — event hot path
public class NpgsqlEventStore : IEventStore
{
    private readonly NpgsqlDataSource _dataSource;

    public async Task<long> AppendAsync(VestaEvent evt)
    {
        await using var cmd = _dataSource.CreateCommand(
            "INSERT INTO events (id, channel_id, sequence, ...) VALUES ($1, $2, ...) RETURNING sequence");
        // ...
    }
}

// Raw Npgsql — real-time notifications
public class NpgsqlEventListener : IAsyncDisposable
{
    // Uses LISTEN/NOTIFY to push new events to WebSocket connections
}
```

This is the same pattern used in Microsoft's eShop reference architecture and other high-throughput .NET apps — EF Core for convenience where it matters, raw ADO.NET/Dapper/Npgsql where performance matters.

### Data Export

Supporting **JSON Lines export** per channel as a portability/backup format:

```
{"id":"...","channelId":"...","sequence":1,"type":"app.todo.item-added","payload":{...},"timestamp":"..."}
{"id":"...","channelId":"...","sequence":2,"type":"app.todo.item-done","payload":{...},"timestamp":"..."}
```

This is the ultimate escape hatch — human-readable, grep-able, importable into any new Vesta server or consumed by a script.

### Summary

| Layer                   | Storage       | Reason                                                               |
| ----------------------- | ------------- | -------------------------------------------------------------------- |
| **Server**              | PostgreSQL 18 | Concurrent writes, LISTEN/NOTIFY, JSONB, sequences, production-grade |
| **C# Client**           | SQLite        | Embedded, single-file, offline-capable                               |
| **JS Client (browser)** | IndexedDB     | Browser-native                                                       |
| **JS Client (Node)**    | SQLite        | Same as C# client                                                    |
| **Export format**       | JSON Lines    | Human-readable portability                                           |

---

## Conflict Resolution Strategy

Since multiple clients can produce events concurrently, conflicts are inevitable. Vesta provides **building blocks**, not a single policy:

| Strategy                    | Good for                       | How                                |
| --------------------------- | ------------------------------ | ---------------------------------- |
| **Last-writer-wins (LWW)**  | User preferences, simple state | Timestamp comparison               |
| **Append-only (CRDT-like)** | Todo items, chat messages      | No conflict — all events are valid |
| **Operational Transform**   | Collaborative text (future)    | Transform concurrent ops           |
| **Application-level merge** | Games, custom logic            | App registers a merge handler      |

For the POC, focus on **append-only** and **LWW** — they cover all four target use cases.

### SDK Primitives (VestaCore)

These are the concrete building blocks the SDK provides. Apps compose them to build their state:

| Primitive              | Purpose                                                 | Example use                                 |
| ---------------------- | ------------------------------------------------------- | ------------------------------------------- |
| `EventReducer<TState>` | Fold a channel's event stream into a typed state object | Base abstraction — all others build on this |
| `AppendOnlyLog<T>`     | Ordered list where all events are valid (no conflicts)  | Chat messages, activity feed, audit trail   |
| `LwwRegister<T>`       | Single value, last-writer-wins by timestamp             | User display name, clipboard content        |
| `LwwMap<TKey, TValue>` | Key-value map where each key uses LWW independently     | User preferences, todo item states          |
| `ProjectionCheckpoint` | Tracks which sequence a projection has processed up to  | Avoiding re-processing on reconnect         |

**C# sketch:**

```csharp
// Base: reduces an event stream into state
public abstract class EventReducer<TState>
{
    public TState State { get; private set; }
    public long LastSequence { get; private set; }

    public void Apply(SequencedEvent sequencedEvent)
    {
        State = Reduce(State, sequencedEvent);
        LastSequence = sequencedEvent.Sequence;
    }

    protected abstract TState Reduce(TState current, SequencedEvent evt);
}

// Append-only list — every event of a matching type gets added
public class AppendOnlyLog<T> : EventReducer<IReadOnlyList<T>>
{
    private readonly Func<VestaEvent, T?> _project;
    // Reduce: append projected item to list
}

// Single value, last writer wins by timestamp
public class LwwRegister<T> : EventReducer<T?>
{
    // Reduce: if event.Timestamp > current timestamp, replace value
}

// Key-value map where each key independently uses LWW
public class LwwMap<TKey, TValue> : EventReducer<IReadOnlyDictionary<TKey, TValue>>
{
    // Reduce: for the given key, apply LWW logic per-key
}

// Tracks processing position (for resumable projections)
public record ProjectionCheckpoint(string ChannelId, long LastSequence);
```

**How apps use these:**

```csharp
// Todo app: items are append-only, but each item's "done" state is LWW
var todoItems = new AppendOnlyLog<TodoItem>(evt => /* project "item-added" events */);
var itemStates = new LwwMap<Guid, bool>(evt => /* project "item-toggled" events */);

// Chat app: messages are purely append-only
var messages = new AppendOnlyLog<ChatMessage>(evt => /* project "message-sent" events */);

// Preferences: each key is an independent LWW register
var prefs = new LwwMap<string, string>(evt => /* project "pref-set" events */);
```

These primitives don't need to be implemented in milestone 1, but they define the SDK's direction and show app developers what's available.

---

## Ephemeral Events & Presence

### The Problem

Not all events should live forever. Presence ("user X is online") becomes stale after seconds. Storing millions of heartbeat events permanently wastes space and pollutes catch-up replays.

### Design

Ephemeral events are **normal events with an expiration**. They flow through the same protocol but receive special treatment in storage and replay.

#### Event Metadata Extension

Events can carry optional metadata indicating ephemerality:

```json
{
    "id": "...",
    "channelId": "my-app/presence",
    "type": "app.presence.heartbeat",
    "payload": { "status": "online", "lastActive": "2026-05-30T10:00:00Z" },
    "metadata": {
        "ttlSeconds": 30
    }
}
```

The `metadata.ttlSeconds` field (or `metadata.expiresAt` as an absolute timestamp) tells the server this event is short-lived.

#### Storage & Replay Rules

| Behavior                         | Rule                                                                    |
| -------------------------------- | ----------------------------------------------------------------------- |
| **Persist?**                     | Yes — store normally for simplicity (POC). Server can clean up later.   |
| **Relay in real-time?**          | Yes — push to all subscribers immediately like any event.               |
| **Include in catch-up (FETCH)?** | **No** — exclude expired events from catch-up by default.               |
| **Client local storage?**        | Optional — client can choose to discard expired events from its SQLite. |
| **Server cleanup?**              | Background job periodically deletes events past their `expiresAt`.      |

#### Server Implementation (POC)

```sql
-- Add to events table
ALTER TABLE events ADD COLUMN expires_at TIMESTAMPTZ;

-- Catch-up query excludes expired events
SELECT * FROM events
WHERE channel_id = $1
  AND sequence > $2
  AND (expires_at IS NULL OR expires_at > now())
ORDER BY sequence
LIMIT $3;

-- Cleanup job (runs periodically)
DELETE FROM events WHERE expires_at IS NOT NULL AND expires_at < now();
```

#### Presence Pattern

For presence specifically, the recommended pattern is:

1. Client publishes heartbeat events every N seconds to a `{app}/presence` channel
2. Each heartbeat has `ttlSeconds: 30` (or 2× the heartbeat interval)
3. Other clients subscribe to the presence channel and maintain a local map of `clientId → lastSeen`
4. If no heartbeat arrives within TTL, the user is considered offline
5. On catch-up (reconnect), expired heartbeats are excluded — client only sees currently-online users

This is the same model as Redis key expiry or DNS TTL — the data self-describes its freshness.

---

## Authentication & Identity

### Two Separate Concerns

Authentication in Vesta is actually **two different problems**:

| Concern           | Question                                         | Who decides                        |
| ----------------- | ------------------------------------------------ | ---------------------------------- |
| **Identity**      | "Who is this client?"                            | The client itself (self-sovereign) |
| **Authorization** | "Is this client allowed to access this channel?" | The server operator                |

These should be decoupled. A client's identity is portable across servers. A server's access policy is its own business.

### Layer 1: Client Identity (Self-Sovereign)

Every Vesta client generates an **Ed25519 keypair** on first run. This is their identity — permanently, across all servers.

```
Client identity = Ed25519 keypair
  - Public key  = client ID (displayed as base64url or fingerprint)
  - Private key = stored locally, never leaves the device
  - Events are signed with the private key
```

**Why keypair-based:**

- No dependency on any server or provider for identity
- Works offline — you can sign events without network
- Portable — move your key to a new device, you're the same "person"
- Verifiable — anyone can verify an event came from a specific client
- Same model as SSH keys / git signing — developers already understand this

**Client ID derivation:**

```
clientId = base64url(sha256(publicKey))[:22]  // ~128 bits, URL-safe
```

This means if a server disappears, your identity persists. You connect to a new server with the same keypair and you're still "you."

### Layer 2: Server Authentication (How You Prove Identity)

When a client connects to a server, it needs to prove it owns the keypair. This is the **auth handshake**:

```
CLIENT → SERVER:  HELLO { publicKey, timestamp, signature(timestamp) }
SERVER:           Verifies signature, now knows client is who they claim
SERVER → CLIENT:  WELCOME { serverId, token }
```

The server issues a **session token** (JWT or opaque) after verifying the challenge signature. Subsequent messages use this token. This is essentially challenge-response auth — similar to SSH auth.

### Layer 3: Authorization (Server Operator's Choice)

This is where external providers come in. The server operator decides **who gets access to what**. Vesta should support multiple strategies via a pluggable auth middleware:

| Strategy                 | How                                                                       | Good for                               |
| ------------------------ | ------------------------------------------------------------------------- | -------------------------------------- |
| **Open**                 | Any valid client can connect and create/join channels                     | Dev/local/personal servers             |
| **Invite-only**          | Server admin pre-registers allowed public keys                            | Small teams, private servers           |
| **Channel secrets**      | Channel has a shared secret; client must present it to subscribe          | Simple sharing (like a game room code) |
| **External OIDC/OAuth2** | Server verifies client has a valid token from Google/GitHub/Azure AD/etc. | Enterprise, multi-tenant servers       |
| **Custom webhook**       | Server calls an external URL to ask "is this client allowed?"             | Full flexibility                       |

### External Provider Flow (OIDC)

For servers that want to gate access via Google/GitHub/Azure AD:

```
1. Client authenticates with OIDC provider (browser redirect or device flow)
2. Client receives an ID token (JWT) from provider
3. Client connects to Vesta server, presents both:
   - Its Vesta keypair (identity)
   - The OIDC token (authorization proof)
4. Server verifies OIDC token, links the external identity to the Vesta public key
5. Server applies access policies based on the linked external identity

Result: Client's Vesta identity is still self-sovereign,
        but the server knows "this key belongs to jesper@company.com"
```

This is the same model as GitHub: you have SSH keys (your identity) but your GitHub account (linked to an email/OAuth) determines what repos you can access.

### POC Implementation Plan

For the POC, implement in layers:

**Phase 1 (Milestone 1-2): Keypair + Open access**

- Client generates Ed25519 keypair on first run, stores in config file
- Client signs HELLO with its key
- Server verifies signatures, allows all valid clients
- Events carry signatures for integrity verification

**Phase 2 (Milestone 3): Channel secrets**

- Channels can optionally require a shared secret
- Client presents secret when subscribing
- Simple enough for game room codes and private todo lists

**Phase 3 (Milestone 4+): OIDC integration**

- Server supports configuring one or more OIDC providers
- Client can link external identity to keypair
- Server enforces access policies based on linked identities

### Server Auth Configuration (Draft)

```json
// vesta-server.config.json
{
    "auth": {
        "mode": "open", // "open" | "invite" | "oidc" | "webhook"

        // For "invite" mode:
        "allowedKeys": ["base64url-public-key-1", "base64url-public-key-2"],

        // For "oidc" mode:
        "oidc": {
            "providers": [
                {
                    "name": "google",
                    "issuer": "https://accounts.google.com",
                    "clientId": "your-client-id",
                    "allowedDomains": ["yourcompany.com"]
                },
                {
                    "name": "github",
                    "issuer": "https://token.actions.githubusercontent.com",
                    "clientId": "your-github-app-id"
                }
            ]
        },

        // For "webhook" mode:
        "webhook": {
            "url": "https://your-service.com/vesta/auth-check",
            "secret": "hmac-shared-secret"
        }
    }
}
```

### Key Storage (Client-Side)

```
~/.vesta/
├── identity.json           # { "publicKey": "...", "privateKey": "..." (encrypted) }
├── known_servers.json      # Like ~/.ssh/known_hosts
└── tokens/
    └── server-abc.token    # Cached session token
```

The private key should be encrypted at rest (passphrase or OS keychain integration). For the POC, plain file is acceptable.

### Why Not Just OAuth Tokens?

If we only used external OAuth tokens for identity:

- Client identity is **tied to the provider** — if Google locks your account, you lose your Vesta identity
- Doesn't work offline — can't sign events without network to refresh tokens
- Server-dependent — different servers might use different providers, so you'd be a different "person" on each

With keypair-first identity:

- You ARE your key, regardless of any provider
- External auth is just an **authorization layer** the server optionally applies
- Same philosophy as Vesta: the fire lives within, not in Google's infrastructure

### Database Schema Addition (Server)

```sql
-- Links external identities to Vesta public keys
CREATE TABLE identity_links (
    public_key      TEXT NOT NULL,
    provider        TEXT NOT NULL,           -- "google", "github", "azure-ad"
    external_id     TEXT NOT NULL,           -- email or sub claim
    linked_at       TIMESTAMPTZ NOT NULL DEFAULT now(),

    PRIMARY KEY (public_key, provider)
);

-- Channel access control
CREATE TABLE channel_access (
    channel_id      TEXT NOT NULL REFERENCES channels(id),
    grant_type      TEXT NOT NULL,           -- "public_key" | "provider_identity" | "secret"
    grant_value     TEXT NOT NULL,           -- the key, email, or hashed secret
    permission      TEXT NOT NULL DEFAULT 'read_write',  -- "read" | "write" | "read_write" | "admin"
    granted_at      TIMESTAMPTZ NOT NULL DEFAULT now(),

    PRIMARY KEY (channel_id, grant_type, grant_value)
);
```

### Channel Lifecycle

#### Key Questions & Answers

| Question                                    | Answer                                               |
| ------------------------------------------- | ---------------------------------------------------- |
| Who can create a channel?                   | Depends on server auth mode (see below)              |
| Does SUBSCRIBE create a channel if missing? | Yes, in open mode. No, in private mode.              |
| Does PUBLISH create a channel if missing?   | Yes, in open mode. No, in private mode.              |
| Can channel metadata change over time?      | Yes — channel admins can update metadata.            |
| How are channel secrets configured?         | By the channel creator or server admin via API.      |
| Can channels be deleted?                    | By admins only. Soft-delete (mark inactive) for POC. |

#### Creation Rules by Auth Mode

| Server Mode        | Channel Creation Rule                                                                                               |
| ------------------ | ------------------------------------------------------------------------------------------------------------------- |
| **Open**           | First PUBLISH or SUBSCRIBE to a non-existent channel implicitly creates it. The first client becomes channel admin. |
| **Invite-only**    | Only server-registered clients can create channels (explicit API call or admin tool).                               |
| **OIDC / Webhook** | Channel creation requires valid authorization. Server calls auth check before creating.                             |

#### Channel States

```
Created  →  Active  →  Archived (soft-delete)
                    →  (events still readable, no new publishes)
```

#### POC Rules (Simple)

For the POC, keep it minimal:

1. **Open mode** (default): Any authenticated client can create channels implicitly by publishing or subscribing to a new channel ID.
2. The creating client is recorded as `admin` in `channel_access`.
3. Channel metadata is mutable by admins (e.g., setting a secret, changing config).
4. No deletion in POC — channels live forever once created.
5. Channel IDs are validated as slugs: lowercase alphanumeric + hyphens + slashes, max 128 chars. (e.g., `my-app/todo-list`, `game-room-42`)

#### Channel ID Format

```
channel_id = [a-z0-9][a-z0-9\-/]*[a-z0-9]
             └─ 1-128 characters
             └─ allows namespacing via slashes: "myapp/chat/general"
             └─ no leading/trailing hyphens or slashes
```

Apps are encouraged to namespace their channels: `{app-name}/{channel-type}/{instance}`. This is convention, not enforced by the server.

### Target Use Cases

| #   | Use Case                            | Pattern                          | What it proves                                                    |
| --- | ----------------------------------- | -------------------------------- | ----------------------------------------------------------------- |
| 1   | **Todo list with sync**             | Append-only + LWW per item       | Basic CRUD over events, multi-device sync                         |
| 2   | **Simple game with state tracking** | Single-writer append             | Progress/score synced across devices                              |
| 3   | **Turn-based multiplayer game**     | Multi-writer ordered events      | Shared channel, sequencing matters                                |
| 4   | **User preferences with sync**      | LWW key-value                    | Replace-semantics, minimal state                                  |
| 5   | **Chat room**                       | Append-only, many-writer         | Real-time fan-out, high write volume, many subscribers            |
| 6   | **Presence ("who's online")**       | Ephemeral LWW with TTL/heartbeat | Not all state is permanent; forces TTL/volatile channel design    |
| 7   | **Shared clipboard / pinboard**     | Single-writer, replace-semantics | "Give me the latest value" — proves Vesta isn't only growing logs |

### Protocol Patterns Exercised

```
                        Append-only    LWW    Multi-writer   Ephemeral   Fan-out
Todo list                   ✓           ✓         -             -          -
Game state                  ✓           -         -             -          -
Multiplayer game            ✓           -         ✓             -          -
User preferences            -           ✓         -             -          -
Chat room                   ✓           -         ✓             -          ✓
Presence                    -           ✓         ✓             ✓          ✓
Shared clipboard            -           ✓         -             -          ✓
```

This gives us full coverage of the protocol's capabilities with minimal overlap.

### Milestone 1: Foundation (Week 1–2)

- [ ] Define `Event`, `Channel`, `Cursor` types in VestaCore
- [ ] Implement JSON serialization for protocol messages
- [ ] Implement server with PostgreSQL event log + WebSocket endpoint
- [ ] Implement basic C# client library (connect, publish, subscribe, receive)
- [ ] Ed25519 keypair generation and event signing

### Milestone 2: Persistence & Sync (Week 2–3)

- [ ] Server-side PostgreSQL persistence with LISTEN/NOTIFY
- [ ] Client-side SQLite local storage
- [ ] Offline outbox + sync-on-reconnect
- [ ] Cursor tracking and catch-up (FETCH)
- [x] Ephemeral/volatile channel support (TTL on events for presence) — `VestaEvent.Metadata` carries unsigned `ttlSeconds`; `VestaEventMetadata.TryGetTtlSeconds` reads it; server persists `expires_at` column; `ExpiredEventCleanupService` sweeps expired rows; both stores filter expired events from catch-up

### Milestone 3: Reference Clients (Week 3–4)

- [ ] C# CLI client: interactive **chat room** (fastest demo of real-time fan-out)
- [ ] C# CLI client: interactive **todo-list** app over Vesta
- [ ] C# CLI client: turn-based **tic-tac-toe** over Vesta
- [ ] C# CLI client: **shared clipboard** (copy on one terminal, paste on another)
- [ ] JavaScript/TypeScript client: browser-based todo-list + chat connecting to same server

### Milestone 4: Hardening & Remaining Use Cases (Week 4+)

- [ ] Event signing verification end-to-end
- [ ] Channel access control (shared-secret for POC)
- [ ] **Presence** demo (online indicator with heartbeat + TTL)
- [ ] **User preferences** demo (settings sync across devices)
- [ ] Snapshot support for large channels
- [ ] Basic observability (event counts, connected clients, channel stats)

---

## Solution Structure

The repo is split into two clear concerns:

1. **Core** — the Vesta protocol, server, and client libraries (the "product")
2. **Examples** — demo applications that consume the client libraries (the "proof")

```
Vesta/
├── Vesta.sln
├── PLANNING.md
├── Directory.Build.props
├── .github/
│   └── copilot-instructions.md
│
├── src/                            # ═══ CORE INFRASTRUCTURE ═══
│   ├── VestaCore/                  # Shared types, protocol messages, serialization
│   ├── VestaServer/                # ASP.NET Core host (relay + persistence)
│   └── VestaClient/                # C# client library (connect, publish, subscribe)
│
├── clients/                        # ═══ CLIENT LIBRARIES (other languages) ═══
│   ├── vesta-client-ts/            # TypeScript client library (npm package)
│   └── vesta-client-py/            # Python client library (pip package)
│
├── examples/                       # ═══ EXAMPLE APPLICATIONS ═══
│   ├── ChatRoom.CLI/               # C# — real-time chat (fan-out demo)
│   ├── TodoList.CLI/               # C# — synced todo list (append + LWW demo)
│   ├── Presence.CLI/               # C# — presence/heartbeat (ephemeral + replace demo)
│   ├── clipboard-ts/               # TypeScript — shared clipboard (proves TS client works)
│   ├── collab-edit-py/             # Python — collaborative text editor (tkinter GUI)
│   └── colorwheel-py/              # Python — color wheel picker (proves Python client works)
│
└── tests/
    ├── VestaCore.Tests/
    ├── VestaServer.Tests/
    └── VestaClient.Tests/
```

### What Goes Where

| Question                                | Answer                     |
| --------------------------------------- | -------------------------- |
| Protocol message types?                 | `src/VestaCore/`           |
| Event store interface?                  | `src/VestaCore/`           |
| WebSocket server handler?               | `src/VestaServer/`         |
| PostgreSQL event store impl?            | `src/VestaServer/`         |
| C# connect/publish/subscribe API?       | `src/VestaClient/`         |
| SQLite client-side storage?             | `src/VestaClient/`         |
| "How to build a todo app with Vesta"    | `examples/TodoList.CLI/`   |
| "How to build a clipboard sync in TS"   | `examples/clipboard-ts/`   |
| Python protocol implementation?         | `clients/vesta-client-py/` |
| Python collab editor using the library? | `examples/collab-edit-py/` |

This separation means:

- Someone browsing the repo immediately sees what's core vs. what's a demo
- Client libraries in other languages live in `clients/` — each is its own package
- Examples are self-contained apps that reference the client libraries
- You could ship `src/` and `clients/` as the "Vesta SDK" — examples are documentation

---

## Open Questions (To Refine)

1. ~~**Identity**: Should clients have persistent identity (keypair) or is anonymous + channel-secret enough for POC?~~ → **Decided: Ed25519 keypairs, self-sovereign identity**
2. ~~**Channel discovery**: How does a client find/join a channel? Server-provided list? Out-of-band share link?~~ → **Decided: Not our problem — app creators configure which server/channels their app connects to. Most will self-host for cost reasons. Vesta provides the connection primitives, the app decides the UX.**
3. ~~**Event ordering**: Logical clocks (Lamport/vector) vs. wall-clock timestamps? Wall-clock is simpler for POC but less correct.~~ → **Decided: Hybrid approach — server-assigned sequence (authoritative total order) + client wall-clock timestamp (display/LWW tiebreaker) + client-local Lamport counter (offline ordering). No vector clocks — too complex, unbounded growth.**
4. ~~**Max channel size**: Should the server enforce limits, or is that an app concern?~~ → **Decided: Server-enforced. The server operator sets limits (max events, max payload size, retention policy). Sensible defaults, configurable per channel.**
5. ~~**Multi-server**: Is federation/replication in scope, or strictly single-server for now?~~ → **Decided: Single-server for POC, but design must not preclude multi-server later. Events carry globally-unique IDs (UUID v7), client identity is server-independent, and the protocol doesn't assume a single server URL.**
6. ~~**Auth**: Mutual TLS, tokens, or just shared secrets for the POC?~~ → **Decided: Keypair challenge-response + pluggable server-side authorization (open → invite → OIDC)**

---

## Analogies That Guide Design

| Analogy                   | What we borrow                                            |
| ------------------------- | --------------------------------------------------------- |
| **Git**                   | Distributed log, local-first, remotes are interchangeable |
| **BitTorrent tracker**    | Server helps peers find each other; doesn't own the data  |
| **CRDTs**                 | Conflict-free data structures for certain state shapes    |
| **SSE/WebSocket pub-sub** | Real-time event delivery model                            |
| **ActivityPub**           | Federated protocol over HTTP (future inspiration)         |

---

## Implementation Order

Refined step-by-step build order. The key insight: **get the protocol loop working with in-memory storage first**, then layer in persistence complexity.

| #   | Step                                                                                                | What it proves                                               |
| --- | --------------------------------------------------------------------------------------------------- | ------------------------------------------------------------ |
| 1   | Restructure solution into `src/`, `tests/`, `examples/`, `clients/`                                 | Clean foundation, `dotnet build` works                       |
| 2   | Define protocol/domain types in VestaCore (`VestaEvent`, `SequencedEvent`, `ProtocolMessage`, etc.) | Type system compiles, all message shapes defined             |
| 3   | Add protocol JSON serialization tests                                                               | Messages round-trip correctly, canonical form verified       |
| 4   | Implement in-memory `IEventStore`                                                                   | Storage abstraction works without any DB dependency          |
| 5   | Build minimal WebSocket server using in-memory store                                                | Server accepts connections, relays events, assigns sequences |
| 6   | Build C# client library and prove round-trip                                                        | Client → Server → Client event flow works end-to-end         |
| 7   | Swap server store to PostgreSQL (`NpgsqlEventStore`)                                                | Real persistence, LISTEN/NOTIFY, concurrent writes           |
| 8   | Add SQLite client-side cache + outbox                                                               | Offline support, reconnect catch-up                          |
| 9   | Add Ed25519 signing + verification                                                                  | Events are signed, server verifies, integrity proven         |
| 10  | Build example apps (chat, todo, tic-tac-toe, clipboard)                                             | Protocol handles real use cases across patterns              |

**Why this order:**

- Steps 1–6 produce a fully working system with zero infrastructure dependencies (no Docker, no PostgreSQL). You can `dotnet run` the server and two clients and watch events flow.
- Step 7 adds PostgreSQL — by this point the protocol is proven and stable, so you're only swapping one interface implementation.
- Steps 8–9 add client resilience and security.
- Step 10 is the payoff — building real apps that prove the SDK works.

Each step has a clear "it works" signal — no half-finished layers.

---

## Example App Pattern

Examples are **real applications**, not protocol demos. They exist to prove the SDK works for actual use cases and to serve as reference implementations for developers building on Vesta.

### Requirements for Every Example

Each example app must demonstrate all of the following:

| Requirement                           | What it means                                                                                             |
| ------------------------------------- | --------------------------------------------------------------------------------------------------------- |
| **Durable identity**                  | Uses `VestaIdentity.LoadOrCreate(path)` — identity persists across runs, user is always the same "person" |
| **Local event cache**                 | Uses `SqliteClientEventStore` (or equivalent) — events received from the server are persisted locally     |
| **Offline behavior**                  | Works meaningfully without a server connection — queues events to outbox, displays cached state           |
| **Projection / state reconstruction** | Rebuilds application state from the event log on startup (not just displaying raw events)                 |
| **Clear event schemas**               | Documents the event types and payload shapes the app uses (in code comments or a section in the README)   |

### Structure of an Example

```
examples/MyApp.CLI/
├── MyApp.CLI.csproj          # References VestaClient + VestaCore
├── Program.cs                # Entry point — wiring, connection, UI loop
├── State/                    # (optional) Projection/reducer classes
│   └── MyAppState.cs
└── README.md                 # What it does, how to run, event schema docs
```

### Event Schema Documentation

Each example should declare its event types clearly, either in a README or as doc comments:

```
Channel: "myapp/todos"
Event types:
  - app.todo.item-added    { title: string, id: string }
  - app.todo.item-toggled  { id: string, done: bool }
  - app.todo.item-removed  { id: string }
```

### Anti-Patterns (What Examples Should NOT Be)

- ❌ A thin wrapper that just prints raw JSON from the server
- ❌ Ephemeral state that disappears on restart (no local cache)
- ❌ Anonymous / throwaway identity per session
- ❌ Only works while connected — crashes or shows nothing offline
- ❌ Undocumented payload shapes that require reading source to understand

---

### Faster realtime messages without db storage

Im going to add a `volatile` flag to the protocol that means "don't store this event in the database, just relay it to current subscribers". This is for things like presence heartbeats where you want real-time updates but no history. The server will still assign a sequence number for ordering, but won't persist the event. Clients won't see these on catch-up, only in real-time.

## TODO

Known gaps and deferred work, in rough priority order.

| #   | Area                                           | What's missing                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          | Notes                                                                                                                                                                                                                                                                                 |
| --- | ---------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1   | **Ephemeral events / TTL**                     | ✅ **Done.** `VestaEvent.Metadata` carries unsigned `ttlSeconds`. Server computes `expires_at` at PUBLISH (Npgsql + InMemory stores), filters expired rows from catch-up reads, and `ExpiredEventCleanupService` (`EventCleanup:Enabled`, opt-in, 60 s default) sweeps expired rows in the Postgres branch. Migration `20260601194710_AddExpiresAtToEvents`.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            | See `src/VestaServer/Storage/ExpiredEventCleanupService.cs` and `src/VestaServer/Storage/NpgsqlEventStore.cs`.                                                                                                                                                                        |
| 2   | **`VestaEvent` metadata field**                | ✅ **Done.** Added unsigned `JsonElement? Metadata` to `VestaEvent` (excluded from `EventSigner.BuildSigningInput` — verified by tests). `VestaCore.Events.VestaEventMetadata.TryGetTtlSeconds` is the canonical reader. TS + Python SDKs round-trip the field; Presence.CLI carries `ttlSeconds` there instead of in `Payload`.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        | See `src/VestaCore/Events/VestaEvent.cs` and `VestaEventMetadataTests`.                                                                                                                                                                                                               |
| 3   | **SDK conflict resolution primitives**         | ✅ **Done.** `EventReducer<T>`, `AppendOnlyLog<T>`, `LwwRegister<T>`, `LwwMap<TKey,TValue>` (with `LwwMapUpdate<TKey,TValue>`) shipped in `VestaCore.Projections`, plus `ProjectionCheckpoint`. `Apply(SequencedEvent)` advances `LastSequence`; `ApplyLocal(VestaEvent)` does not (optimistic local apply). `PresenceState` was refactored onto `LwwMap<string, Heartbeat>` (bye = Remove, heartbeat = Set) — bye now removes from the list. `TodoListState` keeps its hand-rolled multi-field per-item LWW.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           | See `src/VestaCore/Projections/` and `examples/Presence.CLI/PresenceState.cs`.                                                                                                                                                                                                        |
| 4   | **Channel access control**                     | ✅ **Done.** Open / private / invite-only modes implemented via `channel_access` metadata table + `IChannelAccessStore`. `CREATE_CHANNEL` protocol message lets clients declare visibility and seed the member list. ACL is enforced on HELLO subscribe and PUBLISH.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    | See `src/VestaServer/Storage/IChannelAccessStore.cs`.                                                                                                                                                                                                                                 |
| 5   | **Server-side signature verification**         | ✅ **Done.** Three layers: (a) if HELLO includes a `PublicKey`, server verifies it derives to the announced `clientId`; (b) every PUBLISH now requires `event.ClientId == connection.ClientId` (always-on, prevents a logged-in client from impersonating another); (c) when a public key is registered, every PUBLISH must carry a valid Ed25519 signature over the JCS-canonicalized event. Strict mode `Protocol:RequireSignedEvents=true` additionally rejects HELLO without a public key and forbids unsigned events globally.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     | See `ProtocolHandler.HandlePublishAsync` and `RequireSignedEventsTests`.                                                                                                                                                                                                              |
| 6   | **Browser example**                            | ✅ **Done.** `examples/chess-web/` is a full multiplayer chess client (Vite + chess.js + vesta-client-ts), with lobby presence, invite/accept flow, persistent matches across reconnects, login/logout, and bucketed match list.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        | Proves the TS client works in a real browser app.                                                                                                                                                                                                                                     |
| 7   | **CI/CD**                                      | ✅ **Done.** GitHub Actions deploys VestaServer to Azure. Tests run on push.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            | Testcontainers for PostgreSQL integration tests on the runner.                                                                                                                                                                                                                        |
| 8   | **Port projection primitives to TS / Python**  | ✅ **Done.** `EventReducer`, `AppendOnlyLog`, `LwwRegister`, `LwwMap` (with `LwwMapUpdate`) and `ProjectionCheckpoint` ported to both clients with identical semantics to the C# reference: `apply()` advances `lastSequence` only on strict-greater sequence; `applyLocal()` does not; strict-greater timestamp wins; tombstones survive stale sets. Python uses `threading.RLock`; TS skips locking (single-threaded). `examples/clipboard-ts` refactored onto `LwwMap<string, ClipboardEntry>` as smoke test. 16 unittest tests pass for Python, 16 `node:test` tests pass for TS. See [clients/vesta-client-ts/src/projections/](../clients/vesta-client-ts/src/projections/) and [clients/vesta-client-py/vesta_client/projections.py](../clients/vesta-client-py/vesta_client/projections.py).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    | Pure convenience layer — wire protocol unaffected.                                                                                                                                                                                                                                    |
| 9a  | **App registration & namespace enforcement**   | ✅ **Done.** `apps` table (migration `20260601211648_AddApps`) with `id` PK, `owner_client_id`, `created_at`, plus nullable quota columns reserved for #9b. `IAppStore` interface with `InMemoryAppStore` + `NpgsqlAppStore` impls. New `REGISTER_APP` protocol message (`AckMessage` on success, `ERROR { code: "DUPLICATE_APP" / "INVALID_APP" / "HELLO_REQUIRED" / "APPS_NOT_SUPPORTED" }` on failure). New `Protocol:RequireAppRegistration` option (default off): when on, `EnsureAppRegisteredAsync` gates HELLO subscribe, PUBLISH, SUBSCRIBE, FETCH, CREATE_CHANNEL — rejected with `ERROR { code: "UNKNOWN_APP" }`. `AppId` validator in `VestaCore.Channels` (same charset as a channel slug segment, max 64 chars). C#/TS/Python SDKs all expose `registerApp(appId)`. Per-app quotas still pending — tracked under #9b.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     | See `src/VestaServer/Storage/IAppStore.cs`, `src/VestaServer/Connections/ProtocolHandler.cs`, `RequireAppRegistrationTests`.                                                                                                                                                          |
| 9b  | **Per-app quotas & rate limits**               | ✅ **Done.** All six quotas in `AppQuotas` now enforced. Hot-path checks in `ProtocolHandler.EnforcePublishQuotasAsync` / `EnforceMaxChannelsAsync`: `max_payload_bytes` (UTF-8 byte count of payload + metadata), `publish_rate_per_minute` (in-process token bucket per `(appId, clientId)` in `AppRateLimiter`), `max_channels` (via `IChannelAccessStore.CountChannelsByAppAsync`), and `total_storage_bytes` (checked against an in-process cached rollup maintained by `IAppStorageAccountant`; cached value is seeded by the pruner and incremented on every successful PUBLISH). Background `AppQuotaPrunerService` (Postgres-only, opt-in via `AppQuotaPruner:Enabled`) enforces `retention_days` (DELETE by `received_at < now() - make_interval`) and `max_events_per_channel` (DELETE via `ROW_NUMBER() OVER (PARTITION BY channel_id ORDER BY sequence DESC)`), and refreshes the storage rollup via `SUM(pg_column_size(payload))`. New error codes: `QUOTA_EXCEEDED`, `RATE_LIMITED`. Multi-host rate limiting (shared bucket) remains tracked under #15.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                | See `src/VestaServer/Connections/AppRateLimiter.cs`, `src/VestaServer/Storage/AppQuotaPrunerService.cs`, `src/VestaServer/Storage/IAppStorageAccountant.cs`, `AppQuotasTests`, `AppRateLimiterTests`, `AppQuotaPrunerServiceTests`.                                                   |
| 10  | **Projection snapshotting**                    | ✅ **Done (C#).** `ProjectionSnapshot(LastSequence, StateJson)` record + `Snapshot()` / `Restore()` virtual methods on `EventReducer<T>`, overridden by all three built-in reducers (each serializes its full internal state: items + dedup ids for `AppendOnlyLog`, value + timestamp for `LwwRegister`, entries + tombstones for `LwwMap`). User-defined reducers opt in by overriding; default throws `SnapshotNotSupportedException`. `IProjectionStore` SDK interface in `VestaClient.Storage` with `SqliteProjectionStore` default impl, keyed by `(channelId, projectionId)`. Convenience extensions `store.SaveAsync(reducer)` / `store.RestoreAsync(reducer)`. `Presence.CLI` refactored to restore on boot and save on shutdown as smoke test. 11 new tests, all 203 solution tests pass. TS / Python ports still pending — tracked here.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     | See `src/VestaCore/Projections/ProjectionSnapshot.cs`, `src/VestaClient/Storage/SqliteProjectionStore.cs`, and `examples/Presence.CLI/Program.cs`.                                                                                                                                    |
| 11  | **Reconnect / outbox correctness audit**       | ✅ **Done.** Two correctness bugs fixed by audit: (1) `IEventStore.AppendAsync` was not idempotent on `evt.Id`. `InMemoryEventStore` would silently double-store with fresh sequences; `NpgsqlEventStore` would throw `23505` on the unique constraint. Both now dedup: `InMemoryEventStore` scans the channel log inside the lock and returns the existing `SequencedEvent`; `NpgsqlEventStore` does a fast-path `SELECT` before opening the transaction, and on the INSERT race catches `PostgresException` 23505, rolls back, and re-reads the winning row. (2) `SqliteClientEventStore.GetPendingOutboxAsync` filtered to `status = 'pending'` only, so an outbox entry that died between SEND and ACK was orphaned forever. It now returns both `Pending` AND `Sent` — safe because the server-side dedup makes re-flushing idempotent. The TS and Python clients now also have outbox + local-cache: `ClientEventStore` interface ported to both languages with matching semantics (pending + sent flush set, dedup on event id), `InMemoryClientEventStore` in both, `SqliteClientEventStore` in Python (stdlib `sqlite3`). Wired into both `VestaConnection`s: enqueue on offline-publish, flush on WELCOME, cache on ACK, mark confirmed on ACK. TS browser/Node persistent stores (IndexedDB, better-sqlite3) are an orthogonal concern — implement `ClientEventStore` to BYO. New tests: 6 C# (`InMemoryEventStoreDedupTests`, Npgsql idempotency, `OfflineOutboxSyncTests`), 7 TS (`outbox.test.mjs`), 10 Python (`test_outbox.py`).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        | See `src/VestaServer/Storage/NpgsqlEventStore.cs` (TryGetByIdAsync + 23505 catch), `src/VestaCore/Storage/InMemoryEventStore.cs`, `src/VestaClient/Storage/SqliteClientEventStore.cs`, `clients/vesta-client-ts/src/storage.ts`, `clients/vesta-client-py/vesta_client/storage.py`.   |
| 12a | **Channel deletion + admin auth (foundation)** | ✅ **Done.** Admin auth via Ed25519 public-key allow-list (`Admin:BootstrapPublicKeys` in `appsettings.json`): `IAdminStore` + `ConfigAdminStore` decode base64url-encoded 32-byte keys at startup, `ProtocolHandler.HandleHelloAsync` flips `ClientConnection.IsAdmin` after the existing public-key validation — same trust model as the rest of Vesta (no passwords, no JWTs). New protocol message `DELETE_CHANNEL` (`DeleteChannelMessage`) — admin-only, returns `NOT_ADMIN` otherwise. Soft-delete via nullable `deleted_at` column on `channels` (`UPDATE channels SET deleted_at = COALESCE(deleted_at, now())` — idempotent); migration `20260601222848_AddChannelDeletedAt` generated via `dotnet ef` with index. `IChannelAccessStore` grew `IsDeletedAsync` + `DeleteChannelAsync` + `RecordImplicitChannelAsync` (in-memory backend now records implicit channels in the access store after every `AppendAsync`; Postgres is a no-op because its event store already inserts the channels row). PUBLISH / SUBSCRIBE / FETCH / CREATE_CHANNEL on a deleted channel are rejected with `CHANNEL_DELETED`. SDK ports: `VestaConnection.DeleteChannelAsync` (C#), `deleteChannel(channelId)` (TS), `delete_channel(channel_id)` (Python). Events stay in the events table — hard-delete + pruner is #12b; no HTTP admin surface yet (#12c). 8 new tests in `DeleteChannelTests`; full suite 265/265 green.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     | See `src/VestaServer/Storage/IAdminStore.cs`, `ProtocolHandler.HandleDeleteChannelAsync`, `tests/VestaServer.Tests/DeleteChannelTests.cs`, `docs/server-configuration.md#server-admins`.                                                                                              |
| 12b | **Channel hard-delete pruner**                 | ✅ **Done.** `ChannelDeletionPrunerService` (Postgres-only, opt-in via `ChannelDeletionPruner:Enabled`) sweeps every soft-deleted channel whose `deleted_at` has aged past `ChannelDeletionPruner:GracePeriod` (default 24 h), deletes its events, and drops the `channels` row so the id becomes available again (a fresh `PUBLISH` recreates it implicitly). Two statements per eligible channel — `DELETE FROM events WHERE channel_id = $1` then `DELETE FROM channels WHERE id = $1 AND deleted_at < now() - make_interval(secs => $2)` (tombstone re-checked in the predicate as cheap insurance). Mirrors the `AppQuotaPrunerService` shape: opt-in, `PeriodicTimer`-driven, public `SweepOnceAsync` for tests. 6 new tests in `ChannelDeletionPrunerServiceTests` covering the grace-period boundary, immediate-delete (`GracePeriod = Zero`), id-reuse after sweep, and untouched-live-channels. Full suite 271/271 green. Snapshot-before-delete (the "if #10 covers all reducers" hook from the original note) deferred — no server-side projection registry today.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          | See `src/VestaServer/Storage/ChannelDeletionPrunerService.cs`, `tests/VestaServer.Tests/ChannelDeletionPrunerServiceTests.cs`, `docs/server-configuration.md#channeldeletionpruner-postgres-only`.                                                                                    |
| 12c | **Admin HTTP API + GUI (RabbitMQ-style)**      | ✅ **Done.** Operator-facing HTTP surface under `/admin/*` plus a single-page web GUI served from `wwwroot/admin/index.html`. Auth is a signed-challenge → bearer-token flow that reuses the existing `Admin:BootstrapPublicKeys` allow-list: `POST /admin/auth/challenge` returns a 32-byte nonce (cached for `AdminApi:ChallengeTtl`, default 60 s); client signs the nonce bytes with Ed25519; `POST /admin/auth/verify { publicKey, nonce, signature }` verifies, checks `IAdminStore.IsAdminAsync`, and issues a 32-byte bearer token (cached for `AdminApi:TokenTtl`, default 1 h). Subsequent calls carry `Authorization: Bearer <token>` and pass through an `AddEndpointFilter`-based gate. Endpoints: `GET /admin/channels` (with `?app=` prefix filter and `?includeDeleted=` toggle), `GET /admin/channels/{id}` (visibility, timestamps, event count, payload bytes, latest sequence, members), `DELETE /admin/channels/{id}` (soft-delete — same effect as the protocol message), `GET /admin/apps`, `GET /admin/apps/{id}` (with channel count + storage rollup), `PATCH /admin/apps/{id}/quotas`, `GET /admin/metrics` (active connections, total apps, total channels). New abstractions: `IChannelStatsService` (server-only — kept out of `IEventStore` because the latter is shared with the SDK; Postgres impl uses `SUM(pg_column_size(payload))`, in-memory walks events), `ListChannelsAsync` + `ListMembersAsync` on `IChannelAccessStore` (both backends), `AdminAuthService` (in-process `ConcurrentDictionary` for challenges + tokens, auto-prune on issue). Tokens are lost on restart — matches the bootstrap trust model; multi-host shared backend is tracked under #15. The GUI is vanilla HTML + a sprinkle of `@noble/ed25519` from esm.sh: paste a base64url seed → sign the challenge in-browser (private key never leaves the page) → token cached in `sessionStorage`. Provides an overview dashboard, channel browser with soft-delete, and a per-app quota editor. 10 new tests in `AdminApiTests`; full suite 281/281 green. | See `src/VestaServer/Admin/AdminAuthService.cs`, `src/VestaServer/Admin/AdminEndpoints.cs`, `src/VestaServer/Storage/IChannelStatsService.cs`, `src/VestaServer/wwwroot/admin/index.html`, `tests/VestaServer.Tests/AdminApiTests.cs`, `docs/server-configuration.md#admin-http-api`. |
| 13  | **Client library docs**                        | `clients/vesta-client-ts/README.md` and `clients/vesta-client-py/README.md` are thin. Write a "getting started" per language covering: install, identity, connect, publish, subscribe, projections (with the new primitives from #8), reconnect semantics. Include a 20-line end-to-end snippet that mirrors the C# `ChatRoom.CLI` so a developer landing on `npm install vesta-client` can ship something in 10 minutes without reading the source.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    | Pure docs, but the SDK can't be adopted without it.                                                                                                                                                                                                                                   |
| 14  | **Server observability**                       | No metrics today — if something goes wrong in prod the deployment is blind. Add Prometheus-compatible metrics (events/sec per channel and per app, active connections, log size, publish latency p50/p95/p99, ACL denials, quota rejections), structured logging with consistent fields (`channelId`, `clientId`, `appId`, `eventId`), and a `/health` + `/metrics` endpoint. Pairs naturally with #9b (you can only enforce quotas you can measure).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   | Operational prerequisite for running a public relay. Cheap to add now, painful to retrofit later.                                                                                                                                                                                     |
| 15  | **Multi-server (design TBD)**                  | Three distinct flavours, each with open design questions: (1) **horizontal scale-out** — multiple ASP.NET instances behind a load balancer sharing one Postgres (LISTEN/NOTIFY already fans out; needs sticky-session decision); (2) **federation** — independent servers replicating channels (touches identity portability, sequence authority, trust model); (3) **client-side multi-relay** — one client connects to several servers, picks per channel (touches channel-ID qualification, projection merge). Needs a design discussion before becoming actionable. Captured here so the question doesn't get lost.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 | Not blocking v1. Decide flavour first, then split into concrete TODOs.                                                                                                                                                                                                                |
| 16  | **Client portal (end-user self-service)**      | Public-facing website + HTTP API where humans can sign up, generate or import a Vesta identity (Ed25519 keypair, ideally generated client-side in the browser so the relay never sees the private key), register applications (#9a), see their owned apps and channels, current usage vs. quotas, rotate keys, and revoke compromised identities. Conceptually "GitHub.com for Vesta clients" — separate from #12c (operator-facing). The portal sits on top of a dedicated `/portal/` HTTP API with its own auth model (likely an Ed25519-signed bearer challenge issued at sign-in). Out of scope here: billing, OAuth-for-3rd-party-apps; in scope: identity lifecycle + app/channel inventory. The relay still works without it — the portal is a convenience layer on top of `IAppStore` and `IChannelAccessStore`. Needs a small design pass before becoming actionable (auth flow, browser key custody, recovery if the user loses their key).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   | New TODO. Pairs with #12c (operator GUI) and #9a (apps + ownership). Decide auth model first, then split into concrete sub-slices.                                                                                                                                                    |
