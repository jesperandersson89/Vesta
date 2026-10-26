"""Receive-side event verification: lets a client trust an event by signer instead of by relay."""

from __future__ import annotations

import unittest
from dataclasses import replace

from vesta_client import VestaIdentity, create_event, verify_event
from vesta_client.identity import derive_client_id


class VerifyEventTests(unittest.TestCase):
    def _signed(self, identity: VestaIdentity):
        return create_event(
            "game/shop",
            identity.client_id,
            "shop.purchase-confirmed",
            {"item": "scarecrow"},
            identity=identity,
        )

    def test_accepts_an_event_signed_by_the_matching_key(self) -> None:
        shopkeeper = VestaIdentity.generate()
        event = self._signed(shopkeeper)

        self.assertTrue(verify_event(event, shopkeeper.public_key))
        self.assertEqual(event.client_id, derive_client_id(shopkeeper.public_key))

    def test_rejects_other_key_tampered_payload_and_bad_signatures(self) -> None:
        shopkeeper = VestaIdentity.generate()
        impostor = VestaIdentity.generate()
        event = self._signed(shopkeeper)

        self.assertFalse(verify_event(event, impostor.public_key))
        self.assertFalse(verify_event(replace(event, payload={"item": "golden-scarecrow"}), shopkeeper.public_key))
        self.assertFalse(verify_event(replace(event, signature=None), shopkeeper.public_key))
        self.assertFalse(verify_event(replace(event, signature="not base64url!!"), shopkeeper.public_key))

    def test_ignores_unsigned_metadata(self) -> None:
        shopkeeper = VestaIdentity.generate()
        event = self._signed(shopkeeper)

        self.assertTrue(verify_event(replace(event, metadata={"ttlSeconds": 1}), shopkeeper.public_key))


if __name__ == "__main__":
    unittest.main()
