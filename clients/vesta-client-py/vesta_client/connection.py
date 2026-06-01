"""Vesta WebSocket connection with auto-reconnect."""

from __future__ import annotations

import asyncio
import json
import logging
from collections.abc import Callable
from typing import Any

import websockets
from websockets.asyncio.client import ClientConnection

from vesta_client.types import (
    AckMessage,
    ErrorMessage,
    EventMessage,
    EventsBatchMessage,
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
        auto_reconnect: bool = True,
        initial_reconnect_delay: float = 1.0,
        max_reconnect_delay: float = 30.0,
        last_sequences: dict[str, int] | None = None,
        public_key: str | None = None,
    ):
        self.server_url = server_url
        self.client_id = client_id
        self._channels = list(channels)
        self.auto_reconnect = auto_reconnect
        self.initial_reconnect_delay = initial_reconnect_delay
        self.max_reconnect_delay = max_reconnect_delay
        self._last_sequences: dict[str, int] = {ch: 0 for ch in channels}
        if last_sequences:
            self._last_sequences.update(last_sequences)
        self.public_key = public_key

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

    @property
    def is_connected(self) -> bool:
        return self._is_connected

    @property
    def server_id(self) -> str | None:
        return self._server_id

    @property
    def channels(self) -> list[str]:
        return list(self._channels)

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

    # ── Connection lifecycle ──────────────────────────────────────────────────

    async def connect(self) -> None:
        """Open the WebSocket connection and perform the HELLO handshake."""
        if self._disposed:
            raise RuntimeError("Connection has been disposed")

        self._ws = await websockets.connect(self.server_url)

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
        """Publish a VestaEvent to its channel."""
        if not self._ws or not self._is_connected:
            raise RuntimeError("Not connected")
        msg = {
            "type": "PUBLISH",
            "channelId": event.channel_id,
            "event": event.to_dict(),
        }
        await self._ws.send(json.dumps(msg))

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
                if self._on_event:
                    self._on_event(m)
            case EventsBatchMessage() as m:
                if m.events:
                    self.update_sequence(m.channel_id, m.events[-1].sequence)
                if self._on_events_batch:
                    self._on_events_batch(m)
            case AckMessage() as m:
                self.update_sequence(m.channel_id, m.sequence)
                if self._on_ack:
                    self._on_ack(m)
            case ErrorMessage() as m:
                if self._on_error:
                    self._on_error(m)
