"""
Outbox + crash-recovery tests for the Python client.

Mirrors the C# tests in ``tests/VestaClient.Tests/OfflineOutboxSyncTests.cs``
and the TS tests in ``clients/vesta-client-ts/tests/outbox.test.mjs``.

Covers both the store implementations (in-memory + sqlite) and the connection's
outbox flush behaviour using a fake WebSocket.
"""

from __future__ import annotations

import asyncio
import json
import unittest
from collections.abc import Awaitable, Callable
from typing import Any

from vesta_client import (
    InMemoryClientEventStore,
    SequencedEvent,
    SqliteClientEventStore,
    VestaConnection,
    VestaEvent,
)
from vesta_client.connection import logger as _connection_logger  # noqa: F401


def _make_event(channel_id: str, evt_id: str) -> VestaEvent:
    return VestaEvent(
        id=evt_id,
        channel_id=channel_id,
        timestamp="2026-01-01T00:00:00.000Z",
        client_id="test-client",
        event_type="test.evt",
        payload={"hello": "world"},
    )


async def _run(coro: Awaitable[Any]) -> Any:
    return await coro


def _run_sync(coro: Awaitable[Any]) -> Any:
    return asyncio.run(coro)


# ── In-memory store ────────────────────────────────────────────────────────


class InMemoryClientEventStoreTests(unittest.TestCase):
    def test_outbox_returns_pending_and_sent(self) -> None:
        async def _t() -> None:
            store = InMemoryClientEventStore()
            e1 = _make_event("test/outbox", "id-1")
            e2 = _make_event("test/outbox", "id-2")
            await store.enqueue_outbox(e1)
            await store.enqueue_outbox(e2)
            await store.mark_outbox_sent("id-1")

            pending = await store.get_pending_outbox()
            self.assertEqual(len(pending), 2)
            by_id = {e.event.id: e for e in pending}
            self.assertEqual(by_id["id-1"].status, "sent")
            self.assertEqual(by_id["id-2"].status, "pending")

        _run_sync(_t())

    def test_mark_confirmed_removes_entry(self) -> None:
        async def _t() -> None:
            store = InMemoryClientEventStore()
            e = _make_event("test/outbox", "id-1")
            await store.enqueue_outbox(e)
            await store.mark_outbox_sent("id-1")
            await store.mark_outbox_confirmed("id-1")
            self.assertEqual(await store.get_pending_outbox(), [])

        _run_sync(_t())

    def test_store_event_dedups(self) -> None:
        async def _t() -> None:
            store = InMemoryClientEventStore()
            e = _make_event("test/outbox", "id-1")
            await store.store_event(SequencedEvent(e, 5, "2026-01-01T00:00:00.000Z"))
            await store.store_event(SequencedEvent(e, 5, "2026-01-01T00:00:00.000Z"))
            events = await store.get_events("test/outbox", 0)
            self.assertEqual(len(events), 1)

        _run_sync(_t())


# ── Sqlite store ───────────────────────────────────────────────────────────


