"""Vesta WebSocket connection with auto-reconnect."""

from __future__ import annotations

import asyncio
import json
import logging
from collections.abc import Callable
from datetime import datetime, timezone
from typing import Any

import websockets
from websockets.asyncio.client import ClientConnection

from vesta_client.identity import VestaIdentity
from vesta_client.relay import (
    RELAY_MANIFEST_EVENT_TYPE,
    RelayDirectory,
    RelayManifest,
)
from vesta_client.storage import ClientEventStore
from vesta_client.types import (
    AckMessage,
    ErrorMessage,
    EventMessage,
    EventsBatchMessage,
    SequencedEvent,
    ServerMessage,
    VestaEvent,
    WelcomeMessage,
    parse_server_message,
)

logger = logging.getLogger("vesta_client")


class VestaConnection:
    """
    Async WebSocket connection to a Vesta server.

    Handles the HELLO/WELCOME handshake, message dispatch, and
    automatic reconnection with exponential backoff.
    """

    def __init__(
        self,
        server_url: str,
        client_id: str,
        channels: list[str],
        *,
        relays: list[str] | None = None,
        auto_reconnect: bool = True,
        initial_reconnect_delay: float = 1.0,
        max_reconnect_delay: float = 30.0,
        last_sequences: dict[str, int] | None = None,
        public_key: str | None = None,
        local_store: ClientEventStore | None = None,
        identity: VestaIdentity | None = None,
        relay_directory: RelayDirectory | None = None,
    ):
        self.server_url = server_url
        self._relay_candidates = list(relays) if relays else [server_url]
        self._active_relay_index = 0
        self._notified_relay_index = -1
        self.client_id = client_id
        self._channels = list(channels)
        self.auto_reconnect = auto_reconnect
        self.initial_reconnect_delay = initial_reconnect_delay
        self.max_reconnect_delay = max_reconnect_delay
        self._last_sequences: dict[str, int] = {ch: 0 for ch in channels}
        if last_sequences:
            self._last_sequences.update(last_sequences)
        self._identity = identity
        self.public_key = public_key or (identity.public_key_b64 if identity else None)
        self._local_store = local_store
        self._relay_directory = relay_directory
        self._pending_publishes: dict[str, VestaEvent] = {}

        self._ws: ClientConnection | None = None
        self._receive_task: asyncio.Task | None = None
        self._reconnect_attempt = 0
        self._disposed = False
        self._is_connected = False
        self._server_id: str | None = None

        # Callbacks
        self._on_event: Callable[[EventMessage], None] | None = None
        self._on_events_batch: Callable[[EventsBatchMessage], None] | None = None
        self._on_ack: Callable[[AckMessage], None] | None = None
        self._on_error: Callable[[ErrorMessage], None] | None = None
        self._on_connected: Callable[[WelcomeMessage], None] | None = None
        self._on_disconnected: Callable[[str], None] | None = None
        self._on_relay_switched: Callable[[str], None] | None = None
        self._on_manifest_applied: Callable[[RelayManifest], None] | None = None

    @property
    def is_connected(self) -> bool:
        return self._is_connected

    @property
    def server_id(self) -> str | None:
        return self._server_id

    @property
    def channels(self) -> list[str]:
        return list(self._channels)

    @property
    def active_relay(self) -> str:
        """The relay the connection is currently using (or last attempted)."""
        return self._relay_candidates[self._active_relay_index]

    @property
    def relays(self) -> list[str]:
        """The ordered relay candidate list tried on connect/failover."""
        return list(self._relay_candidates)

    # ── Event registration ────────────────────────────────────────────────────

    def on_event(self, callback: Callable[[EventMessage], None]) -> None:
        self._on_event = callback

    def on_events_batch(self, callback: Callable[[EventsBatchMessage], None]) -> None:
        self._on_events_batch = callback

    def on_ack(self, callback: Callable[[AckMessage], None]) -> None:
        self._on_ack = callback

    def on_error(self, callback: Callable[[ErrorMessage], None]) -> None:
        self._on_error = callback

    def on_connected(self, callback: Callable[[WelcomeMessage], None]) -> None:
        self._on_connected = callback

    def on_disconnected(self, callback: Callable[[str], None]) -> None:
        self._on_disconnected = callback

    def on_relay_switched(self, callback: Callable[[str], None]) -> None:
        self._on_relay_switched = callback

    def on_manifest_applied(self, callback: Callable[[RelayManifest], None]) -> None:
        self._on_manifest_applied = callback

    # ── Relay directory / failover ────────────────────────────────────────────

    def attach_relay_directory(self, directory: RelayDirectory) -> None:
        """
        Attach a relay directory so the connection discovers, verifies, and adopts
        owner-signed manifests. Call BEFORE :meth:`connect`. Accepted manifests refresh the
        candidate list and fire ``on_manifest_applied``.
        """
        self._relay_directory = directory

    def update_relay_candidates(self, relays: list[str]) -> None:
        """
        Replace the relay candidate list (e.g. after a newer manifest). Keeps the active
        relay if still present, else resets to the top. Does not reconnect.
        """
        if not relays:
            raise ValueError("At least one relay URL is required.")
        active = self.active_relay
        self._relay_candidates = list(relays)
        if active in self._relay_candidates:
            self._active_relay_index = self._relay_candidates.index(active)
        else:
            self._active_relay_index = 0
            self._notified_relay_index = -1

    async def switch_relay(self, url: str) -> None:
        """Switch to a specific relay (must be in the candidate list) and reconnect now."""
        if url not in self._relay_candidates:
            raise ValueError(f"Relay '{url}' is not in the current candidate list.")
        self._active_relay_index = self._relay_candidates.index(url)
        await self.disconnect()
        await self.connect()

    # ── Connection lifecycle ──────────────────────────────────────────────────

    async def connect(self) -> None:
        """Connect to the first reachable relay candidate and perform the HELLO handshake."""
        if self._disposed:
            raise RuntimeError("Connection has been disposed")

        if not await self._try_connect_candidates():
            raise RuntimeError(
                f"Could not connect to any of the {len(self._relay_candidates)} configured relay(s)."
            )

    async def _try_connect_candidates(self) -> bool:
        count = len(self._relay_candidates)
        for offset in range(count):
            index = (self._active_relay_index + offset) % count
            relay = self._relay_candidates[index]
            try:
                await self._connect_to(relay)
            except Exception:
                continue

            self._active_relay_index = index
            if self._notified_relay_index != index:
                self._notified_relay_index = index
                if self._on_relay_switched:
                    self._on_relay_switched(relay)
            return True
        return False

    async def _connect_to(self, relay: str) -> None:
        """Open the WebSocket connection to a specific relay and perform the HELLO handshake."""
        self._ws = await websockets.connect(relay)

        # Send HELLO
        hello: dict[str, Any] = {
            "type": "HELLO",
            "clientId": self.client_id,
            "channels": self._channels,
            "lastSequences": self._last_sequences,
        }
        if self.public_key:
            hello["publicKey"] = self.public_key
        await self._ws.send(json.dumps(hello))

        # Wait for WELCOME
        raw = await self._ws.recv()
        msg = parse_server_message(json.loads(raw))
        if not isinstance(msg, WelcomeMessage):
            raise RuntimeError(f"Expected WELCOME, got {type(msg).__name__}")

        self._is_connected = True
        self._server_id = msg.server_id
        self._channels = list(msg.channels)
        self._reconnect_attempt = 0

        if self._on_connected:
            self._on_connected(msg)

        # Start receive loop
        self._receive_task = asyncio.create_task(self._receive_loop())

        # Ensure we are subscribed to the manifest channel when a directory is attached.
        if (
            self._relay_directory is not None
            and self._relay_directory.manifest_channel not in self._channels
        ):
            await self.subscribe(self._relay_directory.manifest_channel, 0)

        # Flush any pending outbox events
        if self._local_store is not None:
            await self._flush_outbox()

    async def disconnect(self) -> None:
        """Gracefully close the connection."""
        self._is_connected = False
        if self._receive_task:
            self._receive_task.cancel()
            try:
                await self._receive_task
            except asyncio.CancelledError:
                pass
            self._receive_task = None
        if self._ws:
            await self._ws.close()
            self._ws = None

    async def dispose(self) -> None:
        """Permanently dispose the connection."""
        self._disposed = True
        self.auto_reconnect = False
        await self.disconnect()

    # ── Publishing ────────────────────────────────────────────────────────────

    async def publish(self, event: VestaEvent) -> None:
        """
        Publish a VestaEvent to its channel.

        If connected, sends immediately. If disconnected and a ``local_store``
        is configured, the event is enqueued in the outbox and flushed on the
        next successful connect. If disconnected and no store is configured,
        raises :class:`RuntimeError`.
        """
        if self._ws and self._is_connected:
            if self._local_store is not None:
                self._pending_publishes[event.id] = event
            msg = {
                "type": "PUBLISH",
                "channelId": event.channel_id,
                "event": event.to_dict(),
            }
            await self._ws.send(json.dumps(msg))
            return

        if self._local_store is not None:
            await self._local_store.enqueue_outbox(event)
            return

        raise RuntimeError(
            "Not connected and no local_store configured for offline publishing"
        )

    # ── Subscriptions ─────────────────────────────────────────────────────────

    async def subscribe(self, channel_id: str, from_sequence: int | None = None) -> None:
        """Subscribe to a new channel."""
        if channel_id not in self._channels:
            self._channels.append(channel_id)
        msg: dict[str, Any] = {"type": "SUBSCRIBE", "channelId": channel_id}
        if from_sequence is not None:
            msg["fromSequence"] = from_sequence
        await self._send(msg)

    async def unsubscribe(self, channel_id: str) -> None:
        """Unsubscribe from a channel."""
        self._channels = [ch for ch in self._channels if ch != channel_id]
        await self._send({"type": "UNSUBSCRIBE", "channelId": channel_id})

    async def fetch(
        self,
        channel_id: str,
        from_sequence: int,
        to_sequence: int | None = None,
        limit: int | None = None,
    ) -> None:
        """Fetch historical events from a channel."""
        msg: dict[str, Any] = {
            "type": "FETCH",
            "channelId": channel_id,
            "fromSequence": from_sequence,
        }
        if to_sequence is not None:
            msg["toSequence"] = to_sequence
        if limit is not None:
            msg["limit"] = limit
        await self._send(msg)

    # ── Channel management (ACL) ──────────────────────────────────────────────

    async def create_channel(
        self,
        channel_id: str,
        *,
        visibility: str = "private",
        members: list[str] | None = None,
    ) -> None:
        """
        Create a channel with explicit visibility and initial members.

        For `visibility="private"`, only the caller (admin) and the listed
        members may publish/subscribe. The caller is auto-subscribed.
        """
        await self._send({
            "type": "CREATE_CHANNEL",
            "channelId": channel_id,
            "visibility": visibility,
            "initialMembers": list(members) if members else [],
        })

    async def grant_access(
        self,
        channel_id: str,
        client_id: str,
        *,
        role: str = "member",
    ) -> None:
        """Grant a client access to a private channel. Caller must be admin."""
        await self._send({
            "type": "GRANT_ACCESS",
            "channelId": channel_id,
            "clientId": client_id,
            "role": role,
        })

    async def register_app(self, app_id: str) -> None:
        """
        Register an app namespace. The first slug segment of every channel ID
        belongs to an app. When the server is configured with
        `Protocol:RequireAppRegistration=true`, the app must be registered
        before publishing or subscribing on any channel in its namespace.
        The connecting client becomes the owner.
        """
        await self._send({
            "type": "REGISTER_APP",
            "appId": app_id,
        })

    async def delete_channel(self, channel_id: str) -> None:
        """
        Soft-delete a channel. Requires the connection's public key to be in
        the server's ``Admin:BootstrapPublicKeys`` allow-list. Existing events
        are retained for a future hard-delete sweep; further PUBLISH /
        SUBSCRIBE / FETCH / CREATE_CHANNEL for that channel are rejected with
        ``CHANNEL_DELETED``. Idempotent: deleting an already-deleted channel
        succeeds.
        """
        await self._send({
            "type": "DELETE_CHANNEL",
            "channelId": channel_id,
        })

    # ── Device group convenience methods ────────────────────────────────────

    async def create_device_group(
        self,
        device_name: str | None = None,
    ) -> str:
        """
        Create a new device group with this connection's identity as the founder.
        Publishes a ``vesta.identity.announce`` event and returns the new ``group_id``.
        Requires ``identity`` to be set in the constructor.
        """
        from vesta_client.device_groups import build_announce, generate_group_id
        identity = self._require_identity("create_device_group")
        group_id = generate_group_id()
        await self.publish(build_announce(identity, group_id, device_name))
        return group_id

    async def link_device(
        self,
        group_id: str,
        target_public_key: bytes,
        reason: str | None = None,
    ) -> None:
        """
        Vouch for another device as a member of the group.
        Publishes a ``vesta.identity.link`` event signed by this connection's identity.
        Requires ``identity`` to be set in the constructor.
        """
        from vesta_client.device_groups import build_link
        identity = self._require_identity("link_device")
        await self.publish(build_link(identity, group_id, target_public_key, reason))

    async def join_device_group(
        self,
        group_id: str,
        device_name: str | None = None,
    ) -> None:
        """
        Announce this connection's identity as joining an existing group.
        Publishes a ``vesta.identity.announce`` event.
        Requires ``identity`` to be set in the constructor.
        """
        from vesta_client.device_groups import build_announce
        identity = self._require_identity("join_device_group")
        await self.publish(build_announce(identity, group_id, device_name))

    async def unlink_device(
        self,
        group_id: str,
        target_public_key: bytes,
        reason: str | None = None,
    ) -> None:
        """
        Remove a device from the group.
        Publishes a ``vesta.identity.unlink`` event signed by this connection's identity.
        Requires ``identity`` to be set in the constructor.
        """
        from vesta_client.device_groups import build_unlink
        identity = self._require_identity("unlink_device")
        await self.publish(build_unlink(identity, group_id, target_public_key, reason))

    async def get_device_group_members(
        self,
        group_id: str,
        timeout: float = 5.0,
    ):
        """
        Subscribe to the group's identity channel, replay the full history into a
        ``DeviceGroupProjection``, and return the current membership as a
        ``DeviceGroup``.

        This is a one-shot convenience method for occasional inspection.
        For continuous tracking, subscribe to the channel directly and feed
        events into your own ``DeviceGroupProjection``.
        """
        from vesta_client.device_groups import DeviceGroupProjection, device_group_channel
        channel_id = device_group_channel(group_id)
        projection = DeviceGroupProjection(group_id)
        batch_event = asyncio.Event()

        orig_batch = self._on_events_batch
        orig_event = self._on_event

        def _on_batch(msg: EventsBatchMessage) -> None:
            if msg.channel_id == channel_id:
                projection.apply_batch(msg.events)
                batch_event.set()
            if orig_batch:
                orig_batch(msg)

        def _on_evt(msg: EventMessage) -> None:
            if msg.event.channel_id == channel_id:
                projection.apply(
                    SequencedEvent(event=msg.event, sequence=msg.sequence, received_at=msg.received_at)
                )
            if orig_event:
                orig_event(msg)
        self._on_events_batch = _on_batch
        self._on_event = _on_evt
        try:
            await self.subscribe(channel_id, from_sequence=0)
            try:
                await asyncio.wait_for(batch_event.wait(), timeout=timeout)
            except asyncio.TimeoutError:
                pass  # Channel may be empty; return whatever we have.
        finally:
            self._on_events_batch = orig_batch
            self._on_event = orig_event

        return projection.state

    def _require_identity(self, method_name: str) -> VestaIdentity:
        if self._identity is None:
            raise RuntimeError(
                f"{method_name}() requires the VestaConnection to be constructed "
                "with an 'identity' argument."
            )
        return self._identity

    # ── Sequence tracking ─────────────────────────────────────────────────────

    def update_sequence(self, channel_id: str, sequence: int) -> None:
        """Update the last known sequence for a channel."""
        self._last_sequences[channel_id] = sequence

    # ── Internals ─────────────────────────────────────────────────────────────

    async def _send(self, msg: dict[str, Any]) -> None:
        if not self._ws or not self._is_connected:
            raise RuntimeError("Not connected")
        await self._ws.send(json.dumps(msg))

    async def _receive_loop(self) -> None:
        try:
            async for raw in self._ws:  # type: ignore[union-attr]
                data = json.loads(raw)
                msg = parse_server_message(data)
                self._dispatch(msg)
        except websockets.ConnectionClosed:
            pass
        except asyncio.CancelledError:
            return
        finally:
            self._is_connected = False
            if self._on_disconnected:
                self._on_disconnected("Connection closed")
            if self.auto_reconnect and not self._disposed:
                asyncio.create_task(self._reconnect())

    async def _reconnect(self) -> None:
        self._reconnect_attempt += 1
        delay = min(
            self.initial_reconnect_delay * (2 ** (self._reconnect_attempt - 1)),
            self.max_reconnect_delay,
        )
        logger.info("Reconnecting in %.1fs (attempt %d)", delay, self._reconnect_attempt)
        await asyncio.sleep(delay)
        try:
            await self.connect()
        except Exception:
            if self.auto_reconnect and not self._disposed:
                asyncio.create_task(self._reconnect())

    def _dispatch(self, msg: ServerMessage) -> None:
        match msg:
            case EventMessage() as m:
                self.update_sequence(m.channel_id, m.sequence)
                if self._local_store is not None:
                    asyncio.create_task(
                        self._local_store.store_event(
                            SequencedEvent(
                                event=m.event,
                                sequence=m.sequence,
                                received_at=m.received_at,
                            )
                        )
                    )
                self._maybe_apply_manifest_event(m.channel_id, m.event)
                if self._on_event:
                    self._on_event(m)
            case EventsBatchMessage() as m:
                if m.events:
                    self.update_sequence(m.channel_id, m.events[-1].sequence)
                    if self._local_store is not None:
                        asyncio.create_task(self._local_store.store_events(m.events))
                if (
                    self._relay_directory is not None
                    and m.channel_id == self._relay_directory.manifest_channel
                ):
                    for se in m.events:
                        self._maybe_apply_manifest_event(m.channel_id, se.event)
                if self._on_events_batch:
                    self._on_events_batch(m)
            case AckMessage() as m:
                self.update_sequence(m.channel_id, m.sequence)
                if self._local_store is not None:
                    asyncio.create_task(self._cache_event_on_ack(m))
                if self._on_ack:
                    self._on_ack(m)
            case ErrorMessage() as m:
                if self._on_error:
                    self._on_error(m)

    def _maybe_apply_manifest_event(self, channel_id: str, event: VestaEvent) -> None:
        directory = self._relay_directory
        if directory is None or channel_id != directory.manifest_channel:
            return
        if event.event_type != RELAY_MANIFEST_EVENT_TYPE:
            return

        try:
            manifest = RelayManifest.from_dict(event.payload)
        except (KeyError, TypeError):
            return

        if not directory.try_apply_manifest(manifest):
            return

        self.update_relay_candidates(directory.resolve_candidates())
        if self._on_manifest_applied:
            self._on_manifest_applied(manifest)

    async def _cache_event_on_ack(self, ack: AckMessage) -> None:
        if self._local_store is None:
            return
        evt = self._pending_publishes.pop(ack.event_id, None)
        if evt is not None:
            await self._local_store.store_event(
                SequencedEvent(
                    event=evt,
                    sequence=ack.sequence,
                    received_at=datetime.now(timezone.utc).isoformat(),
                )
            )
        await self._local_store.mark_outbox_confirmed(ack.event_id)

    async def _flush_outbox(self) -> None:
        if self._local_store is None or self._ws is None:
            return
        pending = await self._local_store.get_pending_outbox()
        for entry in pending:
            self._pending_publishes[entry.event.id] = entry.event
            try:
                await self._ws.send(
                    json.dumps(
                        {
                            "type": "PUBLISH",
                            "channelId": entry.event.channel_id,
                            "event": entry.event.to_dict(),
                        }
                    )
                )
                await self._local_store.mark_outbox_sent(entry.event.id)
            except Exception:
                # Socket dropped mid-flush. Remaining entries stay in outbox
                # and will be retried on the next successful connect.
                self._pending_publishes.pop(entry.event.id, None)
                return
