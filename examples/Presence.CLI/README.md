# Presence.CLI

A "who's online" presence board — proves ephemeral/volatile-style state via TTL heartbeats,
last-writer-wins-per-user projection, and projection snapshotting (fast cold start).

## Run

```powershell
dotnet run --project examples/Presence.CLI
```

You'll be prompted for a display name.

| Variable / arg | Default | Purpose |
| --- | --- | --- |
| `VESTA_RELAY_URL` (or 1st positional arg) | `ws://localhost:5150/ws` | Relay URL. |
| 2nd positional arg | `vesta-presence` | Room name — the second channel segment. |
| `VESTA_APP_ID` | `presence` | App namespace — the first channel segment. |
| `VESTA_IDENTITY_FILE` | `~/.vesta/presence-{room}-{name}-identity.json` | Point at an identity downloaded from Atrium instead of a locally generated one. |
| `VESTA_APP_OWNER_KEY` | this client's own key | base64url Ed25519 public key that owner-signed relay manifests must match. |
| `--register` | off | Call `RegisterAppAsync` once after connecting. |

## Identity & local cache

- **Identity**: `~/.vesta/presence-{room}-{name}-identity.json`.
- **Local cache**: `~/.vesta/presence-{room}-{name}.db` (SQLite) — cached events + outbox.
- **Projection snapshot**: `~/.vesta/presence-{room}-{name}-snapshots.db` — the `LwwMap`
  presence state is saved here on shutdown and restored on the next launch, so the UI shows
  known users instantly instead of waiting for a full channel replay.

## Offline behavior

Heartbeats sent while disconnected are queued in the SQLite outbox like any other event and
flush automatically on reconnect. Because presence heartbeats carry a short TTL
(`metadata.ttlSeconds`), a queued heartbeat that sits in the outbox long enough to expire before
it's sent is simply a no-op from other clients' perspective — this is expected for ephemeral
state.

## Event schema

Channel: `{appId}/{room}`

| Event type | Payload | Metadata | Notes |
| --- | --- | --- | --- |
| `app.presence.heartbeat` | `{ "username": string, "status": "online" }` | `{ "ttlSeconds": 15 }` | `Replace: true` — last-writer-wins per client id. Sent every 5s. |
| `app.presence.bye` | `{ "username": string }` | — | Sent on graceful shutdown (Ctrl+C). |

Presence state is projected with `VestaCore.Projections.LwwMap<string, Heartbeat>` (see
`PresenceState.cs`): a heartbeat is a `Set`, a `bye` is a `Remove` (tombstone).

## Commands

There are no interactive commands — this is a live dashboard. Press Ctrl+C to quit, which
publishes `app.presence.bye` and saves a final projection snapshot before exiting.
