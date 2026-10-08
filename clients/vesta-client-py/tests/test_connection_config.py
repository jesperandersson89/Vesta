"""Zero-plumbing VestaConnection surface: app_config auto-wiring, identity-derived client_id,
RelayDirectory.create_default, and the owner relay-manifest helper."""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from vesta_client import (
    InMemoryClientEventStore,
    RelayDirectory,
    RelayOverride,
    VestaAppConfig,
    VestaConnection,
    VestaIdentity,
    load_identity_file,
    manifest_channel_for,
    verify_manifest,
)
from vesta_client.identity import b64url_encode

APP_ID = "zero-plumbing"


def _config(owner: VestaIdentity) -> VestaAppConfig:
    return VestaAppConfig(
        app_id=APP_ID,
        owner_public_key=owner.public_key_b64,
        default_relays=["wss://a.example/ws", "wss://b.example/ws"],
    )


class _TempHome(unittest.IsolatedAsyncioTestCase):
    """Keeps default file-backed stores out of the real home directory."""

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        patcher = mock.patch.object(Path, "home", return_value=Path(self._tmp.name))
        patcher.start()
        self.addCleanup(patcher.stop)


class LoadIdentityFileTests(_TempHome):
    def test_restores_identity_from_exported_json(self) -> None:
        identity = VestaIdentity.generate()
        path = Path(self._tmp.name) / "app.identity.json"
        path.write_text(
            json.dumps({
                "clientId": identity.client_id,
                "publicKey": identity.public_key_b64,
                "privateKey": b64url_encode(identity.export_private_key()),
            }),
            encoding="utf-8",
        )

        loaded = load_identity_file(path)

        self.assertEqual(loaded.client_id, identity.client_id)
        self.assertEqual(loaded.public_key_b64, identity.public_key_b64)


class CreateDefaultTests(_TempHome):
    def test_create_default_resolves_default_relays(self) -> None:
        owner = VestaIdentity.generate()

        directory = RelayDirectory.create_default(_config(owner))

        self.assertEqual(directory.resolve_candidates(), ["wss://a.example/ws", "wss://b.example/ws"])

    def test_create_default_persists_override_under_home_relays_dir(self) -> None:
        owner = VestaIdentity.generate()
        directory = RelayDirectory.create_default(
            VestaAppConfig(app_id="my/app", owner_public_key=owner.public_key_b64, default_relays=["wss://a/ws"])
        )

        directory.set_user_override(RelayOverride(relay="wss://mine.example/ws"))

        stored = Path(self._tmp.name) / ".vesta" / "relays" / "my_app.override.json"
        self.assertTrue(stored.exists())


class ConnectionConfigTests(_TempHome):
    def test_app_config_seeds_relays_and_directory(self) -> None:
        owner = VestaIdentity.generate()

        conn = VestaConnection(app_config=_config(owner), identity=owner, channels=[f"{APP_ID}/room"])

        self.assertEqual(conn.relays, ["wss://a.example/ws", "wss://b.example/ws"])
        self.assertIsNotNone(conn.relay_directory)

    def test_client_id_and_public_key_come_from_identity(self) -> None:
        owner = VestaIdentity.generate()

        conn = VestaConnection(app_config=_config(owner), identity=owner, channels=[f"{APP_ID}/room"])

        self.assertEqual(conn.client_id, owner.client_id)
        self.assertEqual(conn.public_key, owner.public_key_b64)

    def test_positional_server_url_still_works(self) -> None:
        conn = VestaConnection("ws://localhost:5150/ws", "client-1", ["a/b"])

        self.assertEqual(conn.relays, ["ws://localhost:5150/ws"])
        self.assertEqual(conn.client_id, "client-1")

    def test_missing_relay_source_raises(self) -> None:
        with self.assertRaisesRegex(ValueError, "app_config"):
            VestaConnection(client_id="c", channels=["a/b"])

    def test_missing_client_id_raises(self) -> None:
        owner = VestaIdentity.generate()
        with self.assertRaisesRegex(ValueError, "client_id"):
            VestaConnection(app_config=_config(owner), channels=["a/b"])


class PublishRelayManifestTests(_TempHome):
    def _connection(self, config_owner: VestaIdentity, identity: VestaIdentity):
        store = InMemoryClientEventStore()
        conn = VestaConnection(
            app_config=_config(config_owner),
            identity=identity,
            channels=[f"{APP_ID}/room"],
            local_store=store,
        )
        return conn, store

    async def test_owner_publishes_verifiable_manifest_with_next_version(self) -> None:
        owner = VestaIdentity.generate()
        conn, store = self._connection(owner, owner)

        manifest = await conn.publish_relay_manifest(["wss://new.example/ws"])

        self.assertEqual(manifest.version, 1)
        self.assertTrue(verify_manifest(manifest, owner.public_key_b64))
        pending = await store.get_pending_outbox()
        self.assertEqual(len(pending), 1)
        self.assertEqual(pending[0].event.channel_id, manifest_channel_for(APP_ID))
        self.assertEqual(pending[0].event.event_type, "vesta.relay-manifest")

    async def test_non_owner_cannot_publish(self) -> None:
        owner = VestaIdentity.generate()
        stranger = VestaIdentity.generate()
        conn, _ = self._connection(owner, stranger)

        with self.assertRaisesRegex(RuntimeError, "app owner"):
            await conn.publish_relay_manifest(["wss://new.example/ws"])


if __name__ == "__main__":
    unittest.main()
