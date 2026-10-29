# Vesta — Planning

Compact working reference: what Vesta is, the decisions that must not be re-litigated, what is
done/open, how to release, and the quirks that bite when switching machines. Full historical
rationale (storage comparison, device-group phases, every TODO row, original milestones) is
archived at [docs/archive/PLANNING-2026-10-01.md](docs/archive/PLANNING-2026-10-01.md); user-facing
how-to lives in [docs/](docs/README.md). Code is the source of truth when docs disagree.

## Vision

A protocol + runtime for networked apps where **the relay is like a git remote or torrent
tracker, not the authority**. Clients own their data (local SQLite log, self-sovereign Ed25519
identity); any relay can be swapped for another and the app runs unchanged.

### Target audience (decided 2026-10-28)

Vesta is for people who want to ship a connected app **without promising to run its backend
forever**. In order of priority:

1. **DIY / tinkerers** — side projects, tools for friends, clubs and households, IFTTT-style glue
   between a sensor and a dashboard. They cannot guarantee a server in five years and should not
   have to.
2. **Idealists** — the "software should outlive its maker" crowd (the *Stop Killing Games*
   sentiment, but for general applications): self-verifying logs, interchangeable relays, no
   dead-server lock-in, built in from the start.
3. **Companies seeking goodwill** — run a relay while it makes sense, hand users their data when
   it does not. They may self-host; Atrium is the convenience for those who would rather not.

**Explicitly not a target: fast-tick real-time games.** Without substantial refactoring (UDP /
binary transport, server-side simulation authority, tick-rate scheduling) a storing JSON-over-
WebSocket relay is the wrong shape, and we will not bend the core toward it. If it ever happens
it is a *separate flavour/version*, not a mode of this relay. Consequences: the docs use
general vocabulary (users, members, a trusted writer / service; not players, shopkeeper,
`mygame/`), the `README` has a "Who Vesta is for" + "Good fit / not a fit" table that says so,
and the #25–#32 hardening items below stay because they are general (ACL, limits, identity
proof) — they were merely *found* through a game evaluation. Turn-based / slow multiplayer is
fine and stays in the fit table. Atrium's hidden Realtime tier (its D10) remains scaffolded
and unpriced; its blurb no longer leads with games.

## Core principles (non-negotiable)

1. **Data belongs to the client.** The server stores and forwards signed, immutable events; it
   never interprets payloads (business logic is client-side).
2. **Server is a relay, not an authority.** It assigns per-channel sequence numbers and enforces
   operator policy (ACL, quotas) — nothing else.
