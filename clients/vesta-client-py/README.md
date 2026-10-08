# vesta-client (Python)

Python client library for the [Vesta protocol](../../PLANNING.md).

## Installation

```bash
pip install vesta-client
```

## Usage

```python
import asyncio
from vesta_client import VestaAppConfig, VestaConnection, create_event, load_or_create_identity

async def main():
    identity = load_or_create_identity("myapp-main-alice")

    conn = VestaConnection(
        app_config=VestaAppConfig(
            app_id="myapp",
            owner_public_key=identity.public_key_b64,
            default_relays=["ws://localhost:5150/ws"],
        ),
        identity=identity,
        channels=["myapp/chat"],
    )

    conn.on_event(lambda msg: print(f"Event: {msg.event.event_type}"))
    conn.on_connected(lambda welcome: print(f"Connected to {welcome.server_id}"))

    await conn.connect()

    # Publish
    await conn.publish(create_event(
        "myapp/chat", identity.client_id, "app.chat.message",
        {"text": "Hello!", "username": "alice"}, identity=identity,
    ))

    # Keep running
    await asyncio.Event().wait()

asyncio.run(main())
```

`app_config` gives the connection a default file-backed `RelayDirectory` (override, manifest and
peer cache under `~/.vesta/relays/`), resolves its relays from it and derives `client_id` from the
identity. For a quick local test you can still pass `server_url="ws://localhost:5150/ws"` and a
`client_id` instead.

## API

### `VestaConnection`

Async WebSocket connection with auto-reconnect.

#### Constructor

```python
VestaConnection(
    server_url: str | None = None,   # or pass app_config / relay_directory / relays
    client_id: str | None = None,    # defaults to identity.client_id
    channels: list[str] | None = None,
    *,
    app_config: VestaAppConfig | None = None,  # builds a default RelayDirectory and resolves relays
    relays: list[str] | None = None,
    auto_reconnect: bool = True,
    initial_reconnect_delay: float = 1.0,
    max_reconnect_delay: float = 30.0,
    last_sequences: dict[str, int] | None = None,
    public_key: str | None = None,
    identity: VestaIdentity | None = None,  # enables device-group helpers
    local_store: ClientEventStore | None = None,  # offline outbox + event cache
    relay_directory: RelayDirectory | None = None,  # manifest verification + relay failover
)
```

#### Methods

- `await connect()` — Open connection and handshake
- `await disconnect()` — Gracefully close
- `await dispose()` — Permanently dispose
- `await publish(event)` — Publish a VestaEvent
- `await subscribe(channel_id, from_sequence=None)` — Subscribe
- `await unsubscribe(channel_id)` — Unsubscribe
- `await fetch(channel_id, from_sequence, to_sequence=None, limit=None)` — Fetch history
- `update_sequence(channel_id, sequence)` — Update catch-up position
- `await delete_channel(channel_id)` — Soft-delete a channel
- `await register_app(app_id)` — Register an app namespace (needed when the relay requires app registration)
- `set_user_relay_override(url)` / `clear_user_relay_override()` — Persist or clear the user's manual relay choice (requires `relay_directory`)
- `await switch_relay(url)` — Switch to a specific relay from the current candidate list and reconnect
- `await discover_relays(include_all=False)` — Ask a reachable relay which relays host this app (or every relay it knows); show-only
- `await publish_relay_manifest(relays)` — Sign and publish an owner relay manifest (`identity` must be the app owner)

**Device group helpers** (require `identity` in constructor):

- `await create_device_group(device_name=None)` — Create a new group, publish an announce, return `group_id`
- `await link_device(group_id, target_public_key, reason=None)` — Vouch for another device
- `await join_device_group(group_id, device_name=None)` — Announce this device joining an existing group
- `await unlink_device(group_id, target_public_key, reason=None)` — Remove a device from the group
- `await get_device_group_members(group_id, timeout=5.0)` — Replay the identity channel and return current membership as `DeviceGroup`

#### Event callbacks

- `on_event(callback)` — Real-time event received
- `on_events_batch(callback)` — Batch of events received
- `on_ack(callback)` — Publish acknowledged
- `on_error(callback)` — Raw server error
- `on_limited(callback)` — Semantic "your app is being limited" signal — see "Limit notices" below
- `on_connected(callback)` — Connection established
- `on_reconnected(callback)` — Fired instead of/alongside `on_connected` on a *subsequent* WELCOME
- `on_disconnected(callback)` — Connection lost
- `on_relay_switched(callback)` — The active relay changed
- `on_manifest_applied(callback)` — A newer owner-signed relay manifest was adopted

