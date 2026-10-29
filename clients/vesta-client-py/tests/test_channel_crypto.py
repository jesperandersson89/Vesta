"""Shared #33 phase-B vectors; identical to ChannelCryptoTests.cs and channel-crypto.test.mjs."""

from __future__ import annotations

import unittest
from dataclasses import replace

from vesta_client import VestaIdentity
from vesta_client.channel_crypto import (
    ChannelCryptoError,
    ChannelKey,
    ChannelKeyPair,
    SealedChannelKey,
    decrypt_payload,
    encrypt_payload,
    is_envelope,
    open_key,
    read_epoch,
    seal_key,
)
from vesta_client.identity import b64url_decode, b64url_encode

SEED_A = bytes(range(0, 32))
SEED_B = bytes(range(32, 64))
X25519_PRIV_A = "bWmf93nEz6Ln3iFyUFjrQzEo5HTi4hcLCL4rNXcQgSY"
X25519_PUB_A = "FOghQ_QoWARIegjdmUQMvxDlo52xNSuxywtywgqy-BU"
X25519_PRIV_B = "0lw_0gknxoNrVg9GMMaRk4nNGGtsc0at7L-sn1cUXI4"
X25519_PUB_B = "N1z9KAjpCNOuEulV1jAnYyChQxyuXMaA5ILsIluV7ms"
CLIENT_ID_B = "JPbtasv-EAnAMNfKVnwzyk"

KEY = ChannelKey(1, bytes([0x42]) * 32)
CHANNEL_ID = "notes/secret"
EVENT_ID = "0190f7e2-0000-7000-8000-000000000001"
EVENT_TYPE = "app.note"
NONCE = bytes(range(0, 12))
EXPECTED_CT = "fvz__4sd3inpYzxn8-d5U1fxogWCNBPNbm8s9i1AGezwveYgVlM"

EPH_PRIV = bytes([0x07]) * 32
EPH_PUB = "E75P6uryBMf9M1j8nAByGIHRdCeBKCJ-xnTzf3_pe20"
SEAL_NONCE = bytes(range(16, 28))
SEALED_CT = "9unGHrePRVJRMfa0kInth8NBHJHdikR2Bs9so3BpuVmbUrQja7pEObBBpmmLyB96"


