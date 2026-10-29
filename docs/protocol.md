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
  │◀───────── EVENTS_BATCH (×N) ──│  ← catch-up since lastSequences (paged, see below)
  │                                 │
  │── PUBLISH ────────────────────▶│
  │       (channelId, event)       │
  │                                 │
  │◀────────────────────── ACK ───│  (channelId, eventId, sequence)
  │                                 │
  │◀──────────────────── EVENT ───│  ← real-time broadcast to subscribers
```

A client may subscribe to additional channels mid-session (`SUBSCRIBE`), request historical data (`FETCH`), or create new channels (`CREATE_CHANNEL`).

Catch-up (after `HELLO` or `SUBSCRIBE { fromSequence }`) arrives as one or more `EVENTS_BATCH` frames per channel, each holding at most the relay's `Protocol:MaxFetchLimit` events (default 1000), in ascending sequence order; a shorter-than-full batch is the last one. `FETCH` limits are clamped to the same value, so clients paging manually should continue from `lastSequence + 1`. Because the clamp is relay configuration the client cannot see, a short `FETCH` page does not prove the end of the channel: the SDKs' paging helpers (`FetchAllAsync` / `fetchAll` / `fetch_all`) keep requesting until the relay returns an empty page. Rejections of a `FETCH` (`ACCESS_DENIED`, `CHANNEL_DELETED`, `INVALID_CHANNEL`, `UNKNOWN_APP`, `APP_NOT_ALLOWED`) carry the `channelId` so a client can tell which request failed.

## Client → Server messages

| `$type`          | Record                 | Purpose                                                                                                                                                                                                                                                                                                                                      |
| ---------------- | ---------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `HELLO`          | `HelloMessage`         | Handshake. Declares `clientId`, channels to resume, last-seen sequence per channel, optional Ed25519 public key.                                                                                                                                                                                                                             |
| `PUBLISH`        | `PublishMessage`       | Append an event to a channel. Server validates, assigns sequence, broadcasts. Idempotent on `event.id`: re-publishing an event the server has already stored returns the original `ACK` (same `sequence`) and does **not** allocate a new slot — clients can safely retry after a dropped `ACK`.                                             |
| `SUBSCRIBE`      | `SubscribeMessage`     | Subscribe to a channel, optionally resuming from `fromSequence`.                                                                                                                                                                                                                                                                             |
| `PROVE`          | `ProveMessage`         | Answer to a `CHALLENGE`: Ed25519 signature over the nonce, proving the client holds the key behind its `clientId`. See [HELLO key-possession proof](#hello-key-possession-proof). |
| `UNSUBSCRIBE`    | `UnsubscribeMessage`   | Stop receiving real-time events for a channel.                                                                                                                                                                                                                                                                                               |
| `FETCH`          | `FetchMessage`         | Request a batch of historical events: `(channelId, fromSequence, toSequence?, limit?)`.                                                                                                                                                                                                                                                      |
| `CREATE_CHANNEL` | `CreateChannelMessage` | Explicitly create a channel: `(channelId, visibility, initialMembers[], encrypted?)`. Issuer becomes admin. `encrypted: true` marks the channel [end-to-end encrypted](#encrypted-channels) (requires `private`).                                                                                                                                                              |
| `GRANT_ACCESS`   | `GrantAccessMessage`   | Admin-only: grant `member`/`admin`/`reader` role on a private or broadcast channel.                                                                                                                                                                                                                                                                                |
| `REVOKE_ACCESS`  | `RevokeAccessMessage`  | Admin-only: remove a client's access entry `(channelId, clientId)` from a private or broadcast channel. Idempotent (an unknown client still gets `ACK`); an admin cannot revoke itself (`ACCESS_DENIED`), so a channel never loses its last admin. Relays that predate it answer `UNKNOWN_MESSAGE`; check for the `encrypted-channels` [feature](#relay-features). |
| `REGISTER_APP`   | `RegisterAppMessage`   | Register an app namespace (`appId`, optional `discoverable`). Connecting client becomes the app owner. `discoverable` opts the app into [server-to-server discovery](#server-to-server-discovery-federation). See [server-configuration.md](server-configuration.md#app-registration).                                                                                                                                                                                       |
| `DELETE_CHANNEL` | `DeleteChannelMessage` | **Server admin only.** Soft-delete a channel: stamps a deletion tombstone. Existing events are retained for a future hard-delete sweep; further `PUBLISH` / `SUBSCRIBE` / `FETCH` / `CREATE_CHANNEL` for that channel are rejected with `CHANNEL_DELETED`. Idempotent. See [server-configuration.md](server-configuration.md#server-admins). |

## Server → Client messages

| `$type`        | Record               | Purpose                                                                                                                                                                                                                                                        |
| -------------- | -------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `CHALLENGE`    | `ChallengeMessage`   | Sent instead of `WELCOME` when a `HELLO` set `proofSupported` and carried a `publicKey`: `(nonce)`. The client must reply `PROVE` before the handshake completes. |
| `WELCOME`      | `WelcomeMessage`     | Response to `HELLO`. Confirms connection and lists accessible channels. `features` lists optional relay capabilities (see [Relay features](#relay-features)); absent on older relays.                                                                                                                                                        |
| `EVENT`        | `EventMessage`       | A single real-time event pushed to subscribers: `(channelId, event, sequence, receivedAt)`.                                                                                                                                                                    |
| `EVENTS_BATCH` | `EventsBatchMessage` | A batch of `SequencedEvent` (response to `FETCH` or initial catch-up after `HELLO`).                                                                                                                                                                           |
| `ACK`          | `AckMessage`         | Acknowledges a `PUBLISH`: `(channelId, eventId, sequence)`. Also answers a successful `REGISTER_APP`, `CREATE_CHANNEL`, `GRANT_ACCESS` and `DELETE_CHANNEL` with the nil event id (`00000000-0000-0000-0000-000000000000`) and sequence `0`; all three SDKs surface it through their ack callback/event, so an app can tell a control request succeeded (failures arrive as `ERROR`). |
| `ERROR`        | `ErrorMessage`       | `(code, message, eventId?, channelId?)`. Codes include `unauthorized`, `channel_not_found`, `invalid_signature`, `NOT_ADMIN` (caller is not a server admin), `CHANNEL_DELETED` (channel has been soft-deleted), `CHANNEL_NOT_FOUND` (target of `DELETE_CHANNEL` doesn't exist), plus the per-app limit codes `RATE_LIMITED`, `QUOTA_EXCEEDED`, `MESSAGE_QUOTA_EXCEEDED` (over `max_messages_per_month`), `CONNECTION_LIMITED` (per-connection cap such as subscriptions; not tied to an event), `UNKNOWN_APP`, `ACCESS_DENIED`, `APP_NOT_ALLOWED` (app not on the server's allow-list), `APP_PAUSED` (an operator paused the app: publishes and channel creation are refused until it is resumed; transient), `ENCRYPTION_REQUIRED` (a non-envelope payload was published to an [encrypted channel](#encrypted-channels)). An operator-imposed throttle is reported as `RATE_LIMITED`. On publish rejections the relay stamps the optional `eventId` / `channelId` so the client can correlate the failure to the exact event. |

### Limits and the client's reaction

When the relay limits an app (managed quotas on Atrium, or `AppQuotas` on a self-hosted server), the publish is rejected with an `ERROR` frame carrying the offending `eventId` / `channelId`. All three SDKs interpret these codes (`VestaErrorCodes.Classify` in C#, `classifyErrorCode` in TS, `classify_error_code` in Python) and:

- raise a typed limit signal — `OnLimited(VestaLimitNotice)` in C#, the `limited` event in TS, `on_limited(VestaLimitNotice)` in Python — distinct from the raw error callback, so apps can back off, surface UI, or prompt an upgrade without string-matching codes;
- **dead-letter** the offending outbox entry for non-transient codes (`QUOTA_EXCEEDED`, `MESSAGE_QUOTA_EXCEEDED`, `UNKNOWN_APP`, `ACCESS_DENIED`, `APP_NOT_ALLOWED`) so a doomed offline event is never re-sent on every reconnect. Transient limits (`RATE_LIMITED`, `APP_PAUSED`) are left in the outbox to retry later. `CONNECTION_LIMITED` surfaces via `OnLimited` but dead-letters nothing.

The TypeScript and Python `ErrorMessage` types carry the same optional `eventId` / `channelId` fields, and both clients now ship the same classification + dead-letter behavior as C#.

`ENCRYPTION_REQUIRED` (a plaintext publish to an [encrypted channel](#encrypted-channels)) is classified like the other protocol errors (`INVALID_SIGNATURE`, `CHANNEL_DELETED`, …): not a limit, but event-fatal, so the outbox entry is dead-lettered.

## Relay features

`WELCOME.features` is an optional list of capability names the relay supports. A relay that predates the field sends none, and clients must treat that as "no optional features". Current names (`RelayFeatures` in C#/TS, `RelayFeatures` class in Python):

| Feature              | Meaning                                                                                                                                                                                                       |
| -------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `encrypted-channels` | `CREATE_CHANNEL.encrypted` is understood and enforced, `REVOKE_ACCESS` exists. The SDKs refuse to create an encrypted channel on a relay without it (`NotSupportedException` / `Error` / `NotSupportedError`), because an older relay would ignore the flag and store plaintext. |

Clients read it as `connection.Features` / `connection.features` and `SupportsFeature(name)` / `supportsFeature(name)` / `supports_feature(name)`.

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

## HELLO key-possession proof

`HELLO` announces a `clientId` and `publicKey` and the relay checks they match, but that does not show the client holds the private key. To let a relay authenticate a connection, a client that has an identity sets `proofSupported: true` in `HELLO`:

```
client                              server
  │── HELLO { publicKey, proofSupported: true } ─▶│
  │◀─────────────── CHALLENGE { nonce } ──────────│   random 32 bytes, base64url, single-use, 30 s
  │── PROVE { signature } ───────────────────────▶│   verify, then subscribe + catch-up
  │◀──────────────────────────────── WELCOME ─────│
