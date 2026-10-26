"""
HELLO key-possession proof: CHALLENGE -> PROVE handshake and the signing vector shared with the
C# (``HelloProofTests``) and TypeScript (``hello-proof.test.mjs``) SDKs.
"""

from __future__ import annotations

import asyncio
import json
import unittest
from typing import Any
from unittest.mock import AsyncMock, patch

from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey, Ed25519PublicKey

from vesta_client import VestaConnection
from vesta_client.identity import VestaIdentity, b64url_decode
from vesta_client.signing import build_hello_proof_input, sign_hello_proof


class _ScriptedSocket:
    """Replies to HELLO/PROVE with a fixed script and records what the client sent."""

    def __init__(self, replies: list[dict[str, Any]]) -> None:
        self.sent: list[dict[str, Any]] = []
        self._replies = list(replies)

    async def send(self, data: str) -> None:
        self.sent.append(json.loads(data))

    async def recv(self) -> str:
        return json.dumps(self._replies.pop(0))

    def __aiter__(self) -> "_ScriptedSocket":
        return self

    async def __anext__(self) -> str:
        raise StopAsyncIteration

    async def close(self) -> None:
        pass


def _identity_from_seed(seed: bytes) -> VestaIdentity:
    from cryptography.hazmat.primitives import serialization

    from vesta_client.identity import derive_client_id

    priv = Ed25519PrivateKey.from_private_bytes(seed)
    pub = priv.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)
    return VestaIdentity(client_id=derive_client_id(pub), public_key=pub, _private_key=priv)


def _connect(identity: VestaIdentity | None, replies: list[dict[str, Any]]) -> _ScriptedSocket:
    socket = _ScriptedSocket(replies)

    async def _run() -> None:
        conn = VestaConnection(
            server_url="ws://test",
            client_id=identity.client_id if identity else "anon-client",
            channels=["game/farm"],
            auto_reconnect=False,
            identity=identity,
        )
        with patch("vesta_client.connection.websockets.connect", AsyncMock(return_value=socket)):
            await conn._connect_to("ws://test")

    asyncio.run(_run())
    return socket


WELCOME = {"type": "WELCOME", "serverId": "srv", "channels": ["game/farm"]}


class HelloProofTests(unittest.TestCase):
    def test_sign_hello_proof_matches_cross_language_vector(self) -> None:
        identity = _identity_from_seed(bytes(32))

        self.assertEqual(identity.client_id, "E545QOZLVJFyIIjZoNdBYo")
        self.assertEqual(
            sign_hello_proof(identity, "bm9uY2Utdm9uY2Utdm9uY2U"),
            "9hGCvE4icwU-17knAeGGHxw0xvyqpm38vbgrC0z8ZREQWCtRk9UoPGNP_o0bp0-ABGhIXJAY5rrDsblaYih_DQ",
        )

    def test_challenge_is_answered_with_a_valid_prove(self) -> None:
        identity = VestaIdentity.generate()

        socket = _connect(identity, [{"type": "CHALLENGE", "nonce": "abc123"}, WELCOME])

        self.assertEqual(socket.sent[0]["type"], "HELLO")
        self.assertIs(socket.sent[0]["proofSupported"], True)
        prove = socket.sent[1]
        self.assertEqual(prove["type"], "PROVE")
        Ed25519PublicKey.from_public_bytes(identity.public_key).verify(
            b64url_decode(prove["signature"]),
            build_hello_proof_input("abc123", identity.client_id),
        )

    def test_relay_without_challenge_still_connects(self) -> None:
        identity = VestaIdentity.generate()

        socket = _connect(identity, [WELCOME])

        self.assertEqual(len(socket.sent), 1)

    def test_identityless_client_does_not_advertise_proof(self) -> None:
        socket = _connect(None, [WELCOME])

        self.assertNotIn("proofSupported", socket.sent[0])


if __name__ == "__main__":
    unittest.main()
