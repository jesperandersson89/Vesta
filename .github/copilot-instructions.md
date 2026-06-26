# Vesta — Copilot Instructions

## Project Overview

Vesta is a protocol and runtime for building networked applications without surrendering ownership to a central service. The server is a relay (like a git remote or torrent tracker), not the authority. Clients own their data.

Read `PLANNING.md` at the repo root for full architecture decisions.

## Tech Stack

- **Language**: C# (.NET 10), with TypeScript/JS and Python clients
- **Server**: ASP.NET Core minimal API + WebSocket
- **Server DB**: PostgreSQL 18 (via raw Npgsql for events, EF Core for metadata)
- **Client DB**: SQLite (via Microsoft.Data.Sqlite)
- **Serialization**: System.Text.Json
- **Identity**: Ed25519 keypairs (self-sovereign)
- **Transport**: WebSocket (primary), HTTP fallback

## Solution Structure

```
Vesta/
├── .github/infra/              # Bicep IaC for the relay (App Service + Postgres + Key Vault)
├── src/                        # Core infrastructure (the "product")
│   ├── VestaCore/              # Shared types, protocol, serialization
│   ├── VestaServer/            # ASP.NET Core host (relay + persistence)
│   └── VestaClient/            # C# client library
├── clients/                    # Client libraries in other languages
│   ├── vesta-client-ts/        # TypeScript client (npm)
│   └── vesta-client-py/        # Python client (pip)
├── examples/                   # Demo apps consuming the client libraries
│   ├── ChatRoom.CLI/           # C# chat demo
│   ├── TodoList.CLI/           # C# todo demo
│   ├── Presence.CLI/           # C# presence/heartbeat demo (TTL + LwwMap showcase)
│   ├── chess-web/              # TS browser multiplayer chess demo
│   ├── clipboard-ts/           # TS shared clipboard demo
│   ├── colorwheel-py/          # Python colorwheel demo
│   └── collab-edit-py/         # Python collaborative editor demo
└── tests/
    ├── VestaCore.Tests/
    ├── VestaServer.Tests/
    └── VestaClient.Tests/
```

**Key distinction**: `src/` + `clients/` = the SDK. `examples/` = proof it works.

## Code Conventions

- **Target**: net10.0
- **Nullable**: enable (all projects)
- **Implicit usings**: enable
- **Naming**: PascalCase for public members, \_camelCase for private fields, camelCase for local variables
- **No `var`**: Always use explicit types — never use `var` for variable declarations
- **Async**: All I/O methods should be async, suffixed with `Async`
- **Records**: Prefer records for immutable data types (events, protocol messages)
- **Pattern matching**: Prefer switch expressions over if/else chains
- **File-scoped namespaces**: Always use file-scoped (`namespace X;`)
- **Primary constructors**: Use where appropriate for DI
- **No regions**: Never use `#region`

## Architecture Rules

1. **Events are immutable** — never modify an event after creation
2. **Server doesn't interpret payloads** — it stores and forwards; business logic is client-side
3. **All protocol messages go through VestaCore types** — server and client share the same message definitions
4. **IEventStore interface** is the storage abstraction — implementations differ (Npgsql server-side, SQLite client-side)
5. **EF Core** handles metadata tables (channels, cursors, access control) — NOT the events table
6. **Raw Npgsql** handles the event hot path (append, range reads, LISTEN/NOTIFY)
7. **Conflict resolution** is app-defined — VestaCore provides LWW and append-only helpers, apps choose

## Key Types (VestaCore)

- `VestaEvent` — client-authored immutable event record (id, channelId, timestamp, clientId, type, payload, parentId, signature)
- `SequencedEvent` — wraps `VestaEvent` + server-assigned metadata (sequence, receivedAt). Composition, not inheritance.
- `Channel` — channel metadata
- `ProtocolMessage` — base type for all wire messages (HELLO, PUBLISH, EVENT, ACK, etc.)
- `IEventStore` — storage abstraction (accepts `VestaEvent`, returns `SequencedEvent`)
- `VestaIdentity` — Ed25519 keypair wrapper