class SqliteClientEventStoreTests(unittest.TestCase):
    def test_outbox_returns_pending_and_sent(self) -> None:
        async def _t() -> None:
            store = SqliteClientEventStore(":memory:")
            try:
                e1 = _make_event("test/outbox", "id-1")
                e2 = _make_event("test/outbox", "id-2")
                await store.enqueue_outbox(e1)
                await store.enqueue_outbox(e2)
                await store.mark_outbox_sent("id-1")

                pending = await store.get_pending_outbox()
                self.assertEqual(len(pending), 2)
                by_id = {e.event.id: e for e in pending}
                self.assertEqual(by_id["id-1"].status, "sent")
                self.assertEqual(by_id["id-2"].status, "pending")
            finally:
                store.close()

        _run_sync(_t())

    def test_outbox_persists_across_instances(self) -> None:
        import tempfile
        import os

        async def _t() -> None:
            tmp = tempfile.NamedTemporaryFile(delete=False, suffix=".db")
            tmp.close()
            try:
                store1 = SqliteClientEventStore(tmp.name)
                try:
                    e = _make_event("test/outbox", "id-persist")
                    await store1.enqueue_outbox(e)
                    await store1.mark_outbox_sent("id-persist")
                finally:
                    store1.close()

                store2 = SqliteClientEventStore(tmp.name)
                try:
                    pending = await store2.get_pending_outbox()
                    self.assertEqual(len(pending), 1)
                    self.assertEqual(pending[0].event.id, "id-persist")
                    self.assertEqual(pending[0].status, "sent")
                finally:
                    store2.close()
            finally:
                os.unlink(tmp.name)

        _run_sync(_t())

    def test_store_event_roundtrip_preserves_payload_and_metadata(self) -> None:
        async def _t() -> None:
            store = SqliteClientEventStore(":memory:")
            try:
                e = VestaEvent(
                    id="id-1",
                    channel_id="test/payload",
                    timestamp="2026-01-01T00:00:00.000Z",
                    client_id="c",
                    event_type="test.evt",
                    payload={"nested": [1, 2, 3]},
                    metadata={"ttlSeconds": 60},
                )
                await store.store_event(
                    SequencedEvent(e, 7, "2026-01-01T00:00:00.000Z")
                )
                events = await store.get_events("test/payload", 0)
                self.assertEqual(len(events), 1)
                self.assertEqual(events[0].sequence, 7)
                self.assertEqual(events[0].event.payload, {"nested": [1, 2, 3]})
                self.assertEqual(events[0].event.metadata, {"ttlSeconds": 60})
            finally:
                store.close()

        _run_sync(_t())


# ── Connection + outbox integration ────────────────────────────────────────


class _FakeWebSocket:
    """A minimal stand-in for ``websockets.asyncio.client.ClientConnection``."""

    def __init__(self) -> None:
        self.sent: list[dict] = []
        self._incoming: asyncio.Queue[str] = asyncio.Queue()
        self._closed = False

    async def send(self, data: str) -> None:
        if self._closed:
            raise RuntimeError("socket closed")
        self.sent.append(json.loads(data))

    async def recv(self) -> str:
        msg = await self._incoming.get()
        if msg is None:  # sentinel
            raise StopAsyncIteration
        return msg

    def __aiter__(self) -> "_FakeWebSocket":
        return self

    async def __anext__(self) -> str:
        msg = await self._incoming.get()
        if msg is None:
            raise StopAsyncIteration
        return msg

    async def close(self) -> None:
        self._closed = True
        await self._incoming.put(None)  # type: ignore[arg-type]

    # Test driver helpers
    def inject(self, msg: dict) -> None:
        self._incoming.put_nowait(json.dumps(msg))


def _make_connection_with_fake_socket(
    store: Any, fake: _FakeWebSocket
) -> VestaConnection:
    conn = VestaConnection(
        server_url="ws://test",
        client_id="test-client",
        channels=["test/outbox"],
        auto_reconnect=False,
        local_store=store,
    )
    # Patch the connect() call to bypass real websockets.
    async def _fake_connect() -> None:
        conn._ws = fake  # type: ignore[assignment]
        await fake.send(
            json.dumps(
                {
                    "type": "HELLO",
                    "clientId": conn.client_id,
                    "channels": conn._channels,
                    "lastSequences": conn._last_sequences,
                }
            )
        )
        # Wait for WELCOME injected by the test.
        raw = await fake.recv()
        from vesta_client.types import WelcomeMessage, parse_server_message

        msg = parse_server_message(json.loads(raw))
        assert isinstance(msg, WelcomeMessage)
        conn._is_connected = True
        conn._server_id = msg.server_id
        conn._channels = list(msg.channels)
        conn._reconnect_attempt = 0
        if conn._on_connected:
            conn._on_connected(msg)
        conn._receive_task = asyncio.create_task(conn._receive_loop())
        if conn._local_store is not None:
            await conn._flush_outbox()

    conn.connect = _fake_connect  # type: ignore[method-assign]
    return conn


