# vesta-client (Python)

Python client library for the [Vesta protocol](../../PLANNING.md).

## Installation

```bash
pip install vesta-client
```

## Usage

```python
import asyncio
from vesta_client import VestaConnection, create_event, load_or_create_identity

async def main():
    client_id = load_or_create_identity("myapp-main-alice")

    conn = VestaConnection(
        server_url="ws://localhost:5150/ws",
        client_id=client_id,
        channels=["myapp/chat"],
    )

    conn.on_event(lambda msg: print(f"Event: {msg.event.event_type}"))
    conn.on_connected(lambda welcome: print(f"Connected to {welcome.server_id}"))

    await conn.connect()

    # Publish
    event = create_event(
        channel_id="myapp/chat",
        client_id=client_id,
        event_type="app.chat.message",
        payload={"text": "Hello!", "username": "alice"},
    )
    await conn.publish(event)

    # Keep running
    await asyncio.Event().wait()

asyncio.run(main())
```

## API

### `VestaConnection`

Async WebSocket connection with auto-reconnect.

#### Constructor

```python
VestaConnection(
    server_url: str,
    client_id: str,
    channels: list[str],
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
verified owner-signed manifest, and the app's compiled-in defaults (`VestaAppConfig`). Attach one
to get manifest verification/adoption and `set_user_relay_override()` / `clear_user_relay_override()`:

```python
from vesta_client import RelayDirectory, VestaAppConfig, FileRelayOverrideStore, FileManifestStore

app_config = VestaAppConfig(app_id="myapp", owner_public_key="...", default_relays=["wss://relay.example/ws"])
relay_directory = RelayDirectory(
    app_config,
    FileRelayOverrideStore("~/.vesta/relays/myapp.override.json"),
    FileManifestStore("~/.vesta/relays/myapp.manifest.json"),
)

conn = VestaConnection(
    relays=relay_directory.resolve_candidates(),
    client_id=client_id, channels=["myapp/chat"],
    relay_directory=relay_directory,
)
```

`InMemoryRelayOverrideStore` / `InMemoryManifestStore` are also available for tests or transient
sessions.

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
