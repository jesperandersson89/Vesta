"""Tests for vesta_client.device_groups — mirrors VestaCore.Tests.Identity.DeviceGroupProjectionTests."""

from __future__ import annotations

import unittest

from vesta_client import (
    DeviceGroupProjection,
    PairingPayload,
    VestaIdentity,
    build_announce,
    build_link,
    build_unlink,
    device_group_channel,
    generate_group_id,
    is_protocol_channel,
)
from vesta_client.identity import b64url_encode


class GenerateGroupIdTests(unittest.TestCase):
    def test_returns_32_hex_chars(self):
        gid = generate_group_id()
        self.assertEqual(32, len(gid))
        self.assertTrue(all(c in "0123456789abcdef" for c in gid))

    def test_channel_id_uses_reserved_prefix(self):
        channel = device_group_channel("abc123")
        self.assertEqual("vesta/identity/abc123", channel)
        self.assertTrue(is_protocol_channel(channel))


class DeviceGroupProjectionTests(unittest.TestCase):
    def test_founder_announce_becomes_trusted_member(self):
        founder = VestaIdentity.generate()
        gid = generate_group_id()
        projection = DeviceGroupProjection(gid)

        projection.apply_local(build_announce(founder, gid))

        state = projection.state
        self.assertEqual(1, len(state.members))
        self.assertTrue(state.is_member(founder.client_id))

    def test_link_from_trusted_member_adds_target(self):
        founder = VestaIdentity.generate()
        new_device = VestaIdentity.generate()
        gid = generate_group_id()
        projection = DeviceGroupProjection(gid)

        projection.apply_local(build_announce(founder, gid))
        projection.apply_local(build_link(founder, gid, new_device.public_key))

        state = projection.state
        self.assertEqual(2, len(state.members))
        self.assertTrue(state.is_member(founder.client_id))
        self.assertTrue(state.is_member(new_device.client_id))

    def test_link_from_untrusted_device_is_ignored(self):
        founder = VestaIdentity.generate()
        outsider = VestaIdentity.generate()
        new_device = VestaIdentity.generate()
        gid = generate_group_id()
        projection = DeviceGroupProjection(gid)

        projection.apply_local(build_announce(founder, gid))
        projection.apply_local(build_link(outsider, gid, new_device.public_key))

        state = projection.state
        self.assertEqual(1, len(state.members))
        self.assertFalse(state.is_member(new_device.client_id))
        self.assertFalse(state.is_member(outsider.client_id))

    def test_transitive_link_propagates_trust(self):
        founder = VestaIdentity.generate()
        device_b = VestaIdentity.generate()
        device_c = VestaIdentity.generate()
        gid = generate_group_id()
        projection = DeviceGroupProjection(gid)

        projection.apply_local(build_announce(founder, gid))
        projection.apply_local(build_link(founder, gid, device_b.public_key))
        projection.apply_local(build_link(device_b, gid, device_c.public_key))

        state = projection.state
        self.assertEqual(3, len(state.members))
        self.assertTrue(state.is_member(device_c.client_id))

    def test_unlink_removes_target(self):
        founder = VestaIdentity.generate()
        device_b = VestaIdentity.generate()
        gid = generate_group_id()
        projection = DeviceGroupProjection(gid)

        projection.apply_local(build_announce(founder, gid))
        projection.apply_local(build_link(founder, gid, device_b.public_key))
        self.assertTrue(projection.state.is_member(device_b.client_id))

        projection.apply_local(build_unlink(founder, gid, device_b.public_key))
        self.assertFalse(projection.state.is_member(device_b.client_id))


class PairingPayloadTests(unittest.TestCase):
    def test_round_trip(self):
        identity = VestaIdentity.generate()
        payload = PairingPayload(
            group_id="abc123",
            public_key=b64url_encode(identity.public_key),
            server_url="wss://vesta.example/ws",
        )

        encoded = payload.to_base64()
        decoded = PairingPayload.from_base64(encoded)

        self.assertEqual(payload.group_id, decoded.group_id)
        self.assertEqual(payload.public_key, decoded.public_key)
        self.assertEqual(payload.server_url, decoded.server_url)

    def test_round_trip_without_server_url(self):
        payload = PairingPayload(group_id="g", public_key="pk")
        decoded = PairingPayload.from_base64(payload.to_base64())
        self.assertIsNone(decoded.server_url)


if __name__ == "__main__":
    unittest.main()
