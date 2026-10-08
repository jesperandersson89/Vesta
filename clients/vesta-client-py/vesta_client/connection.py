"""Vesta WebSocket connection with auto-reconnect."""

from __future__ import annotations

import asyncio
import json
import logging
from collections.abc import Callable
from dataclasses import replace
from datetime import datetime, timezone
from typing import Any

import websockets
from websockets.asyncio.client import ClientConnection

from vesta_client.events import create_event
from vesta_client.federation import DiscoveredRelay, FederationClient
from vesta_client.identity import VestaIdentity
from vesta_client.limits import VestaLimitNotice, classify_error_code
from vesta_client.relay import (
    RELAY_MANIFEST_EVENT_TYPE,
    RelayAttempt,
    RelayDirectory,
    RelayEndpoint,
    RelayManifest,
    RelayOverride,
    RelaysExhaustedError,
    RelaysExhaustedInfo,
    VestaAppConfig,
    manifest_channel_for,
    sign_manifest,
)
from vesta_client.relay_recovery import RelayAdoptResult
from vesta_client.signing import sign_event
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

# Error codes meaning "the relay does not accept this app namespace".
NAMESPACE_ERROR_CODES = frozenset(
    {
        "APP_NOT_ALLOWED",
        "UNKNOWN_APP",
        "INVALID_APP",
        "APPS_NOT_SUPPORTED",
        "INVALID_CHANNEL",
        "ACCESS_DENIED",
    }
)
RELAY_ADOPT_PROBATION = 1.5  # seconds
NIL_EVENT_ID = "00000000-0000-0000-0000-000000000000"

# ── Built-in relay picker wiring ─────────────────────────────────────────────
# The picker is part of the connection: when every relay is exhausted the connection prompts
# the user itself via a loopback web page. No app wiring is required.

_relay_picker_factory: Callable[["VestaConnection", Any], Any] | None = None
_relay_picker_enabled: bool = True


def set_relay_picker_factory(factory: Callable[["VestaConnection", Any], Any] | None) -> None:
    """Override the built-in picker (mainly for tests)."""
    global _relay_picker_factory
    _relay_picker_factory = factory


def set_relay_picker_enabled(enabled: bool) -> None:
    """Enable or disable the built-in relay picker globally (on by default). Mainly for tests."""
    global _relay_picker_enabled
    _relay_picker_enabled = enabled


def remap_channel(channel_id: str, from_ns: str | None, to_ns: str | None) -> str:
    """Rewrite a channel id's namespace segment, e.g. ``myapp/x`` -> ``theirs/x``."""
    if not from_ns or not to_ns or from_ns == to_ns:
        return channel_id
    if channel_id.startswith("vesta/"):
        return channel_id
    if channel_id == from_ns:
        return to_ns
    prefix = f"{from_ns}/"
    if channel_id.startswith(prefix):
        return to_ns + channel_id[len(from_ns):]
    return channel_id


