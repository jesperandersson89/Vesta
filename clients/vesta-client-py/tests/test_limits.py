"""
Limit-notice classification + outbox dead-letter tests for the Python client.

Mirrors the C# tests around VestaClient.VestaErrorCodes.Classify / VestaConnection.OnLimited
and clients/vesta-client-ts/tests/limits.test.mjs.
"""

from __future__ import annotations

import asyncio
import json
import unittest
from typing import Any

from vesta_client import InMemoryClientEventStore, VestaConnection, VestaEvent, classify_error_code
from vesta_client.types import WelcomeMessage, parse_server_message


def _make_event(evt_id: str) -> VestaEvent:
    return VestaEvent(
        id=evt_id,
        channel_id="myapp/chat",
        timestamp="2026-01-01T00:00:00.000Z",
        client_id="test-client",
        event_type="chat.message",
        payload={"text": "hi"},
    )


class ClassifyErrorCodeTests(unittest.TestCase):
    def test_rate_limited_is_transient_limit_not_event_fatal(self) -> None:
        c = classify_error_code("RATE_LIMITED")
        self.assertTrue(c.is_limit)
        self.assertTrue(c.is_transient)
        self.assertFalse(c.is_event_fatal)

    def test_app_paused_is_transient_limit_not_event_fatal(self) -> None:
        c = classify_error_code("APP_PAUSED")
        self.assertTrue(c.is_limit)
        self.assertTrue(c.is_transient)
        self.assertFalse(c.is_event_fatal)

    def test_quota_exceeded_is_fatal_limit(self) -> None:
        c = classify_error_code("QUOTA_EXCEEDED")
        self.assertTrue(c.is_limit)
        self.assertFalse(c.is_transient)
        self.assertTrue(c.is_event_fatal)

    def test_fatal_limit_codes(self) -> None:
        for code in ["UNKNOWN_APP", "ACCESS_DENIED", "APP_NOT_ALLOWED", "MESSAGE_QUOTA_EXCEEDED"]:
            c = classify_error_code(code)
            self.assertTrue(c.is_limit, code)
            self.assertFalse(c.is_transient, code)
            self.assertTrue(c.is_event_fatal, code)

    def test_protocol_errors_are_fatal_but_not_limits(self) -> None:
        for code in [
            "INVALID_CHANNEL",
            "INVALID_SIGNATURE",
            "SIGNATURE_REQUIRED",
            "CLIENT_ID_MISMATCH",
            "PROTOCOL_NAMESPACE_RESERVED",
            "CHANNEL_DELETED",
        ]:
            c = classify_error_code(code)
            self.assertFalse(c.is_limit, code)
            self.assertTrue(c.is_event_fatal, code)

    def test_unknown_codes_are_conservative(self) -> None:
        c = classify_error_code("SOME_FUTURE_CODE")
        self.assertFalse(c.is_limit)
        self.assertTrue(c.is_transient)
        self.assertFalse(c.is_event_fatal)


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
        if msg is None:
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

    def inject(self, msg: dict) -> None:
        self._incoming.put_nowait(json.dumps(msg))


def _make_connection_with_fake_socket(store: Any, fake: _FakeWebSocket) -> VestaConnection:
    conn = VestaConnection(
        server_url="ws://test",
        client_id="test-client",
        channels=["myapp/chat"],
        auto_reconnect=False,
        local_store=store,
    )

    async def _fake_connect() -> None:
        conn._ws = fake  # type: ignore[assignment]
        await fake.send(json.dumps({"type": "HELLO", "clientId": conn.client_id, "channels": conn._channels}))
        raw = await fake.recv()
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


class ConnectionLimitedTests(unittest.TestCase):
    def test_fatal_limit_dead_letters_outbox_entry(self) -> None:
        async def _t() -> None:
            store = InMemoryClientEventStore()
            fake = _FakeWebSocket()
            conn = _make_connection_with_fake_socket(store, fake)

            await conn.publish(_make_event("doomed-1"))  # offline -> outbox

            notices = []
            conn.on_limited(lambda n: notices.append(n))

            fake.inject({"type": "WELCOME", "serverId": "s1", "channels": ["myapp/chat"]})
            await conn.connect()
            await asyncio.sleep(0.05)  # let flush_outbox send the pending entry

            fake.inject(
                {
                    "type": "ERROR",
                    "code": "QUOTA_EXCEEDED",
                    "message": "storage quota exceeded",
                    "eventId": "doomed-1",
                    "channelId": "myapp/chat",
                }
            )
            await asyncio.sleep(0.05)

            self.assertEqual(len(notices), 1)
            self.assertEqual(notices[0].code, "QUOTA_EXCEEDED")
            self.assertFalse(notices[0].is_transient)

            pending = await store.get_pending_outbox()
            self.assertEqual(len(pending), 0, "rejected entry must not be retried")

            await conn.disconnect()

        asyncio.run(_t())

    def test_rate_limited_does_not_dead_letter(self) -> None:
        async def _t() -> None:
            store = InMemoryClientEventStore()
            fake = _FakeWebSocket()
            conn = _make_connection_with_fake_socket(store, fake)

            await conn.publish(_make_event("retryable-1"))

            notices = []
            conn.on_limited(lambda n: notices.append(n))

            fake.inject({"type": "WELCOME", "serverId": "s1", "channels": ["myapp/chat"]})
            await conn.connect()
            await asyncio.sleep(0.05)

            fake.inject(
                {
                    "type": "ERROR",
                    "code": "RATE_LIMITED",
                    "message": "slow down",
                    "eventId": "retryable-1",
                    "channelId": "myapp/chat",
                }
            )
            await asyncio.sleep(0.05)

            self.assertEqual(len(notices), 1)
            self.assertTrue(notices[0].is_transient)

            pending = await store.get_pending_outbox()
            self.assertEqual(len(pending), 1, "transient limits must not dead-letter")
            self.assertEqual(pending[0].status, "sent")

            await conn.disconnect()

        asyncio.run(_t())


if __name__ == "__main__":
    unittest.main()
