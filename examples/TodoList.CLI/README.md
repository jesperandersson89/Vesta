# TodoList.CLI

A synced todo list — proves append-only + last-writer-wins-per-field state, multi-device
sync via shared credentials, offline editing, and cross-device identity (device-group pairing).

## Run

```powershell
dotnet run --project examples/TodoList.CLI
```

You'll be prompted for a username and password (or pass `--user <name> --password <pw>`).
**Any device that signs in with the same username + password lands on the same list** — the
channel name is derived deterministically from the credentials (SHA-256 hash), not from a
server-side account.

| Variable / arg | Default | Purpose |
| --- | --- | --- |
| `VESTA_RELAY_URL` (or positional arg) | `ws://localhost:5150/ws` | Relay URL. |
| `--user <name>` | prompted | Username (also derives the channel). |
| `--password <pw>` | prompted | Password (also derives the channel). |
| `VESTA_APP_ID` | `todo` | App namespace — the first channel segment. |
| `VESTA_IDENTITY_FILE` | `~/.vesta/todo-identity.json` | Point at an identity downloaded from Atrium instead of a locally generated one. |
| `VESTA_APP_OWNER_KEY` | this client's own key | base64url Ed25519 public key that owner-signed relay manifests must match. |
| `--register` | off | Call `RegisterAppAsync` once after connecting. |

## Identity & local cache

- **Identity**: `~/.vesta/todo-identity.json` — one Ed25519 keypair per machine (not per
  username — the channel, not the identity, is what's shared across devices logging in with the
  same credentials).
- **Local cache**: `~/.vesta/todo-{credentials-hash}.db` (SQLite) — the full event log for this
  list, replayed into `TodoListState` on every launch, plus an offline outbox.

## Offline behavior

Every command (`add`, `done`, `rename`, `remove`) applies to the in-memory projection
immediately (optimistic local apply) and is queued in the SQLite outbox if disconnected, so the
UI never blocks on the network. Queued edits flush automatically on reconnect.

## Event schema

Channel: `{appId}/{sha256(username:password)[:16]}`

| Event type | Payload | Notes |
| --- | --- | --- |
| `app.todo.item-added` | `{ "id": guid, "title": string, "createdBy": string }` | |
| `app.todo.item-toggled` | `{ "id": guid, "done": bool }` | |
| `app.todo.item-renamed` | `{ "id": guid, "title": string }` | |
| `app.todo.item-removed` | `{ "id": guid }` | |

## Device-group pairing (cross-device identity)

This is a **separate mechanism** from the username/password channel sharing above — it proves
two distinct Ed25519 identities (e.g. two different machines) belong to the same person via
signed link events on a dedicated identity channel (`vesta/identity/{groupId}`), independent of
which todo list they're editing. See `PLANNING.md` → "Cross-Device Identity (Device Groups)".

| Command | Run on | Effect |
| --- | --- | --- |
| `/pair` | First device | Creates a device group (if none exists yet) and prints a pairing code |
| `/join <code>` | Second device | Announces itself to the group using the pairing code from `/pair` |
| `/link <public-key>` | First device | Vouches for the device that ran `/join`, using the public key it printed |
| `/devices` | Either | Lists every device currently trusted as a member of the group |

Two-terminal walkthrough:

1. Terminal A: `/pair` → copy the printed pairing code.
2. Terminal B: `/join <pairing code>` → copy the public key it prints.
3. Terminal A: `/link <public key from B>`.
4. Either terminal: `/devices` — both devices now appear as trusted members.

The group's `groupId` is cached in `~/.vesta/todo-{credentials-hash}-group.json` so `/link` and
`/devices` work across restarts without re-pairing.

## Commands

| Command | Purpose |
| --- | --- |
| `add <title>` | Add a new item |
| `done <index>` / `undo <index>` | Toggle completion |
| `rename <index> <title>` | Rename an item |
| `remove <index>` / `rm <index>` | Remove an item |
| `list` / `ls` | Show all items |
| `help` / `?` | Show command help |
| `quit` / `exit` / `q` | Exit |
| `/pair`, `/join`, `/link`, `/devices` | Device-group pairing — see above |
