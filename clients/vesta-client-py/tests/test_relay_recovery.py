"""
Relay-recovery tests: exhaustion detection on the connection and the headless
RelayRecoverySession state machine.

Mirrors tests/VestaClient.Tests/RelayRecoveryTests.cs and
clients/vesta-client-ts/tests/relay-recovery.test.mjs.
"""

from __future__ import annotations

import asyncio
import os
import tempfile
import unittest
from collections.abc import Callable
from dataclasses import replace
from datetime import datetime, timezone
from unittest import mock

from vesta_client import (
    FilePeerCacheStore,
    InMemoryPeerCacheStore,
    InMemoryRelayOverrideStore,
    RelayAdoptResult,
    RelayAttempt,
    RelayDirectory,
    RelayOverride,
    RelayRecoverySession,
    RelaysExhaustedError,
    RelaysExhaustedInfo,
    VestaAppConfig,
    VestaConnection,
    VestaIdentity,
    normalize_relay_url,
)
from vesta_client.federation import (
    DiscoverableApp,
    DiscoveredRelay,
    FederationClient,
    ServerDescriptor,
    build_descriptor_signing_input,
)
from vesta_client.identity import derive_client_id

DEAD_RELAY = "wss://dead.example/ws"


def _descriptor_dict(owner: VestaIdentity, relay: VestaIdentity, url: str, hosts_app: bool) -> dict:
    apps = [DiscoverableApp("chess", derive_client_id(owner.public_key))] if hosts_app else []
    descriptor = ServerDescriptor(
        relay_public_key=relay.public_key_b64,
        urls=[url],
        apps=apps,
        issued_at=datetime.now(timezone.utc).isoformat(),
        ttl_seconds=300,
    )
    signature = relay.sign_b64(build_descriptor_signing_input(descriptor))
    descriptor = replace(descriptor, signature=signature)
    return {
        "relayPublicKey": descriptor.relay_public_key,
        "urls": descriptor.urls,
        "apps": [{"appId": a.app_id, "ownerClientId": a.owner_client_id} for a in descriptor.apps],
        "issuedAt": descriptor.issued_at,
        "ttlSeconds": descriptor.ttl_seconds,
        "signature": descriptor.signature,
    }


class FakeHost:
    def __init__(self, directory: RelayDirectory, connect_succeeds: bool) -> None:
        self.relay_directory = directory
        self.relays = [DEAD_RELAY]
        self.auto_reconnect = True
        self.adopted: list[str] = []
        self.connect_succeeds = connect_succeeds
        self._listeners: dict[str, list[Callable[..., None]]] = {}

    def add_listener(self, event: str, listener: Callable[..., None]) -> Callable[[], None]:
        self._listeners.setdefault(event, []).append(listener)
        return lambda: self._listeners[event].remove(listener)

    def emit(self, event: str, *args: object) -> None:
        for listener in list(self._listeners.get(event, [])):
            listener(*args)

    async def reconnect(self) -> bool:
        return self.connect_succeeds

    @property
    def active_relay(self) -> str:
        return self.relays[0]

    async def adopt_relay(self, override: RelayOverride) -> RelayAdoptResult:
        self.adopted.append(override.relay)
        return RelayAdoptResult(
            connected=self.connect_succeeds,
            failure_reason=None if self.connect_succeeds else "connection refused",
        )

    async def clear_relay_override(self) -> bool:
        return True


def _exhausted() -> RelaysExhaustedInfo:
    return RelaysExhaustedInfo([RelayAttempt(DEAD_RELAY, "connection refused")], 3)


class RelayRecoveryTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self) -> None:
        self.owner = VestaIdentity.generate()
        self.config = VestaAppConfig("chess", self.owner.public_key_b64, [DEAD_RELAY])

    def _session(
        self,
        peer_cache: InMemoryPeerCacheStore | None = None,
        connect_succeeds: bool = True,
        responses: dict[str, list[dict]] | None = None,
    ) -> tuple[RelayRecoverySession, FakeHost]:
        directory = RelayDirectory(self.config, InMemoryRelayOverrideStore(), None, peer_cache)
        host = FakeHost(directory, connect_succeeds)
        table = responses or {}

        def fetch(url: str) -> list[dict]:
            return table.get(url, [])

        return RelayRecoverySession(host, FederationClient(self.config, fetch=fetch)), host

    def _discovery(self) -> dict[str, list[dict]]:
        hosting = _descriptor_dict(self.owner, VestaIdentity.generate(), "wss://good.example/ws", True)
        other = _descriptor_dict(self.owner, VestaIdentity.generate(), "wss://stranger.example/ws", False)
        return {
            "https://dead.example/federation/apps/chess": [hosting],
            "https://dead.example/federation/peers": [hosting, other],
        }

    def test_file_peer_cache_round_trips_and_survives_corrupt_file(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "sub", "chess.peers.json")
            store = FilePeerCacheStore(path)
            self.assertEqual(store.load(), [])
            peer = DiscoveredRelay("key", ["wss://a.example/ws"], True, "2026-01-01T00:00:00+00:00")
            store.save([peer])
            self.assertEqual(store.load(), [peer])
            with open(path, "w", encoding="utf-8") as f:
                f.write("{not json")
            self.assertEqual(store.load(), [])

    async def test_connection_raises_exhausted_once_after_configured_passes(self) -> None:
        connection = VestaConnection(
            DEAD_RELAY,
            "client",
            [],
            relays=[DEAD_RELAY],
            initial_reconnect_delay=0.001,
            max_reconnect_delay=0.002,
            relay_exhaustion_passes=2,
        )
        seen: list[RelaysExhaustedInfo] = []
        done: asyncio.Future[None] = asyncio.get_running_loop().create_future()

        def on_exhausted(info: RelaysExhaustedInfo) -> None:
            seen.append(info)
            if not done.done():
                done.set_result(None)

        connection.add_listener("relays_exhausted", on_exhausted)
        with mock.patch(
            "vesta_client.connection.websockets.connect", side_effect=OSError("connection refused")
        ):
            with self.assertRaises(RuntimeError):
                await connection.connect()
            connection._schedule_reconnect()
            await asyncio.wait_for(done, timeout=5)
            await asyncio.sleep(0.05)
        await connection.dispose()

        self.assertEqual(len(seen), 1)
        self.assertEqual(seen[0].passes, 2)
        self.assertEqual(seen[0].attempts[0].relay, DEAD_RELAY)
        self.assertEqual(seen[0].attempts[0].reason, "connection refused")

    async def test_session_moves_through_degraded_and_exhausted(self) -> None:
        session, host = self._session()
        host.emit("disconnected", "closed")
        self.assertEqual(session.snapshot.phase, "degraded")
        host.emit("relays_exhausted", _exhausted())
        self.assertEqual(session.snapshot.phase, "exhausted")
        self.assertEqual(session.snapshot.tried_relays[0].relay, DEAD_RELAY)
        self.assertTrue(session.snapshot.background_retrying)

    async def test_connect_failure_raises_typed_error_and_report_exhausted_opens_prompt(self) -> None:
        connection = VestaConnection(DEAD_RELAY, "client", [], relays=[DEAD_RELAY])
        with mock.patch(
            "vesta_client.connection.websockets.connect", side_effect=OSError("connection refused")
        ):
            with self.assertRaises(RelaysExhaustedError) as caught:
                await connection.connect()
        await connection.dispose()
        self.assertEqual(caught.exception.info.attempts[0].reason, "connection refused")

        session, _host = self._session()
        session.report_exhausted(caught.exception.info)
        self.assertEqual(session.snapshot.phase, "exhausted")
        self.assertEqual(session.snapshot.tried_relays[0].relay, DEAD_RELAY)

    async def test_discover_lists_hosting_relay_first_and_never_adopts(self) -> None:
        session, host = self._session(responses=self._discovery())
        host.emit("relays_exhausted", _exhausted())
        await session.discover()
        self.assertEqual(session.snapshot.phase, "choices")
        urls = [c.url for c in session.snapshot.choices]
        self.assertEqual(urls[0], "wss://good.example/ws")
        self.assertIn("wss://stranger.example/ws", urls)
        self.assertTrue(session.snapshot.choices[0].hosts_requested_app)
        self.assertFalse(session.snapshot.choices[-1].hosts_requested_app)
        self.assertEqual(host.adopted, [])

    async def test_discover_with_nothing_reachable_reports_nothing_found(self) -> None:
        session, host = self._session()
        host.emit("relays_exhausted", _exhausted())
        await session.discover()
        self.assertEqual(session.snapshot.phase, "nothing_found")

    async def test_discover_uses_peer_cache_when_live_lookup_fails(self) -> None:
        cache = InMemoryPeerCacheStore()
        cache.save([DiscoveredRelay("key", ["wss://cached.example/ws"], True, "2026-01-01T00:00:00+00:00")])
        session, host = self._session(peer_cache=cache)
        host.emit("relays_exhausted", _exhausted())
        await session.discover()
        self.assertEqual(len(session.snapshot.choices), 1)
        self.assertTrue(session.snapshot.choices[0].from_cache)

    async def test_adopt_success_returns_to_healthy(self) -> None:
        session, host = self._session(responses=self._discovery())
        host.emit("relays_exhausted", _exhausted())
        await session.discover()
        await session.adopt(session.snapshot.choices[0])
        self.assertEqual(session.snapshot.phase, "healthy")
        self.assertEqual(host.adopted, ["wss://good.example/ws"])

    async def test_use_manual_failure_reports_failed_and_keeps_retrying(self) -> None:
        session, host = self._session(connect_succeeds=False)
        host.emit("relays_exhausted", _exhausted())
        await session.use_manual("https://typed.example")
        self.assertEqual(session.snapshot.phase, "failed")
        self.assertEqual(host.adopted, ["wss://typed.example/"])
        self.assertTrue(session.snapshot.background_retrying)

    async def test_use_manual_rejects_invalid_scheme(self) -> None:
        session, host = self._session()
        await session.use_manual("ftp://nope.example")
        self.assertEqual(session.snapshot.phase, "failed")
        self.assertEqual(host.adopted, [])

    async def test_dismiss_returns_to_degraded_and_keeps_retrying(self) -> None:
        session, host = self._session()
        host.emit("relays_exhausted", _exhausted())
        session.dismiss()
        self.assertEqual(session.snapshot.phase, "degraded")
        self.assertTrue(session.snapshot.background_retrying)

    async def test_background_reconnect_returns_to_healthy(self) -> None:
        session, host = self._session()
        host.emit("relays_exhausted", _exhausted())
        host.emit("reconnected", None)
        self.assertEqual(session.snapshot.phase, "healthy")
        self.assertFalse(session.snapshot.background_retrying)

    def test_normalize_relay_url(self) -> None:
        self.assertEqual(normalize_relay_url("https://typed.example"), "wss://typed.example/")
        self.assertEqual(normalize_relay_url("http://r.example:8080/ws"), "ws://r.example:8080/ws")
        self.assertEqual(normalize_relay_url("wss://r.example:443/ws"), "wss://r.example/ws")
        self.assertIsNone(normalize_relay_url("ftp://nope.example"))
        self.assertIsNone(normalize_relay_url("nonsense"))


if __name__ == "__main__":
    unittest.main()
