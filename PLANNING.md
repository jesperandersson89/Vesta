# Vesta — Planning Document

> *The fire lives within — it doesn't come from somewhere external, it burns at the center of the home itself.*

## Vision

Vesta is a protocol and runtime that lets developers build **networked applications without surrendering ownership to a central service**. The server is a commodity — like a git remote or a torrent tracker — not the source of truth. Clients hold their own state, speak a generic sync protocol, and can switch servers (or go serverless) without data loss.

The goal: if the server disappears tomorrow, the application still works locally and can reconnect to any other Vesta-compatible host.

---

## Core Principles

| # | Principle | Implication |
|---|-----------|-------------|
| 1 | **Data belongs to the client** | All state is reconstructable from the client's local log |
| 2 | **Server is a relay, not an authority** | Server stores and forwards; it doesn't interpret business logic |
| 3 | **Protocol over platform** | Any client that speaks Vesta protocol can participate |
| 4 | **Offline-first** | Operations queue locally and sync when connectivity returns |
| 5 | **Conflict resolution is explicit** | The protocol provides mechanisms; the app decides policy |

---

## Architecture Overview

```
┌─────────────┐       ┌─────────────┐       ┌─────────────┐
│  Client A   │       │  Client B   │       │  Client C   │
│  (C# CLI)   │       │  (C# GUI)   │       │  (JS/Web)   │
└──────┬──────┘       └──────┬──────┘       └──────┬──────┘
       │                     │                     │
       │    Vesta Protocol (WebSocket / HTTP)      │
       │                     │                     │
       └─────────────┬───────┴─────────────────────┘
                     │
              ┌──────┴──────┐
              │ Vesta Server │
              │  (C# host)  │
              └──────┬──────┘
                     │
              ┌──────┴──────┐
              │   Storage   │
              │ (append-log │
              │  + indexes) │
              └─────────────┘
```

### Components

| Component | Role | Tech |
|-----------|------|------|
| **VestaCore** | Shared protocol types, serialization, conflict helpers | C# class library (net10.0) |
| **VestaServer** | Hosts channels, relays messages, persists log | C# (ASP.NET Core minimal API + WebSocket) |
| **VestaClient** | C# client library (connect, publish, subscribe) | C# class library (net10.0) |
| **vesta-client-js** | JS/TS client library implementing the protocol | TypeScript (npm package) |
| **vesta-client-py** | Python client library implementing the protocol | Python (pip package) |

Example apps (in `examples/`) consume these libraries to prove the protocol works across languages.

---

## Protocol Design (Draft)

### Concepts

| Concept | Description |
|---------|-------------|
| **Channel** | A named scope of collaboration (like a git repo or torrent swarm) |
| **Event** | An immutable, timestamped, signed payload appended to a channel's log |
| **Sequence** | A per-channel monotonic integer assigned by the server on append. This is the ordering and cursor mechanism. |
| **Snapshot** | A materialized view of state at a point in time (optimization, not source of truth) |

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
  "event": { /* VestaEvent as above, untouched */ },
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

| Field | Signed | Reason |
|-------|--------|--------|
| `id` | ✅ | Prevents ID substitution |
| `channelId` | ✅ | Prevents event being moved to different channel |
| `timestamp` | ✅ | Prevents timestamp tampering |
| `clientId` | ✅ | Binds event to its author |
| `type` | ✅ | Prevents type substitution |
| `payload` | ✅ | The actual data — obviously must be signed |
| `parentId` | ✅ | Prevents causal chain manipulation |
| `signature` | ❌ | Can't sign itself |
| `sequence` | ❌ | Server-assigned, doesn't exist at signing time |
| `receivedAt` | ❌ | Server-assigned, doesn't exist at signing time |

#### Canonical Signing Input

