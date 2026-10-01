"""
Relay namespace tests: override parsing, channel remapping, register-on-connect and the
probationary adoption. Mirrors tests/VestaClient.Tests/RelayNamespaceTests.cs and
clients/vesta-client-ts/tests/relay-namespace.test.mjs.
"""

from __future__ import annotations

import asyncio
import json
import unittest
from datetime import datetime, timezone
from typing import Any
from unittest import mock

import websockets
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey

from vesta_client import (
    InMemoryRelayOverrideStore,
    RelayAdoptResult,
    RelayDirectory,
    RelayOverride,
    VestaAppConfig,
    VestaConnection,
    VestaIdentity,
    parse_relay_override,
    set_relay_picker_enabled,
)
from vesta_client.connection import remap_channel
from vesta_client.identity import b64url_decode
from vesta_client.signing import build_signing_input
from vesta_client.types import VestaEvent

set_relay_picker_enabled(False)

NIL = "00000000-0000-0000-0000-000000000000"
CONFIG = VestaAppConfig("chess", "unused", ["wss://home.example/ws"])


async def wait_until(predicate, timeout: float = 2.0, interval: float = 0.01) -> None:
    loop = asyncio.get_event_loop()
    deadline = loop.time() + timeout
    while not predicate():
        if loop.time() > deadline:
            raise AssertionError("condition not met in time")
        await asyncio.sleep(interval)


class FakeSocket:
    """Mimics the subset of a websockets ClientConnection that VestaConnection uses."""

    def __init__(self) -> None:
        self.sent: list[dict[str, Any]] = []
        self._inbox: asyncio.Queue = asyncio.Queue()
        self._closed = False

    async def send(self, data: str) -> None:
        self.sent.append(json.loads(data))

    async def recv(self) -> str:
        item = await self._inbox.get()
        if item is None:
            raise websockets.ConnectionClosedOK(None, None)
        return item

    def __aiter__(self) -> "FakeSocket":
        return self

    async def __anext__(self) -> str:
        item = await self._inbox.get()
        if item is None:
            raise websockets.ConnectionClosedOK(None, None)
        return item

    async def close(self, code: int = 1000, reason: str = "") -> None:
        self._closed = True
        await self._inbox.put(None)

    def inject(self, msg: dict[str, Any]) -> None:
        self._inbox.put_nowait(json.dumps(msg))


def welcome(channels: list[str]) -> dict[str, Any]:
    return {
        "type": "WELCOME",
        "serverId": "srv",
        "channels": channels,
        "serverTime": datetime.now(timezone.utc).isoformat(),
    }


def make_connection(override: RelayOverride | None, **extra: Any):
    store = InMemoryRelayOverrideStore()
    if override is not None:
        store.set_override(override)
    directory = RelayDirectory(CONFIG, store)
    sockets: list[FakeSocket] = []

    async def fake_connect(_url: str, **_kwargs: Any) -> FakeSocket:
        socket = FakeSocket()
        sockets.append(socket)
        return socket

    relays = [override.relay] if override is not None else list(CONFIG.default_relays)
    client_id = extra.pop("client_id", "test-client")
    conn = VestaConnection(
        relays[0],
        client_id,
        ["chess/room"],
        relays=relays,
        auto_reconnect=False,
        relay_directory=directory,
        **extra,
    )
    conn.on_error(lambda *_: None)
    return conn, sockets, store, directory, fake_connect


class ParseRelayOverrideTests(unittest.TestCase):
    def test_reads_the_object_form(self) -> None:
        parsed = parse_relay_override('{"relay":"wss://a/ws","appId":"chess-2","registerApp":true}')
        self.assertEqual(parsed, RelayOverride("wss://a/ws", "chess-2", True))

    def test_accepts_legacy_plain_url_and_url_forms(self) -> None:
        self.assertEqual(parse_relay_override("wss://a/ws\n"), RelayOverride("wss://a/ws"))
        self.assertEqual(
            parse_relay_override('{"url":"wss://a/ws"}'),
            RelayOverride("wss://a/ws", None, False),
        )

    def test_rejects_empty_and_malformed_input(self) -> None:
        self.assertIsNone(parse_relay_override(""))
        self.assertIsNone(parse_relay_override("{ not json"))
        self.assertIsNone(parse_relay_override('{"appId":"x"}'))
        self.assertIsNone(parse_relay_override(42))


