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
│   ├── TicTacToe.CLI/          # C# multiplayer game demo
│   ├── SharedClipboard.CLI/    # C# clipboard demo
│   ├── chat-web/               # JS browser chat demo
│   └── todo-py/                # Python CLI todo demo
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
- **Naming**: PascalCase for public members, _camelCase for private fields, camelCase for local variables
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

## Problem Handling

- **Stop and ask** when encountering environment/infrastructure problems (Docker not running, database not reachable, missing tools, permission errors, etc.) — do NOT silently work around them
- Do not substitute a different tool or approach just because the expected one isn't available — ask the user to fix the environment first
- If a test or command fails due to external dependencies being unavailable, report the issue clearly and wait for confirmation before proceeding

## EF Core Migrations

**NEVER hand-write migration files.** Always generate them with `dotnet ef migrations add <Name> --project src/VestaServer/VestaServer.csproj`.

- A migration consists of TWO files that must agree: `<timestamp>_<Name>.cs` AND `<timestamp>_<Name>.Designer.cs`. The `.Designer.cs` carries the `[Migration]` attribute and the model snapshot at that revision — without it, EF will not discover the migration and `Database.MigrateAsync()` silently skips it. CI then fails with `column "..." does not exist`.
- If `dotnet ef` is unavailable, Docker/Postgres is down, or the generation produces an empty `Up`/`Down`, **STOP and ask the user** — do not paper over it by writing migration bodies by hand.
- If a generated migration is wrong, fix it by adjusting the model + running `dotnet ef migrations remove` and re-adding it. Manual tweaks are only acceptable for cosmetic details on the body (e.g. adding `filter:` to an index) — never for inventing the migration itself or its Designer file.
- Always verify both files exist (`ls src/VestaServer/Data/Migrations/`) and that `VestaDbContextModelSnapshot.cs` reflects the new schema before committing.
