"""Control ACKs (nil event id) reach the ack callback, like C# VestaConnection.OnAck and the TS "ack" event."""

from __future__ import annotations

import asyncio
import json
import unittest

from vesta_client import VestaConnection
from vesta_client.types import AckMessage, WelcomeMessage, parse_server_message

NIL = "00000000-0000-0000-0000-000000000000"


class _FakeWebSocket:
    def __init__(self) -> None:
        self.sent: list[dict] = []
        self._incoming: asyncio.Queue[str | None] = asyncio.Queue()

    async def send(self, data: str) -> None:
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
        await self._incoming.put(None)

    def inject(self, msg: dict) -> None:
        self._incoming.put_nowait(json.dumps(msg))


async def _connected() -> tuple[VestaConnection, _FakeWebSocket]:
    fake = _FakeWebSocket()
    conn = VestaConnection(server_url="ws://test", client_id="test-client", channels=[], auto_reconnect=False)
    fake.inject({"type": "WELCOME", "serverId": "s1", "channels": []})
    conn._ws = fake  # type: ignore[assignment]
    msg = parse_server_message(json.loads(await fake.recv()))
    assert isinstance(msg, WelcomeMessage)
    conn._is_connected = True
    conn._receive_task = asyncio.create_task(conn._receive_loop())
    return conn, fake


class ControlAckTests(unittest.TestCase):
    def test_control_ack_reaches_the_callback_without_touching_sequence_tracking(self) -> None:
        async def _t() -> None:
            conn, fake = await _connected()
            acks: list[AckMessage] = []
            conn.on_ack(acks.append)

            fake.inject({"type": "ACK", "channelId": "myapp/shop", "eventId": NIL, "sequence": 0})
            await asyncio.sleep(0.05)

            self.assertEqual(len(acks), 1)
            self.assertEqual(acks[0].channel_id, "myapp/shop")
            self.assertEqual(acks[0].event_id, NIL)
            self.assertNotIn("myapp/shop", conn._last_sequences)
            await conn.disconnect()

        asyncio.run(_t())

    def test_publish_ack_is_still_delivered_and_tracked(self) -> None:
        async def _t() -> None:
            conn, fake = await _connected()
            acks: list[AckMessage] = []
            conn.on_ack(acks.append)

            fake.inject({"type": "ACK", "channelId": "myapp/chat", "eventId": "evt-1", "sequence": 7})
            await asyncio.sleep(0.05)

            self.assertEqual(len(acks), 1)
            self.assertEqual(conn._last_sequences["myapp/chat"], 7)
            await conn.disconnect()

        asyncio.run(_t())


if __name__ == "__main__":
    unittest.main()