class ConnectionOutboxIntegrationTests(unittest.TestCase):
    def test_publish_while_disconnected_enqueues(self) -> None:
        async def _t() -> None:
            store = InMemoryClientEventStore()
            conn = VestaConnection(
                server_url="ws://test",
                client_id="test-client",
                channels=["test/outbox"],
                local_store=store,
            )
            await conn.publish(_make_event("test/outbox", "id-offline"))
            pending = await store.get_pending_outbox()
            self.assertEqual(len(pending), 1)
            self.assertEqual(pending[0].event.id, "id-offline")
            self.assertEqual(pending[0].status, "pending")

        _run_sync(_t())

    def test_publish_without_store_disconnected_raises(self) -> None:
        async def _t() -> None:
            conn = VestaConnection(
                server_url="ws://test",
                client_id="test-client",
                channels=["test/outbox"],
            )
            with self.assertRaises(RuntimeError):
                await conn.publish(_make_event("test/outbox", "id-1"))

        _run_sync(_t())

    def test_outbox_flushes_on_welcome(self) -> None:
        async def _t() -> None:
            store = InMemoryClientEventStore()
            e_pending = _make_event("test/outbox", "id-pending")
            e_sent = _make_event("test/outbox", "id-sent")
            await store.enqueue_outbox(e_pending)
            await store.enqueue_outbox(e_sent)
            await store.mark_outbox_sent("id-sent")

            fake = _FakeWebSocket()
            conn = _make_connection_with_fake_socket(store, fake)

            # Inject WELCOME so the patched connect() returns.
            fake.inject(
                {"type": "WELCOME", "serverId": "test-server", "channels": ["test/outbox"]}
            )
            await conn.connect()

            # Yield once for the flush to publish both entries.
            await asyncio.sleep(0)

            publishes = [m for m in fake.sent if m["type"] == "PUBLISH"]
            self.assertEqual(len(publishes), 2)
            ids = sorted(m["event"]["id"] for m in publishes)
            self.assertEqual(ids, ["id-pending", "id-sent"])

            pending_after = await store.get_pending_outbox()
            self.assertEqual(len(pending_after), 2)
            self.assertTrue(all(e.status == "sent" for e in pending_after))

            # Inject ACKs — outbox should drain.
            fake.inject(
                {
                    "type": "ACK",
                    "channelId": "test/outbox",
                    "eventId": "id-pending",
                    "sequence": 1,
                }
            )
            fake.inject(
                {
                    "type": "ACK",
                    "channelId": "test/outbox",
                    "eventId": "id-sent",
                    "sequence": 2,
                }
            )
            # Let _receive_loop process the messages.
            await asyncio.sleep(0.05)

            self.assertEqual(await store.get_pending_outbox(), [])

            await conn.dispose()

        _run_sync(_t())

    def test_ack_caches_event_with_assigned_sequence(self) -> None:
        async def _t() -> None:
            store = InMemoryClientEventStore()
            fake = _FakeWebSocket()
            conn = _make_connection_with_fake_socket(store, fake)
            fake.inject(
                {"type": "WELCOME", "serverId": "test-server", "channels": ["test/outbox"]}
            )
            await conn.connect()
            await asyncio.sleep(0)

            e = _make_event("test/outbox", "id-live")
            await conn.publish(e)

            fake.inject(
                {
                    "type": "ACK",
                    "channelId": "test/outbox",
                    "eventId": "id-live",
                    "sequence": 42,
                }
            )
            await asyncio.sleep(0.05)

            cached = await store.get_events("test/outbox", 0)
            self.assertEqual(len(cached), 1)
            self.assertEqual(cached[0].sequence, 42)
            self.assertEqual(cached[0].event.id, "id-live")

            await conn.dispose()

        _run_sync(_t())


if __name__ == "__main__":
    unittest.main()
