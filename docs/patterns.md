# Patterns

Recipes for things the relay deliberately does not do for you. The relay stores, orders and forwards signed events; it never interprets payloads. Anything that must be *true* (a purchase, a balance, a rule) is therefore enforced by whoever **signs** the events, and every client decides whom to believe.

## One trusted writer, everyone reads

Use this for a shop, a world-state feed, announcements, leaderboards: data that many clients read but only one service may author.

```
players ──(shop.purchase-request)──▶  mygame/shop-requests   (public: anyone may write)
                                              │
                                   shopkeeper service  ← validates against its own database / payment provider
                                              │
players ◀─(shop.purchase-confirmed)──  mygame/shop           (broadcast: only the shopkeeper writes)
```

1. **Give the service its own identity.** The shopkeeper is an ordinary Vesta client with a persistent Ed25519 key. Keep the private key on the server only.
2. **Create a `broadcast` channel as that identity**, before the game ships: `CreateChannelAsync("mygame/shop", "broadcast")` / `createChannel(id, { visibility: "broadcast" })` / `create_channel(id, visibility="broadcast")`. The creator is the channel admin; extra writers go in `initialMembers`. Everyone else can subscribe and fetch, and the relay answers `ACCESS_DENIED` to any other `PUBLISH` (volatile included). See [protocol.md](protocol.md#channel-visibility).
3. **Pin the shopkeeper's public key in the game**, the same way the app owner's key is compiled into `VestaAppConfig`. Reducers then accept an inventory-changing event only if it passes both checks (never just one, and never trust the relay for it):
   - `event.clientId == deriveClientId(shopkeeperPublicKey)`
   - the signature verifies against that key: `EventSigner.VerifyEvent` (C#), `verifyEvent` (TS), `verify_event` (Python).
4. **Players ask, the service decides.** A player publishes a signed request to a public inbox channel. The shopkeeper checks its own source of truth (gold balance, a Stripe webhook for real money, stock) and, only if it holds, publishes a `confirmed` event naming the player's `clientId` and the request's event id. Dedupe on that id: events are idempotent on `event.id`, but your service must not confirm twice.
5. **Derive state, don't store it.** A player's inventory is a fold over the confirmed events from the pinned signer (`EventReducer` / `LwwMap` in [projections.md](projections.md)). Snapshot it like any projection.

```ts
const trusted = (e: VestaEvent) =>
    e.clientId === SHOPKEEPER_CLIENT_ID && verifyEvent(e, SHOPKEEPER_PUBLIC_KEY);

connection.on("event", (msg) => {
    if (msg.channelId === "mygame/shop" && trusted(msg.event)) inventory.apply(msg);
});
```

### Why both the relay rule and the client check

The `broadcast` write rule is spam and quota protection: strangers can't fill the channel and burn your message allowance. The client-side signer check is the actual trust anchor, because it keeps working if a player fails over to a different relay or a relay misbehaves. Two caveats:

- **Create the channel first.** Unless the operator has turned on [owner-only channel creation](server-configuration.md#owner-only-channel-creation) for your app, the relay lets any client create a channel under any app namespace, so a stranger could register `mygame/shop` as their own broadcast channel before you do. Your client-side check still rejects their events, but your service could not claim the name. Create channels at deploy time, and on a relay you do not operate ask the operator to enable owner-only creation.
- **Run production relays with `Protocol:RequireSignedEvents = true` and `Protocol:RequireHelloProof = true`** ([server-configuration.md](server-configuration.md#signature-verification)). Write control relies on publishes being signed; private-channel reads (for example a per-player receipt inbox) rely on the connection having proven its key.

## What the relay will never validate

None of these can be enforced by Vesta, so design for them:

| You might expect the relay to...                    | What actually happens                                                                              |
| --------------------------------------------------- | -------------------------------------------------------------------------------------------------- |
| check a balance before accepting a purchase         | Nothing. Only the signer's service can know the balance; make it the sole authority (above).        |
| make two writes atomic (trade A for B)              | Each event is separate. Use one service-authored event that records the whole exchange.            |
| reject an impossible action (crop grew in 2 s)      | Accepted and relayed. Validate in the reducer, or have an authority service re-check and confirm.   |
| keep players from lying about time                  | `timestamp` is author-claimed (signed, but the author chose it). See below.                        |
| trust `metadata`, `replace` or `volatile`           | These transport hints are **unsigned**; keep anything that matters in `payload`.                   |

### Time

Every delivered event carries `receivedAt`, assigned by the relay when it was stored. For elapsed-time rules (growth timers, cooldowns) on player-authored events, measure against `receivedAt` rather than `timestamp`, and treat a large gap between the two as a backdated event. `receivedAt` is not signed, so it is only as trustworthy as the relay you are connected to; for money, let the authority service timestamp its own confirmations.

## If the developer goes away

Relays are interchangeable but authority is not: the shopkeeper's signed events are the record of who owns what, and the service that signs them is run by the developer. Plan for that:

- Have the service publish its full ledger to every relay the game may use, so a new relay can be seeded from it.
- Signed events are self-verifying, so anyone holding the history can check it against the pinned key, but only the key holder can add to it. If the service and its key are gone, nobody can issue new confirmations: players keep the game and their device-local state and start a new ledger, but they are not locked to a dead relay.

See also: [events.md](events.md) (signing and unsigned fields), [projections.md](projections.md), [protocol.md](protocol.md#hello-key-possession-proof).
