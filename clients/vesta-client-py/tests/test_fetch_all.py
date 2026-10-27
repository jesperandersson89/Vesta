"""
fetch_all: auto-paging FETCH, against a fake relay that clamps page size like Protocol:MaxFetchLimit.

Mirrors tests/VestaClient.Tests/FetchAllTests.cs and clients/vesta-client-ts/tests/fetch-all.test.mjs.
"""

from __future__ import annotations

import asyncio
import json
import unittest
from typing import Any

from vesta_client import VestaConnection
from vesta_client.types import WelcomeMessage, parse_server_message

CHANNEL = "myapp/world"


def _stored(sequence: int) -> dict[str, Any]:
    return {
        "event": {
            "id": f"evt-{sequence}",
            "channelId": CHANNEL,
            "timestamp": "2026-01-01T00:00:00.000Z",
            "clientId": "someone",
            "eventType": "test.message",
            "payload": {"n": sequence},
        },
        "sequence": sequence,
        "receivedAt": "2026-01-01T00:00:00.000Z",
    }


class _FakeRelaySocket:
    """A stand-in for ``websockets``' client connection that answers FETCH like the relay does."""

    def __init__(self, event_count: int, max_page: int, refuse: bool = False, silent: bool = False) -> None:
        self.fetches: list[dict] = []
        self._incoming: asyncio.Queue[str | None] = asyncio.Queue()
        self._stored = [_stored(i + 1) for i in range(event_count)]
        self._max_page = max_page
        self._refuse = refuse
        self._silent = silent

    async def send(self, data: str) -> None:
        msg = json.loads(data)
        if msg["type"] != "FETCH" or self._silent:
            return
        self.fetches.append(msg)
        if self._refuse:
            self.inject({"type": "ERROR", "code": "ACCESS_DENIED", "message": "Not authorized", "channelId": msg["channelId"]})
            return
        limit = min(max(msg.get("limit") or 100, 1), self._max_page)
        events = [e for e in self._stored if e["sequence"] >= msg["fromSequence"]][:limit]
        to_sequence = msg.get("toSequence")
        events = [e for e in events if to_sequence is None or e["sequence"] <= to_sequence]
        self.inject({"type": "EVENTS_BATCH", "channelId": msg["channelId"], "events": events})

    async def recv(self) -> str:
        msg = await self._incoming.get()
        if msg is None:
            raise StopAsyncIteration
        return msg

    def __aiter__(self) -> "_FakeRelaySocket":
        return self

    async def __anext__(self) -> str:
        msg = await self._incoming.get()
        if msg is None:
            raise StopAsyncIteration
        return msg

    async def close(self) -> None:
        await self._incoming.put(None)

    def inject(self, msg: dict) -> None:
        self._incoming.put_nowait(json.dumps(msg))


async def _connect(fake: _FakeRelaySocket) -> VestaConnection:
    conn = VestaConnection(
        server_url="ws://test",
        client_id="test-client",
        channels=[CHANNEL],
        auto_reconnect=False,
    )
    fake.inject({"type": "WELCOME", "serverId": "s1", "channels": [CHANNEL]})
    conn._ws = fake  # type: ignore[assignment]
    msg = parse_server_message(json.loads(await fake.recv()))
    assert isinstance(msg, WelcomeMessage)
    conn._is_connected = True
    conn._server_id = msg.server_id
    conn._receive_task = asyncio.create_task(conn._receive_loop())
    return conn


class FetchAllTests(unittest.TestCase):
    def test_pages_past_the_relays_clamp_and_returns_everything_in_order(self) -> None:
        async def _t() -> None:
            fake = _FakeRelaySocket(event_count=10, max_page=3)
            conn = await _connect(fake)
            events = await conn.fetch_all(CHANNEL, page_size=100)
            self.assertEqual([e.sequence for e in events], list(range(1, 11)))
            # 3, 3, 3, 1 events and one empty page that ends the loop.
            self.assertEqual([f["fromSequence"] for f in fake.fetches], [0, 4, 7, 10, 11])
            await conn.disconnect()

        asyncio.run(_t())

    def test_to_sequence_stops_at_the_upper_bound(self) -> None:
        async def _t() -> None:
            conn = await _connect(_FakeRelaySocket(event_count=10, max_page=3))
            events = await conn.fetch_all(CHANNEL, from_sequence=3, to_sequence=7)
            self.assertEqual([e.sequence for e in events], [3, 4, 5, 6, 7])
            await conn.disconnect()

        asyncio.run(_t())

    def test_empty_channel_returns_empty_after_one_page(self) -> None:
        async def _t() -> None:
            fake = _FakeRelaySocket(event_count=0, max_page=3)
            conn = await _connect(fake)
            self.assertEqual(await conn.fetch_all(CHANNEL), [])
            self.assertEqual(len(fake.fetches), 1)
            await conn.disconnect()

        asyncio.run(_t())

    def test_relay_error_raises_with_the_code(self) -> None:
        async def _t() -> None:
            conn = await _connect(_FakeRelaySocket(event_count=5, max_page=3, refuse=True))
            with self.assertRaisesRegex(RuntimeError, "ACCESS_DENIED"):
                await conn.fetch_all(CHANNEL)
            await conn.disconnect()

        asyncio.run(_t())

    def test_missing_page_times_out(self) -> None:
        async def _t() -> None:
            conn = await _connect(_FakeRelaySocket(event_count=5, max_page=3, silent=True))
            with self.assertRaises(TimeoutError):
                await conn.fetch_all(CHANNEL, page_timeout=0.05)
            await conn.disconnect()

        asyncio.run(_t())

    def test_other_channels_are_ignored_and_chained_handlers_are_restored(self) -> None:
        async def _t() -> None:
            fake = _FakeRelaySocket(event_count=2, max_page=5)
            conn = await _connect(fake)
            seen: list[str] = []
            conn.on_events_batch(lambda m: seen.append(m.channel_id))
            pending = asyncio.ensure_future(conn.fetch_all(CHANNEL))
            fake.inject({"type": "ERROR", "code": "ACCESS_DENIED", "message": "other", "channelId": "myapp/other"})
            fake.inject({"type": "EVENTS_BATCH", "channelId": "myapp/other", "events": []})
            events = await pending
            self.assertEqual([e.sequence for e in events], [1, 2])
            self.assertIn("myapp/other", seen)
            self.assertEqual(conn._on_events_batch.__name__, "<lambda>")
            with self.assertRaises(ValueError):
                await conn.fetch_all(CHANNEL, page_size=0)
            await conn.disconnect()

        asyncio.run(_t())


if __name__ == "__main__":
    unittest.main()