**Important**: `VestaEvent` is what clients create/sign. `SequencedEvent` is what the server stores/broadcasts. Never mix them up — the type system enforces this.

## Protocol Key Decisions

- **Sequence** = per-channel monotonic BIGINT assigned by the server. This IS the ordering/position mechanism. No separate "cursor" concept.
- **Channel IDs** are human-readable string slugs (e.g., `"myapp/todo-list"`), not UUIDs. Validated: `[a-z0-9][a-z0-9\-/]*[a-z0-9]`, max 128 chars.
- **Event signing** uses RFC 8785 (JSON Canonicalization Scheme) for the signing input, Ed25519 for the signature. Sign all client-authored fields except `signature` itself.
- **Ephemeral events** carry `metadata.ttlSeconds` — persisted normally but excluded from catch-up queries when expired.
- **Channel creation** is implicit in open mode (first PUBLISH/SUBSCRIBE creates it).

## Testing

- Use xUnit for all test projects
- Prefer integration tests that test the full event round-trip (publish → store → receive)
- Use `Testcontainers` for PostgreSQL in server integration tests
- Name tests: `MethodName_Scenario_ExpectedBehavior`

## When Making Changes

- Always check `PLANNING.md` for architectural decisions before proposing alternatives
- Keep the protocol generic — don't add app-specific logic to VestaCore or VestaServer
- When adding a new protocol message, update both the C# types AND the message type documentation
- Run `dotnet build` after changes to verify compilation
- Event payloads are always `JsonElement` or `JsonDocument` — never strongly-typed app models in core

## Keeping Clients and Examples in Sync

When you change anything in `src/VestaCore/` (protocol messages, `VestaEvent` shape, signing rules, metadata semantics, channel ACL, etc.) or in `src/VestaClient/`, you MUST sweep the other SDKs and the examples before declaring the task done:

- **TypeScript client** — `clients/vesta-client-ts/src/` (`types.ts`, `events.ts`, `connection.ts`, `identity.ts`, `index.ts`). Mirror new fields, message types, signing rules.
- **Python client** — `clients/vesta-client-py/vesta_client/` (`types.py`, `events.py`, `connection.py`, `identity.py`). Same mirror.
- **C# examples** — every `examples/*.CLI/` project that uses the changed surface. Build each affected project.
- **TS/JS examples** — `examples/chess-web/`, `examples/clipboard-ts/` if affected.
- **Python examples** — `examples/colorwheel-py/`, `examples/collab-edit-py/` if affected.

Rules of thumb:

- If you added a wire-level field (e.g. `metadata` on `VestaEvent`), all three client libraries must serialize / deserialize it round-trip and exclude it from signing input where applicable.
- If you added an SDK primitive (e.g. `VestaCore.Projections.*`), pick at least one example to refactor onto it as a smoke test — don't leave the primitive unused.
- If an example would need a large rewrite, note that explicitly in the response instead of silently leaving it stale.
- After edits, run `dotnet build Vesta.sln` and, for touched non-C# clients, the relevant `npm run build` / `python -m compileall` (or import-check) to confirm nothing rotted.

## Documentation

User-facing documentation lives in `docs/` (entry point: [docs/README.md](../docs/README.md)). Architectural rationale lives in [PLANNING.md](../PLANNING.md). User-facing setup lives in [README.md](../README.md). Keep all three in sync when behaviour changes.

When you change something that has user-visible impact, update the relevant doc in the same change:

| You changed...                                                                   | Update                                |
| -------------------------------------------------------------------------------- | ------------------------------------- |
| `VestaEvent` shape, signing rules, `replace` / `volatile` / `metadata` semantics | `docs/events.md`                      |
| `VestaCore.Projections.*` (new primitive, new method, semantics)                 | `docs/projections.md`                 |
| Protocol message types, `$type` discriminators, wire format                      | `docs/protocol.md`                    |
| Server config keys (`appsettings.json`), hosted services, ACL behaviour          | `docs/server-configuration.md`        |
| Anything user-visible at the SDK or CLI surface                                  | `README.md` if it affects quick-start |
| Architecture decision (why, not how)                                             | `PLANNING.md`                         |