class RemapChannelTests(unittest.TestCase):
    def test_rewrites_only_the_namespace_segment(self) -> None:
        self.assertEqual(remap_channel("chess/room", "chess", "chess-2"), "chess-2/room")
        self.assertEqual(remap_channel("chess/a/b", "chess", "chess-2"), "chess-2/a/b")
        self.assertEqual(remap_channel("chess", "chess", "chess-2"), "chess-2")
        self.assertEqual(remap_channel("chessboard/room", "chess", "chess-2"), "chessboard/room")

    def test_leaves_protocol_channels_and_identical_or_missing_namespaces_alone(self) -> None:
        self.assertEqual(remap_channel("vesta/relay-manifest", "vesta", "x"), "vesta/relay-manifest")
        self.assertEqual(remap_channel("chess/room", "chess", "chess"), "chess/room")
        self.assertEqual(remap_channel("chess/room", None, "x"), "chess/room")
        self.assertEqual(remap_channel("chess/room", "chess", None), "chess/room")


class RelayNamespaceWireTests(unittest.IsolatedAsyncioTestCase):
    async def test_override_namespace_remaps_hello_inbound_events_and_publish(self) -> None:
        identity = VestaIdentity.generate()
        conn, sockets, _store, _directory, fake_connect = make_connection(
            RelayOverride("wss://alt.example/ws", "chess-2"),
            identity=identity,
            client_id=identity.client_id,
        )
        with mock.patch("vesta_client.connection.websockets.connect", side_effect=fake_connect):
            task = asyncio.create_task(conn.connect())
            await wait_until(lambda: len(sockets) >= 1)
            socket = sockets[0]
            self.assertEqual(socket.sent[0]["channels"], ["chess-2/room"])

            socket.inject(welcome(["chess-2/room"]))
            await task
            self.assertIn("chess/room", conn.channels)
            self.assertFalse(any(c.startswith("chess-2/") for c in conn.channels))

            received: list[Any] = []
            conn.on_event(lambda m: received.append(m))
            socket.inject(
                {
                    "type": "EVENT",
                    "channelId": "chess-2/room",
                    "sequence": 1,
                    "receivedAt": datetime.now(timezone.utc).isoformat(),
                    "event": {
                        "id": "e1",
                        "channelId": "chess-2/room",
                        "timestamp": "2026-01-01T00:00:00.000Z",
                        "clientId": "peer",
                        "eventType": "t",
                        "payload": {},
                    },
                }
            )
            await wait_until(lambda: len(received) >= 1)
            self.assertEqual(received[0].channel_id, "chess/room")
            self.assertEqual(received[0].event.channel_id, "chess/room")

            await conn.publish(
                VestaEvent(
                    id="e2",
                    channel_id="chess/room",
                    timestamp="2026-01-01T00:00:00.000Z",
                    client_id=identity.client_id,
                    event_type="t",
                    payload={"a": 1},
                    signature="stale",
                )
            )
            publish = next(m for m in socket.sent if m["type"] == "PUBLISH")
            self.assertEqual(publish["channelId"], "chess-2/room")
            self.assertEqual(publish["event"]["channelId"], "chess-2/room")
            self.assertNotEqual(publish["event"]["signature"], "stale")

            signing_input = build_signing_input(VestaEvent.from_dict(publish["event"]))
            public_key = Ed25519PublicKey.from_public_bytes(identity.public_key)
            public_key.verify(b64url_decode(publish["event"]["signature"]), signing_input)

            await conn.dispose()

    async def test_namespace_only_applies_to_overridden_relay(self) -> None:
        conn, sockets, _store, _directory, fake_connect = make_connection(
            RelayOverride("wss://alt.example/ws", "chess-2")
        )
        with mock.patch("vesta_client.connection.websockets.connect", side_effect=fake_connect):
            conn.update_relay_candidates(["wss://home.example/ws"])
            task = asyncio.create_task(conn.connect())
            await wait_until(lambda: len(sockets) >= 1)
            sockets[0].inject(welcome(["chess/room"]))
            await task
            self.assertEqual(sockets[0].sent[0]["channels"], ["chess/room"])
            await conn.dispose()


