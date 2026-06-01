# Vesta Chess (Web)

Browser chess example built on the Vesta protocol. Two players see each other in a public **lobby**, send invitations, and play matches over a **private channel** that only the invited opponent (and the inviter) can access — enforced by the server's channel ACL.

## Architecture

- **Identity**: Each browser tab generates a persistent Ed25519 keypair on first launch (stored in `localStorage`). The derived `clientId` is registered with the server, and every event is Ed25519-signed.
- **Lobby channel** `chess/lobby` (public, implicit-create):
  - `app.chess.presence` — volatile heartbeat with `username` (TTL 60 s, re-sent every 25 s).
  - `app.chess.invite` / `invite-accepted` / `invite-declined` — invitation lifecycle.
- **Match channel** `chess/match/{matchId}` (**private**, created by the inviter with `CREATE_CHANNEL` and the invitee as the single initial member):
  - `app.chess.match-started` — exchanged once both sides have joined; carries colour assignment + display names.
  - `app.chess.move` — `{ from, to, promotion, fen }`. Rule validation is local via `chess.js`; FEN is included as a tiebreaker if local state diverges.
  - `app.chess.resign` — match-ending.

The server has no idea this is chess — it just relays signed events and refuses non-members on private match channels.

## Run

You need a Vesta server running on `ws://localhost:5050/ws` (or change it in the footer UI).

```bash
# from the repo root, in a separate shell:
dotnet run --project src/VestaServer

# then in this folder:
cd examples/chess-web
npm install
npm run dev
```

Open the printed Vite URL (default `http://localhost:5173`) in **two** browser windows (or two browsers) to play against yourself, or share it on your network for a real match.

## Notes

- Promotions auto-queen for simplicity.
- The inviter plays white; the invitee plays black.
- Identity is namespaced per origin (one identity per tab profile). Clear `localStorage` to rotate.