class ChannelCryptoTests(unittest.TestCase):
    def test_from_seed_derives_the_shared_x25519_vectors(self) -> None:
        a = ChannelKeyPair.from_seed(SEED_A)
        b = ChannelKeyPair.from_seed(SEED_B)
        self.assertEqual(b64url_encode(a.export_private_key()), X25519_PRIV_A)
        self.assertEqual(b64url_encode(a.public_key), X25519_PUB_A)
        self.assertEqual(b64url_encode(b.export_private_key()), X25519_PRIV_B)
        self.assertEqual(b64url_encode(b.public_key), X25519_PUB_B)

    def test_from_identity_matches_from_seed_and_client_id_vector(self) -> None:
        identity = VestaIdentity.from_private_key(SEED_B)
        self.assertEqual(identity.client_id, CLIENT_ID_B)
        self.assertEqual(b64url_encode(ChannelKeyPair.from_identity(identity).public_key), X25519_PUB_B)

    def test_encrypt_payload_with_fixed_nonce_matches_vector_and_canonicalises(self) -> None:
        envelope = encrypt_payload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, {"text": "hello", "n": 1}, nonce=NONCE)
        self.assertTrue(is_envelope(envelope))
        self.assertEqual(envelope, {"enc": 1, "kid": 1, "n": "AAECAwQFBgcICQoL", "ct": EXPECTED_CT})

    def test_decrypt_payload_round_trips_and_rejects_any_aad_change(self) -> None:
        envelope = encrypt_payload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, {"text": "hello", "n": 1}, nonce=NONCE)
        self.assertEqual(decrypt_payload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, envelope), {"n": 1, "text": "hello"})

        for args in (
            (KEY, "notes/other", EVENT_ID, EVENT_TYPE),
            (KEY, CHANNEL_ID, "0190f7e2-0000-7000-8000-000000000002", EVENT_TYPE),
            (KEY, CHANNEL_ID, EVENT_ID, "app.other"),
            (replace(KEY, key=bytes(32)), CHANNEL_ID, EVENT_ID, EVENT_TYPE),
            (replace(KEY, epoch=2), CHANNEL_ID, EVENT_ID, EVENT_TYPE),
        ):
            with self.assertRaises(ChannelCryptoError):
                decrypt_payload(*args, envelope)

    def test_event_id_case_is_normalised_in_the_aad(self) -> None:
        envelope = encrypt_payload(KEY, CHANNEL_ID, EVENT_ID.upper(), EVENT_TYPE, {"a": 1}, nonce=NONCE)
        self.assertEqual(decrypt_payload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, envelope), {"a": 1})

    def test_random_nonce_envelopes_differ_and_both_decrypt(self) -> None:
        first = encrypt_payload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, {"x": True})
        second = encrypt_payload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, {"x": True})
        self.assertNotEqual(first["n"], second["n"])
        self.assertEqual(decrypt_payload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, first), {"x": True})
        self.assertEqual(decrypt_payload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, second), {"x": True})

    def test_scalar_and_array_payloads_round_trip(self) -> None:
        for payload in (1, "text", True, None, [3, 1, 2]):
            envelope = encrypt_payload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, payload)
            self.assertEqual(decrypt_payload(KEY, CHANNEL_ID, EVENT_ID, EVENT_TYPE, envelope), payload)

    def test_read_epoch_and_is_envelope(self) -> None:
        envelope = encrypt_payload(replace(KEY, epoch=7), CHANNEL_ID, EVENT_ID, EVENT_TYPE, {"a": 1})
        self.assertEqual(read_epoch(envelope), 7)
        self.assertFalse(is_envelope({"text": "hi"}))
        self.assertFalse(is_envelope({**envelope, "extra": 1}))
        self.assertFalse(is_envelope({**envelope, "enc": True}))
        with self.assertRaises(ChannelCryptoError):
            read_epoch({"text": "hi"})

    def test_seal_key_with_fixed_ephemeral_and_nonce_matches_vector(self) -> None:
        ephemeral = ChannelKeyPair.from_private_key(EPH_PRIV)
        sealed = seal_key(KEY, CHANNEL_ID, CLIENT_ID_B, b64url_decode(X25519_PUB_B), ephemeral=ephemeral, nonce=SEAL_NONCE)
        self.assertEqual(sealed, SealedChannelKey(1, EPH_PUB, "EBESExQVFhcYGRob", SEALED_CT))

    def test_open_key_recovers_the_channel_key_only_for_the_recipient(self) -> None:
        sealed = SealedChannelKey(1, EPH_PUB, "EBESExQVFhcYGRob", SEALED_CT)
        a = ChannelKeyPair.from_seed(SEED_A)
        b = ChannelKeyPair.from_seed(SEED_B)

        self.assertEqual(open_key(sealed, CHANNEL_ID, CLIENT_ID_B, b), KEY)

        for args in (
            (sealed, CHANNEL_ID, CLIENT_ID_B, a),
            (sealed, "notes/other", CLIENT_ID_B, b),
            (sealed, CHANNEL_ID, "someone-else", b),
            (replace(sealed, epoch=2), CHANNEL_ID, CLIENT_ID_B, b),
        ):
            with self.assertRaises(ChannelCryptoError):
                open_key(*args)

    def test_seal_key_random_ephemeral_round_trips_and_survives_dict(self) -> None:
        recipient = ChannelKeyPair.generate()
        key = ChannelKey.generate(3)
        sealed = SealedChannelKey.from_dict(seal_key(key, CHANNEL_ID, "rcpt", recipient.public_key).to_dict())
        self.assertEqual(open_key(sealed, CHANNEL_ID, "rcpt", recipient), key)


if __name__ == "__main__":
    unittest.main()
