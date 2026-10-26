"""Identity backup, recovery phrase and relay-stored backup. Mirrors tests/VestaCore.Tests/IdentityBackupTests.cs."""

from __future__ import annotations

import json
import unittest
from dataclasses import replace

from vesta_client import (
    IdentityBackupError,
    IdentityBackupOptions,
    RecoveryPhraseError,
    RelayBackup,
    VestaIdentity,
    recovery_phrase_from_seed,
    seed_from_recovery_phrase,
    verify_event,
)

FAST = IdentityBackupOptions(memory_kib=256, iterations=1)


class RecoveryPhraseTests(unittest.TestCase):
    def test_bip39_vectors(self) -> None:
        self.assertEqual(recovery_phrase_from_seed(bytes(32)), " ".join(["abandon"] * 23 + ["art"]))
        self.assertEqual(recovery_phrase_from_seed(b"\xff" * 32), " ".join(["zoo"] * 23 + ["vote"]))

    def test_round_trip_and_tolerance(self) -> None:
        identity = VestaIdentity.generate()
        phrase = identity.to_recovery_phrase()

        self.assertEqual(len(phrase.split()), 24)
        self.assertEqual(VestaIdentity.from_recovery_phrase(phrase).client_id, identity.client_id)
        self.assertEqual(seed_from_recovery_phrase("  " + phrase.upper().replace(" ", "\n ")), identity.export_private_key())

    def test_rejects_bad_phrases(self) -> None:
        for phrase in (" ".join(["abandon"] * 12), " ".join(["abandon"] * 23 + ["notaword"]), " ".join(["abandon"] * 24)):
            with self.assertRaises(RecoveryPhraseError):
                seed_from_recovery_phrase(phrase)


class IdentityBackupTests(unittest.TestCase):
    def test_round_trip(self) -> None:
        identity = VestaIdentity.generate()

        restored = VestaIdentity.import_encrypted(identity.export_encrypted("correct horse", FAST), "correct horse")

        self.assertEqual(restored.client_id, identity.client_id)
        self.assertEqual(restored.export_private_key(), identity.export_private_key())

    def test_wrong_password_tamper_and_malformed(self) -> None:
        identity = VestaIdentity.generate()
        other = VestaIdentity.generate()
        blob = identity.export_encrypted("right", FAST)

        with self.assertRaises(IdentityBackupError):
            VestaIdentity.import_encrypted(blob, "wrong")
        with self.assertRaises(IdentityBackupError):
            VestaIdentity.import_encrypted(blob.replace(identity.client_id, other.client_id), "right")
        for bad in ("not json", "{}", json.dumps({"format": "x", "version": 1})):
            with self.assertRaises(IdentityBackupError):
                VestaIdentity.import_encrypted(bad, "right")

    def test_unicode_password_forms_agree(self) -> None:
        identity = VestaIdentity.generate()

        restored = VestaIdentity.import_encrypted(identity.export_encrypted("caf\u00e9", FAST), "cafe\u0301")

        self.assertEqual(restored.client_id, identity.client_id)

    def test_abusive_kdf_settings_are_refused(self) -> None:
        identity = VestaIdentity.generate()
        blob = json.loads(identity.export_encrypted("pw", FAST))
        blob["kdf"]["memoryKiB"] = 4_000_000

        with self.assertRaises(IdentityBackupError):
            VestaIdentity.import_encrypted(json.dumps(blob), "pw")
        with self.assertRaises(IdentityBackupError):
            identity.export_encrypted("pw", IdentityBackupOptions(memory_kib=1, iterations=1))


class RelayBackupTests(unittest.TestCase):
    def test_channel_is_deterministic_and_depends_on_every_input(self) -> None:
        channel = RelayBackup.derive("game", "alice", "pw", FAST).channel_id

        self.assertEqual(channel, RelayBackup.derive("game", "  Alice ", "pw", FAST).channel_id)
        for other in (("other", "alice", "pw"), ("game", "bob", "pw"), ("game", "alice", "other")):
            self.assertNotEqual(channel, RelayBackup.derive(*other, FAST).channel_id)
        self.assertRegex(channel, r"^vesta/identity-backup/[0-9a-f]{32}$")

    def test_event_is_signed_replace_and_restores(self) -> None:
        identity = VestaIdentity.generate()
        backup = RelayBackup.derive("game", "alice", "pw", FAST)

        event = backup.create_event(identity)

        self.assertTrue(event.replace)
        self.assertTrue(verify_event(event, identity.public_key))
        restored = RelayBackup.derive("game", "alice", "pw", FAST).restore([event])
        self.assertIsNotNone(restored)
        self.assertEqual(restored.client_id, identity.client_id)

    def test_restore_skips_junk_and_wrong_passwords(self) -> None:
        identity = VestaIdentity.generate()
        attacker = VestaIdentity.generate()
        backup = RelayBackup.derive("game", "alice", "pw", FAST)
        real = backup.create_event(identity)
        junk = replace(RelayBackup.derive("game", "alice", "attacker-pw", FAST).create_event(attacker), channel_id=backup.channel_id)
        garbage = replace(real, payload={"hello": "world"})

        self.assertEqual(backup.restore([junk, garbage, real]).client_id, identity.client_id)
        self.assertIsNone(RelayBackup.derive("game", "alice", "wrong", FAST).restore([real]))
        self.assertIsNone(RelayBackup.derive("game", "alice", "pw", IdentityBackupOptions(512, 1)).restore([real]))


if __name__ == "__main__":
    unittest.main()
