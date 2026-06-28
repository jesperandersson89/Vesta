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

| `$type`          | Record                 | Purpose                                                                                                                                                                                                                                                                                                                                      |
| ---------------- | ---------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `HELLO`          | `HelloMessage`         | Handshake. Declares `clientId`, channels to resume, last-seen sequence per channel, optional Ed25519 public key.                                                                                                                                                                                                                             |
| `PUBLISH`        | `PublishMessage`       | Append an event to a channel. Server validates, assigns sequence, broadcasts. Idempotent on `event.id`: re-publishing an event the server has already stored returns the original `ACK` (same `sequence`) and does **not** allocate a new slot — clients can safely retry after a dropped `ACK`.                                             |
| `SUBSCRIBE`      | `SubscribeMessage`     | Subscribe to a channel, optionally resuming from `fromSequence`.                                                                                                                                                                                                                                                                             |
| `UNSUBSCRIBE`    | `UnsubscribeMessage`   | Stop receiving real-time events for a channel.                                                                                                                                                                                                                                                                                               |
| `FETCH`          | `FetchMessage`         | Request a batch of historical events: `(channelId, fromSequence, toSequence?, limit?)`.                                                                                                                                                                                                                                                      |
| `CREATE_CHANNEL` | `CreateChannelMessage` | Explicitly create a channel: `(channelId, visibility, initialMembers[])`. Issuer becomes admin.                                                                                                                                                                                                                                              |
| `GRANT_ACCESS`   | `GrantAccessMessage`   | Admin-only: grant `member`/`admin` role on a private channel.                                                                                                                                                                                                                                                                                |
| `REGISTER_APP`   | `RegisterAppMessage`   | Register an app namespace (`appId`). Connecting client becomes the app owner. See [server-configuration.md](server-configuration.md#app-registration).                                                                                                                                                                                       |
| `DELETE_CHANNEL` | `DeleteChannelMessage` | **Server admin only.** Soft-delete a channel: stamps a deletion tombstone. Existing events are retained for a future hard-delete sweep; further `PUBLISH` / `SUBSCRIBE` / `FETCH` / `CREATE_CHANNEL` for that channel are rejected with `CHANNEL_DELETED`. Idempotent. See [server-configuration.md](server-configuration.md#server-admins). |

## Server → Client messages

| `$type`        | Record               | Purpose                                                                                                                                                                                                                                                        |
| -------------- | -------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `WELCOME`      | `WelcomeMessage`     | Response to `HELLO`. Confirms connection and lists accessible channels.                                                                                                                                                                                        |
| `EVENT`        | `EventMessage`       | A single real-time event pushed to subscribers: `(channelId, event, sequence, receivedAt)`.                                                                                                                                                                    |
| `EVENTS_BATCH` | `EventsBatchMessage` | A batch of `SequencedEvent` (response to `FETCH` or initial catch-up after `HELLO`).                                                                                                                                                                           |
| `ACK`          | `AckMessage`         | Acknowledges a `PUBLISH`: `(channelId, eventId, sequence)`.                                                                                                                                                                                                    |
| `ERROR`        | `ErrorMessage`       | `(code, message)`. Codes include `unauthorized`, `channel_not_found`, `invalid_signature`, `NOT_ADMIN` (caller is not a server admin), `CHANNEL_DELETED` (channel has been soft-deleted), `CHANNEL_NOT_FOUND` (target of `DELETE_CHANNEL` doesn't exist), etc. |

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

## Relay manifests (server independence)

An app's relay set is not hard-wired to a single endpoint. A client resolves an ordered list
of relay candidates and fails over between them, so an app survives its designated relay (or
its developer) going away.

Candidate order (highest priority first):

1. **User local override** — a relay the end-user pins client-side. The ownership escape hatch; always wins.
2. **Owner-signed manifest relays** — primary relays (by `priority`) then any active pre-signed escape fallbacks.
3. **App compiled-in defaults** — the relay(s) the developer shipped in `VestaAppConfig.defaultRelays`.

The manifest is an ordinary signed event, so the relay stays a pure relay — no server changes:

- **Channel:** `{appId}/vesta/relays` (a normal app channel; it does _not_ start with the reserved `vesta/` prefix).
- **Event type:** `vesta.relay-manifest`
- **Payload:**

  ```jsonc
  {
    "appId": "myapp",
    "version": 3,                       // monotonic; clients keep the highest seen (anti-rollback)
    "issuedAt": "2025-01-01T00:00:00Z", // display only
    "relays": [
      { "url": "wss://r1.example/ws", "priority": 0, "label": "primary" },
      { "url": "wss://r2.example/ws", "priority": 1 }
    ],
    "escapeFallbacks": [
      { "url": "wss://rescue.example/ws", "validFrom": "2025-06-01T00:00:00Z" }
    ],
    "ownerPublicKey": "<base64url>",     // the manifest trust anchor
    "signature": "<base64url>"           // Ed25519 over the RFC 8785 (JCS) canonicalization of all other fields
  }
  ```

Trust: clients accept a manifest only if it is signed by the **compiled-in app-owner key**
(`VestaAppConfig.ownerPublicKey`) and its `version` is newer than the one already cached. This
lets the owner publish a new manifest to migrate the swarm to fresh relays while defeating a
malicious relay trying to hijack the app's clients. Each client also keeps a personal override
that takes precedence locally regardless of the manifest.

**SDK behaviour (C#):** this is on by default — it is not something an app has to build. A
`VestaConnection` **requires** a `VestaAppConfig` and automatically manages a `RelayDirectory`
(file-backed under `~/.vesta/relays/`), so it auto-subscribes to the manifest channel, verifies
and adopts newer manifests (anti-rollback), and fails over across the resolved candidates with no
per-app wiring. Apps connect with `ConnectAsync(channels)` (relays come from the config) and can
surface the user escape hatch via `SetUserRelayOverrideAsync` / `ClearUserRelayOverrideAsync`. The
TypeScript and Python clients ship the same primitives but still attach the directory explicitly.


## See also

- [events.md](events.md) — the `VestaEvent` carried in `PUBLISH` / `EVENT` / `EVENTS_BATCH`
- [server-configuration.md](server-configuration.md) — signature verification, ACL enforcement, TTL cleanup
- [src/VestaCore/Protocol/](../src/VestaCore/Protocol/) — the C# message types are the source of truth
