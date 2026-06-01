# Events

Everything in Vesta is an event. An event is an **immutable**, **signed**, **client-authored** record that gets a server-assigned sequence number on publish. This page covers the event shape, signing, and the four transport-level hints (`replace`, `volatile`, `metadata`, `parentId`).

## The `VestaEvent` shape

```jsonc
{
    "id": "0190f7e2-...", // UUID v7, client-generated
    "channelId": "myapp/chat/general", // human-readable slug
    "timestamp": "2026-06-01T12:00:00.000Z", // client wall-clock
    "clientId": "Xk9mQ2pLvN3wR8tY5uA7bC", // base64url(sha256(pubkey))[:22]
    "eventType": "app.chat.message", // app-defined
    "payload": { "text": "hello" }, // arbitrary JSON
    "parentId": null, // optional causal link
    "signature": "base64url-ed25519-sig", // over signed-field set (see below)
    "replace": false, // hint: replace prev (chan,client,type)
    "volatile": false, // hint: relay-only, do not persist
    "metadata": null, // unsigned transport hints
}
```

When the server accepts the event it wraps it in a `SequencedEvent` for broadcast:

```jsonc
{
    "event":      { ...the original VestaEvent, untouched... },
    "sequence":   42,                           // per-channel monotonic
    "receivedAt": "2026-06-01T12:00:00.123Z"    // server wall-clock
}
```

Sequence numbers are the canonical ordering for a channel. They are also how clients ask "give me everything after N" on reconnect.

## Signing

Events are signed with Ed25519 over an [RFC 8785 (JCS)](https://datatracker.ietf.org/doc/html/rfc8785) canonical JSON encoding of the **signed-field set**:

| Field       | Signed?                                             |
| ----------- | --------------------------------------------------- |
| `id`        | ✅                                                  |
| `channelId` | ✅                                                  |
| `timestamp` | ✅                                                  |
| `clientId`  | ✅                                                  |
| `eventType` | ✅                                                  |
| `payload`   | ✅                                                  |
| `parentId`  | ✅                                                  |
| `signature` | ❌                                                  |
| `replace`   | ❌                                                  |
| `volatile`  | ❌                                                  |
| `metadata`  | ❌                                                  |
| `sequence`  | ❌ (server-assigned, doesn't exist at signing time) |

The reference signing input is built by `VestaCore.Serialization.EventSigner.BuildSigningInput`. The TypeScript (`buildSigningInput` in `clients/vesta-client-ts/src/signing.ts`) and Python (`clients/vesta-client-py/vesta_client/signing.py`) clients produce byte-identical output.

### Verification on the server

The server verifies signatures in three layers — see [server-configuration.md](server-configuration.md#signature-verification) for the modes:

1. If `HELLO` includes a `publicKey`, the server checks it derives to the announced `clientId`.
2. Every `PUBLISH` requires `event.clientId == connection.clientId` (always on).
3. Once a public key is registered for a client, every subsequent `PUBLISH` must carry a valid signature.

## Transport-level hints (unsigned)

These four fields steer how the server treats the event. They are **not signed**, so the server is free to inspect or strip them — but they're also not trusted as part of the event's identity.

### `replace`

```jsonc
{
    "eventType": "cursor.position",
    "payload": { "x": 12, "y": 34 },
    "replace": true,
}
```

When `replace: true`, the server **deletes any previous event** with the same `(channelId, clientId, eventType)` before storing this one. Catch-up readers only ever see the latest. Ideal for:

- Cursor / pointer position
- "Currently editing" indicators
- Per-user LWW state where history is uninteresting

### `volatile`

```jsonc
{
    "eventType": "typing.indicator",
    "payload": { "isTyping": true },
    "volatile": true,
}
```

When `volatile: true`, the server **does not persist the event at all** — it is broadcast to currently-subscribed clients and then dropped. New subscribers will never see it. Ideal for:

- Typing indicators
- Real-time mouse trails / drawing strokes
- Heartbeats (though see TTL below for a longer-lived alternative)

### `metadata.ttlSeconds` — ephemeral events

```jsonc
{
    "eventType": "app.presence.heartbeat",
    "payload": { "username": "alice" },
    "metadata": { "ttlSeconds": 30 },
}
```

Events with `metadata.ttlSeconds` are persisted normally, but the server computes an `expires_at` at publish time. Expired events are:

- Excluded from catch-up `FETCH` queries
- Cleaned up by the `ExpiredEventCleanupService` background sweep (Postgres backend only — interval and batch size are configurable, see [server-configuration.md](server-configuration.md#expiredeventcleanupservice))

This gives you "live for a while, then forget" semantics — perfect for presence/heartbeats that should outlast a single disconnect but not accumulate forever. The canonical reader is `VestaCore.Events.VestaEventMetadata.TryGetTtlSeconds`.

`metadata` is reserved for **wire-level transport hints**. Apps must not put domain data there — domain data goes in `payload` where it is signed.

### `parentId`

A causal pointer to another event by `id`. Vesta does **not** enforce parent ordering — the server still assigns sequences in receive order. `parentId` is purely a hint for app-level conflict resolution (e.g. "this edit was based on event X, reject if X was superseded").

## Client identity

`clientId` is `base64url(sha256(publicKey))[:22]`. It is derived deterministically from the Ed25519 public key, which means:

- No account creation, no registration
- The same keypair always produces the same `clientId`
- Losing the private key means losing the identity (back up your `~/.vesta/*-identity.json`)

See [src/VestaCore/Identity/VestaIdentity.cs](../src/VestaCore/Identity/VestaIdentity.cs) for the reference implementation.

## Event lifecycle

```
┌──────────────────┐  PUBLISH  ┌──────────────────┐  store  ┌──────────────────┐
│ Client creates   │──────────▶│ Server validates │────────▶│ events table     │
│ VestaEvent +     │           │ + assigns seq    │         │ (Postgres or     │
│ signs it         │           │                  │         │  in-memory)      │
└──────────────────┘           └────────┬─────────┘         └──────────────────┘
                                        │
                                        │ broadcast EVENT
                                        ▼
                               ┌──────────────────┐
                               │ All subscribers  │
                               │ on that channel  │
                               └──────────────────┘
```

A client reconnecting after downtime sends `FETCH { channelId, sinceSequence }` and gets an `EVENTS_BATCH` of everything it missed (expired TTL events excluded). The sequence number is the only cursor — there is no separate "read position" concept.

## See also

- [projections.md](projections.md) — turning event streams into materialized state
- [protocol.md](protocol.md) — the full wire message reference
- [PLANNING.md §Event Schema](../PLANNING.md) — design rationale
