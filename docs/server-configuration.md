# Server configuration

> **Relay operators only.** App developers connect to a relay someone else operates and never need this page. See [operating-a-relay.md](operating-a-relay.md).

The Vesta server is a stock ASP.NET Core app — configuration follows the standard precedence: `appsettings.json` → `appsettings.{Environment}.json` → environment variables → command-line args. This page documents Vesta-specific keys; for ASP.NET Core hosting (URLs, Kestrel, logging), refer to Microsoft's docs.

## Storage backends

The server picks its backend based on whether a Postgres connection string is configured:

```jsonc
{
    "ConnectionStrings": {
        "Vesta": "Host=localhost;Port=5432;Database=vesta;Username=vesta;Password=...",
    },
}
```

| Connection string present? | Event store          | Channel/ACL store  | Migrations run? |
| -------------------------- | -------------------- | ------------------ | --------------- |
| Yes                        | `NpgsqlEventStore`   | EF Core + Postgres | Yes, at startup |
| No                         | `InMemoryEventStore` | In-memory          | N/A             |

The in-memory backend is intended for development and tests only — it loses everything on restart.

## Protocol options

```jsonc
{
    "Protocol": {
        "RequireSignedEvents": false,
        "RequireAppRegistration": false,
        "AllowedApps": [],
    },
}
```

### Signature verification

The server enforces three layers of identity checks. The first two are **always on**:

1. If `HELLO` includes a `publicKey`, the server verifies it derives to the announced `clientId`.
2. Every `PUBLISH` requires `event.clientId == connection.clientId` — a logged-in client cannot impersonate another.
3. Once a public key has been registered for a client, every subsequent `PUBLISH` from that client must carry a valid Ed25519 signature.

When `Protocol:RequireSignedEvents = true`, the server additionally:

- Rejects any `HELLO` that does not include a `publicKey`.
- Rejects any `PUBLISH` whose event is unsigned, regardless of whether the client previously registered a key.

Use the strict mode for production. Leave it off for local development with the demo CLIs.

### App registration

A Vesta server can be configured to require every channel namespace to belong to a registered **app**. The app namespace is the first slug segment of a channel ID (e.g. `myapp` in `myapp/chat/general`).

With `Protocol:RequireAppRegistration = true`, the server rejects `PUBLISH`, `SUBSCRIBE`, `FETCH`, `CREATE_CHANNEL`, and resume-on-`HELLO` operations whose channel namespace is not registered, with an `ERROR { code: "UNKNOWN_APP" }` frame.

