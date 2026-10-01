# Vesta

**Vesta** is a protocol and runtime for building networked applications without surrendering ownership to a central service. The server is a relay (like a git remote or a torrent tracker), not the authority. Clients own their data.

If the server disappears tomorrow, the application still works locally and can reconnect to any other Vesta-compatible host.

## Core Principles

- **Data belongs to the client** — all state is reconstructable from the client's local log
- **Server is a relay, not an authority** — it stores and forwards; no business logic
- **Protocol over platform** — any client that speaks Vesta protocol can participate
- **Offline-first** — operations queue locally and sync when connectivity returns
- **Conflict resolution is explicit** — the protocol provides mechanisms; the app decides policy

## Architecture

```
┌─────────────┐       ┌─────────────┐       ┌─────────────┐
│  Client A   │       │  Client B   │       │  Client C   │
│  (C# CLI)   │       │  (Python)   │       │  (JS/TS)    │
└──────┬──────┘       └──────┬──────┘       └──────┬──────┘
       │                     │                     │
       │      Vesta Protocol (WebSocket)           │
       │                     │                     │
       └─────────────────────┬─────────────────────┘
                             │
                      ┌──────┴──────┐
                      │ Vesta Server│
                      │  (C# host)  │
                      └──────┬──────┘
                             │
                      ┌──────┴──────┐
                      │  PostgreSQL │
                      └─────────────┘
```

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Server | C# / .NET 10, ASP.NET Core minimal API + WebSocket |
| Server DB | PostgreSQL 18 (raw Npgsql for events, EF Core for metadata) |
| Client DB | SQLite (offline cache + outbox) |
| Identity | Ed25519 keypairs (self-sovereign, no accounts) |
| Serialization | System.Text.Json, RFC 8785 (JCS) for signing |

## Project Structure

```
src/
├── VestaCore/          # Shared types, protocol, serialization
├── VestaServer/        # ASP.NET Core relay + persistence
└── VestaClient/        # C# client library

examples/
├── ChatRoom.CLI/       # C# real-time chat
├── TodoList.CLI/       # C# todo list with offline support
├── Presence.CLI/       # C# online presence (heartbeats + TTL)
├── colorwheel-py/      # Python tkinter color picker (real-time sync)
├── collab-edit-py/     # Python collaborative text editor
└── clipboard-ts/       # TypeScript shared clipboard (Node.js)

tests/
├── VestaCore.Tests/
├── VestaServer.Tests/
└── VestaClient.Tests/
```

## Quick Start

### Install a client library

```bash
dotnet add package VestaProtocol.Client
npm install vesta-client
pip install vesta-client
```

`VestaProtocol.Client` depends on `VestaProtocol.Core` (protocol types); npm and PyPI ship a single
`vesta-client` package per language. See the `## Releasing` runbook in
[PLANNING.md](PLANNING.md) for how these are published.

### Get a relay to connect to

Vesta apps connect to a **relay** (the server). **You do not deploy a relay to build an app** — relays are run by *operators*, and your app just needs a relay URL plus your identity:

- **Production:** use a relay someone operates, e.g. an [Atrium](PLANNING.md#boundaries) managed endpoint.
- **Development:** run a throwaway local relay (needs Docker):

  ```bash
  docker compose up --build   # ws://localhost:5150/ws — open mode, dev only
  ```

> **Relay operators:** if you run a relay *for other people's apps to connect to*, read [docs/operating-a-relay.md](docs/operating-a-relay.md). Most developers never need to.

<details>
<summary>Hacking on the server itself (contributors)</summary>

```bash
cd src/VestaServer
dotnet run -- --UseInMemoryStore=true   # ws://localhost:5150/ws, in-memory store
```

</details>

### Run an example

```bash
# C# chat room
cd examples/ChatRoom.CLI
dotnet run

# Python color wheel (requires: pip install -r requirements.txt)
cd examples/colorwheel-py
python main.py

# TypeScript shared clipboard (requires: npm install)
cd examples/clipboard-ts
npm start
```

## Protocol Overview

Clients communicate with the server via WebSocket using JSON messages:

```
CLIENT → SERVER          SERVER → CLIENT
  HELLO                    WELCOME
  PUBLISH                  ACK
  SUBSCRIBE                EVENT
  UNSUBSCRIBE              EVENTS_BATCH
  FETCH                    ERROR
```

Events are immutable, signed, and append-only:

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "channelId": "myapp/todos",
  "timestamp": "2026-06-01T12:00:00.000Z",
  "clientId": "Xk9mQ2pLvN3wR8tY5uA7bC",
  "eventType": "app.todo.item-added",
  "payload": { "title": "Buy milk", "done": false },
  "signature": "base64url-ed25519-sig"
}
```

The server assigns a per-channel monotonic **sequence number** and broadcasts to subscribers. It never interprets payloads — all business logic lives in the client.

## Key Features

- **Replace events** — `replace: true` tells the server to keep only the latest event per (channel, client, type). Ideal for presence, cursors, LWW state.
- **Volatile events** — `volatile: true` skips DB storage entirely, just relays to current subscribers. Ideal for real-time ephemeral data.
- **Offline-first** — clients queue events to a local outbox and sync on reconnect.
- **Auto-reconnect** — exponential backoff with seamless catch-up via sequence tracking.
- **Server independence** — clients fail over across an ordered list of relays and follow an owner-signed relay manifest, so an app survives its relay (or developer) going away. Each user can also pin a personal relay override. See [docs/protocol.md](docs/protocol.md#relay-manifests-server-independence).
- **Cross-language** — the protocol is JSON over WebSocket; examples exist in C#, Python, and TypeScript.

## License

MIT
