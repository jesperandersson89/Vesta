# Server configuration

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

A client registers an app with `REGISTER_APP { appId }`. The connecting client becomes the app owner. Re-registering an existing app returns `ERROR { code: "DUPLICATE_APP" }`.

App IDs share the same character set as a channel slug segment (`[a-z0-9][a-z0-9\-]*[a-z0-9]`, max 64 chars, no slashes). The `apps` table also stores nullable per-app quotas (`max_channels`, `max_events_per_channel`, `publish_rate_per_minute`, `retention_days`, `max_payload_bytes`, `total_storage_bytes`). All six are now enforced — see [App quotas & rate limits](#app-quotas--rate-limits).

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

All six per-app limits are enforced today (set via `IAppStore.SetQuotasAsync` or a direct `UPDATE apps SET ... WHERE id = '<app>'`). Synchronous limits run on the request hot path; quota-driven pruning runs in a background sweep.

| Column                    | Enforced where                      | Error frame on breach              |
| ------------------------- | ----------------------------------- | ---------------------------------- |
| `max_payload_bytes`       | `PUBLISH` (payload + metadata)      | `ERROR { code: "QUOTA_EXCEEDED" }` |
| `publish_rate_per_minute` | `PUBLISH` (per `(app, client)`)     | `ERROR { code: "RATE_LIMITED" }`   |
| `max_channels`            | `CREATE_CHANNEL`                    | `ERROR { code: "QUOTA_EXCEEDED" }` |
| `total_storage_bytes`     | `PUBLISH` (cached rollup + add)     | `ERROR { code: "QUOTA_EXCEEDED" }` |
| `retention_days`          | Background sweep (`AppQuotaPruner`) | n/a — silent deletion              |
| `max_events_per_channel`  | Background sweep (`AppQuotaPruner`) | n/a — silent deletion              |

`publish_rate_per_minute` uses an in-memory token bucket per `(appId, clientId)` — good enough for a single-host relay (multi-host needs a shared backend, tracked under TODO #15). The bucket refills continuously at `rate / 60` tokens per second and caps at the configured rate.

`total_storage_bytes` is checked against an in-process cached rollup maintained by `IAppStorageAccountant` (default `InMemoryAppStorageAccountant`). The pruner sweep seeds and refreshes the cache via `SUM(pg_column_size(payload))` per app namespace; successful PUBLISHes increment it in-line. On a cold cache (server restart before the first sweep), PUBLISH is allowed.

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

The server exposes a small HTTP surface under `/admin/*` for operator tooling and the bundled web GUI (served from `/`). Every key in the bootstrap allow-list can authenticate.

### Auth flow

1. `POST /admin/auth/challenge` → `{ "nonce": "<base64url>", "expiresAt": "..." }` — server issues a random 32-byte nonce, cached for `AdminApi:ChallengeTtl` (default 60 s).
2. Client signs the decoded nonce bytes with their Ed25519 private key.
3. `POST /admin/auth/verify { "publicKey": "...", "nonce": "...", "signature": "..." }` → `{ "token": "...", "expiresAt": "..." }` — server verifies the signature, checks the key against `Admin:BootstrapPublicKeys`, and issues a 32-byte bearer token cached for `AdminApi:TokenTtl` (default 1 h). Returns `401` on signature failure or unknown key.
4. Subsequent requests carry `Authorization: Bearer <token>`. Missing or expired tokens return `401`.

Tokens are kept in-process; a server restart invalidates everything. Multi-host deployments need a shared backend (tracked under TODO #15).

```jsonc
{
    "AdminApi": {
        "ChallengeTtl": "00:01:00",
        "TokenTtl": "01:00:00",
    },
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
| `GET`    | `/admin/apps`             | List registered apps with quotas and current storage usage.                                     |
| `GET`    | `/admin/apps/{id}`        | App detail including channel count and storage rollup.                                          |
| `PATCH`  | `/admin/apps/{id}/quotas` | Replace the app's `AppQuotas` body.                                                             |
| `GET`    | `/admin/metrics`          | `{ activeConnections, totalApps, totalChannels }`.                                              |

### Web GUI

The static SPA at `/` (served from `wwwroot/admin/index.html`) provides a minimal RabbitMQ-style dashboard: overview metrics, channel browser with soft-delete, app list with editable quotas. The login screen takes a base64url-encoded 32-byte Ed25519 seed and performs the challenge / sign / verify dance entirely in the browser — the seed never leaves the page. Token + expiry are cached in `sessionStorage`.

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
