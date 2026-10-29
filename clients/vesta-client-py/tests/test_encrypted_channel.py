"""Encrypted channel glue (phase C) through an in-process fake relay; mirrors the C# and TS flow tests."""

from __future__ import annotations

import asyncio
import json
import unittest
from datetime import datetime, timezone

from vesta_client import (
    EncryptedChannel,
    EncryptedChannelOptions,
    InMemoryChannelKeyStore,
    VestaConnection,
    VestaIdentity,
    is_envelope,
    try_parse_key_grant,
    try_parse_key_request,
)
from vesta_client.types import ErrorMessage, VestaEvent

NIL = "00000000-0000-0000-0000-000000000000"


class _FakeSocket:
    def __init__(self, relay: "_FakeRelay", client_id: str) -> None:
        self.relay = relay
        self.client_id = client_id
        self.subscriptions: set[str] = set()
        self._incoming: asyncio.Queue[str | None] = asyncio.Queue()

    async def send(self, data: str) -> None:
        self.relay.handle(self, json.loads(data))

    def __aiter__(self) -> "_FakeSocket":
        return self

    async def __anext__(self) -> str:
        msg = await self._incoming.get()
        if msg is None:
            raise StopAsyncIteration
        return msg

    async def close(self) -> None:
        self.relay.sockets.discard(self)
        await self._incoming.put(None)

    def inject(self, msg: dict) -> None:
        self._incoming.put_nowait(json.dumps(msg))


class _FakeRelay:
    def __init__(self) -> None:
        self.sockets: set[_FakeSocket] = set()
        self.sequences: dict[str, int] = {}
        self.stored: dict[str, list[dict]] = {}
        self.members: dict[str, set[str]] = {}
        self.encrypted: set[str] = set()

    def socket(self, client_id: str) -> _FakeSocket:
        sock = _FakeSocket(self, client_id)
        self.sockets.add(sock)
        return sock

    def handle(self, sock: _FakeSocket, msg: dict) -> None:  # noqa: C901 - a small protocol switch
        t = msg["type"]
        if t == "CREATE_CHANNEL":
            cid = msg["channelId"]
            if cid in self.members:
                sock.inject({"type": "ERROR", "code": "CHANNEL_EXISTS", "message": "exists", "channelId": cid})
                return
            self.members[cid] = {sock.client_id, *msg["initialMembers"]}
            if msg.get("encrypted"):
                self.encrypted.add(cid)
            sock.subscriptions.add(cid)
            sock.inject({"type": "ACK", "channelId": cid, "eventId": NIL, "sequence": 0})
        elif t == "GRANT_ACCESS":
            self.members.setdefault(msg["channelId"], set()).add(msg["clientId"])
            sock.inject({"type": "ACK", "channelId": msg["channelId"], "eventId": NIL, "sequence": 0})
        elif t == "REVOKE_ACCESS":
            self.members.get(msg["channelId"], set()).discard(msg["clientId"])
            sock.inject({"type": "ACK", "channelId": msg["channelId"], "eventId": NIL, "sequence": 0})
        elif t == "SUBSCRIBE":
            cid = msg["channelId"]
            members = self.members.get(cid)
            if members is not None and sock.client_id not in members:
                sock.inject({"type": "ERROR", "code": "ACCESS_DENIED", "message": "nope", "channelId": cid})
                return
            sock.subscriptions.add(cid)
            start = msg.get("fromSequence") or 0
            history = [e for e in self.stored.get(cid, []) if e["sequence"] >= start]
            if history:
                sock.inject({"type": "EVENTS_BATCH", "channelId": cid, "events": history})
        elif t == "PUBLISH":
            cid, event = msg["channelId"], msg["event"]
            members = self.members.get(cid)
            if members is not None and sock.client_id not in members:
                sock.inject({"type": "ERROR", "code": "ACCESS_DENIED", "message": "nope", "eventId": event["id"], "channelId": cid})
                return
            if cid in self.encrypted and not event["eventType"].startswith("vesta.") and not is_envelope(event["payload"]):
                sock.inject({"type": "ERROR", "code": "ENCRYPTION_REQUIRED", "message": "plain", "eventId": event["id"], "channelId": cid})
                return
            seq = self.sequences.get(cid, 0) + 1
            self.sequences[cid] = seq
            received = datetime.now(timezone.utc).isoformat()
            self.stored.setdefault(cid, []).append({"event": event, "sequence": seq, "receivedAt": received})
            sock.inject({"type": "ACK", "channelId": cid, "eventId": event["id"], "sequence": seq})
            for s in list(self.sockets):
                if cid in s.subscriptions:
                    s.inject({"type": "EVENT", "channelId": cid, "event": event, "sequence": seq, "receivedAt": received})


