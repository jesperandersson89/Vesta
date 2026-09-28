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

## Relay independence, limits & federation

The footer's relay panel (visible once connected) demonstrates the SDK's relay-independence
story:

- **Failover**: paste a comma-separated relay list into the connect field; the client walks it
  automatically if the active relay drops.
- **Owner-signed manifests**: set `VITE_VESTA_OWNER_PUBLIC_KEY` (see below) to enable manifest
  verification — an accepted manifest republishes the candidate list and shows a banner.
- **User override**: "My relay override" persists a personal relay choice in `localStorage`,
  taking precedence over the manifest and defaults.
- **Federation discovery**: "Discover relays" / "Browse all relays" query the active relay's
  `/federation/*` HTTP surface for other relays hosting this app (or the whole mesh). Every
  result is signature-verified and owner-matched before being shown; adopting one is still a
  manual "Use" click — discovery never auto-fails-over.
- **Limit notices**: a `QUOTA_EXCEEDED` / `RATE_LIMITED` / etc. response from the relay surfaces
  as a red `[LIMITED]` line in the panel via `connection.on("limited", ...)`.

Set these before `npm run build` / `npm run dev` (e.g. in a `.env` file) to exercise manifests
and federation:

```
VITE_VESTA_APP_ID=chess
VITE_VESTA_RELAY_URL=wss://relay.example/ws
VITE_VESTA_OWNER_PUBLIC_KEY=<base64url Ed25519 public key>
```

## Notes

- Promotions auto-queen for simplicity.
- The inviter plays white; the invitee plays black.
- Identity is namespaced per origin (one identity per tab profile). Clear `localStorage` to rotate.
