"""Encrypted channels (relay phase A): WELCOME ``features``, ``create_channel(encrypted=...)`` gating,
``revoke_access`` and ENCRYPTION_REQUIRED classification. Mirrors the C# / TS tests."""

from __future__ import annotations

import asyncio
import json
import unittest

from vesta_client import NotSupportedError, RelayFeatures, VestaConnection, classify_error_code
from vesta_client.types import WelcomeMessage, parse_server_message


class _FakeWebSocket:
    def __init__(self) -> None:
        self.sent: list[dict] = []
        self._incoming: asyncio.Queue[str | None] = asyncio.Queue()

    async def send(self, data: str) -> None:
        self.sent.append(json.loads(data))

    def __aiter__(self) -> "_FakeWebSocket":
        return self

    async def __anext__(self) -> str:
        msg = await self._incoming.get()
        if msg is None:
            raise StopAsyncIteration
        return msg

    async def close(self) -> None:
        await self._incoming.put(None)


def _connected(features: list[str]) -> tuple[VestaConnection, _FakeWebSocket]:
    fake = _FakeWebSocket()
    conn = VestaConnection(server_url="ws://test", client_id="test-client", channels=[], auto_reconnect=False)
    conn._ws = fake  # type: ignore[assignment]
    conn._is_connected = True
    conn._features = list(features)
    return conn, fake


class EncryptedChannelTests(unittest.TestCase):
    def test_welcome_features_are_parsed_and_default_to_empty(self) -> None:
        modern = parse_server_message({"type": "WELCOME", "serverId": "s1", "channels": [], "features": ["encrypted-channels"]})
        legacy = parse_server_message({"type": "WELCOME", "serverId": "s1", "channels": []})
        null = parse_server_message({"type": "WELCOME", "serverId": "s1", "channels": [], "features": None})

        self.assertIsInstance(modern, WelcomeMessage)
        self.assertEqual(modern.features, [RelayFeatures.ENCRYPTED_CHANNELS])  # type: ignore[union-attr]
        self.assertEqual(legacy.features, [])  # type: ignore[union-attr]
        self.assertEqual(null.features, [])  # type: ignore[union-attr]

    def test_supports_feature_reflects_welcome(self) -> None:
        conn, _ = _connected(["encrypted-channels"])
        self.assertTrue(conn.supports_feature(RelayFeatures.ENCRYPTED_CHANNELS))
        self.assertEqual(conn.features, ["encrypted-channels"])

        legacy, _ = _connected([])
        self.assertFalse(legacy.supports_feature(RelayFeatures.ENCRYPTED_CHANNELS))

    def test_create_channel_encrypted_sends_flag_on_supporting_relay(self) -> None:
        async def _t() -> None:
            conn, fake = _connected(["encrypted-channels"])
            await conn.create_channel("notes/secret", visibility="private", encrypted=True)
            create = next(m for m in fake.sent if m["type"] == "CREATE_CHANNEL")
            self.assertEqual(create["channelId"], "notes/secret")
            self.assertEqual(create["visibility"], "private")
            self.assertIs(create["encrypted"], True)

        asyncio.run(_t())

    def test_create_channel_without_encrypted_omits_the_field(self) -> None:
        async def _t() -> None:
            conn, fake = _connected(["encrypted-channels"])
            await conn.create_channel("notes/plain")
            create = next(m for m in fake.sent if m["type"] == "CREATE_CHANNEL")
            self.assertNotIn("encrypted", create)

        asyncio.run(_t())

    def test_create_channel_encrypted_refused_against_legacy_relay(self) -> None:
        async def _t() -> None:
            conn, fake = _connected([])
            with self.assertRaises(NotSupportedError):
                await conn.create_channel("notes/secret", encrypted=True)
            self.assertFalse(any(m["type"] == "CREATE_CHANNEL" for m in fake.sent))

        asyncio.run(_t())

    def test_revoke_access_sends_revoke_message(self) -> None:
        async def _t() -> None:
            conn, fake = _connected(["encrypted-channels"])
            await conn.revoke_access("notes/secret", "former-member")
            self.assertEqual(
                fake.sent[-1],
                {"type": "REVOKE_ACCESS", "channelId": "notes/secret", "clientId": "former-member"},
            )

        asyncio.run(_t())

    def test_encryption_required_is_event_fatal_but_not_a_limit(self) -> None:
        c = classify_error_code("ENCRYPTION_REQUIRED")
        self.assertFalse(c.is_limit)
        self.assertFalse(c.is_transient)
        self.assertTrue(c.is_event_fatal)


if __name__ == "__main__":
    unittest.main()
