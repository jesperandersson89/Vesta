# Vesta

**Vesta** is a protocol and runtime for building networked applications without surrendering ownership to a central service. The server is a relay (like a git remote or a torrent tracker), not the authority. Clients own their data.

If the server disappears tomorrow, the application still works locally and can reconnect to any other Vesta-compatible host.

## Who Vesta is for

Vesta is for people who want to ship a connected app **without promising to run its backend forever**:

- **Tinkerers and side projects.** You built a shared shopping list, a club roster, a home-automation
  dashboard or a tool for your friends, and you can't guarantee a server will be up in five years. With
  Vesta the data lives on your users' devices, and they can point the app at any other relay if yours
  goes away.
- **Apps that should outlive their maker.** If you believe software shouldn't die when its company
  does, Vesta is a way to build that in from the start: signed, self-verifying event logs, self-sovereign
  identities, and a relay that is interchangeable by design.
- **Companies that want to hand users ownership.** Host a relay for as long as it makes sense, let users
  take their data and keep going when it doesn't. No lock-in to offer, nothing to migrate when you stop.

Your **users never deal with Vesta**: they just use your app. Only you, the developer, pick a relay (run
one, or use a hosted one) and register an app namespace on it.

### Good fit / not a fit

| Good fit                                                                                      | Not a fit (today)                                                                                              |
| --------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------- |
| Group chat, comments, activity feeds (append-only logs)                                        | **Fast-tick real-time games** (shooters, physics): WebSocket + JSON + a storing relay is the wrong shape; a separate realtime flavour may come later |
| Shared lists, boards, notes; anything that must work offline and merge later                  | **Confidential data needing end-to-end encryption** is now covered by [encrypted channels](docs/encryption.md) (unreleased until SDK `0.4.0`); on ordinary channels the relay stores payloads in the clear, so treat those as visible to the operator |
| Your own data synced across your devices (phone + laptop) with no account                      | **Files, photos, binary blobs**: payloads are JSON with a size cap; keep large media elsewhere and sync links/metadata |
| Community tools: club rosters, rotas, noticeboards, event sign-ups, neighbourhood sharing      | **Search, queries, joins**: a channel is an ordered stream, not a database. You derive state client-side with projections |
| Hobby automation and IFTTT-style glue: a sensor on a Pi publishing, a browser subscribing      | **A plain CRUD/REST backend**: everything is event-sourced; if you want rows and PUT/GET, this isn't it |
| Turn-based or slow multiplayer (chess, board games, play-by-post), presence and "who's online" | **Server-side business rules**: the relay never validates payloads. Rules live in clients, or in a small service you run that signs the authoritative events ([docs/patterns.md](docs/patterns.md)) |
| One trusted writer, many readers: announcements, a service publishing confirmations            |                                                                                                                |

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
├── AccountServer/      # Reference account server: password login on any device, key never exposed
├── colorwheel-py/      # Python tkinter color picker (real-time sync)
├── collab-edit-py/     # Python collaborative text editor
├── clipboard-ts/       # TypeScript shared clipboard (Node.js)
└── chess-web/          # TypeScript browser chess (turn-based multiplayer)

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

- **Production:** use a relay someone operates, e.g. an [Atrium](PLANNING.md#boundaries) managed endpoint. Real relays are `wss://` (TLS); mobile platforms refuse anything else.
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

- **Replace events** — `replace: true` tells the server to keep only the latest event per (channel, client, type). Ideal for presence, "last known value" state, device status.
- **Volatile events** — `volatile: true` skips DB storage entirely, just relays to current subscribers. Ideal for typing indicators, live cursors and other throwaway signals.
- **Channel access control** — `private` (members only) and `broadcast` (anyone reads, only listed writers publish) channels, so a small service you run can be the one trusted writer. See [docs/patterns.md](docs/patterns.md).
- **End-to-end encrypted channels** — `encrypted: true` channels whose payloads the relay (and any relay a user fails over to) cannot read; members get keys from an admin over the channel itself, rotation locks removed members out. See [docs/encryption.md](docs/encryption.md).
- **Offline-first** — clients queue events to a local outbox and sync on reconnect.
- **Auto-reconnect** — exponential backoff with seamless catch-up via sequence tracking.
- **Server independence** — clients fail over across an ordered list of relays and follow an owner-signed relay manifest, so an app survives its relay (or developer) going away. Each user can also pin a personal relay override. See [docs/protocol.md](docs/protocol.md#relay-manifests-server-independence).
- **Cross-language** — the protocol is JSON over WebSocket; examples exist in C#, Python, and TypeScript.

## License

MIT
