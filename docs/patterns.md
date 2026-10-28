# Patterns

Recipes for things the relay deliberately does not do for you. The relay stores, orders and forwards signed events; it never interprets payloads. Anything that must be *true* (a booking, a balance, a rule) is therefore enforced by whoever **signs** the events, and every client decides whom to believe.

## Which recipe for which app

Most apps are one of a handful of shapes. Find yours, then read the sections it points to.

| You are building…                                                           | Shape                                                   | Use                                                                                                                                  |
| --------------------------------------------------------------------------- | ------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| Group chat, comments, a guestbook, an activity feed                          | Many writers, append-only                               | One channel per room, `AppendOnlyLog` ([projections.md](projections.md)); `volatile` for typing indicators                            |
| A shared list, board or notes that must work offline and merge              | Many writers, per-item last-writer-wins                 | `LwwMap` keyed by item id, snapshot it ([projections.md](projections.md#snapshotting)); optimistic `ApplyLocal`                      |
| Your own notes/settings synced across your phone and laptop                 | One person, several devices                             | Device groups ([identity.md](identity.md#pair-a-second-device-early)); key state by group, not by `clientId`                          |
| A club roster, rota, noticeboard, event sign-up, neighbourhood sharing      | Many readers, a few trusted writers                     | [One trusted writer, everyone reads](#one-trusted-writer-everyone-reads) with a `broadcast` channel; `private` for members-only data |
| Hobby automation / IFTTT-style glue (a sensor publishes, a dashboard reacts) | One writer per device, latest value matters             | `replace: true` per reading type, `ttlSeconds` so a dead sensor disappears, `LwwMap` on the dashboard ([events.md](events.md))       |
| "Who's online", live cursors, status lights                                 | Throwaway signals                                       | [Durable state and live signals: separate channels](#durable-state-and-live-signals-separate-channels)                              |
| A turn-based game, play-by-post, a shared playlist queue                    | Ordered moves, everyone writes in turn                  | One channel per match; the server sequence *is* the turn order; validate moves in the reducer                                        |
| Anything involving money, scarce items or rules that must hold              | Request / confirm                                       | [One trusted writer](#one-trusted-writer-everyone-reads) plus [What the relay will never validate](#what-the-relay-will-never-validate) |
| An app you may stop maintaining one day                                      | Everything above                                        | [If you stop maintaining the app](#if-you-stop-maintaining-the-app)                                                                  |

## One trusted writer, everyone reads

Use this for announcements, a roster or schedule, a booking ledger, confirmations of any kind: data that many clients read but only one service (or a few people) may author. The running example is a neighbourhood tool library: anyone may *ask* to borrow a drill, but only the librarian decides.

```
members ──(toolshed.borrow-request)──▶  toolshed/requests   (public: anyone may write)
                                                │
                                      librarian service  ← checks its own records: is the tool in, is the deposit paid
                                                │
members ◀─(toolshed.borrow-confirmed)──  toolshed/ledger    (broadcast: only the librarian writes)
```

The same shape fits a shop (request a purchase, confirm against a payment provider), a help-desk (open a ticket, an agent answers), a sign-up sheet (request a slot, the organiser confirms) or a moderated noticeboard.

1. **Give the service its own identity.** The librarian is an ordinary Vesta client with a persistent Ed25519 key. Keep the private key on the machine that runs the service only. It can be a tiny always-on script on a Raspberry Pi, a cron job, or a person's laptop running your app in "librarian mode"; the relay does not care.
2. **Create a `broadcast` channel as that identity**, before the app ships: `CreateChannelAsync("toolshed/ledger", "broadcast")` / `createChannel(id, { visibility: "broadcast" })` / `create_channel(id, visibility="broadcast")`. The creator is the channel admin; extra writers (co-librarians) go in `initialMembers`. Everyone else can subscribe and fetch, and the relay answers `ACCESS_DENIED` to any other `PUBLISH` (volatile included). See [protocol.md](protocol.md#channel-visibility).
3. **Pin the librarian's public key in the app**, the same way the app owner's key is compiled into `VestaAppConfig`. Reducers then accept a ledger event only if it passes both checks (never just one, and never trust the relay for it):
   - `event.clientId == deriveClientId(librarianPublicKey)`
   - the signature verifies against that key: `EventSigner.VerifyEvent` (C#), `verifyEvent` (TS), `verify_event` (Python).
4. **Members ask, the service decides.** A member publishes a signed request to a public inbox channel. The librarian checks its own source of truth (is the tool on the shelf, is the deposit paid, a payment webhook for real money) and, only if it holds, publishes a `confirmed` event naming the member's `clientId` and the request's event id. Dedupe on that id: events are idempotent on `event.id`, but your service must not confirm twice.
5. **Derive state, don't store it.** "Who has which tool" is a fold over the confirmed events from the pinned signer (`EventReducer` / `LwwMap` in [projections.md](projections.md)). Snapshot it like any projection.

```ts
const trusted = (e: VestaEvent) =>
    e.clientId === LIBRARIAN_CLIENT_ID && verifyEvent(e, LIBRARIAN_PUBLIC_KEY);

connection.on("event", (msg) => {
    if (msg.channelId === "toolshed/ledger" && trusted(msg.event)) ledger.apply(msg);
});
```

### Why both the relay rule and the client check

The `broadcast` write rule is spam and quota protection: strangers can't fill the channel and burn your message allowance. The client-side signer check is the actual trust anchor, because it keeps working if a user fails over to a different relay or a relay misbehaves. Two caveats:

- **Create the channel first.** Unless the operator has turned on [owner-only channel creation](server-configuration.md#owner-only-channel-creation) for your app, the relay lets any client create a channel under any app namespace, so a stranger could register `toolshed/ledger` as their own broadcast channel before you do. Your client-side check still rejects their events, but your service could not claim the name. Create channels at deploy time, and on a relay you do not operate ask the operator to enable owner-only creation.
- **Run production relays with `Protocol:RequireSignedEvents = true` and `Protocol:RequireHelloProof = true`** ([server-configuration.md](server-configuration.md#signature-verification)). Write control relies on publishes being signed; private-channel reads (for example a per-member receipt inbox) rely on the connection having proven its key.

## Durable state and live signals: separate channels

Most apps send two very different kinds of traffic. Keep them on different channels, because catch-up, storage and snapshots all work per channel.

| Kind         | Examples                                                        | How to publish                                                                                                                       | Channel (example)                 |
| ------------ | --------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------ | --------------------------------- |
| **Durable**  | list items, bookings, messages, document edits, the ledger      | Ordinary events: stored, ordered, replayed to late joiners                                                                           | `toolshed/ledger`, `club/notices` |
| **Live**     | "typing", live cursors, "online now", a sensor's current value  | [`volatile: true`](events.md#volatile) for streams nobody needs later, [`replace: true`](events.md#replace) for "last known value", [`metadata.ttlSeconds`](events.md#metadatattlseconds--ephemeral-events) for "online now" | `club/room-7/presence`            |

Why not one channel:

- **Catch-up replays everything stored since the client's last position.** A client with no local position (new device, cleared storage) starts at 0. Live signals stored in the durable channel make every join fetch them all; on their own channel they stay tiny or are never stored.
- **Storage and the monthly message allowance count stored events.** Volatile events are neither stored nor counted toward `max_messages_per_month` (they still draw from the publish rate limit, or from `volatile_rate_per_minute` when the operator set one). A `replace` stream keeps one row per `(channel, client, eventType)`, but every publish still counts as a message.
- **Access rules differ.** The durable channel is often `broadcast` (one trusted writer, see above); presence is writable by every user.

### Snapshotting

- **Snapshot the durable projections.** Restore, then fetch only what is newer: `store.RestoreAsync(...)`, then `FetchAsync(channel, fromSequence: projection.LastSequence + 1)` (see [projections.md](projections.md#snapshotting)). Save at shutdown and every few hundred events. Use `FetchAll` / `fetchAll` / `fetch_all` for a gap longer than the relay's per-page limit (`Protocol:MaxFetchLimit`).
- **Do not snapshot live signals.** Start empty and let the next updates or heartbeats fill it in; a restored cursor position from last week is wrong anyway.
- **A snapshot only helps a returning client.** The relay prunes old events when the operator sets `retention_days` (one window for every channel in the app) or `max_events_per_channel` (a cap on each channel), so a first-time join, or a client offline longer than the retention window, cannot rebuild from history. For state that must outlive that, have the trusted writer publish a periodic checkpoint event carrying the full state, signed by the pinned key and republished more often than the retention window; new clients start from the latest checkpoint and verify it like any other event.

## What the relay will never validate

None of these can be enforced by Vesta, so design for them:

| You might expect the relay to...                    | What actually happens                                                                              |
| --------------------------------------------------- | -------------------------------------------------------------------------------------------------- |
| check a balance or availability before accepting a booking | Nothing. Only the signer's service can know; make it the sole authority (above).            |
| make two writes atomic (swap slot A for slot B)     | Each event is separate. Use one service-authored event that records the whole exchange.            |
| reject an impossible action (a task completed before it was created) | Accepted and relayed. Validate in the reducer, or have a trusted writer re-check and confirm. |
| keep clients from lying about time                  | `timestamp` is author-claimed (signed, but the author chose it). See below.                        |
| trust `metadata`, `replace` or `volatile`           | These transport hints are **unsigned**; keep anything that matters in `payload`.                   |
| hide payloads from the operator                     | Payloads are stored in the clear today; per-channel encryption is planned ([PLANNING.md](../PLANNING.md)). Until then, do not put secrets in events. |

### Time

Every delivered event carries `receivedAt`, assigned by the relay when it was stored. For elapsed-time rules (cooldowns, "edited within 5 minutes", a borrow that is overdue) on user-authored events, measure against `receivedAt` rather than `timestamp`, and treat a large gap between the two as a backdated event. `receivedAt` is not signed, so it is only as trustworthy as the relay you are connected to; for money, let the trusted writer timestamp its own confirmations.

## If you stop maintaining the app

Relays are interchangeable but authority is not: the librarian's signed events are the record of who has what, and the service that signs them is run by you. Plan for the day you stop:

- Have the service publish its full ledger to every relay the app may use, so a new relay can be seeded from it.
- Signed events are self-verifying, so anyone holding the history can check it against the pinned key, but only the key holder can add to it. If the service and its key are gone, nobody can issue new confirmations: users keep the app and their device-local state and start a new ledger (with a new trusted writer, if someone steps up), but they are not locked to a dead relay.
- Users who log in with a username and password through your [account server](identity.md#developer-hosted-accounts) should be able to download their identity blob while you still run it; see [identity.md](identity.md#if-you-stop-running-it).

See also: [events.md](events.md) (signing and unsigned fields), [projections.md](projections.md), [protocol.md](protocol.md#hello-key-possession-proof).
