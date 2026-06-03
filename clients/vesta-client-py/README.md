# vesta-client (Python)

Python client library for the [Vesta protocol](../../PLANNING.md).

## Installation

```bash
pip install -e clients/vesta-client-py
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
- `on_error(callback)` — Server error
- `on_connected(callback)` — Connection established
- `on_disconnected(callback)` — Connection lost

### `create_event(channel_id, client_id, event_type, payload, **kwargs)`

Create a `VestaEvent` with a UUID and current timestamp.

### `load_or_create_identity(prefix)`

Persist a stable clientId in `~/.vesta/{prefix}-identity.json`.