```

- **Signed bytes:** the UTF-8 encoding of `vesta-hello-v1\n{nonce}\n{clientId}` (nonce exactly as received). `signature` is the base64url Ed25519 signature. Reference: [HelloProof.cs](../src/VestaCore/Identity/HelloProof.cs); `signHelloProof` (TS) and `sign_hello_proof` (Python). Test vector — seed `32 × 0x00`, clientId `E545QOZLVJFyIIjZoNdBYo`, nonce `bm9uY2Utdm9uY2Utdm9uY2U` → signature `9hGCvE4icwU-17knAeGGHxw0xvyqpm38vbgrC0z8ZREQWCtRk9UoPGNP_o0bp0-ABGhIXJAY5rrDsblaYih_DQ`.
- **No identity is established until the proof passes.** A wrong signature gets `PROOF_INVALID`, a late one `PROOF_EXPIRED`, a `PROVE` with no pending `HELLO` (including a replay) `UNEXPECTED_PROOF`; the connection then has no client id.
- **Compatibility:** relays only challenge clients that set `proofSupported`, and clients that receive `WELCOME` straight away (older relays ignore the unknown field) simply continue. A client without a key never sets the flag.
- **`Protocol:RequireHelloProof`** makes it mandatory: other clients get `PROOF_REQUIRED`, and nothing but `HELLO`/`PROVE` is accepted until the proof succeeds. Server admin status and `MaxConnectionsPerClientId` only ever apply to proven connections.

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
| `broadcast`| Anyone       | Members only | `CREATE_CHANNEL`         |

"Members" means any `channel_access` entry that can write (`member` or `admin`). Roles:

| Role     | Read (private) | Publish | `GRANT_ACCESS` |
| -------- | -------------- | ------- | -------------- |
| `admin`  | yes            | yes     | yes            |
| `member` | yes            | yes     | no             |
| `reader` | yes            | **no**  | no             |

A `broadcast` channel is the building block for "one trusted writer, everyone reads" (a shop, a world-state feed, an announcements channel): create it with the writer as issuer (admin), list any additional writers as `initialMembers`, and every other client can subscribe and fetch but gets `ACCESS_DENIED` on `PUBLISH` (volatile events included). It is never created implicitly. Unknown roles in `GRANT_ACCESS` are treated as `member`.

> **Identity caveat.** Without the [HELLO key-possession proof](#hello-key-possession-proof) the relay trusts the `clientId` announced in `HELLO` (it checks the public key derives to it, but not that the client holds the private key). Write control is sound when the relay runs with `Protocol:RequireSignedEvents = true`, because publishes must then be signed by that key. Read control on private channels is only proof-backed when `Protocol:RequireHelloProof = true`.

The app owner is the only identity that may publish to `{appId}/vesta/relays` (the relay manifest channel) on a registered app; everyone can still read it. Unregistered apps keep the channel open.

Admin is a role within a private or broadcast channel — only admins can `GRANT_ACCESS` and `REVOKE_ACCESS`.

## Encrypted channels

> **Status: relay side shipped (phase A); the SDK encryption and key distribution (phases B–C) are not yet built.** Until they are, an encrypted channel is only useful to an app that produces the envelope itself. See `PLANNING.md` #33 for the full design.

A channel created with `CREATE_CHANNEL { encrypted: true }` is **end-to-end encrypted**: the relay never holds a key and cannot read payloads. What the relay *does* enforce:

- **Must be `private`.** Keys are distributed by membership, so `public` or `broadcast` + `encrypted` is rejected with `INVALID_CHANNEL`. Encrypted channels are never created implicitly.
- **Envelope shape on `PUBLISH`.** Every event whose `eventType` is not `vesta.*` must have exactly this payload, or the relay answers `ERROR { code: "ENCRYPTION_REQUIRED", eventId, channelId }`:

  ```json
  { "enc": 1, "kid": 3, "n": "<base64url, 12 bytes>", "ct": "<base64url>" }
  ```

  `enc` is the envelope version, `kid` the key epoch (non-negative integer), `n` the AES-256-GCM nonce and `ct` the ciphertext + tag. The relay checks the *shape* only (it cannot verify ciphertext); this is a guard against a misconfigured client leaking plaintext, not a security boundary. `vesta.*` control events (key distribution) are exempt and stay in the clear. The canonical check is [`EncryptedPayload.IsEnvelope`](../src/VestaCore/Events/EncryptedPayload.cs).
- **Everything else is unchanged.** `sequence`, `replace`, `volatile`, `metadata.ttlSeconds`, idempotency on `id`, ACL and signature verification all operate on cleartext fields; the Ed25519 signature covers the envelope. `eventType` stays in the clear.
- **Quota.** `max_encrypted_channels` (see [server-configuration.md](server-configuration.md#app-quotas--rate-limits)) caps how many a registered app may own; the admin API reports `encrypted` per channel and `encryptedChannels` per app, so an operator can count and price them.

Only create an encrypted channel on a relay that advertises `encrypted-channels` in [`WELCOME.features`](#relay-features). `REVOKE_ACCESS` exists so that an admin can remove a member and then rotate the channel key.

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
  "acceptsUnregisteredApps": true,           // optional: true = open mode, false = registration enforced, omitted = not advertised
  "signature": "<base64url>"                 // Ed25519 over the RFC 8785 (JCS) canonicalization of all other fields
}
```

