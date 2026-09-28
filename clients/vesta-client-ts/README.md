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
| `identity` | `VestaIdentity` | — | Full Ed25519 identity. Enables device-group helpers; `publicKey` is derived from it automatically. |
| `localStore` | `ClientEventStore` | — | Local event cache + offline outbox. See "Offline & persistence" below. |
| `relayDirectory` | `RelayDirectory` | — | Owner-signed manifest verification + relay failover. See "Relay independence" below. |

#### Methods

- `connect()` — Open the connection
- `disconnect()` — Gracefully close
- `dispose()` — Permanently dispose (cannot reuse)
- `publish(event)` — Publish a VestaEvent
- `subscribe(channelId, fromSequence?)` — Subscribe to a channel
- `unsubscribe(channelId)` — Unsubscribe from a channel
- `fetch(channelId, fromSequence, options?)` — Fetch historical events
- `updateSequence(channelId, sequence)` — Update catch-up position
- `deleteChannel(channelId)` — Soft-delete a channel
- `registerApp(appId)` — Register an app namespace (needed when the relay runs with `Protocol:RequireAppRegistration=true`)
- `setUserRelayOverride(url)` / `clearUserRelayOverride()` — Persist or clear the user's manual relay choice (requires `relayDirectory`)
- `switchRelay(url)` — Switch to a specific relay from the current candidate list and reconnect

**Device group helpers** (require `identity` in constructor options):

- `createDeviceGroup(deviceName?)` — Create a new group, publish an announce, return `groupId`
- `linkDevice(groupId, targetPublicKey, reason?)` — Vouch for another device
- `joinDeviceGroup(groupId, deviceName?)` — Announce this device joining an existing group
- `unlinkDevice(groupId, targetPublicKey, reason?)` — Remove a device from the group
- `getDeviceGroupMembers(groupId, timeoutMs?)` — Replay the identity channel and return current membership as `DeviceGroup`

#### Events

- `connected(welcome)` — Connection established
- `reconnected(welcome)` — Fired instead of/alongside `connected` when this is a *subsequent* WELCOME after the connection had already reached one before
- `event(msg)` — Real-time event received
- `eventsBatch(msg)` — Batch of events received
- `ack(msg)` — Publish acknowledged
- `error(msg)` — Raw server error
- `limited(notice)` — Semantic "your app is being limited" signal — see "Limit notices" below
- `disconnected(reason)` — Connection lost
- `reconnecting(attempt)` — Reconnection attempt starting
- `relaySwitched(url)` — The active relay changed
- `manifestApplied(manifest)` — A newer owner-signed relay manifest was adopted

### Limit notices

When the relay refuses a publish (quota, rate limit, unregistered app, ACL), the raw `ErrorMessage`
is still emitted via `error`, but `classifyErrorCode(code)` (also run internally) tells you whether
it's worth surfacing to the user and whether retrying can ever succeed:

```typescript
connection.on("limited", (notice) => {
  console.log(notice.code, notice.message, "transient:", notice.isTransient);
});
```

When a `localStore` is configured and the limit is **not transient** (e.g. `QUOTA_EXCEEDED`,
`UNKNOWN_APP`, `ACCESS_DENIED`), the matching outbox entry is automatically marked `"rejected"` so
it is dead-lettered instead of retried forever.

### Offline & persistence

`ClientEventStore` caches received events and queues outbox publishes made while disconnected.
The package ships `InMemoryClientEventStore` (works everywhere, not durable) and, for Node.js,
`FileClientEventStore` from the `vesta-client/node` subpath (durable across restarts; demo-scale,
not high-throughput):

```typescript
import { FileClientEventStore } from "vesta-client/node";

const localStore = new FileClientEventStore("./my-app-cache.json");
```

### Projection snapshots

Every built-in reducer (`AppendOnlyLog`, `LwwRegister`, `LwwMap`) supports `snapshot()` /
`restore()` so a projection can resume from its last sequence instead of replaying the whole
channel on cold start. Persist snapshots with a `ProjectionStore` — `InMemoryProjectionStore` or
`LocalStorageProjectionStore` from the package root, or `FileProjectionStore` from
`vesta-client/node`:

```typescript
import { saveProjection, restoreProjection, InMemoryProjectionStore, LwwMap } from "vesta-client";

const store = new InMemoryProjectionStore();
const presence = new LwwMap(project);

await restoreProjection(store, channelId, "presence", presence);
connection.fetch(channelId, presence.lastSequence + 1);
// ...later, e.g. on shutdown:
await saveProjection(store, channelId, "presence", presence);
```

A reducer that hasn't opted in throws `SnapshotNotSupportedError` — override `snapshot()` /
`restore()` on a custom `EventReducer` subclass to add support.

### Relay independence

`RelayDirectory` resolves an ordered relay candidate list from a user override, the latest
verified owner-signed manifest, and the app's compiled-in defaults (`VestaAppConfig`). Attach one
to get manifest verification/adoption and `setUserRelayOverride()` / `clearUserRelayOverride()`:

```typescript
import { RelayDirectory, InMemoryManifestStore, InMemoryRelayOverrideStore } from "vesta-client";

const appConfig = { appId: "myapp", ownerPublicKey: "...", defaultRelays: ["wss://relay.example/ws"] };
const relayDirectory = new RelayDirectory(appConfig, new InMemoryRelayOverrideStore(), new InMemoryManifestStore());

const connection = new VestaConnection({
  relays: relayDirectory.resolveCandidates(),
  relayDirectory,
  // ...
});
```

For Node.js CLI apps, `FileRelayOverrideStore` / `FileManifestStore` from `vesta-client/node`
persist the override and manifest cache under `~/.vesta/relays/`, matching the C# client's
`RelayDirectory.CreateDefault` layout. For browsers, use `LocalStorageRelayOverrideStore` /
`LocalStorageManifestStore` from the package root.

### Federation (server-to-server discovery)

When every relay in the candidate list is failing and no fresher manifest is available,
`FederationClient` asks any reachable discovery-enabled relay which relays host your app:

```typescript
import { FederationClient } from "vesta-client";

const federation = new FederationClient(appConfig);
const base = FederationClient.toFederationBaseUrl("wss://relay.example/ws"); // "https://relay.example/"
const relays = await federation.discoverRelaysForApp(base);
// Show-only: the user adopts one manually via connection.setUserRelayOverride(url).
```

Every descriptor is verified (self-signature) and cross-checked against the app's owner
(`ownerClientId` must match `deriveClientId(appConfig.ownerPublicKey)`) — a relay cannot spoof
hosting your app under a different owner.

### `createEvent(channelId, clientId, eventType, payload, options?)`

Helper to create a `VestaEvent` with a UUID and timestamp.

### `loadOrCreateIdentity(prefix)`

Persists a stable clientId in `~/.vesta/{prefix}-identity.json`.