class RegisterOnConnectTests(unittest.IsolatedAsyncioTestCase):
    async def test_register_app_greets_with_no_channels_registers_then_subscribes(self) -> None:
        conn, sockets, _store, _directory, fake_connect = make_connection(
            RelayOverride("wss://alt.example/ws", "chess-2", True)
        )
        with mock.patch("vesta_client.connection.websockets.connect", side_effect=fake_connect):
            task = asyncio.create_task(conn.connect())
            await wait_until(lambda: len(sockets) >= 1)
            socket = sockets[0]
            self.assertEqual(socket.sent[0]["type"], "HELLO")
            self.assertEqual(socket.sent[0]["channels"], [])

            socket.inject(welcome([]))
            await task

            await wait_until(lambda: any(m["type"] == "REGISTER_APP" for m in socket.sent))
            register = next(m for m in socket.sent if m["type"] == "REGISTER_APP")
            self.assertEqual(register["appId"], "chess-2")
            self.assertFalse(any(m["type"] == "SUBSCRIBE" for m in socket.sent))

            socket.inject({"type": "ACK", "eventId": NIL, "channelId": "chess-2", "sequence": 0})
            await wait_until(
                lambda: any(
                    m["type"] == "SUBSCRIBE" and m.get("channelId") == "chess-2/room" for m in socket.sent
                )
            )
            await conn.dispose()

    async def test_duplicate_app_during_registration_counts_as_registered(self) -> None:
        conn, sockets, _store, _directory, fake_connect = make_connection(
            RelayOverride("wss://alt.example/ws", "chess-2", True)
        )
        with mock.patch("vesta_client.connection.websockets.connect", side_effect=fake_connect):
            task = asyncio.create_task(conn.connect())
            await wait_until(lambda: len(sockets) >= 1)
            socket = sockets[0]
            socket.inject(welcome([]))
            await task
            await wait_until(lambda: any(m["type"] == "REGISTER_APP" for m in socket.sent))

            socket.inject({"type": "ERROR", "code": "DUPLICATE_APP", "message": "exists"})
            await wait_until(
                lambda: any(
                    m["type"] == "SUBSCRIBE" and m.get("channelId") == "chess-2/room" for m in socket.sent
                )
            )
            await conn.dispose()

    async def test_failed_registration_never_subscribes_and_surfaces_an_error(self) -> None:
        conn, sockets, _store, _directory, fake_connect = make_connection(
            RelayOverride("wss://alt.example/ws", "chess-2", True)
        )
        errors: list[Any] = []
        conn.on_error(lambda e: errors.append(e))
        with mock.patch("vesta_client.connection.websockets.connect", side_effect=fake_connect):
            task = asyncio.create_task(conn.connect())
            await wait_until(lambda: len(sockets) >= 1)
            socket = sockets[0]
            socket.inject(welcome([]))
            await task
            await wait_until(lambda: any(m["type"] == "REGISTER_APP" for m in socket.sent))

            socket.inject({"type": "ERROR", "code": "APP_NOT_ALLOWED", "message": "closed"})
            await wait_until(lambda: len(errors) >= 1)
            self.assertFalse(any(m["type"] == "SUBSCRIBE" for m in socket.sent))
            self.assertEqual(errors[0].code, "REGISTER_APP_FAILED")
            await conn.dispose()


class AdoptRelayTests(unittest.IsolatedAsyncioTestCase):
    async def test_commits_once_namespace_confirmed(self) -> None:
        conn, sockets, store, _directory, fake_connect = make_connection(None)
        with mock.patch("vesta_client.connection.websockets.connect", side_effect=fake_connect):
            task = asyncio.create_task(
                conn.adopt_relay(RelayOverride("wss://alt.example/ws", "chess-2"))
            )
            await wait_until(lambda: len(sockets) >= 1)
            sockets[0].inject(welcome(["chess-2/room"]))

            result = await task
            self.assertEqual(result, RelayAdoptResult(True, None))
            override = store.get_override()
            self.assertIsNotNone(override)
            self.assertEqual(override.relay, "wss://alt.example/ws")
            self.assertEqual(override.app_id, "chess-2")
            await conn.dispose()

    async def test_namespace_error_during_probation_discards_the_override(self) -> None:
        conn, sockets, store, directory, fake_connect = make_connection(None)
        with mock.patch("vesta_client.connection.websockets.connect", side_effect=fake_connect):
            task = asyncio.create_task(
                conn.adopt_relay(RelayOverride("wss://alt.example/ws", "chess-2"))
            )
            await wait_until(lambda: len(sockets) >= 1)
            sockets[0].inject(welcome([]))
            sockets[0].inject(
                {"type": "ERROR", "code": "APP_NOT_ALLOWED", "message": "unregistered apps refused"}
            )

            # The discard triggers a reconnect on the original candidates; fail that attempt.
            await wait_until(lambda: len(sockets) >= 2)
            await sockets[1].close()

            result = await task
            self.assertFalse(result.connected)
            self.assertIsNotNone(result.failure_reason)
            self.assertIn("APP_NOT_ALLOWED", result.failure_reason)
            self.assertIsNone(store.get_override())
            self.assertIsNone(directory.active_override)
            await conn.dispose()

    async def test_unreachable_relay_is_discarded_with_the_connection_reason(self) -> None:
        conn, sockets, store, _directory, fake_connect = make_connection(None)
        with mock.patch("vesta_client.connection.websockets.connect", side_effect=fake_connect):
            task = asyncio.create_task(
                conn.adopt_relay(RelayOverride("wss://alt.example/ws", "chess-2"))
            )
            await wait_until(lambda: len(sockets) >= 1)
            await sockets[0].close()

            result = await task
            self.assertFalse(result.connected)
            self.assertTrue(result.failure_reason)
            self.assertIsNone(store.get_override())
            await conn.dispose()


if __name__ == "__main__":
    unittest.main()
