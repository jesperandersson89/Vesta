# collab-edit-py

A tkinter GUI collaborative text editor — proves single shared last-writer-wins state
(`LwwRegister`) with debounced publishing and defer-while-typing conflict handling, offline
outbox, and projection snapshotting, in Python.

## Run

```powershell
cd examples/collab-edit-py
pip install -r requirements.txt   # installs vesta-client from the local ../../clients/vesta-client-py path
python main.py [ws://host:port/ws] [room-name]
```

You'll be prompted for a display name.

| Variable / arg | Default | Purpose |
| --- | --- | --- |
| `VESTA_RELAY_URL` (or 1st positional arg) | `ws://localhost:5150/ws` | Relay URL. |
| 2nd positional arg | `main` | Room name — the second channel segment. |
| `VESTA_APP_ID` | `collab-edit` | App namespace — the first channel segment. |
| `VESTA_IDENTITY_FILE` | `~/.vesta/collab-edit-{room}-{name}-identity.json` | Point at an identity downloaded from Atrium instead of a locally generated one. |

## Identity & local cache

- **Identity**: `~/.vesta/collab-edit-{room}-{name}-identity.json` (or `VESTA_IDENTITY_FILE`).
- **Local cache + offline outbox**: `~/.vesta/collab-edit-{room}-{name}-cache.db` (SQLite, via
  `SqliteClientEventStore`).
- **Projection snapshot**: `~/.vesta/collab-edit-{room}-{name}-snapshots.db` — the document's
  `LwwRegister` state is restored on startup (populating the editor immediately) and saved when
  the window is closed.

## Conflict model

Last-writer-wins on the **full document** (single shared value, not per-user) — projected with
`vesta_client.LwwRegister[dict]` (see `document_projector` in `main.py`). Typing is debounced
(150ms) before publishing; incoming remote updates are deferred while the local user is actively
typing (300ms window) to avoid fighting with their input, then applied with best-effort cursor
position preservation.

## Offline behavior

Edits made while disconnected still apply to the local editor and projection immediately
(optimistic `apply_local`) and queue in the SQLite outbox; they flush automatically on
reconnect.

## Event schema

Channel: `{appId}/{room}`

| Event type | Payload | Notes |
| --- | --- | --- |
| `app.collab.document-update` | `{ "text": string, "username": string, "cursorPos": number }` | `replace=True` — server/clients keep only the latest version. |

## Limits

A relay quota/rate-limit refusal surfaces as a red status line in the info bar via
`connection.on_limited(...)`.
