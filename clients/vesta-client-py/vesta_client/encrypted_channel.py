"""
An end-to-end encrypted channel on a :class:`VestaConnection` (PLANNING #33 phase C). Encrypts on
:meth:`EncryptedChannel.publish`, decrypts incoming events into the ``on_event`` callback (payload replaced
by the plaintext; the original ciphertext event is ``sealed``), requests keys from an admin when it has
none, buffers events whose epoch key has not arrived, and - when opened as admin - answers key requests
and rotates keys. The relay never sees a key. Mirrors the C# ``EncryptedChannel``.
"""

from __future__ import annotations

import asyncio
import logging
from collections.abc import Callable
from dataclasses import dataclass, field, replace
from datetime import datetime, timezone
from typing import TYPE_CHECKING, Any
from uuid import uuid4

from vesta_client.channel_crypto import (
    ChannelCryptoError,
    ChannelKey,
    ChannelKeyPair,
    SealedChannelKey,
    decrypt_payload,
    encrypt_payload,
    open_key,
    read_epoch,
    seal_key,
)
from vesta_client.channel_key_events import (
    KEY_GRANT_EVENT_TYPE,
    KEY_REQUEST_EVENT_TYPE,
    ChannelKeyRequest,
    build_key_grant,
    build_key_request,
    try_parse_key_grant,
    try_parse_key_request,
)
from vesta_client.channel_key_store import ChannelKeyStore, InMemoryChannelKeyStore
from vesta_client.identity import VestaIdentity
from vesta_client.signing import sign_event
from vesta_client.types import EventMessage, EventsBatchMessage, SequencedEvent, VestaEvent

if TYPE_CHECKING:
    from vesta_client.connection import VestaConnection

logger = logging.getLogger("vesta_client.encrypted")


@dataclass
class EncryptedChannelOptions:
    """Options for ``VestaConnection.open_encrypted_channel`` / ``create_encrypted_channel``."""

    # Where keys (and, for admins, member keys) live. Defaults to an in-memory store.
    key_store: ChannelKeyStore | None = None
    # Ed25519 public keys of the admins whose ``vesta.channel-key`` events this client trusts.
    admin_public_keys: list[bytes] = field(default_factory=list)
    # This identity is a channel admin: it answers key requests and can rotate the key.
    is_admin: bool = False
    # Admin-only hook to approve a key request (the relay already restricts publishing to members).
    approve_key_request: Callable[[ChannelKeyRequest], bool] | None = None
    # Catch-up position for the subscription. 0 so historical key grants are replayed.
    from_sequence: int = 0
    # Always publish a key request on open, even when the store already holds keys.
    always_request_keys: bool = False
    # How long ``create_encrypted_channel`` waits for the relay's control ACK.
    create_timeout: float = 10.0


@dataclass(frozen=True)
class DecryptedEvent:
    """A decrypted event: ``event`` carries the plaintext payload, ``sealed`` the original."""

    event: SequencedEvent
    sealed: SequencedEvent