A client registers an app with `REGISTER_APP { appId }`. The connecting client becomes the app owner. Re-registering an existing app returns `ERROR { code: "DUPLICATE_APP" }`. The owner may also pass `REGISTER_APP { appId, discoverable: true }` to opt the app into [server-to-server discovery](#server-to-server-discovery-federation); this can be toggled later via `PATCH /admin/apps/{id}/discoverable`.

App IDs share the same character set as a channel slug segment (`[a-z0-9][a-z0-9\-]*[a-z0-9]`, max 64 chars, no slashes). The `apps` table also stores nullable per-app quotas (`max_channels`, `max_events_per_channel`, `publish_rate_per_minute`, `retention_days`, `max_payload_bytes`, `total_storage_bytes`, `max_messages_per_month`). All seven are now enforced — see [App quotas & rate limits](#app-quotas--rate-limits).

Leave registration off in development. Turn it on for shared / multi-tenant deployments where you want explicit ownership of namespaces.

#### Allow-list (operator acknowledgement)

`Protocol:RequireAppRegistration` lets *anyone* claim an unclaimed namespace (first `REGISTER_APP` wins). If you self-host and don't want **any** app touching your relay without your say-so, pin an allow-list:

```jsonc
{
    "Protocol": {
        "AllowedApps": ["myapp"]
    }
}
```

With a non-empty `AllowedApps`, only the listed app namespaces may be used or registered — every other `PUBLISH` / `SUBSCRIBE` / `FETCH` / `CREATE_CHANNEL` / `REGISTER_APP` is rejected with `ERROR { code: "APP_NOT_ALLOWED" }`. This gate is independent of `RequireAppRegistration`: listing your app is enough to make it work, and nothing else connects. Protocol-reserved (`vesta/*`) channels are never gated. Empty (the default) means no allow-list — the relay stays open so it's trivial to try out. This is intentionally a single config knob; deployments that need richer, dynamic admission can extend the server.

### App quotas & rate limits

All seven per-app limits are enforced today (set via `IAppStore.SetQuotasAsync` or a direct `UPDATE apps SET ... WHERE id = '<app>'`). Synchronous limits run on the request hot path; quota-driven pruning runs in a background sweep.

| Column                    | Enforced where                        | Error frame on breach                      |
| ------------------------- | -------------------------------------- | ------------------------------------------- |
| `max_payload_bytes`       | `PUBLISH` (payload + metadata)         | `ERROR { code: "QUOTA_EXCEEDED" }`          |
| `publish_rate_per_minute` | `PUBLISH` (per `(app, client)`)        | `ERROR { code: "RATE_LIMITED" }`            |
| `max_channels`            | `CREATE_CHANNEL`                       | `ERROR { code: "QUOTA_EXCEEDED" }`          |
| `total_storage_bytes`     | `PUBLISH` (cached rollup + add)        | `ERROR { code: "QUOTA_EXCEEDED" }`          |
| `max_messages_per_month`  | `PUBLISH` (cached rollup + increment)  | `ERROR { code: "MESSAGE_QUOTA_EXCEEDED" }`  |
| `retention_days`          | Background sweep (`AppQuotaPruner`)    | n/a — silent deletion                       |
| `max_events_per_channel`  | Background sweep (`AppQuotaPruner`)    | n/a — silent deletion                       |

`publish_rate_per_minute` uses an in-memory token bucket per `(appId, clientId)` — good enough for a single-host relay (multi-host needs a shared backend, tracked under TODO #15). The bucket refills continuously at `rate / 60` tokens per second and caps at the configured rate.

`total_storage_bytes` is checked against an in-process cached rollup maintained by `IAppStorageAccountant` (default `InMemoryAppStorageAccountant`). The pruner sweep seeds and refreshes the cache via `SUM(pg_column_size(payload))` per app namespace; successful PUBLISHes increment it in-line. On a cold cache (server restart before the first sweep), PUBLISH is allowed.

`max_messages_per_month` is checked against a durable per-app, per-calendar-month rollup: the `app_usage` table backs an in-process cache (`IAppUsageAccountant`, default `InMemoryAppUsageAccountant`). The cache is seeded synchronously at startup from `app_usage` (so it's never cold on a fresh boot — a brand-new calendar period with no row yet is correctly zero, not unknown), refreshed by the `AppQuotaPruner` sweep (`COUNT(*)` over events received since the period start, upserted back into `app_usage`), and incremented in-line on every successful PUBLISH. The period rolls over automatically at the first UTC calendar-month boundary the server observes. Query the current rollup via `GET /admin/apps/{id}/usage`.

A `null` quota means no limit. Quotas only attach to **registered** apps — unregistered namespaces are subject only to the global checks (signature verification, channel ACL).

## Server admins

A **server admin** is a connection whose Ed25519 public key is listed in the `Admin:BootstrapPublicKeys` allow-list. Admin status is established at `HELLO` time and lasts for the lifetime of the connection.

```json
{
    "Admin": {
        "BootstrapPublicKeys": ["base64url-encoded-32-byte-ed25519-public-key"]
    }
}
```

Entries are base64url-encoded 32-byte Ed25519 public keys (the same encoding used for `HelloMessage.PublicKey` and for event signing). Malformed or wrong-length entries are silently skipped at startup. An empty list (the default) means no connection is ever an admin.

Today the WebSocket protocol exposes one admin capability: **`DELETE_CHANNEL`**. The server soft-deletes the target channel (sets `channels.deleted_at`) and then rejects any further `PUBLISH` / `SUBSCRIBE` / `FETCH` / `CREATE_CHANNEL` for that channel with `ERROR { code: "CHANNEL_DELETED" }`. Existing events are retained until the hard-delete sweep runs (see [`ChannelDeletionPruner`](#channeldeletionpruner-postgres-only)). Non-admin connections receive `ERROR { code: "NOT_ADMIN" }`; deleting a non-existent channel yields `ERROR { code: "CHANNEL_NOT_FOUND" }`. The operation is idempotent — repeated deletes succeed without changing the original `deleted_at` timestamp.

There is no password / JWT layer. The trust root is the same Ed25519 keypair used everywhere else in Vesta. Admins authenticate to the HTTP API by signing a challenge with their private key (see [Admin HTTP API](#admin-http-api)).

## Admin HTTP API

The server exposes a small HTTP surface under `/admin/*` for operator tooling and the bundled web GUI (served from `/admin/`). Every key in the bootstrap allow-list can authenticate and has full access — there are no scopes yet. Every mutating call is recorded in the [audit log](#audit-log).

### Auth flow

1. `POST /admin/auth/challenge` → `{ "nonce": "<base64url>", "expiresAt": "..." }` — server issues a random 32-byte nonce, cached for `AdminApi:ChallengeTtl` (default 60 s).
2. Client signs the decoded nonce bytes with their Ed25519 private key.
3. `POST /admin/auth/verify { "publicKey": "...", "nonce": "...", "signature": "..." }` → `{ "token": "...", "expiresAt": "..." }` — server verifies the signature, checks the key against `Admin:BootstrapPublicKeys`, and issues a 32-byte bearer token cached for `AdminApi:TokenTtl` (default 1 h). Returns `401` on signature failure or unknown key.
4. Subsequent requests carry `Authorization: Bearer <token>`. Missing or expired tokens return `401`.

Tokens are kept in-process; a server restart invalidates everything. Multi-host deployments need a shared backend (tracked under TODO #15).

`/admin/auth/*` is rate limited per client IP (fixed one-minute window, `429` when exceeded). Admin tokens are bearer tokens: terminate TLS in front of the relay and never expose `/admin/*` over plain HTTP. Behind a TLS-terminating proxy every request shares the proxy's IP unless you opt in to `X-Forwarded-For` handling with `ForwardedHeaders:Enabled=true` — only do that when the relay is reachable exclusively through your trusted proxy, because it trusts the header from any sender.

```jsonc
{
    "AdminApi": {
        "ChallengeTtl": "00:01:00",
        "TokenTtl": "01:00:00",
        "AuthRateLimitPerMinute": 20, // per IP; 0 disables
    },
    "ForwardedHeaders": { "Enabled": false },
}
```

### Endpoints

| Method   | Path                      | Description                                                                                     |
| -------- | ------------------------- | ----------------------------------------------------------------------------------------------- |
| `POST`   | `/admin/auth/challenge`   | Issue a nonce. No auth.                                                                         |
| `POST`   | `/admin/auth/verify`      | Exchange a signed nonce for a bearer token. No auth.                                            |
| `GET`    | `/admin/channels`         | List channels. Query: `?app=<prefix>` filter, `?includeDeleted=true\|false` (default true).     |
| `GET`    | `/admin/channels/{id}`    | Channel detail: visibility, timestamps, event count / payload bytes / latest sequence, members. |
| `DELETE` | `/admin/channels/{id}`    | Soft-delete a channel (same effect as the protocol `DELETE_CHANNEL` message).                   |
| `GET`    | `/admin/apps`             | List active apps with quotas, current storage usage, `deletedAt` and `activeAlerts`. `?includeDeleted=true` also lists soft-deleted apps. |
| `GET`    | `/admin/apps/{id}`        | App detail including channel count, storage rollup, `deletedAt` and `activeConnections` (open connections subscribed to the app; best-effort). |
| `GET`    | `/admin/apps/{id}/usage`  | Current-period usage rollup: `{ id, periodStart, messages, storageBytes, channelCount, quotas }`. |
| `GET`    | `/admin/apps/{id}/usage/history` | `?months=12` (1–36) → `[{ periodStart, messages }]`, oldest first, from `app_usage`. In-memory mode only has the current period. |
| `GET`    | `/admin/apps/{id}/channels` | Every channel of the app (including deleted) with `eventCount`, `payloadBytes`, `latestSequence`, busiest first. |
| `GET`    | `/admin/apps/{id}/alerts` | The app's quota alerts (`?active=true` hides resolved). |
| `GET`    | `/admin/apps/{id}/export` | Streams the app's data as [JSON Lines](#json-lines-export-format). Works for soft-deleted apps. |
| `DELETE` | `/admin/apps/{id}`        | Soft-delete the app and its channels and disconnect its subscribers (`204`; `404` if unknown). Idempotent. See [App deletion](#app-deletion). |
| `POST`   | `/admin/apps/{id}/restore` | Undo a soft-delete (`200`; `409` if the app is not deleted). |
| `POST`   | `/admin/apps/{id}/pause`  | Refuse all publishes / channel creation with `APP_PAUSED` (`200 { id, pausedAt }`). See [Pause and throttle](#pause-and-throttle). |
| `POST`   | `/admin/apps/{id}/resume` | Lift a pause (`200 { id, pausedAt: null }`). |
| `PATCH`  | `/admin/apps/{id}/throttle` | `{ "perMinute": int \| null }` — cap total publishes per minute for the app; `null` removes it (`400` for ≤ 0). |
| `PATCH`  | `/admin/apps/{id}/quotas` | Replace the app's `AppQuotas` body. `409` for a deleted app (same for `owner` and `discoverable`). |
| `PATCH`  | `/admin/apps/{id}/discoverable` | `{ "discoverable": bool }` — federation opt-in. |
| `PATCH`  | `/admin/apps/{id}/owner`  | Operator-assisted rebind of the app's recognized owner to a new client id (key rotation / recovery from a lost identity). |
| `GET`    | `/admin/alerts`           | Quota alerts across apps. `?active=true` (default) hides resolved ones, `?app=<id>` filters. |
| `POST`   | `/admin/alerts/{id}/ack`  | Mark an alert acknowledged (`204`); it stays open until usage drops. |
| `GET`    | `/admin/audit`            | Newest-first audit entries. `?limit=` (1–500, default 100), `?target=<exact target>`. |
| `GET`    | `/admin/config`           | Effective grace periods, pruner flags, token TTL and alert thresholds (used by the GUI). |
| `GET`    | `/admin/metrics`          | `{ activeConnections, totalApps, totalChannels, deletedApps, pausedApps, activeAlerts }`.       |

### App deletion

`DELETE /admin/apps/{id}` stamps `apps.deleted_at` and soft-deletes every channel under the app with the **same timestamp**. From then on the app is gone for clients: `ExistsAsync` is false (so registration-gated relays answer `UNKNOWN_APP`) and its channels answer `CHANNEL_DELETED`. Open WebSocket connections subscribed to the app are **closed**: each first receives `ERROR { code: "UNKNOWN_APP" }` and then a close frame (status 1008, "App deleted"); a peer that does not finish the close handshake within 5 s is aborted. The SDK binds one app per connection, so the whole socket is closed; a connection is considered part of the app when it has at least one subscription to one of its channels (a client that only publishes is simply refused on its next publish). The number of closed connections is recorded in the `app.delete` audit entry. Open quota alerts are resolved. The id stays reserved — `REGISTER_APP` answers `DUPLICATE_APP` — until the app is purged.

`POST /admin/apps/{id}/restore` clears the tombstone and restores only the channels carrying the app's timestamp, so channels an operator deleted individually *before* the app stay deleted.

The [`AppDeletionPruner`](#appdeletionpruner-postgres-only) purges the data permanently after a grace period.

### Pause and throttle

Two operator-level traffic controls, independent of the tier-style [quotas](#app-quotas--rate-limits) so a platform that rewrites quotas does not clobber them:

- **Pause** (`POST /admin/apps/{id}/pause`, `.../resume`) — every `PUBLISH` and `CREATE_CHANNEL` is refused with `ERROR { code: "APP_PAUSED" }` (stamped with `eventId` / `channelId`). Reads, `SUBSCRIBE`, `FETCH` and connections are untouched, so clients stay online and read-only. SDKs treat `APP_PAUSED` as a transient limit: it surfaces through `OnLimited` / `limited` / `on_limited` and unsent events stay in the outbox and are delivered after resume. Pausing is idempotent and keeps the original `pausedAt`.
- **Throttle** (`PATCH /admin/apps/{id}/throttle` with `{ "perMinute": 600 }`, `null` removes it) — caps the app's **total** publishes per minute across all clients (token bucket, one shared bucket per app; the per-client `publish_rate_per_minute` quota still applies on top). Excess publishes get `ERROR { code: "RATE_LIMITED" }`. In-process like the other rate limits, so it is per relay instance.

Both persist in the `apps` table (`paused_at`, `throttle_per_minute`), are returned as `pausedAt` / `throttlePerMinute` by `GET /admin/apps[/{id}]`, are audited (`app.pause`, `app.resume`, `app.throttle`), and return `409` for a soft-deleted app. `GET /admin/metrics` reports `pausedApps`.

### JSON Lines export format

`GET /admin/apps/{id}/export` responds with `application/x-ndjson` and `Content-Disposition: attachment; filename="{appId}-{yyyyMMddTHHmmss}Z.jsonl"`. One JSON object per line:

1. A single **manifest** line: `{ "type": "manifest", "version": 1, "exportedAt": "...", "app": { id, ownerClientId, createdAt, quotas, discoverable, deletedAt }, "channels": [{ id, visibility, createdAt, deletedAt, members: [{ clientId, role }] }] }`.
2. Then one **event** line per stored event, channel by channel (manifest order) in ascending sequence: `{ "type": "event", "sequence": 1, "receivedAt": "...", "event": { ...VestaEvent... } }`. The `event` object is the signed client event exactly as stored, so signatures remain verifiable.

Events past their TTL are excluded (same visibility as a client catch-up). The response is streamed page by page; the GUI buffers it in the browser, so use `curl` with a bearer token for very large apps. Each export is audited as `app.export`.

### Audit log

Every mutating admin action (`auth.login`, `channel.delete`, `app.quotas`, `app.discoverable`, `app.owner`, `app.delete`, `app.restore`, `app.pause`, `app.resume`, `app.throttle`, `app.export`, `alert.ack`) and the purge job (`app.purge`, actor `system`) is appended to the `admin_audit` table: time, acting admin's public key (hex), action, target and a small JSON `details` object. In-memory mode keeps the last 1000 entries. Entries are never edited or deleted by the relay.

### Web GUI

The static SPA at `/admin/` (served from `wwwroot/admin/`, no build step, all assets vendored — it makes no third-party requests and ships a strict Content-Security-Policy) provides:

- **Overview** — connections, apps, live channels, active alerts.
- **Apps** — list (with a *show deleted* toggle) and a per-app **dashboard**: active connections, messages / storage / channel utilisation against quotas, a 12-month message chart, top channels, editable limits (blank = no limit), a **Traffic control** card (pause / resume publishing, throttle), owner rebind, discovery toggle, **Export data**, and a *danger zone* with type-to-confirm delete and one-click restore.
- **Channels** — browser with detail, members and soft-delete.
- **Alerts** — open quota alerts with acknowledge.
- **Audit** — the 200 most recent admin actions.

The login screen takes a base64url-encoded 32-byte Ed25519 seed and performs the challenge / sign / verify dance entirely in the browser — the seed never leaves the page. Token + expiry are cached in `sessionStorage`. Signing uses the vendored `@noble/ed25519` 2.1.0 (MIT) and WebCrypto, so the page needs a secure context (HTTPS or `localhost`). Fonts (Inter, JetBrains Mono) are bundled under SIL OFL 1.1; licenses sit next to the files in `wwwroot/admin/fonts/` and `wwwroot/admin/vendor/`.

## `AppQuotaPruner` (Postgres only)

The `AppQuotaPrunerService` periodically enforces `retention_days` and `max_events_per_channel` per app, and refreshes the cached storage rollup used by `total_storage_bytes`. It only runs when the Postgres backend is active and only when explicitly enabled:

```jsonc
{
    "AppQuotaPruner": {
        "Enabled": false, // opt-in
        "Interval": "00:05:00", // TimeSpan; default 5 min
    },
}
```

If `Enabled` is `false` (the default), the service logs a single info message at startup and exits. Quotas registered against apps will still record (for documentation) but no events are deleted until the pruner is enabled.

Each sweep iterates every row in `apps` and, per quota:

- `retention_days` → `DELETE FROM events WHERE channel_id IN (<app namespace>) AND received_at < now() - make_interval(days => $)`.
- `max_events_per_channel` → keeps the most recent N events per channel under the app via `ROW_NUMBER() OVER (PARTITION BY channel_id ORDER BY sequence DESC)`.
- `total_storage_bytes` → `SUM(pg_column_size(payload))` written to the in-process accountant.
- `max_messages_per_month` → `COUNT(*)` over events received since the current calendar-month period start, written to the in-process accountant **and** upserted into `app_usage` for durability across restarts. This runs for **every** app (not only those with the quota) so the admin usage history is complete.

After measuring, each sweep also evaluates [quota alerts](#appalerts) for the app.

## `AppAlerts`

When the `AppQuotaPruner` is enabled, each sweep compares an app's measured usage with its quotas and records an alert in `app_alerts` whenever a threshold is crossed. Metrics: `messages` (vs `max_messages_per_month`), `storage` (vs `total_storage_bytes`), `channels` (vs `max_channels`). Apps without the corresponding quota raise nothing.

```jsonc
{
    "AppAlerts": {
        "ThresholdsPercent": [80, 100], // default
    },
}
```

An alert is open until usage drops below the threshold (for example after a quota increase or a month rollover — message alerts belong to one usage period) and is then resolved automatically; at most one open alert exists per (app, metric, threshold). Acknowledging (`POST /admin/alerts/{id}/ack`) only marks it as seen. The relay does not send notifications: operators and platforms such as Atrium poll `GET /admin/alerts` and notify however they like.

## `AppDeletionPruner` (Postgres only)

The `AppDeletionPrunerService` permanently purges apps soft-deleted via [`DELETE /admin/apps/{id}`](#app-deletion) once the grace period has passed. For each eligible app, in one transaction: events, `client_positions`, `channel_access`, `channel_sequences`, `channels`, `app_usage`, `app_alerts`, then the `apps` row; the in-process accountants are cleared and an `app.purge` audit entry is written. The app id can then be registered again.

```jsonc
{
    "AppDeletionPruner": {
        "Enabled": false, // opt-in
        "Interval": "00:05:00", // TimeSpan; default 5 min
        "GracePeriod": "7.00:00:00", // TimeSpan; default 7 days
    },
}
```

With `Enabled=false` (the default) deleted apps are blocked for clients but their data stays on disk. The [`ChannelDeletionPruner`](#channeldeletionpruner-postgres-only) skips channels belonging to a soft-deleted app, so the app's own grace period governs and a restore within it is lossless. **Purging is irreversible** — export first.

## `ChannelDeletionPruner` (Postgres only)

The `ChannelDeletionPrunerService` periodically hard-deletes channels that were soft-deleted via [`DELETE_CHANNEL`](#server-admins). Each sweep selects every channel whose `deleted_at` tombstone has aged past the grace period, deletes its events, and drops the `channels` row so the id becomes available again (a fresh `PUBLISH` would recreate it implicitly).

```jsonc
{
    "ChannelDeletionPruner": {
        "Enabled": false, // opt-in
        "Interval": "00:05:00", // TimeSpan; default 5 min
        "GracePeriod": "1.00:00:00", // TimeSpan; default 24 h
    },
}
```

If `Enabled` is `false` (the default), the service logs a single info message at startup and exits. Soft-deleted channels still reject new writes with `CHANNEL_DELETED`, but their events stay on disk until you turn the pruner on. Set `GracePeriod` to `00:00:00` for immediate hard-delete on the very next sweep.

Each pass runs two statements per eligible channel: `DELETE FROM events WHERE channel_id = $1`, then `DELETE FROM channels WHERE id = $1 AND deleted_at < now() - make_interval(secs => $2)` (re-checking the tombstone in the predicate as cheap insurance against a future un-delete path).

## `EventCleanup` (Postgres only)

The `ExpiredEventCleanupService` periodically deletes events whose `expires_at` is in the past. It only runs when the Postgres backend is active and only when explicitly enabled:

```jsonc
{
    "EventCleanup": {
        "Enabled": false, // opt-in
        "Interval": "00:01:00", // TimeSpan; default 60 s
        "BatchSize": 10000, // max rows per sweep
    },
}
```

If `Enabled` is `false` (the default), the service logs a single info message at startup and exits. Expired events are still **excluded from catch-up reads** even when the cleanup service is disabled — they just accumulate on disk until you turn it on.

A single sweep runs the equivalent of:

```sql
DELETE FROM events
WHERE id IN (
    SELECT id FROM events
    WHERE expires_at IS NOT NULL AND expires_at <= now()
    LIMIT $BatchSize
);
```

Enable the sweep in production deployments where you actually use TTL events (e.g. presence channels). Tune `Interval` and `BatchSize` based on your event volume.

## Server-to-server discovery (federation)

Relays can optionally gossip signed self-descriptions to each other so a client whose relays are
all failing can discover *other* relays hosting the same app — the recovery path when the owner
never published a fresh [relay manifest](protocol.md#relay-manifests-server-independence). There is
**no central hub**: relays pull each other's descriptors by anti-entropy and any one reachable
relay can answer "who else hosts this app?". See
[protocol.md](protocol.md#server-to-server-discovery-federation) for the wire shapes and trust model.

Federation is **off by default** and enabled per relay:

```jsonc
{
    "Discovery": {
        "Enabled": false,                    // master switch; maps /federation/* + runs the gossip loop
        "Seeds": [                           // HTTP(S) base URLs of relays to bootstrap gossip from
            "https://relay-a.example",
        ],
        "PublicUrls": [                      // this relay's own WebSocket URLs to advertise
            "wss://relay-b.example/ws",
        ],
        "SigningKey": null,                  // base64url Ed25519 seed for the relay identity (see below)
        "GossipIntervalSeconds": 60,         // how often to pull from seeds + known peers (min 5)
        "DescriptorTtlSeconds": 300,         // how long this relay's descriptor stays valid
        "MaxPeers": 256,                     // cap on remembered peer descriptors (oldest evicted)
    },
}
```

**Dual opt-in.** A relay advertises an app only when *both* the operator enabled `Discovery` **and**
the app owner set the per-app `discoverable` flag (at `REGISTER_APP` or via `PATCH
/admin/apps/{id}/discoverable`). `discoverable` is relay-side metadata — it is never parsed from an
event payload, so the relay keeps interpreting nothing about app data.

**Relay identity.** Descriptors are self-signed with an Ed25519 relay key. If `SigningKey` is set
(base64url seed) it is used directly; otherwise the relay loads or generates a key at
`{ContentRoot}/.vesta/relay-key.json` (a warning is logged when it generates one, since a
throwaway key changes the relay's advertised identity on each fresh deployment). The relay key is a
federation identity only — it is unrelated to `Admin:BootstrapPublicKeys` and grants no admin
rights.

**Open-mode hint.** The descriptor's `acceptsUnregisteredApps` field mirrors this relay's own
`!RequireAppRegistration && AllowedApps.Count == 0` check — operators don't set it directly. It
lets a client's [relay-recovery picker](relay-recovery.md#adopting-under-a-different-namespace)
show whether registering a namespace is likely needed before the user adopts a discovered relay.

**HTTP surface** (mapped only when `Discovery:Enabled`, all unauthenticated reads):

| Endpoint                       | Returns                                                                |
| ------------------------------ | ---------------------------------------------------------------------- |
| `GET /federation/descriptor`   | This relay's own freshly-signed descriptor.                            |
| `GET /federation/peers`        | Every descriptor this relay knows (own + gossiped peers), deduped.     |
| `GET /federation/apps/{appId}` | Descriptors (own + peers) that advertise the given app.                |

**Trust.** A descriptor proves only that the relay authored it and *claims* to host an app; a relay
can lie. Clients therefore treat discovered relays as **show-only** — they verify the signature and
cross-check the advertised owner against the app's trust anchor, then present survivors for the user
to adopt manually. Owner-signed manifest relays remain the only **automatic** failover tier.

## EF Core migrations

Migrations run automatically at server startup when Postgres is configured (`Database.MigrateAsync()`). If you are adding a new migration:

```bash
dotnet ef migrations add <Name> --project src/VestaServer/VestaServer.csproj
```

**Never hand-write migration files.** A migration is two files (`<timestamp>_<Name>.cs` + `<timestamp>_<Name>.Designer.cs`) that must agree. The `.Designer.cs` carries the `[Migration]` attribute — without it, EF silently skips the migration and the schema is wrong. See [.github/copilot-instructions.md](../.github/copilot-instructions.md) for the full rule.

## Endpoints

| Path      | Protocol  | Purpose                                                                 |
| --------- | --------- | ----------------------------------------------------------------------- |
| `/ws`     | WebSocket | Primary protocol endpoint (`HELLO` → `PUBLISH` / `SUBSCRIBE` / `FETCH`) |
| `/health` | HTTP GET  | Liveness check (returns `200 OK`)                                       |

## Example `appsettings.Production.json`

```jsonc
{
    "ConnectionStrings": {
        "Vesta": "Host=postgres;Database=vesta;Username=vesta;Password=${VESTA_DB_PASSWORD}",
    },
    "Protocol": {
        "RequireSignedEvents": true,
        "RequireAppRegistration": true,
    },
    "EventCleanup": {
        "Enabled": true,
        "Interval": "00:05:00",
        "BatchSize": 50000,
    },
    "Logging": {
        "LogLevel": {
            "Default": "Warning",
            "VestaServer": "Information",
        },
    },
}
```

## See also

- [events.md](events.md) — TTL events and the `metadata.ttlSeconds` contract
- [PLANNING.md §Server-side Architecture](../PLANNING.md) — design rationale