3. **Protocol over platform.** Three reference SDKs (C#, TS, Python) are peers; wire format is the
   contract.
4. **Offline-first.** Local cache + outbox; reconnect catch-up by sequence.
5. **Explicit conflict resolution.** SDK provides LWW/append-only primitives; apps choose.
6. **Endpoint fungibility.** Swap the relay URL (Atrium-managed ↔ self-hosted) and the app runs
   unchanged. This is the parity test for every feature.

## Boundaries

- **This repo is public and ships artifacts; it operates nothing.** No cloud infra, no deploy
  workflows, no cloud creds, and it never references Atrium internals.
- **Atrium** (`vesta_atrium`, proprietary, sibling folder) is the managed-cloud control plane. It
  consumes `VestaProtocol.Core` from NuGet and the relay image from GHCR, and talks to the relay
  only via `/admin/*`. Changing `VestaIdentity`, `Base64Url`,
  `AppId` (all in namespace `Vesta`), the `/admin/*` JSON, or the image contract (port 8080, non-root,
  `ConnectionStrings__Vesta`, `/health`, `/health/ready`) is a **cross-repo breaking change**.
- Relays are for **operators only**; app developers connect to one, never deploy one.

## Architecture

```
src/VestaCore     shared types, protocol messages, signing, projections, relay manifests
src/VestaServer   ASP.NET Core relay: WS /ws, /admin/*, /federation/*, /health*; Postgres
src/VestaClient   C# SDK (SQLite cache + outbox, relay directory, recovery picker)
clients/vesta-client-ts | vesta-client-py   peer SDKs (same behaviour, same tests)
examples/*        real apps proving the SDK (durable identity, local cache, offline, projection)
tests/*           xUnit; server integration tests use Testcontainers Postgres
```

- **Events:** `VestaEvent` (client-authored, signed) vs `SequencedEvent` (wraps it + server
  `sequence`/`receivedAt`). Composition, never inheritance.
- **Storage split:** raw Npgsql for the events hot path (append, range read, LISTEN/NOTIFY);
  EF Core for metadata (`channels`, `channel_access`, `apps`). Client side: SQLite. Export: JSON
  Lines. `IEventStore` is the abstraction (InMemory + Npgsql server-side, SQLite client-side).
- **Ordering:** server sequence = authoritative total order per channel (there is no separate
  cursor). Client wall-clock timestamp = LWW tiebreaker. Event ids are UUID v7.

## Protocol decisions

- Messages: HELLO/WELCOME, PUBLISH/EVENT/ACK, SUBSCRIBE, FETCH/EVENTS_BATCH, ERROR,
  CREATE_CHANNEL, DELETE_CHANNEL (admin), REGISTER_APP. Details: [docs/protocol.md](docs/protocol.md).
- **Signing:** RFC 8785 JCS canonical JSON + Ed25519 over every client-authored field except
  `signature`; `sequence`, `receivedAt` and `metadata` are never signed. Libraries:
  `JsonCanonicalization` (C#), `canonicalize` (TS), `json-canonicalization` (Py).
- **Identity:** `clientId = base64url(sha256(publicKey))[:22]`. HELLO carries the public key;
  server verifies it derives to the announced `clientId`; PUBLISH must match the connection's
  `clientId`; signed events are verified. `Protocol:RequireSignedEvents=true` makes it strict. **HELLO alone proves nothing**: a client that sends
  `proofSupported` + `publicKey` is challenged (`CHALLENGE`/`PROVE`, #32) and only then gets an identity,
  admin status or a per-client-id connection cap; `Protocol:RequireHelloProof` makes it mandatory (off by
  default for old SDKs).
- **Channels:** slug `[a-z0-9][a-z0-9\-/]*[a-z0-9]`, max 128; created implicitly in open mode;
  ACL modes open/private/broadcast via `CREATE_CHANNEL` (roles admin/member/reader). Soft-delete via `deleted_at`, hard-delete
  by `ChannelDeletionPrunerService` after a grace period.
- **Apps:** `AppId` = one slug segment, max 64. `REGISTER_APP` + `Protocol:RequireAppRegistration`
  gates everything per app (`UNKNOWN_APP`). Six quotas in `AppQuotas` enforced hot-path +
  `AppQuotaPrunerService`; errors `QUOTA_EXCEEDED` / `RATE_LIMITED`.
- **App lifecycle (admin API):** apps are *soft-deleted* (`apps.deleted_at`) with their channels
  stamped with the same timestamp; restore undoes exactly that cascade. `AppDeletionPrunerService`
  purges after a grace period (default 7 d, opt-in); the id stays reserved until purge. Rename is
  deliberately absent: `AppId` is the immutable channel namespace. Quota alerts (`app_alerts`) are
  evaluated by the relay inside the quota sweep (80 % / 100 %); the relay never notifies — consumers
  (Atrium) poll `/admin/alerts`. Export is streamed JSON Lines (manifest + events); every mutating
  admin call lands in the durable `admin_audit` table. See `docs/server-configuration.md`.
- **Traffic control (admin API):** deleting an app also disconnects its subscribers (`UNKNOWN_APP`
  notice + close 1008; the SDK is one-app-per-connection). *Pause* (`paused_at`) refuses PUBLISH /
  CREATE_CHANNEL with `APP_PAUSED` — a transient limit in all three SDKs, so outboxes keep events
  until resume; reads keep working. *Throttle* (`throttle_per_minute`) is one shared per-app token
  bucket answering `RATE_LIMITED`, deliberately separate from `AppQuotas` so tier tooling (Atrium)
  never overwrites operator actions.
- **Metadata flags** (unsigned, in `metadata`): `ttlSeconds` (expires_at, excluded from
  catch-up, swept by `ExpiredEventCleanupService`), `volatile` (relayed, never stored),
  `replace`. See [docs/events.md](docs/events.md).
- **Reserved namespace:** `vesta/*` channels only accept `vesta.*` event types; `CREATE_CHANNEL`
  on them is rejected. Used by device groups (`vesta/identity/{groupId}`) and relay manifests
  (`{appId}/vesta/relays`).
- **Managed vs self-hosted parity:** limit errors must be classified identically everywhere
  (`VestaErrorCodes` / `classifyErrorCode` / `classify_error_code`; `OnLimited` / `limited` /
  `on_limited`; dead-lettering of permanently rejected outbox events).

## SDK primitives

- Projections (namespace `Vesta` + TS/Py mirrors): `EventReducer<T>`, `AppendOnlyLog<T>`,
  `LwwRegister<T>`, `LwwMap<K,V>`, `ProjectionCheckpoint`, snapshots via `IProjectionStore`.
  `Apply(SequencedEvent)` advances `LastSequence`; `ApplyLocal(VestaEvent)` does not.
  [docs/projections.md](docs/projections.md).
- Device groups (Phase 1 only): `IdentityLinkBuilder`, `DeviceGroupProjection`, `PairingPayload`,
  `VestaConnection.{CreateDeviceGroup,LinkDevice,JoinDeviceGroup,GetDeviceGroupMembers}Async`.
  Peer-equality trust; private keys never move. Phases 2–6 (revocation, rotation, cross-app,
  recovery, delegation) deferred; **server-mediated key transfer is rejected** (see archive).
- Identity backup & recovery (#30, `docs/identity.md`): `ExportEncrypted`/`ImportEncrypted` (Argon2id +
  AES-256-GCM), 24-word recovery phrase (BIP-39 wordlist on the raw seed), `RelayBackup` (key + channel from one
  Argon2id run over app/username/password). Byte-compatible across the three SDKs; the server-mediated
  plaintext key transfer stays rejected.
- Relay independence (#20): `VestaConnection` **requires** `VestaAppConfig { AppId,
  OwnerPublicKey, DefaultRelays }` (C# also accepts `(appId, ownerKeyBase64Url, params string[] relays)`);
  `RelayDirectory.CreateDefault` / `createDefault` / `create_default` caches under
  `~/.vesta/relays/`; owner-signed `RelayManifest` (`vesta.relay-manifest` events) adopted only
  for newer `version`; order = user override > manifest/escape fallbacks > app defaults.
  Default-on in all three SDKs: pass `appConfig`/`app_config` (+ `identity`) and the connection
  builds the directory, derives the client id and resolves relays itself. TS needs `vesta-client/node`
  imported to get file-backed stores (browsers fall back to `localStorage`, else in memory).
  `DiscoverRelaysAsync` and `PublishRelayManifestAsync` (`discoverRelays`/`publishRelayManifest`,
  `discover_relays`/`publish_relay_manifest`) wrap federation discovery and owner manifest publishing.
- **One namespace (0.2.0):** every public C# type in `VestaProtocol.Core` and
  `VestaProtocol.Client` is in `namespace Vesta;` (folders unchanged) so an app needs a single
  `using Vesta;`, matching `from "vesta-client"` / `from vesta_client import`. Package ids unchanged.
- **TS runtime (0.2.0):** `createSocket` is optional and defaults to the global `WebSocket`; engines
  `node >=22`, the `ws` peer dependency is gone (pass `createSocket` to use another implementation).
- Federation (#21): gossip pull over `/federation/{descriptor,peers,apps/{appId}}`, self-signed
  `ServerDescriptor`; dual opt-in (`Discovery:Enabled` + per-app `discoverable` column, never
  parsed from payloads). Discovered relays are **show-only**; `FederationClient` drops any whose
  `ownerClientId` mismatches the compiled-in owner key.
- Recovery (#24): `RelayRecoverySession` (headless state machine) after `RelayExhaustionPasses`
  (3) failed passes; console/web/loopback pickers in all SDKs; **never auto-adopts**.
  [docs/relay-recovery.md](docs/relay-recovery.md).

## Status

**Done:** TTL/metadata, projections (+ snapshots), ACL, signature verification, app registration
+ quotas, outbox/idempotency audit, channel delete + pruner, `/admin/*` HTTP API + GUI (challenge
→ Ed25519 sign → bearer; `Admin:BootstrapPublicKeys`), device groups Phase 1, relay
independence, federation, relay recovery, SDK publishing, relay container image, CI, **admin v2**
(per-app dashboard, app soft-delete/restore/purge, JSON Lines export, quota alerts, audit log,
rate-limited `/admin/auth/*`, self-contained GUI with no CDN dependency).

**Open:** #13 client READMEs (TS/Py getting-started), #14 observability (metrics, structured
logging), #15 multi-server (scale-out sticky sessions / shared rate-limit + token store),
#33 per-channel encryption (below).

**Hardening** (originally surfaced by a multiplayer-game-with-store evaluation, kept because every
item is general: signature verification, ACL, or operator-configurable limits, never payload logic;
games themselves are not a target, see *Target audience*):

- **#25 Channel write control.** ✅ **Done** (relay + docs + SDK types/docstrings; `ChannelWriteControlTests`,
  `NpgsqlChannelAccessStoreTests` against Postgres):
  `reader` role (`admin|member|reader`) and `broadcast` visibility (anyone subscribes/fetches, only
  members/admin publish; never implicit). `IChannelAccessStore.CanPublishAsync` is the write check
  (PUBLISH, volatile included); `CanAccessAsync` stays the read check for SUBSCRIBE/FETCH/HELLO. Visibility
  and role are free text columns, so **no migration**. `{appId}/vesta/relays` is owner-only-write for
  registered apps (`EnsureManifestPublisherIsOwnerAsync`). Write control is only as strong as the identity
  behind it — sound with `RequireSignedEvents=true`; read control is proof-backed with `RequireHelloProof=true` (#32). **Still open:** admin GUI could show role
  per member; Atrium `AppDetail` should display the new values (Atrium `RelayChannelMember.Role` /
  `RelayChannelSummary.Visibility` are plain strings, so nothing breaks).
- **#26 Trusted-writer recipe (docs).** ✅ **Done:** `docs/patterns.md` (indexed in `docs/README.md`) — service
  identity + `broadcast` channel + pinned signer key checked in every reducer, request/confirm purchase
  flow, what the relay never validates, `receivedAt` for time, and the developer-abandons case. It needed
  receive-side verification in every SDK, so `verifyEvent` (TS) and `verify_event` (Python) were added
  alongside C# `EventSigner.VerifyEvent` (tests: `verify-event.test.mjs`, `test_verify_event.py`).
  **Still open:** none for the relay. ✅ **Owner-only channel creation** (`apps.owner_only_channel_creation`,
  `PATCH /admin/apps/{id}/channel-creation`, enforced in `HandleCreateChannelAsync` and, for the implicit create,
  in `EnforcePublishQuotasAsync` via `GetVisibilityAsync`; `OwnerOnlyChannelCreationTests`). Decisions: a separate
  operator-set flag rather than a field of `AppQuotas` (quotas are replace-all, so a platform rewriting quotas would
  clobber a policy); a bool, not an `owner|anyone` enum, since there are only two states; operator-only because
  `REGISTER_APP` has no spare field and a wire change would touch all three SDKs for no client-visible gain
  (denials are plain `ACCESS_DENIED`). A stranger's *volatile* publish to a channel that does not exist yet is also
  denied. Left open: Atrium does not call the endpoint yet (see its PLANNING); an optional `examples/Shopkeeper` demo.
- **#27 Relay baseline limits.** ✅ **Done** (`Protocol:Defaults`, `RelayBaselineLimiter`,
  `BaselineLimitsTests`; documented in `docs/server-configuration.md`): default `PublishRatePerMinute`
  (600, per `(app, client)`, also for unregistered apps; an app quota wins),
  `MaxChannelsPerClientPerApp` (100, distinct channels published to — deliberately not "implicitly
  created", which would need a per-publish store lookup), `MaxSubscriptionsPerConnection` (100, new
  `CONNECTION_LIMITED`, classified in all three SDKs as a non-transient, non-event-fatal limit),
  `MaxConnectionsPerIp` and `ConnectionAttemptsPerMinutePerIp` (HTTP 429 before the upgrade; **off by
  default** because behind a proxy without `ForwardedHeaders:Enabled` every client shares one IP — Atrium's
  relay stack does not set it yet). `MaxConnectionsPerClientId` (10) was added with #32 and counts only
  proven connections.
  Relay-wide defaults for the other quotas (payload size etc.) are unchanged (null = unlimited).
  **Deviations from the original plan:** (1) the per-IP limits ship **off** — enabling them requires
  `ForwardedHeaders:Enabled` on the relay first, otherwise they cap every user behind the proxy at once
    (users behind one NAT also share an IP); (2) `MaxConnectionsPerClientId` waited for #32 because
  without a key proof it would let anyone lock out a known client id; (3)
  `MaxChannelsPerClientPerApp` counts distinct channels published to rather than implicitly created
  ones, and `HelloRatePerMinutePerIp` became `ConnectionAttemptsPerMinutePerIp` (enforced at the
  upgrade, before HELLO).
- **#28 Catch-up at scale.** ✅ **Server done:** `Protocol:MaxFetchLimit` (default 1000) clamps
  `FETCH`, and HELLO / SUBSCRIBE catch-up now pages in consecutive `EVENTS_BATCH` frames instead of
  silently truncating at 100 (`ProtocolHandler.SendCatchUpAsync`; `CatchUpPagingTests`). The SDKs already
  apply multiple batches, so catch-up needs no SDK change. ✅ **SDK paging helper:**
  `FetchAllAsync` (C#) / `fetchAll` (TS) / `fetch_all` (Python) page a manual `FETCH` to the end. Decisions: it
  stops on an **empty page**, not a short one, because the relay's clamp is invisible to the client (costs one
  extra round trip); batches and errors are matched by channel id, so it must not run beside a catch-up
  `SUBSCRIBE` for the same channel (documented, not enforced); the relay now puts the `channelId` on FETCH
  rejections (`ACCESS_DENIED`, `CHANNEL_DELETED`, `INVALID_CHANNEL`, `UNKNOWN_APP`, `APP_NOT_ALLOWED`) so a
  failed page is attributable. Tests: `FetchAllTests` (C#, scripted loopback relay), `fetch-all.test.mjs`,
  `test_fetch_all.py`. `VestaTestClient` was left alone (test-only client). ✅ **(b) fixed:** the one-shot
  `GetDeviceGroupMembers` helpers resolved on the first batch, wrong for a channel larger than `MaxFetchLimit`; they
  now `FetchAll` the identity channel and replay it (`DeviceGroupMembersTests`, `device-group-members.test.mjs`,
  `test_device_group_members.py`). Behaviour change: they no longer subscribe (the result is a snapshot, no live
  channel is left behind), the timeout is per page (default 10 s, was 5 s overall), and a relay that does not answer
  now throws instead of silently returning an empty group; an empty channel answers at once. ✅ **(c) done:**
  `docs/patterns.md` § Durable state and presence: separate channels (what goes volatile / replace / TTL, why presence
  gets its own channel, snapshot the durable projections but not presence, retention means a snapshot only helps a
  returning client so authority services republish a checkpoint).
- **#29 Realtime quota fields.** ✅ **Done** (`AppQuotas.MaxConnections` / `VolatileRatePerMinute`, `apps`
  columns `max_connections` / `volatile_rate_per_minute` via migration `AddAppRealtimeQuotas`, Npgsql store,
  admin GUI quota list; `RealtimeQuotaTests`, `NpgsqlAppStoreQuotaTests`; `docs/server-configuration.md`).
  `volatile_rate_per_minute` gives volatile events a bucket of their own (keyed `(app, client)`, `*v:` prefix);
  **null keeps the old behaviour** (volatile shares the publish bucket), so apps that set nothing — including
  every Atrium tier — are unchanged. `max_connections` counts connections that *joined* the app (first
  SUBSCRIBE / CREATE_CHANNEL / PUBLISH / HELLO channel) until disconnect; over the cap = `CONNECTION_LIMITED`.
  The `/admin/*` JSON gains `maxConnections` and `volatileRatePerMinute` automatically (additive).
  **Cross-repo:** `PATCH /admin/apps/{id}/quotas` is replace-all, so a client that does not know the new
  fields (older Atrium) resets them to null on every call; Atrium's `RelayAppQuotas` now mirrors them.
  **Still open:** the connection count is per relay process and unlocked (can overshoot a little; multi-host
  needs the shared backend of #15). ✅ **`max_connections` alerts + dashboard meter** (`connections` alert metric from
  the `AppQuotaPruner` sweep, `joinedConnections` on `GET /admin/apps/{id}`). Decision: the alert observes the
  **peak** joined count since the last sweep (`ConnectionManager.NoteJoined`/`TakePeakJoined`, tracked only for
  apps with a `max_connections` quota), not a snapshot, because a 5-minute sample would miss a game's login
  spike; the cost is that an alert clears one sweep late. Atrium's hidden Realtime tier now uses both quotas with
  indicative values (see its D10).
- **#30 Identity backup primitives.** ✅ **Done** in all three SDKs (`IdentityBackup` / `RelayBackup` /
  `RecoveryPhrase` in `VestaCore`; `vesta_client/backup.py`; `clients/vesta-client-ts/src/backup.ts`; new
  `docs/identity.md`, indexed). Encrypted export: Argon2id (64 MiB, 3 passes, p=1) → 64 bytes, first 32 =
  AES-256-GCM key, `clientId` bound as AAD, versioned JSON blob, NFKC password, cost caps on import (512 MiB /
  10 passes). Recovery phrase: 24 words, BIP-39 wordlist + checksum on the raw seed (no PBKDF2), BIP-39
  vectors in tests. Relay-stored backup: **lookup-id decision** — one Argon2id run over `(appId, username,
  password)` with a deterministic salt gives both the key and the channel (`vesta/identity-backup/{hex32}`,
  bytes 32–47), so neither the channel nor the user list is enumerable; published as a signed `replace` event
  of type `vesta.identity-backup`; `Restore` derives once, ignores foreign salts/costs and junk. Blobs, phrases
  and channels are **byte-for-byte interchangeable** (a Python-made blob and channel id are fixed vectors in the
  C# and TS tests; `IdentityBackupRecoveryFlowTests` runs the whole flow through a real relay). Python now
  needs `cryptography>=44` (Argon2id). **Gotcha:** NSec's `Argon2Parameters.MemorySize` is in KiB, not bytes
  — the cross-language vector caught it. ✅ **One-call recovery:** `VestaConnection.RecoverIdentityAsync` /
  `recoverIdentity` / `recover_identity` derive the keys, connect with a throwaway identity, page the backup
  channel with the #28 `FetchAll` helper and return the identity (or null), plus `RestoreIdentityAsync` /
  `restoreIdentity` / `restore_identity` on an existing connection. Decisions: it lives on `VestaConnection`, not on
  `RelayBackup`, because `VestaCore` has no client; the TS version loads `backup.js` with a dynamic `import()` so
  the Argon2 + word-list code stays out of bundles that never recover, and the Python version derives in a worker
  thread (about a second of CPU); wrong password and unknown username are both `null`, so recovery never leaks
  which usernames exist. Tests: `RecoverIdentityTests`, `recover-identity.test.mjs`, `test_recover_identity.py`
  (scripted relay with a real backup event). **Gotcha:** the Python SDK's best-effort peer-cache refresh makes a
  real HTTP call after any connect, which stalls `asyncio.run` for seconds on Windows when the relay URL is fake;
  tests patch `_refresh_peer_cache`. **Still open:** `VestaTestClient`; a UI/UX path (when to prompt, "pair a
  second device" nudge) belongs to the game / #31; the relay may prune `vesta/identity-backup/*` (not an app
  channel, so app retention does not apply) — decide a retention rule; password rotation leaves the old blob.
- **#31 Developer-hosted accounts.** ✅ **Done** (`examples/AccountServer`, `tests/AccountServer.Tests`, SDK
  support in all three languages, `docs/identity.md` § Developer-hosted accounts, root README). Goal: a game
  can require account + password so a player's identity follows them across devices and survives the
  developer abandoning the game. **Decisions:** (1) the account server stores an *opaque* blob and a verifier
  and **never sees the password or key**: the client derives `AuthSecret = HMAC-SHA256(material,
  "vesta-account-auth-v1")` from the same Argon2id output that encrypts the blob (`RelayBackup.AuthSecret`,
  `ExportBlob`, `ImportBlob` / `auth_secret`, `export_blob`, `import_blob` / `authSecret`, `exportBlob`,
  `importBlob`; shared vector `CLrdKDSa…jcQ` for `("game","alice","pw", 256 KiB, 1 pass)`); rejected
  alternative: sending the password (or a key-derived value that doubles as the key) to the server, which
  would let a breach decrypt identities; (2) I reused `RelayBackup` instead of a new type, because the
  derivation is identical and none of this was released yet; (3) the example has **no Vesta dependency** (so
  it builds against any SDK version and is not in `examples.yml`'s published-package matrix) and is tested
  from a separate project, since a second top-level `Program` cannot share an assembly with
  `VestaServer.Tests`'s; (4) the server verifier is `SHA-256(salt ‖ authSecret)` (the secret is already 256-bit
  random, so a slow hash adds nothing), stateless re-authentication on every call, identical `401` for unknown
  user and wrong secret, throttle 10 failures/user and 50/address per 15 min. **Known trade-offs:** a
  per-username throttle lets anyone lock a known username out for 15 minutes; a leaked database equals a
  leaked backup file (offline guessing, one Argon2id run per guess); signup is unthrottled. **Still open:**
  session tokens, email/recovery flows, signup abuse protection and a database are deliberately left to the
  game; an Atrium-managed account service is listed under Atrium's Later; the minimal API rejects an
  inferred body on `DELETE` (500 until `[FromBody]` is added).
- **#32 HELLO key-possession proof.** ✅ **Done** (core + relay + C#/TS/Python SDKs + docs; `HelloProofTests`
  in VestaCore/VestaServer, `HelloProofHandshakeTests` (C#), `hello-proof.test.mjs`, `test_hello_proof.py`).
  `HELLO` only checked that the announced `PublicKey` derives to the `clientId`, so a client id was a mere
  claim: anyone who knew a member's `clientId` + public key (both visible in event streams) could read
  their private channels, and an operator's (public) admin key announced in HELLO made a connection a
  server admin over WS (`DELETE_CHANNEL`). Now a client sending `proofSupported` + `publicKey` gets
  `CHALLENGE { nonce }` and answers `PROVE { signature }` over `vesta-hello-v1\n{nonce}\n{clientId}`
  (`HelloProof`; shared test vector in all three SDKs); no identity exists until it passes. Compatible by
  design: legacy clients are never challenged (older relays ignore the flag), `Protocol:RequireHelloProof`
  (default **off**) rejects clients that cannot prove and blocks everything but HELLO/PROVE until proven.
  **Behaviour change:** server-admin promotion over WebSocket now requires the proof (the old path was a
  privilege escalation); `DeleteChannelTests` do the proof. Also fixed: a failed HELLO no longer leaves the
  claimed `clientId` on the connection, and a repeated HELLO clears subscriptions/admin/identity.
  **Still open:** none for Atrium's relay: `server-v0.3.1` is released and Atrium's params pin it with
  `requireHelloProof = true` (its `vesta-provision.js` answers `CHALLENGE`, smoke-tested 2026-10-26); only the
  deploy remains, and proof binds the connection, not each message, so a TLS-terminating
  middlebox could still relay a live session; the federation HTTP and `/admin` surfaces are unchanged;
  `VestaTestClient` does not do the proof.
- **#33 Per-channel encryption.** ⏳ **Open — design below, no code yet** (audience decision 2026-10-28,
  design 2026-10-29). Today every payload is stored in the clear, so the relay operator (and anyone with
  the database, and any *stranger's relay a user fails over to*) can read it; the README's fit table says
  so. The decision is that confidentiality is **configurable per channel**, not per app and not
  relay-wide: most channels (a noticeboard, presence) gain nothing from it, and encrypted channels cost
  more to operate (no readable export, key-distribution traffic, no payload-shape debugging), so an
  operator must be able to count and price them. **TLS is not this**: `wss://` protects the pipe and is
  already required (see *Mobile & TLS* below); #33 is about the relay never seeing plaintext, which is
  what makes relay fungibility safe for private data.

  **Design (reviewable, each bullet is a decision):**
  1. **Flag, not mode.** `CreateChannelMessage` gains `bool? Encrypted = null` (additive). `encrypted`
     **implies `private`** (keys are distributed by membership, so `public`/`broadcast` + encrypted is
     meaningless; relay rejects the combination with `INVALID_CHANNEL`). Never implicit, like `broadcast`.
     Stored as `channels.encrypted` (generated migration), returned on `GET /admin/channels*` and
     `GET /admin/apps/{id}` as `encryptedChannels` (count). `WELCOME` gains `features: ["encrypted-channels"]`
     (additive) so an SDK **refuses** to create an encrypted channel against a relay that does not advertise
     it — an old relay would silently ignore the unknown field and store plaintext.
  2. **Relay enforces shape, never content.** A `PUBLISH` to an encrypted channel must have a payload of
     exactly `{ "enc": 1, "kid": <int>, "n": "<b64url 12 B>", "ct": "<b64url>" }` or be a `vesta.*` control
     event; anything else → `ERROR { code: "ENCRYPTION_REQUIRED" }`. This is a cheap guard against a
     misconfigured client leaking plaintext, not a security boundary. Everything else on the relay is
     unchanged: `sequence`, `replace`, `volatile`, `ttlSeconds`, idempotency on `id`, ACL and signature
     verification all operate on cleartext fields. **`eventType` stays in the clear** (it drives `replace`
     and projections). Apps that must hide it publish one opaque type and carry the real type inside the
     plaintext, accepting coarser `replace`; documented, not special-cased.
  3. **Payload crypto.** AES-256-GCM (already in all three SDKs from #30 — deliberately not XChaCha, which
     Python `cryptography` lacks), 96-bit random nonce, AAD = `"vesta-enc-v1\n{channelId}\n{eventId}\n{eventType}"`
     so a ciphertext cannot be re-homed under another event. Plaintext is the canonical-JSON app payload.
     Signing is unchanged: the Ed25519 signature covers the ciphertext object, so authorship is still
     verifiable by the relay and by non-members. Random-nonce budget (2^32 per key) is enforced by
     rotation, not counting.
  4. **Keys.** A channel key is 32 random bytes with an integer `epoch` (= `kid`), starting at 1. Each
     identity gets an X25519 keypair **derived from its Ed25519 seed**, `HKDF-SHA256(ikm = seed, salt = "",
     info = "vesta-x25519-v1")` — derived, not converted, because Ed25519→X25519 conversion is not available
     in NSec / `cryptography` without extra libraries, and the derived public key is what gets announced
     anyway. Private keys never move, consistent with device groups.
  5. **Distribution — the relay stores no public keys** (`GRANT_ACCESS` is by `clientId`, a hash), so the
     sketch's "admin reads members' keys from `channel_access`" does not work. Instead, on the channel
     itself: a member publishes a signed `vesta.channel-key-request { x25519PublicKey }` (its `clientId`
     is derivable from the Ed25519 key in the signature check, so the request is self-authenticating); an
     **admin** answers with `vesta.channel-key { recipient, keys: [{ epoch, epk, n, ct }] }` where each key
     is sealed ECIES-style (ephemeral X25519 `epk` × recipient key → HKDF → AES-256-GCM). A new member
     receives *every* epoch so it can read history; after rotation existing members receive only the new
     one. Ordinary (non-`replace`) events; they ride the normal catch-up. **Admin must be online** to onboard
     a member; an admin may pre-seal for `initialMembers` when it already knows their X25519 keys (invite
     flow below). Recipients accept `vesta.channel-key` only from admins they trust: the **app passes the
     admin public key(s)** when opening the channel (exactly the trusted-writer pattern), or an invite
     (`ChannelInvite`, a `PairingPayload`-style b64url of `channelId + adminPublicKey + inviteeHint`) carries
     it. No trust-on-first-use.
  6. **Rotation & revocation.** Rotation = new epoch sealed to the remaining members. It is only
     meaningful with a `REVOKE_ACCESS` message, **which does not exist yet** — add it in the same release
     (admin-only, mirror of `GRANT_ACCESS`, audited). Revoked members keep what they could already read
     (no forward secrecy; accepted — they had it on disk anyway). Device groups: a member's other devices
     are separate `clientId`s and request keys themselves; the app treats the group as one person.
  7. **SDK surface (names tentative, identical in all three).** `connection.OpenEncryptedChannelAsync(
     channelId, adminPublicKeys, keyStore)` subscribes, sends the key request, decrypts incoming events
     (`EVENT`/`EVENTS_BATCH`) and encrypts on `PublishAsync`; `IChannelKeyStore` is **pluggable** like
     `IProjectionStore` (SQLite default on .NET, file / `localStorage`-`IndexedDB` in TS, file in Python)
     so mobile SDKs can back it with Keychain / Android Keystore. Projections get a decrypt step before the
     projector (`EncryptedReducer<T>` wrapper or a hook on `EventReducer`); local snapshots store plaintext
     (the device holds the key). Events that fail to decrypt (unknown epoch yet) are **buffered** until the
     key arrives, then applied in sequence order. Shared byte-exact vectors as in #30: X25519 derivation,
     sealing, payload encryption.
  8. **Rejected alternatives.** Relay-held or relay-escrowed keys (relay becomes the authority); app-wide
     encryption (every presence heartbeat pays); encrypting the whole event (breaks sequencing, `replace`,
     idempotency); Ed25519→X25519 conversion (library gaps); per-message ratchets / MLS (forward secrecy is
     out of scope for v1 and the operational cost is disproportionate for the audience); public/broadcast
     + encrypted (no membership → no key distribution).
  9. **Phasing.** (A) relay: flag, `REVOKE_ACCESS`, shape check, `features`, admin exposure,
     `max_encrypted_channels` quota → `server-v0.4`; (B) SDK primitives + vectors in all three; (C) key
     distribution events + `OpenEncryptedChannel` + an example (`examples/PrivateNotes` or an encrypted
     mode for `ChatRoom.CLI`) + `docs/encryption.md` (new file, indexed) → SDK `0.4.0`; (D) Atrium quota
     / add-on. A and B are independent; C needs both.

  **Cross-repo:** Atrium wants to **limit and/or charge per encrypted channel**: `AppQuotas.MaxEncryptedChannels`
  (`max_encrypted_channels`, enforced at `CREATE_CHANNEL`, `QUOTA_EXCEEDED`) and the `encryptedChannels`
  count on `GET /admin/apps/{id}`; the quota PATCH is replace-all, so Atrium's `RelayAppQuotas` must learn
  the field in the same release. Until shipped, docs tell developers not to put secrets in events.

- **Mobile & TLS (clarified 2026-10-29).** iOS (App Transport Security) and Android (cleartext traffic
  blocked since API 28) refuse plain `ws://` to public hosts, so a mobile app needs a **`wss://`** relay —
  which every real relay already is: the relay image does not terminate TLS by design, the operator's
  proxy/platform holds the certificate (Atrium's App Service URL is `wss://` with a platform cert; a
  self-hoster uses Caddy / nginx + Let's Encrypt / a CDN). Nothing in the protocol or relay changes for
  mobile; `docker-compose.yml`'s `ws://localhost` is dev-only. The **actual mobile gap is SDK platform
  coverage**: C# under .NET MAUI (NSec native + SQLite, unvalidated — same bucket as the deferred Unity/
  Godot item), TS under React Native / Capacitor (the browser code path; needs a non-`localStorage` store
  and a secure key store for #33), and no Swift/Kotlin SDK. Tracked under *Deferred*.

**Deferred:** device-group phases 2–6; relay Layer C (re-seed empty relay from client logs);
admin scoped keys (read-only vs full), client-side importer for the export format; **mobile SDK
coverage** — validate `VestaProtocol.Client` (NSec native + SQLite) under .NET MAUI, Unity IL2CPP/Mono
and Godot C#, validate the TS SDK under React Native / Capacitor (needs a non-`localStorage` store),
and decide whether a Swift/Kotlin SDK is ever in scope; point the README
Atrium link at the live portal URL; optional `examples/TrustedWriter` demo of the #26 pattern (a
non-game example such as the tool-library "librarian" from `docs/patterns.md`).

## Releasing

- **Shared version lives in 3 manifests**: `Directory.Build.props` (`VersionPrefix`),
  `clients/vesta-client-ts/package.json`, `clients/vesta-client-py/vesta_client/__init__.py`
  (`pyproject.toml` reads it dynamically). Current: **0.3.1**.
- Bump all three → `powershell -File .github/scripts/check-version.ps1 -Tag vX.Y.Z` →
  `git tag vX.Y.Z && git push origin vX.Y.Z`. `release.yml` verifies, publishes NuGet
  (`VestaProtocol.Core`, `VestaProtocol.Client`), npm + PyPI (`vesta-client`) via OIDC trusted
  publishing (environment `release`, repo var `NUGET_USER`), then a GitHub Release. Idempotent —
  safe to re-run. npm publishes directly via the trusted publisher — no manual approval on npmjs.com.
- **Relay image** is a separate tag line: `server-vX.Y.Z` → `release-server.yml` →
  `ghcr.io/jesperandersson89/vesta-server:X.Y.Z` (+`:latest`); `main` pushes get `:sha-<short>`.
- Only `VestaCore`/`VestaClient` pack (`IsPackable=false` default). Examples pin the published
  packages (C# `VestaProtocol.Client`, TS `^X.Y.Z` — regenerate lockfiles, Py `>=X.Y.Z`); bump
  them *after* the release is live (Dependabot or manual). `examples.yml` can be re-dispatched
  with `sdk_version`.
- Atrium pins `VestaProtocol.Core` separately — bump it there after each release.

## Conventions

net10.0, nullable, implicit usings, **no `var`**, file-scoped namespaces (one public namespace,
`Vesta`, for both SDK assemblies), no `#region`, records
for immutable data, switch expressions, `Async` suffix, tests `Method_Scenario_Expected`.
Port client-visible behaviour to **all three SDKs** (say so explicitly if C#-only). Sweep
examples when `VestaCore`/`VestaClient` surfaces change. Never hand-write EF migrations
(`dotnet ef migrations add` → both `.cs` + `.Designer.cs`). Update `docs/*.md` in the same
change (table in `.github/copilot-instructions.md`). Stop and ask on environment problems.

## Quirks & gotchas

- Shell is **Windows PowerShell 5.1**: chain with `;` not `&&`; `pwsh` is not installed — use
  `powershell -File` for the `.ps1` scripts.
- Python dev interpreter: `.venv` at the repo root. Run `python -m unittest discover -s
  clients/vesta-client-py/tests`. TS: `npm test` in `clients/vesta-client-ts`.
- Expected test totals (all green): C# 217 Core + 136 Client + 235 Server (incl. Testcontainers Postgres
  tests) + 9 AccountServer; TS 142; Py 158. Docker **is** available in this dev environment, so run the full server suite
  (about 3–4 min); don't skip the Postgres tests. `build.yml` lists test projects explicitly: add new ones there.
- PowerShell wraps a native command's stderr as `NativeCommandError` and shows `Exit code: 1` even when
  the run passed (Python `unittest` writes its result to stderr): read the `OK` / `FAILED` line, not the
  exit code, and don't pipe through `Select-String` patterns containing unescaped quotes.
- **Crypto interop:** pin cross-language vectors computed by an *independent* implementation (Python's
  OpenSSL-backed `cryptography`, or `@noble/*`) in all three SDKs' tests. That is what caught NSec's
  `Argon2Parameters.MemorySize` being in **KiB** (not bytes), which silently asked for gigabytes. Shared
  vectors live in `HelloProofTests`/`hello-proof.test.mjs`/`test_hello_proof.py` (seed `32 × 0x00`) and
  `IdentityBackupTests`/`backup.test.mjs`/`test_backup.py`.
- TS 5.7+ types `Uint8Array` as `Uint8Array<ArrayBufferLike>`: pass it to WebCrypto with `as BufferSource`.
  Use `argon2idAsync` (not `argon2id`) so a 64 MiB derivation doesn't freeze the event loop/UI.
- `src/VestaCore/Identity/Bip39WordList.cs`, `clients/vesta-client-ts/src/bip39-wordlist.ts` and
  `vesta_client/_bip39_words.py` are **generated** from the BIP-39 English list (SHA-256
  `2f5eed53…dbda`, https://github.com/bitcoin/bips/blob/master/bip-0039/english.txt); regenerate, don't
  hand-edit.
- `VestaConnection` always opens a real `ClientWebSocket`, so TestServer sockets can't drive it. Test the
  SDK handshake against a scripted loopback Kestrel relay (`HelloProofHandshakeTests`); `VestaTestClient`
  is a separate lightweight client and does not do the HELLO proof.
- Server integration tests: `factory.WithWebHostBuilder(b => b.UseSetting("Protocol:…", "…"))` per test
  (options bind from the `Protocol` section); `volatile` publishes are never ACKed, so the first reply
  after a volatile burst is the first error; register apps and set quotas through `IAppStore` from
  `factory.Services`.
- `dotnet ef` (global tool) works from the repo root: `dotnet ef migrations add <Name> --project
  src/VestaServer/VestaServer.csproj`; check both `.cs` + `.Designer.cs` and the snapshot.
- `PATCH /admin/apps/{id}/quotas` is **replace-all** (missing fields become null), and any client that
  does not know a newly added quota field resets it — mirror new `AppQuotas` fields in Atrium first.
- By default the relay does not restrict who may create channels under an app namespace (only the creator becomes
  channel admin); the per-app `owner_only_channel_creation` flag (#26) closes the squatting hole when set.
- SDK gotcha (2026-10-28 pause test), **fixed:** when a relay answers HELLO with an `ERROR` (for example
  `UNKNOWN_APP` because a channel in the HELLO belongs to an unregistered namespace), the attempt's failure reason
  in `RelaysExhaustedError` / `RelaysExhaustedException` was only "Expected WELCOME, got ErrorMessage" (Python), the
  socket close reason (TS) or "Connection closed during handshake" (C#): the code and message were dropped, which
  made a registration script look like a connectivity failure. All three now report `CODE: message`
  (`RefusedHelloTests`, `relay-recovery.test.mjs`, `HelloProofHandshakeTests`). Registering still needs
  `channels=[]` first.
- A registered app namespace with no quotas still has the relay's default 600 events/min per client, and its
  publishes are counted toward the monthly message total, so usage can be pre-loaded before Atrium applies a tier.
- SDK gotcha (found by the 2026-10-28 live smoke test), **fixed:** `REGISTER_APP`, `CREATE_CHANNEL`,
  `GRANT_ACCESS` and `DELETE_CHANNEL` are answered with an ACK whose event id is the nil GUID. C# always raised it
  through `OnAck`, but the TS and Python SDKs swallowed it, so `registerApp` / `createChannel` gave no success
  signal. They now emit it through `ack` / `on_ack` like C# (the internal register-first handshake still consumes
  its own); `control-ack.test.mjs`, `test_control_ack.py`, `ControlAckTests`. Decision: surface the existing ACK
  rather than make `registerApp` / `createChannel` return promises, because a promise a caller ignores turns a
  relay error into an unhandled rejection. Promise helpers (`createChannelAndWait`) remain a possible follow-up.
- Pre-existing build warnings, ignore: NU1902 `Microsoft.Build.Tasks.Git` 8.0.0, NU1903 `SSH.NET`
  2025.1.0 (VestaServer.Tests).
- C# relay picker opens a loopback web page; tests disable it via
  `VestaConnection.RelayPickerEnabled = false` (`TestEnvironment.cs`). `WebRelayPickerTests`
  inject an offline `FederationClient` to avoid a 10 s DNS-timeout flake.
- Python `sign_event()` enforces `event.client_id == identity.client_id`; TS does not — intentional.
- Python connection tests patch `vesta_client.connection.websockets.connect` with a `FakeSocket`
  (`asyncio.Queue`) + `wait_until` helper.
- Server integration tests need Docker (Testcontainers Postgres). Relay needs Postgres
  LISTEN/NOTIFY — never swap the store for Azure SQL; Azure Flexible Server caps at major 16.
- The admin GUI (`wwwroot/admin/`) is no-build plain CSS/JS under a strict CSP (`script-src 'self'`,
  no inline styles/handlers): set widths via JS (`data-pct`), never `style=` attributes. The
  vendored `noble-ed25519-2.1.0.js` is ESM saved as `.js` — to run it in Node copy it to `.mjs`.
  Editing assets with PowerShell 5.1 `Get-Content`/`Set-Content` without `-Encoding UTF8` double-encodes
  non-ASCII (`…`, `—`, `→`); use the edit tools.
- After a release, NuGet/npm indexing lags minutes: `dotnet restore --no-cache`; an
  `examples.yml` run right after may fail `ETARGET` — re-dispatch.
- `docker-compose.yml` is dev-only (open mode, host port 5150). Operator keypair:
  `dotnet run .github/scripts/new-relay-keypair.cs`.
- Archived PLANNING table rows exceed 2000 chars — `grep`/`read_file` truncate them; read with
  `Get-Content` + `Substring` if you need the tail.
