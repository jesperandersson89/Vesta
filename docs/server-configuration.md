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
