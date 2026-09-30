# Relay recovery

What an app should do when every relay it knows has stopped answering. Relay recovery is a
**headless state machine** plus thin per-platform views, so the same flow works in a console, a
browser, and (later) native iOS and Android UIs.

- **App developers:** wire a view to `RelayRecoverySession` (a few lines; see below).
- **SDK implementers (Swift / Kotlin ports):** port the session, not a UI. The rules in
  [Trust rules](#trust-rules) are part of the contract.

For how the candidate list itself is built (config, owner-signed manifest, user override) see
[PLANNING.md](../PLANNING.md) #20. For how relays find each other see
[protocol.md](protocol.md#server-to-server-discovery-federation).

## When the prompt appears

With `AutoReconnect` on, the connection walks its candidate relays in order (user override,
manifest relays, compiled-in defaults). A **pass** is one walk over all candidates. After
`RelayExhaustionPasses` consecutive failed passes (default 3) the connection raises one
*relays exhausted* event per outage, carrying the per-relay failure reasons from the last pass.

Background reconnect keeps running while the prompt is open. If a relay comes back, the session
returns to `Healthy` and the view goes away.

A failed **first** connect has no outage to witness: C# `ConnectAsync` throws
`RelaysExhaustedException` and Python `connect()` raises `RelaysExhaustedError`, both carrying the
same info as `.Info` / `.info`. Hand it to `session.ReportExhausted(ex.Info)` /
`session.report_exhausted(err.info)` to open the prompt. TypeScript `connect()` is not async and
has no error class: with `autoReconnect` on, the failure arrives as the `relaysExhausted` event,
which a session created before `connect()` picks up automatically.

## Session API

| Concept  | C# (`VestaClient.Relay`)                   | TypeScript                        | Python                               |
| -------- | ------------------------------------------ | --------------------------------- | ------------------------------------ |
| Session  | `new RelayRecoverySession(connection)`     | `new RelayRecoverySession(conn)`  | `RelayRecoverySession(conn)`         |
| State    | `Snapshot`, `OnChanged`                    | `snapshot`, `onChange(fn)`        | `snapshot`, `on_change(fn)`          |
| Retry    | `RetryAsync()`                             | `retry()`                         | `await retry()`                      |
| Discover | `DiscoverAsync()`                          | `discover()`                      | `await discover()`                   |
| Pick     | `AdoptAsync(RelayChoice)`                  | `adopt(choice)`                   | `await adopt(choice)`                |
| Manual   | `UseManualAsync(url)`                      | `useManual(url)`                  | `await use_manual(url)`              |
| Reset    | `ClearOverrideAsync()`                     | `clearOverride()`                 | `await clear_override()`             |
| Hide     | `Dismiss()`                                | `dismiss()`                       | `dismiss()`                          |
| Teardown | `Dispose()`                                | `dispose()`                       | `dispose()`                          |

Built-in views: `ConsoleRelayPicker.RunAsync(session, input, output)` (C#),
`runConsoleRelayPicker(session, { readLine, write })` and the `<vesta-relay-picker>` web
component (TS, `element.session = session`), `run_console_relay_picker(session, read_line, write)`
(Python, async `read_line`).

### Snapshot

A view renders **only** the snapshot and holds no other state:

| Field               | Meaning                                                                    |
| ------------------- | -------------------------------------------------------------------------- |
| `phase`             | See the state machine below                                                |
| `triedRelays`       | `(relay, reason)` per failed candidate, from the last pass                 |
| `choices`           | `RelayChoice[]`: `url`, `relayPublicKey?`, `hostsRequestedApp`, `fromCache` |
| `activeOverride`    | The user's saved relay, if any (offer "clear")                             |
| `backgroundRetrying`| True while auto-reconnect is still running underneath the prompt           |
| `failedPasses`      | How many full passes failed                                                |
| `adopting`          | The relay being connected to in the `Adopting` phase                       |
| `failureReason`     | Human-readable reason in the `Failed` phase                                |

### State machine

```
Healthy -> Degraded -> Exhausted -> Discovering -> Choices | NothingFound
                                        ^               |
                                        |               v
                                   Failed <---------- Adopting -> Healthy
```

`Degraded` is a normal blip (views stay hidden). `Exhausted` and later phases are the prompt.
`Dismiss()` returns to `Degraded`; the prompt comes back on the next exhaustion.

## Where choices come from

`Discover` queries `/federation/*` ([protocol.md](protocol.md#server-to-server-discovery-federation))
on every base URL it can derive from: the **peer cache**, the app's `DiscoverySeeds`, the
current candidates, and the default relays (at most 16 bases, queried in parallel). Results are
merged with the cache, relays that just failed are dropped, and relays that verifiably advertise
the app under the trusted owner sort first.

### Peer cache

Every successful connect refreshes a per-app cache from the connected relay's `/federation/peers`,
so there is something to ask even when every default relay is dead.

| Platform | Location                                                          |
| -------- | ----------------------------------------------------------------- |
| C#       | `~/.vesta/relays/{appId}.peers.json` (`PeerCacheStore`)           |
| Python   | `~/.vesta/relays/{appId}.peers.json` (`FilePeerCacheStore`)       |
| Node     | `FilePeerCacheStore` from `vesta-client/node`                     |
| Browser  | `localStorage` (`LocalStoragePeerCacheStore`)                     |

`VestaAppConfig.DiscoverySeeds` (`discoverySeeds` / `discovery_seeds`) lists extra relays to ask
on a cold start when nothing is cached.

## Trust rules

These are what a port must preserve:

1. **Never auto-adopt.** A relay is only used after an explicit `Adopt` / `UseManual` call from a
   user action.
2. **Unverified means warn.** A choice with `hostsRequestedApp = false` (or a manually typed URL)
   may not hold the user's data, and a relay the user doesn't trust can see what they send it.
   Views must show that warning and require an explicit confirmation before calling `Adopt`.
3. **The compiled-in owner key stays the trust anchor.** Adopting a relay only changes where the
   client connects. Event signatures and manifests are still verified against the app owner key.
4. **Relay-derived text is untrusted.** Render URLs and failure reasons as plain text.
5. **Adoption is a local override**, persisted per user and clearable with `ClearOverride`. It
   never changes the owner's manifest.

## Managed relays

Relays that require app registration only accept an app that its owner registered. If the app's
developer is gone, only relays that **already host the app**, or **open-mode** relays, will accept
the user. `hostsRequestedApp` in the choice list is the signal; expect "unverified" choices to
fail on managed relays.

## Adding a platform

Native UIs implement the session contract (snapshot + six inputs) and one view. A Swift or
Kotlin port should ship the session and a reference view (SwiftUI sheet / Compose dialog), driven
by the same phases and the same confirmation rule. No server changes are needed.