class EncryptedChannel:
    def __init__(self, connection: "VestaConnection", identity: VestaIdentity, channel_id: str, options: EncryptedChannelOptions) -> None:
        self.channel_id = channel_id
        self._connection = connection
        self._identity = identity
        self._options = options
        self._store = options.key_store or InMemoryChannelKeyStore()
        self._key_pair = ChannelKeyPair.from_identity(identity)
        self._admin_keys = list(options.admin_public_keys)
        if options.is_admin and identity.public_key not in self._admin_keys:
            self._admin_keys.append(identity.public_key)
        if not options.is_admin and not self._admin_keys:
            raise ValueError("A member must be given at least one admin public key to trust.")
        self._keys: dict[int, ChannelKey] = {}
        self._pending: dict[int, dict[int, SequencedEvent]] = {}
        self._granted_epochs: dict[str, int] = {}
        self._key_available: asyncio.Event = asyncio.Event()
        self._admin_lock = asyncio.Lock()
        self._on_event: Callable[[DecryptedEvent], None] | None = None
        self._on_decrypt_failed: Callable[[SequencedEvent, Exception], None] | None = None
        self._on_key_available: Callable[[int], None] | None = None
        self._on_key_granted: Callable[[ChannelKeyRequest], None] | None = None
        self._tasks: set[asyncio.Task] = set()
        self._unsubscribe = [
            connection.add_listener("event", self._handle_event),
            connection.add_listener("events_batch", self._handle_batch),
        ]

    # ── Callbacks ─────────────────────────────────────────────────────────────

    def on_event(self, callback: Callable[[DecryptedEvent], None]) -> None:
        """Every app event on the channel once decrypted, in sequence order per epoch."""
        self._on_event = callback

    def on_decrypt_failed(self, callback: Callable[[SequencedEvent, Exception], None]) -> None:
        self._on_decrypt_failed = callback

    def on_key_available(self, callback: Callable[[int], None]) -> None:
        self._on_key_available = callback

    def on_key_granted(self, callback: Callable[[ChannelKeyRequest], None]) -> None:
        """Admin only: a key request was answered."""
        self._on_key_granted = callback

    # ── State ─────────────────────────────────────────────────────────────────

    @property
    def is_admin(self) -> bool:
        return self._options.is_admin

    @property
    def current_epoch(self) -> int | None:
        """The newest epoch this client holds a key for, or ``None`` before the first key arrives."""
        return max(self._keys) if self._keys else None

    @property
    def has_keys(self) -> bool:
        return bool(self._keys)

    async def wait_for_key(self, timeout: float = 10.0) -> None:
        """Wait until a key is available (returns immediately when one already is)."""
        if self._keys:
            return
        await asyncio.wait_for(self._key_available.wait(), timeout)

    # ── Lifecycle (called by VestaConnection) ─────────────────────────────────

    async def _initialize(self) -> None:
        for key in await self._store.load_keys(self.channel_id):
            self._add_key(key, announce=False)
        await self._connection.subscribe(self.channel_id, self._options.from_sequence)
        if not self.is_admin and (self._options.always_request_keys or not self.has_keys):
            await self._connection.publish(build_key_request(self._identity, self.channel_id, self._key_pair))

    async def _create_initial_key(self) -> None:
        key = ChannelKey.generate(1)
        await self._store.save_key(self.channel_id, key)
        self._add_key(key, announce=True)

    def close(self) -> None:
        """Stop listening on the connection."""
        for unsubscribe in self._unsubscribe:
            unsubscribe()
        self._unsubscribe = []

    # ── Publishing ────────────────────────────────────────────────────────────

    async def publish(
        self,
        event_type: str,
        payload: Any,
        *,
        replace_previous: bool = False,
        metadata: dict[str, Any] | None = None,
        parent_id: str | None = None,
    ) -> VestaEvent:
        """Encrypt ``payload`` under the newest key and publish it."""
        if event_type.startswith("vesta."):
            raise ValueError("Application events on an encrypted channel must not use the reserved 'vesta.' prefix.")
        epoch = self.current_epoch
        if epoch is None:
            raise RuntimeError("No channel key yet; await wait_for_key() before publishing.")
        key = self._keys[epoch]
        event_id = str(uuid4())
        envelope = encrypt_payload(key, self.channel_id, event_id, event_type, payload)
        event = VestaEvent(
            id=event_id,
            channel_id=self.channel_id,
            timestamp=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z",
            client_id=self._identity.client_id,
            event_type=event_type,
            payload=envelope,
            replace=replace_previous,
            parent_id=parent_id,
            metadata=metadata,
        )
        event = sign_event(event, self._identity)
        await self._connection.publish(event)
        return event

    # ── Admin operations ──────────────────────────────────────────────────────

    async def rotate_key(self) -> None:
        """Admin only: start a new epoch and seal it to every member in the store."""
        self._require_admin()
        async with self._admin_lock:
            key = ChannelKey.generate((self.current_epoch or 0) + 1)
            await self._store.save_key(self.channel_id, key)
            self._add_key(key, announce=True)
            for client_id, x25519 in (await self._store.load_members(self.channel_id)).items():
                await self._publish_grant(client_id, x25519, [key])

    async def remove_member(self, client_id: str) -> None:
        """Admin only: revoke the member on the relay, forget its key, and rotate so it cannot read new events."""
        self._require_admin()
        await self._connection.revoke_access(self.channel_id, client_id)
        await self._store.remove_member(self.channel_id, client_id)
        self._granted_epochs.pop(client_id, None)
        await self.rotate_key()

    # ── Incoming traffic ──────────────────────────────────────────────────────

    def _handle_event(self, msg: EventMessage) -> None:
        if msg.channel_id != self.channel_id:
            return
        self._spawn(self._process([SequencedEvent(msg.event, msg.sequence, msg.received_at)], live=True))

    def _handle_batch(self, msg: EventsBatchMessage) -> None:
        if msg.channel_id != self.channel_id or not msg.events:
            return
        self._spawn(self._process(list(msg.events), live=False))

    def _spawn(self, coro: Any) -> None:
        task = asyncio.ensure_future(coro)
        self._tasks.add(task)
        task.add_done_callback(self._tasks.discard)

    async def _process(self, events: list[SequencedEvent], live: bool) -> None:
        try:
            # Pass 1: grants first, so a historical request that was already answered is not answered again.
            for sequenced in events:
                if sequenced.event.event_type != KEY_GRANT_EVENT_TYPE:
                    continue
                grant = try_parse_key_grant(sequenced.event, self._admin_keys)
                if grant is None:
                    continue
                self._note_granted(grant.recipient_client_id, max(k.epoch for k in grant.keys))
                if grant.recipient_client_id == self._identity.client_id:
                    await self._open_grant(grant.keys)
            # Pass 2: requests (admin only).
            if self.is_admin:
                for sequenced in events:
                    if sequenced.event.event_type != KEY_REQUEST_EVENT_TYPE:
                        continue
                    request = try_parse_key_request(sequenced.event)
                    if request is None or request.client_id == self._identity.client_id:
                        continue
                    await self._answer_request(request)
            # Pass 3: app events, in order.
            for sequenced in events:
                if sequenced.event.event_type.startswith("vesta."):
                    continue
                self._deliver(sequenced)
        except Exception as exc:  # noqa: BLE001
            logger.exception("Processing encrypted channel traffic failed")
            if not live and self._on_decrypt_failed:
                self._on_decrypt_failed(events[0], exc)

    async def _open_grant(self, sealed_keys: list[SealedChannelKey]) -> None:
        for sealed in sealed_keys:
            if sealed.epoch in self._keys:
                continue
            try:
                key = open_key(sealed, self.channel_id, self._identity.client_id, self._key_pair)
            except ChannelCryptoError:
                continue
            await self._store.save_key(self.channel_id, key)
            self._add_key(key, announce=True)

    async def _answer_request(self, request: ChannelKeyRequest) -> None:
        if self._options.approve_key_request is not None and not self._options.approve_key_request(request):
            return
        async with self._admin_lock:
            await self._store.save_member(self.channel_id, request.client_id, request.x25519_public_key)
            current = self.current_epoch
            if current is None:
                return
            if self._granted_epochs.get(request.client_id, -1) >= current:
                return
            keys = [self._keys[e] for e in sorted(self._keys)]
            await self._publish_grant(request.client_id, request.x25519_public_key, keys)
            if self._on_key_granted:
                self._on_key_granted(request)

    async def _publish_grant(self, recipient_client_id: str, recipient_x25519: bytes, keys: list[ChannelKey]) -> None:
        sealed = [seal_key(k, self.channel_id, recipient_client_id, recipient_x25519) for k in keys]
        self._note_granted(recipient_client_id, max(k.epoch for k in keys))
        await self._connection.publish(build_key_grant(self._identity, self.channel_id, recipient_client_id, sealed))

    def _note_granted(self, recipient_client_id: str, epoch: int) -> None:
        if self._granted_epochs.get(recipient_client_id, -1) < epoch:
            self._granted_epochs[recipient_client_id] = epoch

    def _deliver(self, sequenced: SequencedEvent) -> None:
        try:
            epoch = read_epoch(sequenced.event.payload)
        except ChannelCryptoError as exc:
            if self._on_decrypt_failed:
                self._on_decrypt_failed(sequenced, exc)
            return
        key = self._keys.get(epoch)
        if key is None:
            self._pending.setdefault(epoch, {})[sequenced.sequence] = sequenced
            return
        self._decrypt(sequenced, key)

    def _decrypt(self, sequenced: SequencedEvent, key: ChannelKey) -> None:
        try:
            plaintext = decrypt_payload(key, self.channel_id, sequenced.event.id, sequenced.event.event_type, sequenced.event.payload)
        except ChannelCryptoError as exc:
            if self._on_decrypt_failed:
                self._on_decrypt_failed(sequenced, exc)
            return
        decrypted = replace(sequenced, event=replace(sequenced.event, payload=plaintext))
        if self._on_event:
            self._on_event(DecryptedEvent(decrypted, sequenced))

    def _add_key(self, key: ChannelKey, announce: bool) -> None:
        self._keys[key.epoch] = key
        self._key_available.set()
        if announce and self._on_key_available:
            self._on_key_available(key.epoch)
        queue = self._pending.pop(key.epoch, None)
        if queue:
            for sequence in sorted(queue):
                self._decrypt(queue[sequence], key)

    def _require_admin(self) -> None:
        if not self.is_admin:
            raise RuntimeError("Only a channel admin (EncryptedChannelOptions.is_admin) can do this.")