The signing input is a **deterministic JSON byte string** produced using [RFC 8785 — JSON Canonicalization Scheme (JCS)](https://www.rfc-editor.org/rfc/rfc8785):

```json
{"channelId":"my-todo-list","clientId":"Xk9mQ2pLvN3wR8tY5uA7bC","id":"01961a3e-7c5d-7f8a-b1c2-d3e4f5a6b7c8","parentId":null,"payload":{"done":false,"title":"Buy milk"},"timestamp":"2026-05-29T12:00:00Z","type":"app.todo.item-added"}
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

| Alternative | Problem |
|-------------|---------|
| "Just serialize in field order" | Field order varies by language/library — C# and Python won't produce same bytes |
| Custom deterministic serializer | One more thing to implement and keep in sync across 3 languages |
| Hash the raw bytes client sent | Server might re-serialize; can't verify from stored data alone |
| **RFC 8785 (JCS)** | **Standard, deterministic, implementations exist for C# (`JsonCanonicalization`), JS (`canonicalize`), Python (`json-canonicalization`)** |

#### Library References

| Language | Package |
|----------|---------|
| C# | `JsonCanonicalization` (NuGet) |
| JavaScript | `canonicalize` (npm) |
| Python | `json-canonicalization` (PyPI) |

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

| Client | Storage | Why |
|--------|---------|-----|
| **C# CLI / Desktop** | SQLite (`Microsoft.Data.Sqlite`) | Embedded, single-file, no install |
| **JS Browser** | IndexedDB (or OPFS + `sql.js`) | Browser-native, good capacity |
| **JS Node** | SQLite (`better-sqlite3`) | Same as C# — file-based, portable |

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

| Layer | Storage | Reason |
|-------|---------|--------|
| **Server** | PostgreSQL 18 | Concurrent writes, LISTEN/NOTIFY, JSONB, sequences, production-grade |
| **C# Client** | SQLite | Embedded, single-file, offline-capable |
| **JS Client (browser)** | IndexedDB | Browser-native |
| **JS Client (Node)** | SQLite | Same as C# client |
| **Export format** | JSON Lines | Human-readable portability |

---

## Conflict Resolution Strategy

Since multiple clients can produce events concurrently, conflicts are inevitable. Vesta provides **building blocks**, not a single policy:

| Strategy | Good for | How |
|----------|----------|-----|
| **Last-writer-wins (LWW)** | User preferences, simple state | Timestamp comparison |
| **Append-only (CRDT-like)** | Todo items, chat messages | No conflict — all events are valid |
| **Operational Transform** | Collaborative text (future) | Transform concurrent ops |
| **Application-level merge** | Games, custom logic | App registers a merge handler |

For the POC, focus on **append-only** and **LWW** — they cover all four target use cases.

### SDK Primitives (VestaCore)

These are the concrete building blocks the SDK provides. Apps compose them to build their state:

| Primitive | Purpose | Example use |
|-----------|---------|-------------|
| `EventReducer<TState>` | Fold a channel's event stream into a typed state object | Base abstraction — all others build on this |
| `AppendOnlyLog<T>` | Ordered list where all events are valid (no conflicts) | Chat messages, activity feed, audit trail |
| `LwwRegister<T>` | Single value, last-writer-wins by timestamp | User display name, clipboard content |
| `LwwMap<TKey, TValue>` | Key-value map where each key uses LWW independently | User preferences, todo item states |
| `ProjectionCheckpoint` | Tracks which sequence a projection has processed up to | Avoiding re-processing on reconnect |

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

| Behavior | Rule |
|----------|------|
| **Persist?** | Yes — store normally for simplicity (POC). Server can clean up later. |
| **Relay in real-time?** | Yes — push to all subscribers immediately like any event. |
| **Include in catch-up (FETCH)?** | **No** — exclude expired events from catch-up by default. |
| **Client local storage?** | Optional — client can choose to discard expired events from its SQLite. |
| **Server cleanup?** | Background job periodically deletes events past their `expiresAt`. |

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

| Concern | Question | Who decides |
|---------|----------|-------------|
| **Identity** | "Who is this client?" | The client itself (self-sovereign) |
| **Authorization** | "Is this client allowed to access this channel?" | The server operator |

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

| Strategy | How | Good for |
|----------|-----|----------|
| **Open** | Any valid client can connect and create/join channels | Dev/local/personal servers |
| **Invite-only** | Server admin pre-registers allowed public keys | Small teams, private servers |
| **Channel secrets** | Channel has a shared secret; client must present it to subscribe | Simple sharing (like a game room code) |
| **External OIDC/OAuth2** | Server verifies client has a valid token from Google/GitHub/Azure AD/etc. | Enterprise, multi-tenant servers |
| **Custom webhook** | Server calls an external URL to ask "is this client allowed?" | Full flexibility |

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
    "mode": "open",                    // "open" | "invite" | "oidc" | "webhook"
    
    // For "invite" mode:
    "allowedKeys": [
      "base64url-public-key-1",
      "base64url-public-key-2"
    ],
    
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

| Question | Answer |
|----------|--------|
| Who can create a channel? | Depends on server auth mode (see below) |
| Does SUBSCRIBE create a channel if missing? | Yes, in open mode. No, in private mode. |
| Does PUBLISH create a channel if missing? | Yes, in open mode. No, in private mode. |
| Can channel metadata change over time? | Yes — channel admins can update metadata. |
| How are channel secrets configured? | By the channel creator or server admin via API. |
| Can channels be deleted? | By admins only. Soft-delete (mark inactive) for POC. |

#### Creation Rules by Auth Mode

| Server Mode | Channel Creation Rule |
|-------------|---------------------|
| **Open** | First PUBLISH or SUBSCRIBE to a non-existent channel implicitly creates it. The first client becomes channel admin. |
| **Invite-only** | Only server-registered clients can create channels (explicit API call or admin tool). |
| **OIDC / Webhook** | Channel creation requires valid authorization. Server calls auth check before creating. |

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

| # | Use Case | Pattern | What it proves |
|---|----------|---------|----------------|
| 1 | **Todo list with sync** | Append-only + LWW per item | Basic CRUD over events, multi-device sync |
| 2 | **Simple game with state tracking** | Single-writer append | Progress/score synced across devices |
| 3 | **Turn-based multiplayer game** | Multi-writer ordered events | Shared channel, sequencing matters |
| 4 | **User preferences with sync** | LWW key-value | Replace-semantics, minimal state |
| 5 | **Chat room** | Append-only, many-writer | Real-time fan-out, high write volume, many subscribers |
| 6 | **Presence ("who's online")** | Ephemeral LWW with TTL/heartbeat | Not all state is permanent; forces TTL/volatile channel design |
| 7 | **Shared clipboard / pinboard** | Single-writer, replace-semantics | "Give me the latest value" — proves Vesta isn't only growing logs |

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
- [ ] Ephemeral/volatile channel support (TTL on events for presence)

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
│   ├── vesta-client-js/            # TypeScript/JS client library (npm package)
│   └── vesta-client-py/            # Python client library (pip package)
│
├── examples/                       # ═══ EXAMPLE APPLICATIONS ═══
│   ├── ChatRoom.CLI/               # C# — real-time chat (fan-out demo)
│   ├── TodoList.CLI/               # C# — synced todo list (append + LWW demo)
│   ├── TicTacToe.CLI/              # C# — turn-based multiplayer (ordering demo)
│   ├── SharedClipboard.CLI/        # C# — cross-device clipboard (replace-semantics)
│   ├── chat-web/                   # JS — browser chat app (proves JS client works)
│   └── todo-py/                    # Python — CLI todo app (proves Python client works)
│
└── tests/
    ├── VestaCore.Tests/
    ├── VestaServer.Tests/
    └── VestaClient.Tests/
```

### What Goes Where

| Question | Answer |
|----------|--------|
| Protocol message types? | `src/VestaCore/` |
| Event store interface? | `src/VestaCore/` |
| WebSocket server handler? | `src/VestaServer/` |
| PostgreSQL event store impl? | `src/VestaServer/` |
| C# connect/publish/subscribe API? | `src/VestaClient/` |
| SQLite client-side storage? | `src/VestaClient/` |
| "How to build a todo app with Vesta" | `examples/TodoList.CLI/` |
| "How to build a chat app in JS" | `examples/chat-web/` |
| Python protocol implementation? | `clients/vesta-client-py/` |
| Python todo app using the library? | `examples/todo-py/` |

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

| Analogy | What we borrow |
|---------|---------------|
| **Git** | Distributed log, local-first, remotes are interchangeable |
| **BitTorrent tracker** | Server helps peers find each other; doesn't own the data |
| **CRDTs** | Conflict-free data structures for certain state shapes |
| **SSE/WebSocket pub-sub** | Real-time event delivery model |
| **ActivityPub** | Federated protocol over HTTP (future inspiration) |

---

## Implementation Order

Refined step-by-step build order. The key insight: **get the protocol loop working with in-memory storage first**, then layer in persistence complexity.

| # | Step | What it proves |
|---|------|----------------|
| 1 | Restructure solution into `src/`, `tests/`, `examples/`, `clients/` | Clean foundation, `dotnet build` works |
| 2 | Define protocol/domain types in VestaCore (`VestaEvent`, `SequencedEvent`, `ProtocolMessage`, etc.) | Type system compiles, all message shapes defined |
| 3 | Add protocol JSON serialization tests | Messages round-trip correctly, canonical form verified |
| 4 | Implement in-memory `IEventStore` | Storage abstraction works without any DB dependency |
| 5 | Build minimal WebSocket server using in-memory store | Server accepts connections, relays events, assigns sequences |
| 6 | Build C# client library and prove round-trip | Client → Server → Client event flow works end-to-end |
| 7 | Swap server store to PostgreSQL (`NpgsqlEventStore`) | Real persistence, LISTEN/NOTIFY, concurrent writes |
| 8 | Add SQLite client-side cache + outbox | Offline support, reconnect catch-up |
| 9 | Add Ed25519 signing + verification | Events are signed, server verifies, integrity proven |
| 10 | Build example apps (chat, todo, tic-tac-toe, clipboard) | Protocol handles real use cases across patterns |

**Why this order:**
- Steps 1–6 produce a fully working system with zero infrastructure dependencies (no Docker, no PostgreSQL). You can `dotnet run` the server and two clients and watch events flow.
- Step 7 adds PostgreSQL — by this point the protocol is proven and stable, so you're only swapping one interface implementation.
- Steps 8–9 add client resilience and security.
- Step 10 is the payoff — building real apps that prove the SDK works.

Each step has a clear "it works" signal — no half-finished layers.
