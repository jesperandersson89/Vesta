# clipboard-ts

A shared clipboard synced across machines via Vesta — proves single-writer-per-user,
last-writer-wins state, offline outbox, projection snapshotting, and relay independence
in a plain Node.js CLI (TypeScript).

## Run

```powershell
cd examples/clipboard-ts
npm install
npm start           # tsx src/main.ts
```

`npm run dev` runs the same entry point with `tsx watch` for iteration; `npm run build` type-checks
via `tsc`.

You'll be prompted for a display name. Optional environment variables / args:

| Variable / arg | Default | Purpose |
| --- | --- | --- |
| `VESTA_RELAY_URL` (or 1st positional arg) | `ws://localhost:5150/ws` | Relay URL(s). Comma-separate multiple to seed relay failover. |
| 2nd positional arg | `main` | Room name — the second channel segment. |
| `VESTA_APP_ID` | `clipboard` | App namespace — the first channel segment. |
| `VESTA_IDENTITY_FILE` | `~/.vesta/clipboard-{room}-{name}-identity.json` | Point at an identity downloaded from Atrium instead of a locally generated one. |
| `VESTA_APP_OWNER_KEY` | this client's own key | base64url Ed25519 public key that owner-signed relay manifests must match. |

## Identity & local cache

- **Identity**: `~/.vesta/clipboard-{room}-{name}-identity.json` (or `VESTA_IDENTITY_FILE`).
- **Local cache + offline outbox**: `~/.vesta/clipboard-{room}-{name}-cache.json` — a
  `FileClientEventStore` (from `vesta-client/node`). Durable across restarts; rewrites the whole
  file per mutation, so it's demo-scale, not high-throughput.
- **Projection snapshot**: `~/.vesta/clipboard-{room}-{name}-snapshots.json` — the `LwwMap` of
  per-user clipboard entries is restored on startup and saved on Ctrl+C, so the UI shows the last
  known state instantly instead of waiting for a full channel replay.
- **Relay override / manifest cache**: `~/.vesta/relays/{appId}.{override,manifest}.json` (via
  `FileRelayOverrideStore` / `FileManifestStore`), matching the C# examples'
  `RelayDirectory.CreateDefault` layout.

## Offline behavior

Clipboard changes are applied to the local projection immediately (optimistic local apply) and
published through the connection; if disconnected, the local outbox queues the event and it
flushes automatically on reconnect — copying while offline is never blocked or lost.

## Event schema

Channel: `{appId}/{room}`

| Event type | Payload | Notes |
| --- | --- | --- |
| `app.clipboard.update` | `{ "text": string, "username": string }` | `replace: true` — server keeps only the latest per client id. |

Projected with `LwwMap<string, ClipboardEntry>` keyed by client id (see `clipboardProjector` in
`src/main.ts`).

## Relay independence & limits

If every configured relay fails, the app keeps running against its local cache while the SDK
opens its own relay picker — a loopback web page in your system browser — automatically; no app
UI is wired up for it. The choice is remembered via `FileRelayOverrideStore` (see above) across
runs. A `limited` event (quota / rate-limit / registration refusal) is surfaced as a red
`[LIMITED]` line in the footer via `connection.on("limited", ...)`.
