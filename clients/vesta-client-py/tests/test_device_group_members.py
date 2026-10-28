"""get_device_group_members pages through a group channel larger than one relay page."""

from __future__ import annotations

import asyncio
import json
import unittest
from typing import Any

from vesta_client import VestaConnection, VestaIdentity
from vesta_client.device_groups import build_announce, build_link, device_group_channel, generate_group_id
from vesta_client.types import WelcomeMessage, parse_server_message


class _FakeRelaySocket:
    def __init__(self, stored: list[dict[str, Any]], max_page: int) -> None:
        self.fetches: list[dict] = []
        self._stored = stored
        self._max_page = max_page
        self._incoming: asyncio.Queue[str | None] = asyncio.Queue()

    async def send(self, data: str) -> None:
        msg = json.loads(data)
        if msg["type"] != "FETCH":
            return
        self.fetches.append(msg)
        limit = min(max(msg.get("limit") or 100, 1), self._max_page)
        events = [
            e for e in self._stored
            if e["event"]["channelId"] == msg["channelId"] and e["sequence"] >= msg["fromSequence"]
        ][:limit]
        self._incoming.put_nowait(json.dumps({"type": "EVENTS_BATCH", "channelId": msg["channelId"], "events": events}))

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


async def _connected(stored: list[dict[str, Any]], max_page: int, identity: VestaIdentity):
    fake = _FakeRelaySocket(stored, max_page)
    conn = VestaConnection(server_url="ws://test", identity=identity, channels=[], auto_reconnect=False)
    fake.inject({"type": "WELCOME", "serverId": "s1", "channels": []})
    conn._ws = fake  # type: ignore[assignment]
    msg = parse_server_message(json.loads(await fake.recv()))
    assert isinstance(msg, WelcomeMessage)
    conn._is_connected = True
    conn._receive_task = asyncio.create_task(conn._receive_loop())
    return conn, fake


class DeviceGroupMembersTests(unittest.TestCase):
    def test_reads_every_page_not_just_the_first_batch(self) -> None:
        async def _t() -> None:
            group_id = generate_group_id()
            founder = VestaIdentity.generate()
            others = [VestaIdentity.generate() for _ in range(4)]
            events = [build_announce(founder, group_id, "Founder")] + [
                build_link(founder, group_id, o.public_key) for o in others
            ]
            stored = [
                {"event": e.to_dict(), "sequence": i + 1, "receivedAt": "2026-01-01T00:00:00.000Z"}
                for i, e in enumerate(events)
            ]
            conn, fake = await _connected(stored, 2, founder)

            group = await conn.get_device_group_members(group_id)

            self.assertEqual(len(group.members), 5)
            self.assertTrue(group.is_member(founder.client_id))
            self.assertTrue(all(group.is_member(o.client_id) for o in others))
            self.assertGreaterEqual(len(fake.fetches), 3)
            self.assertTrue(all(f["channelId"] == device_group_channel(group_id) for f in fake.fetches))
            await conn.disconnect()

        asyncio.run(_t())

    def test_empty_channel_returns_an_empty_group_without_waiting_for_a_timeout(self) -> None:
        async def _t() -> None:
            conn, _ = await _connected([], 2, VestaIdentity.generate())
            group = await conn.get_device_group_members(generate_group_id(), timeout=2.0)
            self.assertEqual(len(group.members), 0)
            await conn.disconnect()

        asyncio.run(_t())


if __name__ == "__main__":
    unittest.main()
