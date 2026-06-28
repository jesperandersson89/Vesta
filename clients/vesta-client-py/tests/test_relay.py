"""Tests for vesta_client.relay — mirrors VestaCore.Tests.Relay.ManifestSignerTests,
VestaClient.Tests.RelayResolutionTests, and RelayDirectoryTests."""

from __future__ import annotations

import unittest
from dataclasses import replace
from datetime import datetime, timedelta, timezone

from vesta_client import (
    EscapeFallback,
    InMemoryManifestStore,
    InMemoryRelayOverrideStore,
    RelayDirectory,
    RelayEndpoint,
    RelayManifest,
    VestaAppConfig,
    VestaIdentity,
    manifest_channel_for,
    resolve_relay_candidates,
    sign_manifest,
    verify_manifest,
)


def _utc_now_str() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _make_manifest(owner_key: str, version: int = 1, relays=None) -> RelayManifest:
    return RelayManifest(
        app_id="myapp",
        version=version,
        issued_at=_utc_now_str(),
        relays=relays
        or [
            RelayEndpoint(url="wss://r1.example", priority=0, label="primary"),
            RelayEndpoint(url="wss://r2.example", priority=1),
        ],
        owner_public_key=owner_key,
    )


class ManifestChannelTests(unittest.TestCase):
    def test_channel_for_uses_app_namespace(self):
        self.assertEqual("myapp/vesta/relays", manifest_channel_for("myapp"))


class ManifestSigningTests(unittest.TestCase):
    def test_sign_then_verify_succeeds(self):
        owner = VestaIdentity.generate()
        manifest = _make_manifest(owner.public_key_b64)
        signed = sign_manifest(manifest, owner)
        self.assertIsNotNone(signed.signature)
        self.assertTrue(verify_manifest(signed, owner.public_key_b64))

    def test_verify_fails_when_unsigned(self):
        owner = VestaIdentity.generate()
        manifest = _make_manifest(owner.public_key_b64)
        self.assertFalse(verify_manifest(manifest, owner.public_key_b64))

    def test_verify_fails_with_wrong_trust_anchor(self):
        owner = VestaIdentity.generate()
        attacker = VestaIdentity.generate()
        signed = sign_manifest(_make_manifest(owner.public_key_b64), owner)
        self.assertFalse(verify_manifest(signed, attacker.public_key_b64))

    def test_verify_fails_when_relays_tampered(self):
        owner = VestaIdentity.generate()
        signed = sign_manifest(_make_manifest(owner.public_key_b64), owner)
        tampered = replace(
            signed,
            relays=[RelayEndpoint(url="wss://evil.example", priority=0)],
        )
        self.assertFalse(verify_manifest(tampered, owner.public_key_b64))

    def test_verify_fails_when_version_tampered(self):
        owner = VestaIdentity.generate()
        signed = sign_manifest(_make_manifest(owner.public_key_b64), owner)
        tampered = replace(signed, version=99)
        self.assertFalse(verify_manifest(tampered, owner.public_key_b64))

    def test_sign_rejects_owner_key_mismatch(self):
        owner = VestaIdentity.generate()
        other = VestaIdentity.generate()
        manifest = _make_manifest(other.public_key_b64)
        with self.assertRaises(ValueError):
            sign_manifest(manifest, owner)


class ResolveCandidatesTests(unittest.TestCase):
    def test_override_takes_precedence(self):
        result = resolve_relay_candidates(
            defaults=["wss://default.example"],
            user_override="wss://override.example",
            manifest_relays=["wss://manifest.example"],
        )
        self.assertEqual(
            ["wss://override.example", "wss://manifest.example", "wss://default.example"],
            result,
        )

    def test_manifest_before_defaults(self):
        result = resolve_relay_candidates(
            defaults=["wss://default.example"],
            manifest_relays=["wss://manifest.example"],
        )
        self.assertEqual(["wss://manifest.example", "wss://default.example"], result)

    def test_deduplicates_preserving_order(self):
        result = resolve_relay_candidates(
            defaults=["wss://a.example", "wss://b.example"],
            user_override="wss://a.example",
        )
        self.assertEqual(["wss://a.example", "wss://b.example"], result)

    def test_empty_raises(self):
        with self.assertRaises(ValueError):
            resolve_relay_candidates(defaults=[])


