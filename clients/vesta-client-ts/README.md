# vesta-client

TypeScript client library for the [Vesta protocol](../../PLANNING.md).

## Installation

```bash
npm install vesta-client
```

## Usage (Node.js)

```typescript
import WebSocket from "ws";
import { VestaConnection, createEvent } from "vesta-client";

const connection = new VestaConnection({
  serverUrl: "ws://localhost:5150/ws",
  clientId: "my-client-id",
  channels: ["myapp/chat"],
  createSocket: (url) => new WebSocket(url) as unknown as import("vesta-client").VestaSocket,
});

connection.on("connected", (welcome) => {
  console.log("Connected to", welcome.serverId);
});

connection.on("event", (msg) => {
  console.log("Event:", msg.event.eventType, msg.event.payload);
});

connection.connect();

// Publish
const event = createEvent("myapp/chat", "my-client-id", "app.chat.message", {
  text: "Hello!",
  username: "alice",
});
connection.publish(event);
```

## Usage (Browser)

```typescript
import { VestaConnection, createEvent } from "vesta-client";

const connection = new VestaConnection({
  serverUrl: "ws://localhost:5150/ws",
  clientId: "my-client-id",
  channels: ["myapp/chat"],
  createSocket: (url) => new WebSocket(url),
});

connection.connect();
```

## API

### `VestaConnection`

The main class for managing a WebSocket connection to a Vesta server.

#### Constructor options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `serverUrl` | `string` | — | WebSocket URL |
| `clientId` | `string` | — | Unique client identifier |
| `channels` | `string[]` | — | Channels to subscribe on connect |
| `createSocket` | `(url: string) => VestaSocket` | — | WebSocket factory |
| `autoReconnect` | `boolean` | `true` | Auto-reconnect on disconnect |
| `initialReconnectDelay` | `number` | `1000` | Initial backoff in ms |
| `maxReconnectDelay` | `number` | `30000` | Max backoff in ms |
| `lastSequences` | `Record<string, number>` | `{}` | Catch-up positions |
| `publicKey` | `string` | — | Ed25519 public key (base64url) |

#### Methods

- `connect()` — Open the connection
- `disconnect()` — Gracefully close
- `dispose()` — Permanently dispose (cannot reuse)
- `publish(event)` — Publish a VestaEvent
- `subscribe(channelId, fromSequence?)` — Subscribe to a channel
- `unsubscribe(channelId)` — Unsubscribe from a channel
- `fetch(channelId, fromSequence, options?)` — Fetch historical events
- `updateSequence(channelId, sequence)` — Update catch-up position

#### Events

- `connected(welcome)` — Connection established
- `event(msg)` — Real-time event received
- `eventsBatch(msg)` — Batch of events received
- `ack(msg)` — Publish acknowledged
- `error(msg)` — Server error
- `disconnected(reason)` — Connection lost
- `reconnecting(attempt)` — Reconnection attempt starting

### `createEvent(channelId, clientId, eventType, payload, options?)`

Helper to create a `VestaEvent` with a UUID and timestamp.

### `loadOrCreateIdentity(prefix)`

Persists a stable clientId in `~/.vesta/{prefix}-identity.json`.