Rules of thumb:

- Code is the source of truth. If a doc disagrees with the code, fix the doc, not the code (unless the code is wrong).
- Don't create new top-level doc files without a reason — extend the existing four first. If a new file is genuinely needed, add it to the index table in `docs/README.md`.
- Link to source files (`[src/.../X.cs](../src/.../X.cs)`) instead of pasting large code blocks — they go stale.
- When marking a TODO ✅ in `PLANNING.md`, also update the affected doc — `PLANNING.md` is for "why we chose this", not for "how to use it".
- Do NOT create separate "changelog" or "release notes" markdown files to document changes you just made unless the user explicitly asks for them.

## Problem Handling

- **Stop and ask** when encountering environment/infrastructure problems (Docker not running, database not reachable, missing tools, permission errors, etc.) — do NOT silently work around them
- Do not substitute a different tool or approach just because the expected one isn't available — ask the user to fix the environment first
- If a test or command fails due to external dependencies being unavailable, report the issue clearly and wait for confirmation before proceeding

## Infrastructure (IaC)

- The relay's Azure footprint is defined as **Bicep** under `.github/infra/` (`main.bicep` +
  `modules/`), deployed subscription-scoped. It provisions a resource group, Burstable
  PostgreSQL Flexible Server, Log Analytics + Application Insights, Key Vault, and the relay
  Web App (Linux, .NET 10, WebSockets + Always On, managed identity → Key Vault Secrets User).
- **Keep it Atrium-agnostic.** This stack only knows the relay's own config surface
  (`ConnectionStrings:Vesta`, `Admin:BootstrapPublicKeys`, `AdminApi`, `Protocol`, pruners).
  The managed portal has its **own separate** Bicep in the `vesta_atrium` repo — never
  reference it here.
- Secrets (DB password, operator public key) come from env vars at deploy time via
  `.github/infra/params/dev.bicepparam` — never commit them. App settings read secrets through
  `@Microsoft.KeyVault(...)` references.
- Cost controls: `.github/infra/scripts/dev-stop.ps1` / `dev-start.ps1` (stop/start compute) and
  `dev-teardown.ps1` (`az group delete`). Dev sizing is Burstable `Standard_B1ms` + B1 plan.
- Azure PostgreSQL Flexible Server tops out at major **16**; `postgresVersion` defaults to `16`.
- CI: `main_vestaserver.yml` has an opt-in `infrastructure` job (`workflow_dispatch`).
- The **how-to-deploy** companion is the `## Deployment & Operations (relay stack)` runbook in
  `PLANNING.md` (bootstrap secrets, deploy command/order, cost scripts, gotchas). Keep it in
  sync whenever `.github/infra/`, the deploy workflow, or the required deploy secrets change.

## EF Core Migrations

**NEVER hand-write migration files.** Always generate them with `dotnet ef migrations add <Name> --project src/VestaServer/VestaServer.csproj`.

- A migration consists of TWO files that must agree: `<timestamp>_<Name>.cs` AND `<timestamp>_<Name>.Designer.cs`. The `.Designer.cs` carries the `[Migration]` attribute and the model snapshot at that revision — without it, EF will not discover the migration and `Database.MigrateAsync()` silently skips it. CI then fails with `column "..." does not exist`.
- If `dotnet ef` is unavailable, Docker/Postgres is down, or the generation produces an empty `Up`/`Down`, **STOP and ask the user** — do not paper over it by writing migration bodies by hand.
- If a generated migration is wrong, fix it by adjusting the model + running `dotnet ef migrations remove` and re-adding it. Manual tweaks are only acceptable for cosmetic details on the body (e.g. adding `filter:` to an index) — never for inventing the migration itself or its Designer file.
- Always verify both files exist (`ls src/VestaServer/Data/Migrations/`) and that `VestaDbContextModelSnapshot.cs` reflects the new schema before committing.
