# Protocol reference

Vesta is a JSON-over-WebSocket protocol. Every message is a JSON object whose **discriminator** lives at the top-level `$type` field (System.Text.Json polymorphic format). The full discriminator map is the source of truth at [src/VestaCore/Protocol/ProtocolMessage.cs](../src/VestaCore/Protocol/ProtocolMessage.cs).

## Connection lifecycle

```
client                            server
  │                                 │
  │── HELLO ──────────────────────▶│
  │       (clientId, channels[],   │
  │        lastSequences{},        │
  │        publicKey?)             │
  │                                 │
  │◀──────────────────── WELCOME ──│
  │             (serverId,         │
  │              channels[])       │
  │                                 │
  │◀───────── EVENTS_BATCH (×N) ──│  ← catch-up since lastSequences
  │                                 │
  │── PUBLISH ────────────────────▶│
  │       (channelId, event)       │
  │                                 │
  │◀────────────────────── ACK ───│  (channelId, eventId, sequence)
  │                                 │
  │◀──────────────────── EVENT ───│  ← real-time broadcast to subscribers
```

A client may subscribe to additional channels mid-session (`SUBSCRIBE`), request historical data (`FETCH`), or create new channels (`CREATE_CHANNEL`).

## Client → Server messages

| `$type`          | Record                 | Purpose                                                                                                                                                |
| ---------------- | ---------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `HELLO`          | `HelloMessage`         | Handshake. Declares `clientId`, channels to resume, last-seen sequence per channel, optional Ed25519 public key.                                       |
| `PUBLISH`        | `PublishMessage`       | Append an event to a channel. Server validates, assigns sequence, broadcasts.                                                                          |
| `SUBSCRIBE`      | `SubscribeMessage`     | Subscribe to a channel, optionally resuming from `fromSequence`.                                                                                       |
| `UNSUBSCRIBE`    | `UnsubscribeMessage`   | Stop receiving real-time events for a channel.                                                                                                         |
| `FETCH`          | `FetchMessage`         | Request a batch of historical events: `(channelId, fromSequence, toSequence?, limit?)`.                                                                |
| `CREATE_CHANNEL` | `CreateChannelMessage` | Explicitly create a channel: `(channelId, visibility, initialMembers[])`. Issuer becomes admin.                                                        |
| `GRANT_ACCESS`   | `GrantAccessMessage`   | Admin-only: grant `member`/`admin` role on a private channel.                                                                                          |
| `REGISTER_APP`   | `RegisterAppMessage`   | Register an app namespace (`appId`). Connecting client becomes the app owner. See [server-configuration.md](server-configuration.md#app-registration). |

## Server → Client messages

| `$type`        | Record               | Purpose                                                                                         |
| -------------- | -------------------- | ----------------------------------------------------------------------------------------------- |
| `WELCOME`      | `WelcomeMessage`     | Response to `HELLO`. Confirms connection and lists accessible channels.                         |
| `EVENT`        | `EventMessage`       | A single real-time event pushed to subscribers: `(channelId, event, sequence, receivedAt)`.     |
| `EVENTS_BATCH` | `EventsBatchMessage` | A batch of `SequencedEvent` (response to `FETCH` or initial catch-up after `HELLO`).            |
| `ACK`          | `AckMessage`         | Acknowledges a `PUBLISH`: `(channelId, eventId, sequence)`.                                     |
| `ERROR`        | `ErrorMessage`       | `(code, message)`. Codes include `unauthorized`, `channel_not_found`, `invalid_signature`, etc. |

## Wire format

All messages use System.Text.Json polymorphic serialization with `$type` as the discriminator. The canonical example:

```json
{
    "$type": "HELLO",
    "clientId": "Xk9mQ2pLvN3wR8tY5uA7bC",
    "channels": ["myapp/chat"],
    "lastSequences": { "myapp/chat": 0 },
    "publicKey": "base64url-32-bytes"
}
```

The TypeScript and Python clients produce the same wire format — see their respective `connection.ts` / `connection.py` for the encoder/decoder.

## Channel IDs

- Human-readable slugs: `[a-z0-9][a-z0-9\-/]*[a-z0-9]`
- Max 128 chars
- Convention: `<app>/<purpose>`, e.g. `myapp/chat/general`, `presence/vesta-presence`, `chess/match/abc123`

Channel IDs are validated on every `PUBLISH`, `SUBSCRIBE`, and `FETCH`.

## Channel visibility

| Visibility | Subscribe    | Publish      | Created by               |
| ---------- | ------------ | ------------ | ------------------------ |
| (open)     | Anyone       | Anyone       | Implicit (first publish) |
| `public`   | Anyone       | Anyone       | `CREATE_CHANNEL`         |
| `private`  | Members only | Members only | `CREATE_CHANNEL`         |

Admin is a role within a private channel — only admins can `GRANT_ACCESS`.

## See also

- [events.md](events.md) — the `VestaEvent` carried in `PUBLISH` / `EVENT` / `EVENTS_BATCH`
- [server-configuration.md](server-configuration.md) — signature verification, ACL enforcement, TTL cleanup
- [src/VestaCore/Protocol/](../src/VestaCore/Protocol/) — the C# message types are the source of truth