async def _connect(relay: _FakeRelay, identity: VestaIdentity) -> VestaConnection:
    conn = VestaConnection(server_url="ws://test", identity=identity, channels=[], auto_reconnect=False)
    sock = relay.socket(identity.client_id)
    conn._ws = sock  # type: ignore[assignment]
    conn._is_connected = True
    conn._features = ["encrypted-channels"]
    conn._receive_task = asyncio.create_task(conn._receive_loop())
    return conn


async def _until(condition, timeout: float = 5.0) -> None:
    loop = asyncio.get_running_loop()
    deadline = loop.time() + timeout
    while not condition():
        if loop.time() > deadline:
            raise TimeoutError("condition not met in time")
        await asyncio.sleep(0.01)


def _stored_events(relay: _FakeRelay, channel_id: str) -> list[VestaEvent]:
    return [VestaEvent.from_dict(e["event"]) for e in relay.stored.get(channel_id, [])]


class EncryptedChannelFlowTests(unittest.TestCase):
    def test_admin_member_publish_rotate_revoke(self) -> None:
        async def _t() -> None:
            relay = _FakeRelay()
            admin_id, member_id = VestaIdentity.generate(), VestaIdentity.generate()
            admin = await _connect(relay, admin_id)
            member = await _connect(relay, member_id)
            cid = "notes/room"

            admin_channel = await admin.create_encrypted_channel(cid, members=[member_id.client_id])
            self.assertTrue(admin_channel.is_admin)
            self.assertEqual(admin_channel.current_epoch, 1)
            admin_seen: list = []
            admin_channel.on_event(admin_seen.append)
            await admin_channel.publish("app.note", {"text": "first"})
            self.assertTrue(is_envelope(relay.stored[cid][0]["event"]["payload"]))

            member_seen: list = []
            member_channel = await member.open_encrypted_channel(cid, EncryptedChannelOptions(admin_public_keys=[admin_id.public_key]))
            member_channel.on_event(member_seen.append)
            await member_channel.wait_for_key()
            self.assertEqual(member_channel.current_epoch, 1)
            await _until(lambda: len(member_seen) >= 1)
            self.assertEqual(member_seen[0].event.event.payload, {"text": "first"})
            self.assertTrue(is_envelope(member_seen[0].sealed.event.payload))

            await member_channel.publish("app.note", {"text": "from member"})
            await _until(lambda: any(e.event.event.payload.get("text") == "from member" for e in admin_seen))

            grants = [e for e in _stored_events(relay, cid) if e.event_type == "vesta.channel-key"]
            self.assertEqual(len(grants), 1)
            self.assertEqual(try_parse_key_grant(grants[0], [admin_id.public_key]).recipient_client_id, member_id.client_id)  # type: ignore[union-attr]

            await admin_channel.remove_member(member_id.client_id)
            self.assertEqual(admin_channel.current_epoch, 2)
            await admin_channel.publish("app.note", {"text": "after rotation"})
            await asyncio.sleep(0.1)
            self.assertFalse(any(e.event.event.payload.get("text") == "after rotation" for e in member_seen))
            self.assertEqual(member_channel.current_epoch, 1)

            errors: list[ErrorMessage] = []
            member.add_listener("error", errors.append)
            await member_channel.publish("app.note", {"text": "still here?"})
            await _until(lambda: any(e.code == "ACCESS_DENIED" for e in errors))

            member_channel.close()
            admin_channel.close()
            await admin.disconnect()
            await member.disconnect()

        asyncio.run(_t())

    def test_member_opening_later_receives_every_epoch_in_order(self) -> None:
        async def _t() -> None:
            relay = _FakeRelay()
            admin_id, member_id = VestaIdentity.generate(), VestaIdentity.generate()
            admin = await _connect(relay, admin_id)
            cid = "notes/late"
            admin_channel = await admin.create_encrypted_channel(cid)
            await admin_channel.publish("app.note", {"n": 1})
            await admin_channel.rotate_key()
            await admin_channel.publish("app.note", {"n": 2})
            await admin.grant_access(cid, member_id.client_id)

            member = await _connect(relay, member_id)
            seen: list[int] = []
            member_channel = await member.open_encrypted_channel(cid, EncryptedChannelOptions(admin_public_keys=[admin_id.public_key]))
            member_channel.on_event(lambda e: seen.append(e.event.event.payload["n"]))
            await member_channel.wait_for_key()
            await _until(lambda: len(seen) == 2)
            self.assertEqual(seen, [1, 2])
            self.assertEqual(member_channel.current_epoch, 2)
            await admin.disconnect()
            await member.disconnect()

        asyncio.run(_t())

    def test_untrusted_grants_ignored_and_admin_reopen_does_not_regrant(self) -> None:
        async def _t() -> None:
            relay = _FakeRelay()
            admin_id, impostor_id, member_id = VestaIdentity.generate(), VestaIdentity.generate(), VestaIdentity.generate()
            admin = await _connect(relay, admin_id)
            member = await _connect(relay, member_id)
            cid = "notes/trust"
            store = InMemoryChannelKeyStore()

            admin_channel = await admin.create_encrypted_channel(
                cid, members=[member_id.client_id, impostor_id.client_id], options=EncryptedChannelOptions(key_store=store)
            )
            member_channel = await member.open_encrypted_channel(cid, EncryptedChannelOptions(admin_public_keys=[admin_id.public_key]))
            await member_channel.wait_for_key()

            impostor = await _connect(relay, impostor_id)
            impostor_channel = EncryptedChannel(impostor, impostor_id, cid, EncryptedChannelOptions(is_admin=True))
            await impostor_channel._create_initial_key()
            await impostor_channel._initialize()
            await asyncio.sleep(0.05)
            self.assertEqual(member_channel.current_epoch, 1)

            admin_channel.close()
            reopened = await admin.open_encrypted_channel(cid, EncryptedChannelOptions(is_admin=True, key_store=store))
            await asyncio.sleep(0.1)
            grants = [e for e in _stored_events(relay, cid) if e.event_type == "vesta.channel-key" and e.client_id == admin_id.client_id]
            self.assertEqual(len(grants), 1)
            self.assertEqual(reopened.current_epoch, 1)
            request = next(e for e in _stored_events(relay, cid) if e.event_type == "vesta.channel-key-request")
            self.assertIsNotNone(try_parse_key_request(request))
            for c in (admin, member, impostor):
                await c.disconnect()

        asyncio.run(_t())

    def test_create_encrypted_channel_raises_when_relay_refuses(self) -> None:
        async def _t() -> None:
            relay = _FakeRelay()
            admin_id = VestaIdentity.generate()
            admin = await _connect(relay, admin_id)
            await admin.create_encrypted_channel("notes/dup")
            with self.assertRaisesRegex(RuntimeError, "CHANNEL_EXISTS"):
                await admin.create_encrypted_channel("notes/dup")
            await admin.disconnect()

        asyncio.run(_t())


if __name__ == "__main__":
    unittest.main()