class RelayDirectoryTests(unittest.TestCase):
    def _config(self, owner_key: str) -> VestaAppConfig:
        return VestaAppConfig(
            app_id="myapp",
            owner_public_key=owner_key,
            default_relays=["wss://default.example"],
        )

    def test_applies_owner_signed_manifest(self):
        owner = VestaIdentity.generate()
        directory = RelayDirectory(self._config(owner.public_key_b64))
        signed = sign_manifest(_make_manifest(owner.public_key_b64), owner)
        self.assertTrue(directory.try_apply_manifest(signed))
        self.assertEqual(
            ["wss://r1.example", "wss://r2.example", "wss://default.example"],
            directory.resolve_candidates(),
        )

    def test_rejects_forged_manifest(self):
        owner = VestaIdentity.generate()
        attacker = VestaIdentity.generate()
        directory = RelayDirectory(self._config(owner.public_key_b64))
        forged = sign_manifest(_make_manifest(attacker.public_key_b64), attacker)
        self.assertFalse(directory.try_apply_manifest(forged))

    def test_rejects_wrong_app_id(self):
        owner = VestaIdentity.generate()
        directory = RelayDirectory(self._config(owner.public_key_b64))
        manifest = replace(_make_manifest(owner.public_key_b64), app_id="otherapp")
        signed = sign_manifest(manifest, owner)
        self.assertFalse(directory.try_apply_manifest(signed))

    def test_anti_rollback(self):
        owner = VestaIdentity.generate()
        directory = RelayDirectory(self._config(owner.public_key_b64))
        v2 = sign_manifest(_make_manifest(owner.public_key_b64, version=2), owner)
        self.assertTrue(directory.try_apply_manifest(v2))
        v1 = sign_manifest(_make_manifest(owner.public_key_b64, version=1), owner)
        self.assertFalse(directory.try_apply_manifest(v1))

    def test_escape_fallback_respects_valid_from(self):
        owner = VestaIdentity.generate()
        directory = RelayDirectory(self._config(owner.public_key_b64))
        future = (datetime.now(timezone.utc) + timedelta(days=1)).strftime(
            "%Y-%m-%dT%H:%M:%SZ"
        )
        past = (datetime.now(timezone.utc) - timedelta(days=1)).strftime(
            "%Y-%m-%dT%H:%M:%SZ"
        )
        manifest = replace(
            _make_manifest(owner.public_key_b64),
            escape_fallbacks=[
                EscapeFallback(url="wss://future.example", valid_from=future),
                EscapeFallback(url="wss://active.example", valid_from=past),
            ],
        )
        signed = sign_manifest(manifest, owner)
        self.assertTrue(directory.try_apply_manifest(signed))
        candidates = directory.resolve_candidates()
        self.assertIn("wss://active.example", candidates)
        self.assertNotIn("wss://future.example", candidates)

    def test_loads_verified_cached_manifest_on_construct(self):
        owner = VestaIdentity.generate()
        store = InMemoryManifestStore()
        signed = sign_manifest(_make_manifest(owner.public_key_b64), owner)
        store.save(signed)
        directory = RelayDirectory(
            self._config(owner.public_key_b64), manifest_store=store
        )
        self.assertIsNotNone(directory.current_manifest)

    def test_ignores_forged_cached_manifest_on_construct(self):
        owner = VestaIdentity.generate()
        attacker = VestaIdentity.generate()
        store = InMemoryManifestStore()
        store.save(sign_manifest(_make_manifest(attacker.public_key_b64), attacker))
        directory = RelayDirectory(
            self._config(owner.public_key_b64), manifest_store=store
        )
        self.assertIsNone(directory.current_manifest)

    def test_user_override_resolves_first(self):
        owner = VestaIdentity.generate()
        override_store = InMemoryRelayOverrideStore()
        directory = RelayDirectory(
            self._config(owner.public_key_b64), override_store=override_store
        )
        directory.set_user_override("wss://mine.example")
        self.assertEqual("wss://mine.example", directory.resolve_candidates()[0])


if __name__ == "__main__":
    unittest.main()