class VestaConnection:
    """
    Async WebSocket connection to a Vesta server.

    Handles the HELLO/WELCOME handshake, message dispatch, and
    automatic reconnection with exponential backoff.

    Typical use passes only ``app_config``, ``identity`` and ``channels``: relays are resolved
    from a default file-backed :class:`RelayDirectory` and ``client_id`` comes from the identity.
    """

    def __init__(
        self,
        server_url: str | None = None,
        client_id: str | None = None,
        channels: list[str] | None = None,
        *,
        app_config: VestaAppConfig | None = None,
        relays: list[str] | None = None,
        auto_reconnect: bool = True,
        initial_reconnect_delay: float = 1.0,
        max_reconnect_delay: float = 30.0,
        last_sequences: dict[str, int] | None = None,
        public_key: str | None = None,
        local_store: ClientEventStore | None = None,
        identity: VestaIdentity | None = None,
        relay_directory: RelayDirectory | None = None,
        relay_exhaustion_passes: int = 3,
    ):
        if relay_directory is None and app_config is not None:
            relay_directory = RelayDirectory.create_default(app_config)
        if relays:
            candidates = list(relays)
        elif server_url:
            candidates = [server_url]
        elif relay_directory is not None:
            candidates = relay_directory.resolve_candidates()
        else:
            candidates = []
        if not candidates:
            raise ValueError(
                "VestaConnection requires an app_config (or relay_directory) with default relays, "
                "a server_url, or a non-empty relays list."
            )
        resolved_client_id = client_id or (identity.client_id if identity else None)
        if not resolved_client_id:
            raise ValueError("VestaConnection requires a client_id or an identity.")
        channels = channels or []

        self.server_url = server_url or candidates[0]
        self._relay_candidates = candidates
        self._active_relay_index = 0
        self._notified_relay_index = -1
        self.client_id = resolved_client_id
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
        self._has_reached_welcome_once = False
        self._closing = False
        self._reconnect_task: asyncio.Task | None = None
        self._background_tasks: set[asyncio.Task] = set()
        self._federation: FederationClient | None = None

        # Relay exhaustion tracking: fires once per outage after N full failed passes.
        self.relay_exhaustion_passes = max(1, relay_exhaustion_passes)
        self._consecutive_failures = 0
        self._exhaustion_raised = False
        self._last_attempts: dict[str, str] = {}
        self._listeners: dict[str, list[Callable[..., None]]] = {}

        # Callbacks
        self._on_event: Callable[[EventMessage], None] | None = None
        self._on_events_batch: Callable[[EventsBatchMessage], None] | None = None
        self._on_ack: Callable[[AckMessage], None] | None = None
        self._on_error: Callable[[ErrorMessage], None] | None = None
        self._on_limited: Callable[[VestaLimitNotice], None] | None = None
        self._on_connected: Callable[[WelcomeMessage], None] | None = None
        self._on_reconnected: Callable[[WelcomeMessage], None] | None = None
        self._on_disconnected: Callable[[str], None] | None = None
        self._on_relay_switched: Callable[[str], None] | None = None
        self._on_manifest_applied: Callable[[RelayManifest], None] | None = None
        self._on_relays_exhausted: Callable[[RelaysExhaustedInfo], None] | None = None

        # Relay namespace remapping (active relay override with a different app namespace).
        self._remote_app_id: str | None = None
        self._register_on_connect: bool = False
        self._namespace_confirmed: bool = False
        self._registration: asyncio.Future[None] | None = None
        self._probation_fail: Callable[[str], None] | None = None

        # Built-in relay picker.
        self._picker: Any | None = None
        self._picker_shown: bool = False

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

    @property
    def relay_directory(self) -> RelayDirectory | None:
        return self._relay_directory

    def add_listener(self, event: str, listener: Callable[..., None]) -> Callable[[], None]:
        """
        Add a listener for ``"disconnected"`` (reason), ``"reconnected"`` (welcome) or
        ``"relays_exhausted"`` (:class:`RelaysExhaustedInfo`). Unlike the single ``on_*``
        callbacks, any number of listeners coexist. Returns an unsubscribe function.
        """
        self._listeners.setdefault(event, []).append(listener)

        def unsubscribe() -> None:
            if listener in self._listeners.get(event, []):
                self._listeners[event].remove(listener)

        return unsubscribe

    def _emit(self, event: str, *args: Any) -> None:
        for listener in list(self._listeners.get(event, [])):
            try:
                listener(*args)
            except Exception:
                logger.exception("Listener for %s raised", event)

    # ── Event registration ────────────────────────────────────────────────────

    def on_event(self, callback: Callable[[EventMessage], None]) -> None:
        self._on_event = callback

    def on_events_batch(self, callback: Callable[[EventsBatchMessage], None]) -> None:
        self._on_events_batch = callback

    def on_ack(self, callback: Callable[[AckMessage], None]) -> None:
        self._on_ack = callback

    def on_error(self, callback: Callable[[ErrorMessage], None]) -> None:
        self._on_error = callback

    def on_limited(self, callback: Callable[[VestaLimitNotice], None]) -> None:
        """Register a callback for the semantic "your app is being limited" signal —
        see :func:`vesta_client.limits.classify_error_code`."""
        self._on_limited = callback

    def on_connected(self, callback: Callable[[WelcomeMessage], None]) -> None:
        self._on_connected = callback

    def on_reconnected(self, callback: Callable[[WelcomeMessage], None]) -> None:
        """Register a callback fired after WELCOME when this is a subsequent connect
        (i.e. the connection had already reached WELCOME once before)."""
        self._on_reconnected = callback

    def on_disconnected(self, callback: Callable[[str], None]) -> None:
        self._on_disconnected = callback

    def on_relay_switched(self, callback: Callable[[str], None]) -> None:
        self._on_relay_switched = callback

    def on_manifest_applied(self, callback: Callable[[RelayManifest], None]) -> None:
        self._on_manifest_applied = callback

    def on_relays_exhausted(self, callback: Callable[[RelaysExhaustedInfo], None]) -> None:
        """Register a callback fired once per outage when every relay candidate has failed
        ``relay_exhaustion_passes`` full passes while ``auto_reconnect`` is on."""
        self._on_relays_exhausted = callback

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

    async def reconnect(self) -> bool:
        """
        Connect again right now, walking the candidate list once. Returns True once connected,
        False if every candidate failed (auto-reconnect keeps retrying in the background).
        """
        if self._disposed:
            return False
        if self._is_connected:
            return True
        self._cancel_reconnect()
        connected: bool = await self._try_connect_candidates()
        if not connected:
            self._schedule_reconnect()
        return connected

    async def adopt_relay(self, override: RelayOverride) -> RelayAdoptResult:
        """
        Move to the relay (and app namespace) the user chose. The override is applied
        provisionally: it is only persisted once the relay is reachable and stays free of
        namespace errors for a short probation; otherwise the previous state is restored and
        the reason is returned. Requires an attached :class:`RelayDirectory`.
        """
        directory: RelayDirectory | None = self._relay_directory
        if directory is None:
            raise RuntimeError("No relay_directory attached — cannot set a relay override.")

        probation_reason: str | None = None
        failure_event: asyncio.Event = asyncio.Event()

        def _fail(reason: str) -> None:
            nonlocal probation_reason
            if probation_reason is not None:
                return
            probation_reason = reason
            failure_event.set()

        self._probation_fail = _fail
        self._namespace_confirmed = False

        try:
            directory.set_user_override(override, False)
            self.update_relay_candidates(directory.resolve_candidates())
            self._active_relay_index = (
                self._relay_candidates.index(override.relay)
                if override.relay in self._relay_candidates
                else 0
            )
            self._cancel_reconnect()
            await self.disconnect()
            connected: bool = await self._try_connect_candidates(limit=1)
            on_target: bool = connected and self.active_relay == override.relay

            reason: str | None
            if on_target:
                if probation_reason is None and not self._namespace_confirmed:
                    try:
                        await asyncio.wait_for(failure_event.wait(), timeout=RELAY_ADOPT_PROBATION)
                    except asyncio.TimeoutError:
                        pass
                if probation_reason is None:
                    directory.commit_pending_override()
                    return RelayAdoptResult(True, None)
                reason = probation_reason
            else:
                reason = self._last_attempts.get(override.relay) or "Could not connect"

            directory.discard_pending_override()
            self.update_relay_candidates(directory.resolve_candidates())
            if on_target and not self._disposed:
                self._active_relay_index = 0
                await self.disconnect()
                restored: bool = await self._try_connect_candidates()
                if not restored:
                    self._schedule_reconnect()
            return RelayAdoptResult(False, reason)
        finally:
            self._probation_fail = None

    async def clear_relay_override(self) -> bool:
        """Clear the user relay override and reconnect using the freshly resolved candidates."""
        if self._relay_directory is None:
            raise RuntimeError("No relay_directory attached — cannot clear a relay override.")
        self._relay_directory.clear_user_override()
        self.update_relay_candidates(self._relay_directory.resolve_candidates())
        return await self.reconnect()

    async def discover_relays(self, include_all: bool = False) -> list[DiscoveredRelay]:
        """
        Ask a reachable relay which relays host this app (federation gossip), or — with
        ``include_all`` — which relays it knows about at all. Show-only: descriptors are
        signature-checked and owner-matched, but never adopted automatically.
        """
        directory = self._relay_directory
        if directory is None:
            raise RuntimeError("No relay_directory attached — pass app_config or relay_directory.")
        base: str | None = FederationClient.to_federation_base_url(self.active_relay)
        if base is None:
            raise RuntimeError("The active relay URL cannot be used for federation discovery.")
        if self._federation is None:
            self._federation = FederationClient(directory.config)
        if include_all:
            return await self._federation.list_all_relays(base)
        return await self._federation.discover_relays_for_app(base)

    async def publish_relay_manifest(self, relays: list[str]) -> RelayManifest:
        """
        Sign and publish a new owner relay manifest steering every client of this app to
        ``relays`` (in preference order). Requires the connection's ``identity`` to be the app
        owner — the key matching ``app_config.owner_public_key``.
        """
        directory = self._relay_directory
        if directory is None:
            raise RuntimeError("No relay_directory attached — pass app_config or relay_directory.")
        identity = self._identity
        if identity is None or identity.public_key_b64 != directory.config.owner_public_key:
            raise RuntimeError("Only the app owner's identity can publish a relay manifest.")
        if not relays:
            raise ValueError("At least one relay URL is required.")

        current = directory.current_manifest
        manifest = sign_manifest(
            RelayManifest(
                app_id=directory.config.app_id,
                version=(current.version if current else 0) + 1,
                issued_at=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
                relays=[RelayEndpoint(url=url, priority=i) for i, url in enumerate(relays)],
                owner_public_key="",
            ),
            identity,
        )
        await self.publish(
            create_event(
                manifest_channel_for(directory.config.app_id),
                identity.client_id,
                RELAY_MANIFEST_EVENT_TYPE,
                manifest.to_dict(),
                identity=identity,
            )
        )
        return manifest

    # ── Connection lifecycle ──────────────────────────────────────────────────

    async def connect(self) -> None:
        """Connect to the first reachable relay candidate and perform the HELLO handshake."""
        if self._disposed:
            raise RuntimeError("Connection has been disposed")

        if not await self._try_connect_candidates():
            raise RelaysExhaustedError(
                RelaysExhaustedInfo(
                    attempts=[
                        RelayAttempt(relay=url, reason=self._last_attempts[url])
                        for url in self._relay_candidates
                        if url in self._last_attempts
                    ],
                    passes=1,
                )
            )

    async def _try_connect_candidates(self, limit: int | None = None) -> bool:
        count = len(self._relay_candidates)
        for offset in range(count if limit is None else min(limit, count)):
            index = (self._active_relay_index + offset) % count
            relay = self._relay_candidates[index]
            try:
                await self._connect_to(relay)
            except Exception as error:
                self._record_attempt_failure(relay, str(error) or type(error).__name__)
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
        self._resolve_active_namespace(relay)
        register_first: bool = self._register_on_connect

        ws = await websockets.connect(relay)
        self._ws = ws
        try:
            # Registering first: greet with no channels so nothing is requested before the
            # namespace exists.
            hello: dict[str, Any] = {
                "type": "HELLO",
                "clientId": self.client_id,
                "channels": [] if register_first else list(self._channels),
                "lastSequences": {} if register_first else dict(self._last_sequences),
            }
            if self.public_key:
                hello["publicKey"] = self.public_key
            await ws.send(json.dumps(self._map_outbound(hello)))

            # Wait for WELCOME
            raw = await ws.recv()
            msg = parse_server_message(json.loads(raw))
            if not isinstance(msg, WelcomeMessage):
                raise RuntimeError(f"Expected WELCOME, got {type(msg).__name__}")
        except BaseException:
            self._ws = None
            try:
                await ws.close()
            except Exception:
                pass
            raise

        msg = self._map_inbound(msg)
        requested: list[str] = list(self._channels)
        self._namespace_confirmed = (
            not register_first and bool(msg.channels) and any(c in requested for c in msg.channels)
        )
        self._is_connected = True
        self._consecutive_failures = 0
        self._exhaustion_raised = False
        self._last_attempts.clear()
        self._server_id = msg.server_id
        if not register_first:
            self._channels = list(msg.channels)
        self._picker_shown = False
        self._reconnect_attempt = 0
        was_reconnect = self._has_reached_welcome_once
        self._has_reached_welcome_once = True

        if self._on_connected:
            self._on_connected(msg)
        if was_reconnect and self._on_reconnected:
            self._on_reconnected(msg)
        if was_reconnect:
            self._emit("reconnected", msg)

        # Start receive loop
        self._receive_task = asyncio.create_task(self._receive_loop())

        if register_first:
            task: asyncio.Task = asyncio.create_task(self._register_then_subscribe())
            self._background_tasks.add(task)
            task.add_done_callback(self._background_tasks.discard)
            if self._relay_directory is not None and self._relay_directory.peer_cache is not None:
                peer_task: asyncio.Task = asyncio.create_task(self._refresh_peer_cache(relay))
                self._background_tasks.add(peer_task)
                peer_task.add_done_callback(self._background_tasks.discard)
            return

        if self._relay_directory is not None and self._relay_directory.peer_cache is not None:
            task = asyncio.create_task(self._refresh_peer_cache(relay))
            self._background_tasks.add(task)
            task.add_done_callback(self._background_tasks.discard)

        # Ensure we are subscribed to the manifest channel when a directory is attached.
        if (
            self._relay_directory is not None
            and self._relay_directory.manifest_channel not in self._channels
        ):
            await self.subscribe(self._relay_directory.manifest_channel, 0)

        # Flush any pending outbox events
        if self._local_store is not None:
            await self._flush_outbox()

    async def _register_then_subscribe(self) -> None:
        """After a register-first handshake: claim the namespace, then subscribe and flush as usual."""
        app_id: str | None = self._remote_app_id or (
            self._relay_directory.config.app_id if self._relay_directory else None
        )
        try:
            if not app_id:
                raise RuntimeError("No app id to register")
            loop: asyncio.AbstractEventLoop = asyncio.get_running_loop()
            future: asyncio.Future[None] = loop.create_future()
            self._registration = future
            await self._send_mapped({"type": "REGISTER_APP", "appId": app_id})
            await future
        except Exception as error:
            reason: str = str(error) or type(error).__name__
            self._registration = None
            if self._probation_fail is not None:
                self._probation_fail(reason)
            elif self._on_error:
                self._on_error(ErrorMessage(code="REGISTER_APP_FAILED", message=reason))
            return
        self._registration = None
        try:
            for channel_id in self._channels:
                last: int = self._last_sequences.get(channel_id, 0)
                await self._send_mapped(
                    {"type": "SUBSCRIBE", "channelId": channel_id, "fromSequence": last + 1}
                )
            if (
                self._relay_directory is not None
                and self._relay_directory.manifest_channel not in self._channels
            ):
                await self._send_mapped(
                    {
                        "type": "SUBSCRIBE",
                        "channelId": self._relay_directory.manifest_channel,
                        "fromSequence": 0,
                    }
                )
            self._namespace_confirmed = True
            if self._local_store is not None:
                await self._flush_outbox()
        except Exception:
            # Socket dropped mid-setup; the next connect repeats the handshake.
            pass

    def _resolve_active_namespace(self, candidate: str) -> None:
        """Decide which app namespace (if any) applies to ``candidate``, from the active override."""
        local_app_id: str | None = self._relay_directory.config.app_id if self._relay_directory else None
        active: RelayOverride | None = (
            self._relay_directory.active_override if self._relay_directory else None
        )
        applies: bool = active is not None and active.relay == candidate
        ns: str | None = active.app_id if applies and active is not None else None
        self._remote_app_id = None if not ns or ns == local_app_id else ns
        self._register_on_connect = applies and bool(active is not None and active.register_app)

    def _map_outbound(self, msg: dict[str, Any]) -> dict[str, Any]:
        remote: str | None = self._remote_app_id
        local: str | None = self._relay_directory.config.app_id if self._relay_directory else None
        if not remote or not local:
            return msg

        def map_id(channel_id: str) -> str:
            return remap_channel(channel_id, local, remote)

        msg_type: Any = msg.get("type")
        if msg_type == "HELLO":
            last_sequences = {map_id(k): v for k, v in msg.get("lastSequences", {}).items()}
            return {
                **msg,
                "channels": [map_id(c) for c in msg.get("channels", [])],
                "lastSequences": last_sequences,
            }
        if msg_type == "PUBLISH":
            channel_id: str = map_id(msg["channelId"])
            event: dict[str, Any] = msg["event"]
            if channel_id == event.get("channelId"):
                return {**msg, "channelId": channel_id}
            new_event: dict[str, Any] = {**event, "channelId": channel_id}
            new_event.pop("signature", None)
            if self._identity is not None:
                signed: VestaEvent = sign_event(VestaEvent.from_dict(new_event), self._identity)
                new_event = signed.to_dict()
            return {**msg, "channelId": channel_id, "event": new_event}
        if msg_type in ("SUBSCRIBE", "UNSUBSCRIBE", "FETCH", "CREATE_CHANNEL", "GRANT_ACCESS", "DELETE_CHANNEL"):
            return {**msg, "channelId": map_id(msg["channelId"])}
        return msg

    def _map_inbound(self, msg: ServerMessage) -> ServerMessage:
        remote: str | None = self._remote_app_id
        local: str | None = self._relay_directory.config.app_id if self._relay_directory else None
        if not remote or not local:
            return msg

        def map_id(channel_id: str) -> str:
            return remap_channel(channel_id, remote, local)

        if isinstance(msg, WelcomeMessage):
            return replace(msg, channels=[map_id(c) for c in msg.channels])
        if isinstance(msg, EventMessage):
            return replace(
                msg,
                channel_id=map_id(msg.channel_id),
                event=replace(msg.event, channel_id=map_id(msg.event.channel_id)),
            )
        if isinstance(msg, EventsBatchMessage):
            return replace(
                msg,
                channel_id=map_id(msg.channel_id),
                events=[
                    replace(se, event=replace(se.event, channel_id=map_id(se.event.channel_id)))
                    for se in msg.events
                ],
            )
        if isinstance(msg, AckMessage):
            return replace(msg, channel_id=map_id(msg.channel_id))
        if isinstance(msg, ErrorMessage) and msg.channel_id:
            return replace(msg, channel_id=map_id(msg.channel_id))
        return msg

    async def _send_mapped(self, msg: dict[str, Any]) -> None:
        if not self._ws:
            raise RuntimeError("Not connected")
        await self._ws.send(json.dumps(self._map_outbound(msg)))

    async def disconnect(self) -> None:
        """Gracefully close the connection."""
        self._is_connected = False
        if self._receive_task:
            self._closing = True
            self._receive_task.cancel()
            try:
                await self._receive_task
            except asyncio.CancelledError:
                pass
            finally:
                self._closing = False
            self._receive_task = None
        if self._ws:
            await self._ws.close()
            self._ws = None

    async def dispose(self) -> None:
        """Permanently dispose the connection."""
        self._disposed = True
        self.auto_reconnect = False
        self._cancel_reconnect()
        await self.disconnect()
        if self._picker is not None:
            self._picker.dispose()
            self._picker = None

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
            await self._send_mapped(msg)
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
        await self._send_mapped(msg)

    async def _receive_loop(self) -> None:
        try:
            async for raw in self._ws:  # type: ignore[union-attr]
                data = json.loads(raw)
                msg = self._map_inbound(parse_server_message(data))
                self._dispatch(msg)
        except websockets.ConnectionClosed:
            pass
        except asyncio.CancelledError:
            return
        finally:
            self._is_connected = False
            if self._on_disconnected:
                self._on_disconnected("Connection closed")
            self._emit("disconnected", "Connection closed")
            if self.auto_reconnect and not self._disposed and not self._closing:
                self._schedule_reconnect()

    def _schedule_reconnect(self) -> None:
        if not self.auto_reconnect or self._disposed:
            return
        self._cancel_reconnect()
        self._reconnect_task = asyncio.create_task(self._reconnect())

    def _cancel_reconnect(self) -> None:
        task: asyncio.Task | None = self._reconnect_task
        self._reconnect_task = None
        if task is not None and not task.done() and task is not asyncio.current_task():
            task.cancel()

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
            self._schedule_reconnect()

    def _record_attempt_failure(self, relay: str, reason: str) -> None:
        self._last_attempts[relay] = reason
        self._consecutive_failures += 1
        count: int = len(self._relay_candidates)
        threshold: int = self.relay_exhaustion_passes * count
        if (
            self.auto_reconnect
            and not self._exhaustion_raised
            and self._consecutive_failures >= threshold
        ):
            self._exhaustion_raised = True
            info = RelaysExhaustedInfo(
                attempts=[
                    RelayAttempt(relay=url, reason=self._last_attempts[url])
                    for url in self._relay_candidates
                    if url in self._last_attempts
                ],
                passes=self._consecutive_failures // count,
            )
            if self._on_relays_exhausted:
                self._on_relays_exhausted(info)
            self._emit("relays_exhausted", info)
            task: asyncio.Task = asyncio.create_task(self._maybe_show_picker(info))
            self._background_tasks.add(task)
            task.add_done_callback(self._background_tasks.discard)

    async def _maybe_show_picker(self, info: RelaysExhaustedInfo) -> None:
        """Prompt the user (once per outage) to pick another relay, via the built-in picker."""
        if not _relay_picker_enabled or self._disposed or self._picker_shown:
            return
        directory: RelayDirectory | None = self._relay_directory
        if directory is None:
            return
        self._picker_shown = True
        try:
            factory = _relay_picker_factory
            if factory is None:
                from vesta_client.relay_picker import WebRelayPicker

                factory = lambda host, config: WebRelayPicker(host, config)  # noqa: E731
            if self._picker is None:
                self._picker = factory(self, directory.config)
            await self._picker.show(info)
        except Exception:
            # A picker failure must never disturb reconnecting.
            logger.debug("Relay picker failed", exc_info=True)

    async def _refresh_peer_cache(self, relay: str) -> None:
        # Best-effort: remember verified federation peers so recovery has hints if every relay dies.
        directory: RelayDirectory | None = self._relay_directory
        cache = directory.peer_cache if directory else None
        if directory is None or cache is None:
            return
        base: str | None = FederationClient.to_federation_base_url(relay)
        if base is None:
            return
        try:
            if self._federation is None:
                self._federation = FederationClient(directory.config)
            peers = await self._federation.list_all_relays(base)
            if peers:
                cache.save(peers)
        except Exception:
            logger.debug("Peer cache refresh failed", exc_info=True)

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
                if m.event_id == NIL_EVENT_ID:
                    # Registration acknowledgement, not an event.
                    if self._registration is not None and not self._registration.done():
                        self._registration.set_result(None)
                    return
                self.update_sequence(m.channel_id, m.sequence)
                if self._local_store is not None:
                    asyncio.create_task(self._cache_event_on_ack(m))
                if self._on_ack:
                    self._on_ack(m)
            case ErrorMessage() as m:
                if self._registration is not None and not self._registration.done():
                    if m.code == "DUPLICATE_APP":
                        self._registration.set_result(None)
                    else:
                        self._registration.set_exception(RuntimeError(f"{m.code}: {m.message}"))
                    return
                if self._probation_fail is not None and m.code in NAMESPACE_ERROR_CODES:
                    self._probation_fail(f"{m.code}: {m.message}")
                if self._on_error:
                    self._on_error(m)
                self._handle_possible_limit(m)

    def _handle_possible_limit(self, msg: ErrorMessage) -> None:
        classification = classify_error_code(msg.code)
        if classification.is_limit and self._on_limited:
            self._on_limited(
                VestaLimitNotice(
                    code=msg.code,
                    message=msg.message,
                    channel_id=msg.channel_id,
                    event_id=msg.event_id,
                    is_transient=classification.is_transient,
                )
            )
        if classification.is_event_fatal and msg.event_id and self._local_store is not None:
            self._pending_publishes.pop(msg.event_id, None)
            asyncio.create_task(self._local_store.mark_outbox_rejected(msg.event_id, msg.code))

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
                await self._send_mapped(
                    {
                        "type": "PUBLISH",
                        "channelId": entry.event.channel_id,
                        "event": entry.event.to_dict(),
                    }
                )
                await self._local_store.mark_outbox_sent(entry.event.id)
            except Exception:
                # Socket dropped mid-flush. Remaining entries stay in outbox
                # and will be retried on the next successful connect.
                self._pending_publishes.pop(entry.event.id, None)
                return
