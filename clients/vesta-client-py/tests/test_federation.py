"""
Federation (server-to-server discovery) tests: descriptor signing/verification and
FederationClient owner-mismatch spoof rejection.

Mirrors tests/VestaCore.Tests/Relay/DescriptorSignerTests.cs and
clients/vesta-client-ts/tests/federation.test.mjs.
"""

from __future__ import annotations

import asyncio
import unittest
from dataclasses import replace
from datetime import datetime, timedelta, timezone

from vesta_client import VestaAppConfig, VestaIdentity
from vesta_client.federation import (
    DiscoverableApp,
    FederationClient,
    ServerDescriptor,
    build_descriptor_signing_input,
    verify_descriptor,
)
from vesta_client.identity import derive_client_id


def _sign_descriptor(descriptor: ServerDescriptor, identity: VestaIdentity) -> ServerDescriptor:
    with_key = replace(descriptor, relay_public_key=identity.public_key_b64, signature=None)
    signature = identity.sign_b64(build_descriptor_signing_input(with_key))
    return replace(with_key, signature=signature)


def _make_descriptor(apps=None, issued_at: str | None = None) -> ServerDescriptor:
    return ServerDescriptor(
        relay_public_key="",
        urls=["wss://relay-a.example/ws"],
        apps=apps or [],
        issued_at=issued_at or datetime.now(timezone.utc).isoformat(),
        ttl_seconds=300,
    )


class DescriptorSigningTests(unittest.TestCase):
    def test_sign_then_verify_succeeds(self) -> None:
        relay = VestaIdentity.generate()
        signed = _sign_descriptor(_make_descriptor(), relay)
        self.assertTrue(verify_descriptor(signed))

    def test_verify_rejects_tampered_urls(self) -> None:
        relay = VestaIdentity.generate()
        signed = _sign_descriptor(_make_descriptor(), relay)
        tampered = replace(signed, urls=["wss://evil.example/ws"])
        self.assertFalse(verify_descriptor(tampered))

    def test_verify_rejects_signature_from_different_key(self) -> None:
        relay = VestaIdentity.generate()
        attacker = VestaIdentity.generate()
        signed = _sign_descriptor(_make_descriptor(), relay)
        spoofed = replace(signed, relay_public_key=attacker.public_key_b64)
        self.assertFalse(verify_descriptor(spoofed))

    def test_is_expired(self) -> None:
        past = (datetime.now(timezone.utc) - timedelta(hours=1)).isoformat()
        descriptor = _make_descriptor(issued_at=past)
        self.assertTrue(descriptor.is_expired())
        recent = datetime.now(timezone.utc).isoformat()
        self.assertFalse(_make_descriptor(issued_at=recent).is_expired())


def _fake_fetch(responses: dict[str, list[dict]]):
    def _fetch(url: str) -> list[dict]:
        return responses.get(url, [])

    return _fetch


class FederationClientTests(unittest.TestCase):
    def test_discover_relays_for_app_keeps_verified_owner_matching(self) -> None:
        async def _t() -> None:
            owner = VestaIdentity.generate()
            relay = VestaIdentity.generate()
            spoofer = VestaIdentity.generate()
            app_config = VestaAppConfig(app_id="chess", owner_public_key=owner.public_key_b64, default_relays=[])
            expected_owner_client_id = derive_client_id(owner.public_key)

            genuine = _sign_descriptor(
                _make_descriptor([DiscoverableApp("chess", expected_owner_client_id)]), relay
            )
            spoofed = _sign_descriptor(
                _make_descriptor([DiscoverableApp("chess", derive_client_id(spoofer.public_key))]), spoofer
            )

            def _to_dict(d: ServerDescriptor) -> dict:
                return {
                    "relayPublicKey": d.relay_public_key,
                    "urls": d.urls,
                    "apps": [{"appId": a.app_id, "ownerClientId": a.owner_client_id} for a in d.apps],
                    "issuedAt": d.issued_at,
                    "ttlSeconds": d.ttl_seconds,
                    "signature": d.signature,
                }

            fetch = _fake_fetch(
                {"https://relay-a.example/federation/apps/chess": [_to_dict(genuine), _to_dict(spoofed)]}
            )
            client = FederationClient(app_config, fetch=fetch)
            discovered = await client.discover_relays_for_app("https://relay-a.example")

            self.assertEqual(len(discovered), 1)
            self.assertEqual(discovered[0].relay_public_key, relay.public_key_b64)
            self.assertTrue(discovered[0].hosts_requested_app)

        asyncio.run(_t())

    def test_discovery_yields_nothing_on_fetch_error(self) -> None:
        async def _t() -> None:
            owner = VestaIdentity.generate()
            app_config = VestaAppConfig(app_id="chess", owner_public_key=owner.public_key_b64, default_relays=[])

            def _fetch(url: str) -> list[dict]:
                raise OSError("network down")

            client = FederationClient(app_config, fetch=_fetch)
            discovered = await client.discover_relays_for_app("https://unreachable.example")
            self.assertEqual(discovered, [])

        asyncio.run(_t())

    def test_to_federation_base_url(self) -> None:
        self.assertEqual(
            FederationClient.to_federation_base_url("ws://relay.example:8080/ws"),
            "http://relay.example:8080/",
        )
        self.assertEqual(FederationClient.to_federation_base_url("wss://relay.example/ws"), "https://relay.example/")
        self.assertIsNone(FederationClient.to_federation_base_url("not a url"))


if __name__ == "__main__":
    unittest.main()