### Limit notices

When the relay refuses a publish (quota, rate limit, unregistered app, ACL), the raw error is
still delivered via `on_error`, but `classify_error_code(code)` (also run internally) tells you
whether it's worth surfacing to the user and whether retrying can ever succeed:

```python
conn.on_limited(lambda notice: print(notice.code, notice.message, "transient:", notice.is_transient))
```

When `local_store` is configured and the limit is **not transient** (e.g. `QUOTA_EXCEEDED`,
`UNKNOWN_APP`, `ACCESS_DENIED`), the matching outbox entry is automatically marked `"rejected"`
via `mark_outbox_rejected` so it is dead-lettered instead of retried forever.

### Offline & persistence

`ClientEventStore` caches received events and queues outbox publishes made while disconnected.
The package ships `InMemoryClientEventStore` (non-persistent) and `SqliteClientEventStore`
(stdlib `sqlite3`, durable across restarts):

```python
from vesta_client import SqliteClientEventStore

local_store = SqliteClientEventStore("my-app-cache.db")
conn = VestaConnection(server_url="ws://localhost:5150/ws", client_id=client_id,
                       channels=["myapp/chat"], local_store=local_store)
```

### Projection snapshots

Every built-in reducer (`AppendOnlyLog`, `LwwRegister`, `LwwMap`) supports `snapshot()` /
`restore()` so a projection can resume from its last sequence instead of replaying the whole
channel on cold start. Persist snapshots with a `ProjectionStore` — `InMemoryProjectionStore` or
`SqliteProjectionStore`:

```python
from vesta_client import LwwMap, restore_projection, save_projection
from vesta_client.projection_store import SqliteProjectionStore

store = SqliteProjectionStore("my-app-snapshots.db")
presence = LwwMap(project)

await restore_projection(store, channel_id, "presence", presence)
await conn.fetch(channel_id, presence.last_sequence + 1)
# ...later, e.g. on shutdown:
await save_projection(store, channel_id, "presence", presence)
```

A reducer that hasn't opted in raises `SnapshotNotSupportedError` — override `snapshot()` /
`_restore_state()` on a custom `EventReducer` subclass to add support.

### Relay independence

`RelayDirectory` resolves an ordered relay candidate list from a user override, the latest
verified owner-signed manifest, and the app's compiled-in defaults (`VestaAppConfig`). Passing
`app_config` to `VestaConnection` attaches `RelayDirectory.create_default(app_config)` for you,
which gives manifest verification/adoption and `set_user_relay_override()` / `clear_user_relay_override()`.

To control persistence, build the directory yourself and pass `relay_directory`:

```python
from vesta_client import InMemoryManifestStore, InMemoryRelayOverrideStore, RelayDirectory, VestaAppConfig

app_config = VestaAppConfig(app_id="myapp", owner_public_key="...", default_relays=["wss://relay.example/ws"])
relay_directory = RelayDirectory(app_config, InMemoryRelayOverrideStore(), InMemoryManifestStore())

conn = VestaConnection(relay_directory=relay_directory, identity=identity, channels=["myapp/chat"])
```

`FileRelayOverrideStore` / `FileManifestStore` / `FilePeerCacheStore` are the file-backed
equivalents used by `create_default`.

### Federation (server-to-server discovery)

When every relay in the candidate list is failing and no fresher manifest is available,
`FederationClient` asks any reachable discovery-enabled relay which relays host your app:

```python
from vesta_client import FederationClient

federation = FederationClient(app_config)
base = FederationClient.to_federation_base_url("wss://relay.example/ws")  # "https://relay.example/"
relays = await federation.discover_relays_for_app(base)
# Show-only: the user adopts one manually via conn.set_user_relay_override(url).
```

Every descriptor is verified (self-signature) and cross-checked against the app's owner
(`owner_client_id` must match `derive_client_id(app_config.owner_public_key)`) — a relay cannot
spoof hosting your app under a different owner. Uses the stdlib `urllib.request` internally — no
extra dependency.

### `create_event(channel_id, client_id, event_type, payload, **kwargs)`

Create a `VestaEvent` with a UUID and current timestamp.

### `load_or_create_identity(prefix)`

Persist a stable clientId in `~/.vesta/{prefix}-identity.json`.
