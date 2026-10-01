# Vesta — Planning

Compact working reference: what Vesta is, the decisions that must not be re-litigated, what is
done/open, how to release, and the quirks that bite when switching machines. Full historical
rationale (storage comparison, device-group phases, every TODO row, original milestones) is
archived at [docs/archive/PLANNING-2026-10-01.md](docs/archive/PLANNING-2026-10-01.md); user-facing
how-to lives in [docs/](docs/README.md). Code is the source of truth when docs disagree.

## Vision

A protocol + runtime for networked apps where **the relay is like a git remote or torrent
tracker, not the authority**. Clients own their data (local SQLite log, self-sovereign Ed25519
identity); any relay can be swapped for another and the app runs unchanged.

## Core principles (non-negotiable)

1. **Data belongs to the client.** The server stores and forwards signed, immutable events; it
   never interprets payloads (business logic is client-side).
2. **Server is a relay, not an authority.** It assigns per-channel sequence numbers and enforces
   operator policy (ACL, quotas) — nothing else.
3. **Protocol over platform.** Three reference SDKs (C#, TS, Python) are peers; wire format is the
   contract.
4. **Offline-first.** Local cache + outbox; reconnect catch-up by sequence.
5. **Explicit conflict resolution.** SDK provides LWW/append-only primitives; apps choose.
6. **Endpoint fungibility.** Swap the relay URL (Atrium-managed ↔ self-hosted) and the app runs
   unchanged. This is the parity test for every feature.

## Boundaries

- **This repo is public and ships artifacts; it operates nothing.** No cloud infra, no deploy
  workflows, no cloud creds, and it never references Atrium internals.
- **Atrium** (`vesta_atrium`, proprietary, sibling folder) is the managed-cloud control plane. It
  consumes `VestaProtocol.Core` from NuGet and the relay image from GHCR, and talks to the relay
  only via `/admin/*`. Changing `VestaCore.Identity.VestaIdentity`, `VestaCore.Utilities.Base64Url`,
  `VestaCore.Channels.AppId`, the `/admin/*` JSON, or the image contract (port 8080, non-root,
  `ConnectionStrings__Vesta`, `/health`, `/health/ready`) is a **cross-repo breaking change**.
- Relays are for **operators only**; app developers connect to one, never deploy one.

## Architecture

```
src/VestaCore     shared types, protocol messages, signing, projections, relay manifests
src/VestaServer   ASP.NET Core relay: WS /ws, /admin/*, /federation/*, /health*; Postgres
src/VestaClient   C# SDK (SQLite cache + outbox, relay directory, recovery picker)
clients/vesta-client-ts | vesta-client-py   peer SDKs (same behaviour, same tests)
examples/*        real apps proving the SDK (durable identity, local cache, offline, projection)
tests/*           xUnit; server integration tests use Testcontainers Postgres
```

- **Events:** `VestaEvent` (client-authored, signed) vs `SequencedEvent` (wraps it + server
  `sequence`/`receivedAt`). Composition, never inheritance.
- **Storage split:** raw Npgsql for the events hot path (append, range read, LISTEN/NOTIFY);
  EF Core for metadata (`channels`, `channel_access`, `apps`). Client side: SQLite. Export: JSON
  Lines. `IEventStore` is the abstraction (InMemory + Npgsql server-side, SQLite client-side).
- **Ordering:** server sequence = authoritative total order per channel (there is no separate
  cursor). Client wall-clock timestamp = LWW tiebreaker. Event ids are UUID v7.

## Protocol decisions

- Messages: HELLO/WELCOME, PUBLISH/EVENT/ACK, SUBSCRIBE, FETCH/EVENTS_BATCH, ERROR,
  CREATE_CHANNEL, DELETE_CHANNEL (admin), REGISTER_APP. Details: [docs/protocol.md](docs/protocol.md).
- **Signing:** RFC 8785 JCS canonical JSON + Ed25519 over every client-authored field except
  `signature`; `sequence`, `receivedAt` and `metadata` are never signed. Libraries:
  `JsonCanonicalization` (C#), `canonicalize` (TS), `json-canonicalization` (Py).
- **Identity:** `clientId = base64url(sha256(publicKey))[:22]`. HELLO carries the public key;
  server verifies it derives to the announced `clientId`; PUBLISH must match the connection's
  `clientId`; signed events are verified. `Protocol:RequireSignedEvents=true` makes it strict.
- **Channels:** slug `[a-z0-9][a-z0-9\-/]*[a-z0-9]`, max 128; created implicitly in open mode;
  ACL modes open/private/invite via `CREATE_CHANNEL`. Soft-delete via `deleted_at`, hard-delete
  by `ChannelDeletionPrunerService` after a grace period.
- **Apps:** `AppId` = one slug segment, max 64. `REGISTER_APP` + `Protocol:RequireAppRegistration`
  gates everything per app (`UNKNOWN_APP`). Six quotas in `AppQuotas` enforced hot-path +
  `AppQuotaPrunerService`; errors `QUOTA_EXCEEDED` / `RATE_LIMITED`.
- **Metadata flags** (unsigned, in `metadata`): `ttlSeconds` (expires_at, excluded from
  catch-up, swept by `ExpiredEventCleanupService`), `volatile` (relayed, never stored),
  `replace`. See [docs/events.md](docs/events.md).
- **Reserved namespace:** `vesta/*` channels only accept `vesta.*` event types; `CREATE_CHANNEL`
  on them is rejected. Used by device groups (`vesta/identity/{groupId}`) and relay manifests
  (`{appId}/vesta/relays`).
- **Managed vs self-hosted parity:** limit errors must be classified identically everywhere
  (`VestaErrorCodes` / `classifyErrorCode` / `classify_error_code`; `OnLimited` / `limited` /
  `on_limited`; dead-lettering of permanently rejected outbox events).

## SDK primitives

- Projections (`VestaCore.Projections` + TS/Py mirrors): `EventReducer<T>`, `AppendOnlyLog<T>`,
  `LwwRegister<T>`, `LwwMap<K,V>`, `ProjectionCheckpoint`, snapshots via `IProjectionStore`.
  `Apply(SequencedEvent)` advances `LastSequence`; `ApplyLocal(VestaEvent)` does not.
  [docs/projections.md](docs/projections.md).
- Device groups (Phase 1 only): `IdentityLinkBuilder`, `DeviceGroupProjection`, `PairingPayload`,
  `VestaConnection.{CreateDeviceGroup,LinkDevice,JoinDeviceGroup,GetDeviceGroupMembers}Async`.
  Peer-equality trust; private keys never move. Phases 2–6 (revocation, rotation, cross-app,
  recovery, delegation) deferred; **server-mediated key transfer is rejected** (see archive).
- Relay independence (#20): `VestaConnection` **requires** `VestaAppConfig { AppId,
  OwnerPublicKey, DefaultRelays }`; `RelayDirectory.CreateDefault` caches under
  `~/.vesta/relays/`; owner-signed `RelayManifest` (`vesta.relay-manifest` events) adopted only
  for newer `version`; order = user override > manifest/escape fallbacks > app defaults.
  Default-on in C# only; TS/Py attach the directory explicitly (`vesta-client/node` subpath).
- Federation (#21): gossip pull over `/federation/{descriptor,peers,apps/{appId}}`, self-signed
  `ServerDescriptor`; dual opt-in (`Discovery:Enabled` + per-app `discoverable` column, never
  parsed from payloads). Discovered relays are **show-only**; `FederationClient` drops any whose
  `ownerClientId` mismatches the compiled-in owner key.
- Recovery (#24): `RelayRecoverySession` (headless state machine) after `RelayExhaustionPasses`
  (3) failed passes; console/web/loopback pickers in all SDKs; **never auto-adopts**.
  [docs/relay-recovery.md](docs/relay-recovery.md).

## Status

**Done:** TTL/metadata, projections (+ snapshots), ACL, signature verification, app registration
+ quotas, outbox/idempotency audit, channel delete + pruner, `/admin/*` HTTP API + GUI (challenge
→ Ed25519 sign → bearer; `Admin:BootstrapPublicKeys`), device groups Phase 1, relay
independence, federation, relay recovery, SDK publishing, relay container image, CI.

**Open:** #13 client READMEs (TS/Py getting-started), #14 observability (metrics, structured
logging), #15 multi-server (scale-out sticky sessions / shared rate-limit + token store).
**Deferred:** device-group phases 2–6; relay Layer C (re-seed empty relay from client logs);
owner-only-write hardening on `{appId}/vesta/relays`.

## Releasing

- **Shared version lives in 3 manifests**: `Directory.Build.props` (`VersionPrefix`),
  `clients/vesta-client-ts/package.json`, `clients/vesta-client-py/vesta_client/__init__.py`
  (`pyproject.toml` reads it dynamically). Current: **0.1.6**.
- Bump all three → `powershell -File .github/scripts/check-version.ps1 -Tag vX.Y.Z` →
  `git tag vX.Y.Z && git push origin vX.Y.Z`. `release.yml` verifies, publishes NuGet
  (`VestaProtocol.Core`, `VestaProtocol.Client`), npm + PyPI (`vesta-client`) via OIDC trusted
  publishing (environment `release`, repo var `NUGET_USER`), then a GitHub Release. Idempotent —
  safe to re-run. npm publish is **staged**; approve on npmjs.com.
- **Relay image** is a separate tag line: `server-vX.Y.Z` → `release-server.yml` →
  `ghcr.io/jesperandersson89/vesta-server:X.Y.Z` (+`:latest`); `main` pushes get `:sha-<short>`.
- Only `VestaCore`/`VestaClient` pack (`IsPackable=false` default). Examples pin the published
  packages (C# `VestaProtocol.Client`, TS `^X.Y.Z` — regenerate lockfiles, Py `>=X.Y.Z`); bump
  them *after* the release is live (Dependabot or manual). `examples.yml` can be re-dispatched
  with `sdk_version`.
- Atrium pins `VestaProtocol.Core` separately — bump it there after each release.

## Conventions

net10.0, nullable, implicit usings, **no `var`**, file-scoped namespaces, no `#region`, records
for immutable data, switch expressions, `Async` suffix, tests `Method_Scenario_Expected`.
Port client-visible behaviour to **all three SDKs** (say so explicitly if C#-only). Sweep
examples when `VestaCore`/`VestaClient` surfaces change. Never hand-write EF migrations
(`dotnet ef migrations add` → both `.cs` + `.Designer.cs`). Update `docs/*.md` in the same
change (table in `.github/copilot-instructions.md`). Stop and ask on environment problems.

## Quirks & gotchas

- Shell is **Windows PowerShell 5.1**: chain with `;` not `&&`; `pwsh` is not installed — use
  `powershell -File` for the `.ps1` scripts.
- Python dev interpreter: `.venv` at the repo root. Run `python -m unittest discover -s
  clients/vesta-client-py/tests`. TS: `npm test` in `clients/vesta-client-ts`.
- Expected test totals (all green): C# 180 Core + 112 Client + 123 Server; TS 101; Py 111.
- Pre-existing build warnings, ignore: NU1902 `Microsoft.Build.Tasks.Git` 8.0.0, NU1903 `SSH.NET`
  2025.1.0 (VestaServer.Tests).
- C# relay picker opens a loopback web page; tests disable it via
  `VestaConnection.RelayPickerEnabled = false` (`TestEnvironment.cs`). `WebRelayPickerTests`
  inject an offline `FederationClient` to avoid a 10 s DNS-timeout flake.
- Python `sign_event()` enforces `event.client_id == identity.client_id`; TS does not — intentional.
- Python connection tests patch `vesta_client.connection.websockets.connect` with a `FakeSocket`
  (`asyncio.Queue`) + `wait_until` helper.
- Server integration tests need Docker (Testcontainers Postgres). Relay needs Postgres
  LISTEN/NOTIFY — never swap the store for Azure SQL; Azure Flexible Server caps at major 16.
- After a release, NuGet/npm indexing lags minutes: `dotnet restore --no-cache`; an
  `examples.yml` run right after may fail `ETARGET` — re-dispatch.
- `docker-compose.yml` is dev-only (open mode, host port 5150). Operator keypair:
  `dotnet run .github/scripts/new-relay-keypair.cs`.
- Archived PLANNING table rows exceed 2000 chars — `grep`/`read_file` truncate them; read with
  `Get-Content` + `Substring` if you need the tail.
