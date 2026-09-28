# colorwheel-py

A tkinter GUI color wheel — proves ephemeral/volatile state, last-writer-wins-per-user
projection via the SDK's `LwwMap`, offline outbox, and projection snapshotting, in Python.

## Run

```powershell
cd examples/colorwheel-py
pip install -r requirements.txt   # installs vesta-client from the local ../../clients/vesta-client-py path
python main.py [ws://host:port/ws] [room-name]
```

You'll be prompted for a display name.

| Variable / arg | Default | Purpose |
| --- | --- | --- |
| `VESTA_RELAY_URL` (or 1st positional arg) | `ws://localhost:5150/ws` | Relay URL. |
| 2nd positional arg | `main` | Room name — the second channel segment. |
| `VESTA_APP_ID` | `colorwheel` | App namespace — the first channel segment. |
| `VESTA_IDENTITY_FILE` | `~/.vesta/colorwheel-{room}-{name}-identity.json` | Point at an identity downloaded from Atrium instead of a locally generated one. |

## Identity & local cache

- **Identity**: `~/.vesta/colorwheel-{room}-{name}-identity.json` (or `VESTA_IDENTITY_FILE`).
- **Local cache + offline outbox**: `~/.vesta/colorwheel-{room}-{name}-cache.db` (SQLite, via
  `SqliteClientEventStore`).
- **Projection snapshot**: `~/.vesta/colorwheel-{room}-{name}-snapshots.db` — the `LwwMap` of
  per-user colors is restored on startup and saved when the window is closed, so the roster
  shows known users instantly instead of waiting for a full channel replay.

## Offline behavior

Dragging on the wheel while disconnected still updates your own color locally (optimistic
`apply_local`) and queues the publish in the SQLite outbox; it flushes automatically on
reconnect.

## Event schema

Channel: `{appId}/{room}`

| Event type | Payload | Notes |
| --- | --- | --- |
| `app.colorwheel.update` | `{ "color": "#rrggbb", "username": string }` | `replace=True`, `volatile=True` — no need to persist color history, just relay to current subscribers. |

Projected with `vesta_client.LwwMap[str, dict]` keyed by client id (see `colorwheel_projector`
in `main.py`).

## Limits

A relay quota/rate-limit refusal surfaces as a red status line under the connection indicator
via `connection.on_limited(...)`.