The descriptor is **self-signed**: the relay declares its own `relayPublicKey` and signs with the
matching key, so a descriptor relayed through untrusted peers cannot be altered in flight.
`acceptsUnregisteredApps` mirrors the relay's own `!RequireAppRegistration && AllowedApps.Count == 0`
open-mode check; it is included in the signing input only when present, so older relays omit it
without breaking signature verification. [Relay recovery](relay-recovery.md#adopting-under-a-different-namespace)
surfaces it on each `RelayChoice` to tell a user whether registering a namespace is likely needed.

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

- The client (`FederationClient.DiscoverRelaysForAppAsync` / `ListAllRelaysAsync` in C#;
  `discoverRelaysForApp` / `listAllRelays` in TS; `discover_relays_for_app` / `list_all_relays` in
  Python — all three SDKs ship a `FederationClient`) verifies every descriptor signature **and**
  cross-checks each advertised `ownerClientId` against the app's compiled-in trust anchor
  (`deriveClientId(VestaAppConfig.ownerPublicKey)`), dropping relays that advertise the app under
  a different owner.
- Surviving relays are returned as **unverified candidates**. They are **never auto-adopted** —
  owner-signed manifest relays remain the only automatic failover tier. The user adopts a
  discovered relay manually via the existing relay-override escape hatch
  (`SetUserRelayOverrideAsync` in C#, `setUserRelayOverride` in TS, `set_user_relay_override` in
  Python).


## See also

- [events.md](events.md) — the `VestaEvent` carried in `PUBLISH` / `EVENT` / `EVENTS_BATCH`
- [server-configuration.md](server-configuration.md) — signature verification, ACL enforcement, TTL cleanup
- [src/VestaCore/Protocol/](../src/VestaCore/Protocol/) — the C# message types are the source of truth
