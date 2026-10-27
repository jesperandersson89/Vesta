"""
restore_identity / recover_identity: one-call identity recovery from a relay-stored backup.

Mirrors tests/VestaClient.Tests/RecoverIdentityTests.cs and clients/vesta-client-ts/tests/recover-identity.test.mjs.
"""

from __future__ import annotations

import asyncio
import json
import unittest
from typing import Any
from unittest import mock

from vesta_client import IdentityBackupOptions, RelayBackup, VestaAppConfig, VestaConnection, VestaIdentity
from vesta_client.relay import RelaysExhaustedError

FAST = IdentityBackupOptions(memory_kib=256, iterations=1)


class _ScriptedRelaySocket:
    """Answers HELLO with WELCOME and FETCH from a fixed list of sequenced events (one per page)."""

    def __init__(self, stored: list[dict[str, Any]]) -> None:
        self.fetches: list[dict] = []
        self._stored = stored
        self._incoming: asyncio.Queue[str | None] = asyncio.Queue()

    async def send(self, data: str) -> None:
        msg = json.loads(data)
        if msg["type"] == "HELLO":
            self._incoming.put_nowait(json.dumps({"type": "WELCOME", "serverId": "scripted", "channels": []}))
        elif msg["type"] == "FETCH":
            self.fetches.append(msg)
            events = [
                e for e in self._stored
                if e["event"]["channelId"] == msg["channelId"] and e["sequence"] >= msg["fromSequence"]
            ][:1]
            self._incoming.put_nowait(json.dumps({"type": "EVENTS_BATCH", "channelId": msg["channelId"], "events": events}))

    async def recv(self) -> str:
        msg = await self._incoming.get()
        if msg is None:
            raise StopAsyncIteration
        return msg

    def __aiter__(self) -> "_ScriptedRelaySocket":
        return self

    async def __anext__(self) -> str:
        msg = await self._incoming.get()
        if msg is None:
            raise StopAsyncIteration
        return msg

    async def close(self) -> None:
        await self._incoming.put(None)


def _backup_event(identity: VestaIdentity, username: str, password: str) -> list[dict[str, Any]]:
    backup = RelayBackup.derive("game", username, password, FAST)
    event = backup.create_event(identity)
    return [{"event": event.to_dict(), "sequence": 1, "receivedAt": "2026-01-01T00:00:00.000Z"}]


def _config() -> VestaAppConfig:
    return VestaAppConfig(app_id="game", owner_public_key=VestaIdentity.generate().public_key_b64, default_relays=["ws://127.0.0.1:9/ws"])


async def _recover(stored: list[dict[str, Any]], username: str, password: str):
    socket = _ScriptedRelaySocket(stored)

    async def _connect(url: str, *args: Any, **kwargs: Any) -> _ScriptedRelaySocket:
        return socket

    # The SDK's best-effort peer-cache refresh does real HTTP in a worker thread; it is unrelated to recovery.
    with mock.patch("vesta_client.connection.websockets.connect", _connect), mock.patch.object(
        VestaConnection, "_refresh_peer_cache", mock.AsyncMock()
    ):
        identity = await VestaConnection.recover_identity(
            _config(), username, password, options=FAST, relays=["ws://127.0.0.1:9/ws"]
        )
    return identity, socket


class RecoverIdentityTests(unittest.TestCase):
    def test_returns_the_original_identity_for_the_right_login(self) -> None:
        original = VestaIdentity.generate()
        stored = _backup_event(original, "alice", "hunter2")
        recovered, _ = asyncio.run(_recover(stored, "alice", "hunter2"))
        self.assertIsNotNone(recovered)
        self.assertEqual(recovered.client_id, original.client_id)

    def test_username_spelling_is_normalised(self) -> None:
        original = VestaIdentity.generate()
        stored = _backup_event(original, "alice", "hunter2")
        recovered, _ = asyncio.run(_recover(stored, "  ALICE ", "hunter2"))
        self.assertEqual(recovered.client_id, original.client_id)

    def test_wrong_password_or_unknown_username_returns_none(self) -> None:
        stored = _backup_event(VestaIdentity.generate(), "alice", "hunter2")
        self.assertIsNone(asyncio.run(_recover(stored, "alice", "wrong"))[0])
        self.assertIsNone(asyncio.run(_recover(stored, "bob", "hunter2"))[0])

    def test_only_the_derived_backup_channel_is_fetched(self) -> None:
        stored = _backup_event(VestaIdentity.generate(), "alice", "hunter2")
        _, socket = asyncio.run(_recover(stored, "alice", "hunter2"))
        channel = RelayBackup.derive("game", "alice", "hunter2", FAST).channel_id
        self.assertTrue(socket.fetches)
        self.assertTrue(all(f["channelId"] == channel for f in socket.fetches))

    def test_unreachable_relay_raises(self) -> None:
        async def _t() -> None:
            async def _connect(url: str, *args: Any, **kwargs: Any) -> None:
                raise OSError("unreachable")

            with mock.patch("vesta_client.connection.websockets.connect", _connect):
                with self.assertRaises(RelaysExhaustedError):
                    await VestaConnection.recover_identity(_config(), "alice", "hunter2", options=FAST, relays=["ws://127.0.0.1:9/ws"])

        asyncio.run(_t())


if __name__ == "__main__":
    unittest.main()
