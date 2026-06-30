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
| `REGISTER_APP`   | `RegisterAppMessage`   | Register an app namespace (`appId`, optional `discoverable`). Connecting client becomes the app owner. `discoverable` opts the app into [server-to-server discovery](#server-to-server-discovery-federation). See [server-configuration.md](server-configuration.md#app-registration).                                                                                                                                                                                       |
| `DELETE_CHANNEL` | `DeleteChannelMessage` | **Server admin only.** Soft-delete a channel: stamps a deletion tombstone. Existing events are retained for a future hard-delete sweep; further `PUBLISH` / `SUBSCRIBE` / `FETCH` / `CREATE_CHANNEL` for that channel are rejected with `CHANNEL_DELETED`. Idempotent. See [server-configuration.md](server-configuration.md#server-admins). |

## Server → Client messages

| `$type`        | Record               | Purpose                                                                                                                                                                                                                                                        |
| -------------- | -------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `WELCOME`      | `WelcomeMessage`     | Response to `HELLO`. Confirms connection and lists accessible channels.                                                                                                                                                                                        |
| `EVENT`        | `EventMessage`       | A single real-time event pushed to subscribers: `(channelId, event, sequence, receivedAt)`.                                                                                                                                                                    |
| `EVENTS_BATCH` | `EventsBatchMessage` | A batch of `SequencedEvent` (response to `FETCH` or initial catch-up after `HELLO`).                                                                                                                                                                           |
| `ACK`          | `AckMessage`         | Acknowledges a `PUBLISH`: `(channelId, eventId, sequence)`.                                                                                                                                                                                                    |
| `ERROR`        | `ErrorMessage`       | `(code, message, eventId?, channelId?)`. Codes include `unauthorized`, `channel_not_found`, `invalid_signature`, `NOT_ADMIN` (caller is not a server admin), `CHANNEL_DELETED` (channel has been soft-deleted), `CHANNEL_NOT_FOUND` (target of `DELETE_CHANNEL` doesn't exist), plus the per-app limit codes `RATE_LIMITED`, `QUOTA_EXCEEDED`, `UNKNOWN_APP`, `ACCESS_DENIED`, `APP_NOT_ALLOWED` (app not on the server's allow-list). On publish rejections the relay stamps the optional `eventId` / `channelId` so the client can correlate the failure to the exact event. |

### Limits and the client's reaction

When the relay limits an app (managed quotas on Atrium, or `AppQuotas` on a self-hosted server), the publish is rejected with an `ERROR` frame carrying the offending `eventId` / `channelId`. The C# client interprets these codes (see `VestaErrorCodes.Classify`) and:

- raises a typed `OnLimited(VestaLimitNotice)` event (distinct from the raw `OnError`) so apps can back off, surface UI, or prompt an upgrade without string-matching codes;
- **dead-letters** the offending outbox entry for non-transient codes (`QUOTA_EXCEEDED`, `UNKNOWN_APP`, `ACCESS_DENIED`, `APP_NOT_ALLOWED`) so a doomed offline event is never re-sent on every reconnect. Transient limits (`RATE_LIMITED`) are left in the outbox to retry later.

The TypeScript and Python `ErrorMessage` types carry the same optional `eventId` / `channelId` fields; the typed limit/dead-letter behavior is currently C#-only.

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


## Server-to-server discovery (federation)

Relay manifests cover the **planned** migration: the owner pre-lists relays and signs them. But
if a relay dies and the owner never published a fresh manifest, a client's candidate list can run
dry. Federation is the **recovery** path — relays optionally gossip signed self-descriptions to
each other, so a client reaching any one relay in the mesh can ask "who *else* hosts this app?"
**without a central hub.**

This is an HTTP side-channel; it does **not** add any WebSocket message. It is off by default and
enabled per relay via `Discovery:Enabled` (see
[server-configuration.md](server-configuration.md#server-to-server-discovery-federation)).

### Dual opt-in

A relay only advertises an app when **both** parties agree:

1. **The relay operator** turns on `Discovery:Enabled`.
2. **The app owner** sets the per-app `discoverable` flag — either at registration
   (`REGISTER_APP { appId, discoverable: true }`) or later via the admin API
   (`PATCH /admin/apps/{id}/discoverable`). This is a relay-side metadata flag, **not** parsed
   from any event payload, so the relay still never interprets app data.

### Server descriptor

Each discovery-enabled relay maintains a signed `ServerDescriptor` advertising itself and the
discoverable apps it hosts:

```jsonc
{
  "relayPublicKey": "<base64url>",          // the relay's Ed25519 identity (and signer)
  "urls": ["wss://r1.example/ws"],          // publicly reachable WebSocket URLs, preference order
  "apps": [
    { "appId": "myapp", "ownerClientId": "<base64url[:22]>" }  // owner client id = sha256(ownerPublicKey)[:22]
  ],
  "issuedAt": "2025-01-01T00:00:00Z",
  "ttlSeconds": 300,                         // peers evict the descriptor once this expires
  "signature": "<base64url>"                 // Ed25519 over the RFC 8785 (JCS) canonicalization of all other fields
}
```

The descriptor is **self-signed**: the relay declares its own `relayPublicKey` and signs with the
matching key, so a descriptor relayed through untrusted peers cannot be altered in flight.

### HTTP surface

Mapped only when `Discovery:Enabled`. All three are unauthenticated reads of already-public,
signed descriptors:

| Endpoint                       | Returns                                                                 |
| ------------------------------ | ----------------------------------------------------------------------- |
| `GET /federation/descriptor`   | This relay's own freshly-signed descriptor.                             |
| `GET /federation/peers`        | Every descriptor this relay knows (its own + gossiped peers), deduped.  |
| `GET /federation/apps/{appId}` | Descriptors (own + peers) that advertise the given app.                 |

### Gossip (anti-entropy pull)

A discovery-enabled relay runs a background loop (`Discovery:GossipIntervalSeconds`, default 60 s)
that pulls `/federation/descriptor` and `/federation/peers` from its configured `Discovery:Seeds`
and from relays it has already learned about, verifies each descriptor's signature, drops expired
ones, and merges the rest (last-write-wins by `issuedAt`, capped at `Discovery:MaxPeers`). This
Matrix-style pull mesh converges without any relay being authoritative.

### Trust model — discovered relays are show-only

A signed descriptor proves the relay **authored** it and that it **claims** to host an app — it
cannot prove the relay actually carries the app's data, and a relay can lie about its `apps`. So
discovery is deliberately **show-only**:

- The C# client (`FederationClient.DiscoverRelaysForAppAsync` / `ListAllRelaysAsync`) verifies
  every descriptor signature **and** cross-checks each advertised `ownerClientId` against the app's
  compiled-in trust anchor (`VestaIdentity.DeriveClientId(VestaAppConfig.OwnerPublicKey)`), dropping
  relays that advertise the app under a different owner.
- Surviving relays are returned as **unverified candidates**. They are **never auto-adopted** —
  owner-signed manifest relays remain the only automatic failover tier. The user adopts a
  discovered relay manually via the existing `SetUserRelayOverrideAsync` escape hatch.


## See also

- [events.md](events.md) — the `VestaEvent` carried in `PUBLISH` / `EVENT` / `EVENTS_BATCH`
- [server-configuration.md](server-configuration.md) — signature verification, ACL enforcement, TTL cleanup
- [src/VestaCore/Protocol/](../src/VestaCore/Protocol/) — the C# message types are the source of truth
