# ChatRoom.CLI

A real-time, multi-user chat room built on Vesta — proves fan-out (many writers,
many subscribers), presence-style join/leave events, offline outbox, relay
independence, and server-to-server discovery (federation).

## Run

```powershell
dotnet run --project examples/ChatRoom.CLI
```

You'll be prompted for a username. Optional environment variables / args:

| Variable / arg | Default | Purpose |
| --- | --- | --- |
| `VESTA_RELAY_URL` (or 1st positional arg) | `ws://localhost:5150/ws` | Relay URL. Comma-separate multiple URLs to seed relay failover. |
| 2nd positional arg | `{appId}/general` | Channel to join. |
| `VESTA_APP_ID` | `chat` | App namespace — the first channel segment. |
| `VESTA_IDENTITY_FILE` | `~/.vesta/chat-{username}-identity.json` | Point at an identity downloaded from Atrium instead of a locally generated one. |
| `VESTA_APP_OWNER_KEY` | this client's own key | base64url Ed25519 public key that owner-signed relay manifests must match. |
| `--register` | off | Call `RegisterAppAsync` once after connecting (needed when the relay requires app registration or restricts registration via an allow-list). |

## Identity & local cache

- **Identity**: `~/.vesta/chat-{username}-identity.json` — an Ed25519 keypair, generated on
  first run and reused on every subsequent run for that username.
- **Local cache**: `~/.vesta/chat-{username}.db` (SQLite) — every event the server has ever
  sent this client, plus an offline outbox for messages sent while disconnected.

## Offline behavior

If the relay is unreachable at startup, the app still runs: messages you send are queued in the
SQLite outbox and flushed automatically the moment a connection succeeds (including via
auto-reconnect or a relay failover). Historical messages are read from the local cache on
launch even with zero network connectivity.

## Event schema

Channel: `{appId}/general` (or your chosen channel name)

| Event type | Payload | Notes |
| --- | --- | --- |
| `app.chat.join` | `{ "sender": string }` | Published once on connect/reconnect. |
| `app.chat.leave` | `{ "sender": string }` | Not currently published by this CLI (Ctrl+C just disconnects), documented for completeness. |
| `app.chat.message` | `{ "sender": string, "text": string }` | One per line typed. |

## Commands

Type a message and press Enter to send it. Slash commands:

| Command | Purpose |
| --- | --- |
| `/help` | List commands |
| `/relays` | Show the current relay candidate list and adopted manifest |
| `/relay` | Open the relay picker (retry, discover other relays, enter a URL). Also opens automatically when no relay is reachable at startup |
| `/relay use <ws-url>` | Set a local relay override and switch to it |
| `/relay clear` | Clear the local override |
| `/discover` | Find other relays hosting this app (federation) |
| `/discover all` | Browse every relay the current relay knows about |
| `/publish-manifest <url> [url...]` | Sign & publish an owner relay manifest |
| `/register` | Register this app id with the relay (see `--register` above) |
