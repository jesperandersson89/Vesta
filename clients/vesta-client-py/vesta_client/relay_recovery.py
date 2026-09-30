"""
Headless relay-recovery state machine — the Python mirror of the C# ``RelayRecoverySession``
and the TypeScript ``relay-recovery.ts``.

It owns no UI. A view (console prompt, GUI toolkit, ...) renders the immutable snapshot and
forwards user actions. Discovered relays are always show-only: nothing is adopted without an
explicit :meth:`RelayRecoverySession.adopt` / :meth:`RelayRecoverySession.use_manual` call.
"""

from __future__ import annotations

import asyncio
import urllib.parse
from collections.abc import Callable
from dataclasses import dataclass, field, replace
from typing import Any, Protocol

from vesta_client.federation import DiscoveredRelay, FederationClient
from vesta_client.relay import RelayAttempt, RelayDirectory, RelaysExhaustedInfo

HEALTHY = "healthy"
DEGRADED = "degraded"
EXHAUSTED = "exhausted"
DISCOVERING = "discovering"
CHOICES = "choices"
NOTHING_FOUND = "nothing_found"
ADOPTING = "adopting"
FAILED = "failed"

_MAX_SEEDS = 16


@dataclass(frozen=True)
class RelayChoice:
    """A relay the user may pick. ``hosts_requested_app`` is False for unverified relays."""

    url: str
    relay_public_key: str
    hosts_requested_app: bool
    from_cache: bool


@dataclass(frozen=True)
class RelayRecoverySnapshot:
    phase: str
    tried_relays: list[RelayAttempt] = field(default_factory=list)
    choices: list[RelayChoice] = field(default_factory=list)
    active_override: str | None = None
    background_retrying: bool = False
    failed_passes: int = 0
    adopting: str | None = None
    failure_reason: str | None = None


class RelayRecoveryHost(Protocol):
    """What the session needs from a connection — :class:`VestaConnection` satisfies it."""

    relay_directory: RelayDirectory | None
    relays: list[str]
    auto_reconnect: bool

    def add_listener(self, event: str, listener: Callable[..., None]) -> Callable[[], None]: ...
    async def reconnect(self) -> bool: ...
    async def adopt_relay(self, url: str) -> bool: ...
    async def clear_relay_override(self) -> bool: ...


def normalize_relay_url(url: str) -> str | None:
    """Turn typed input into a canonical ``ws://`` / ``wss://`` URL, or None if unusable."""
    try:
        parsed = urllib.parse.urlparse(url.strip())
        port: int | None = parsed.port
    except ValueError:
        return None
    scheme: str | None = {"ws": "ws", "wss": "wss", "http": "ws", "https": "wss"}.get(parsed.scheme)
    host: str | None = parsed.hostname
    if scheme is None or not host:
        return None
    if ":" in host:
        host = f"[{host}]"
    default_port: int = 443 if scheme == "wss" else 80
    authority: str = host if port is None or port == default_port else f"{host}:{port}"
    path: str = parsed.path or "/"
    query: str = f"?{parsed.query}" if parsed.query else ""
    return f"{scheme}://{authority}{path}{query}"


class RelayRecoverySession:
    def __init__(self, host: RelayRecoveryHost, federation: FederationClient) -> None:
        if host.relay_directory is None:
            raise ValueError("RelayRecoverySession requires the connection to have a relay_directory.")
        self._host = host
        self._federation = federation
        self._directory: RelayDirectory = host.relay_directory
        self._listeners: list[Callable[[RelayRecoverySnapshot], None]] = []
        self._snapshot: RelayRecoverySnapshot = self._build(HEALTHY)
        self._unsubscribes: list[Callable[[], None]] = [
            host.add_listener("disconnected", self._handle_disconnected),
            host.add_listener("reconnected", self._handle_reconnected),
            host.add_listener("relays_exhausted", self._handle_exhausted),
        ]

    @property
    def snapshot(self) -> RelayRecoverySnapshot:
        return self._snapshot

    def on_change(self, listener: Callable[[RelayRecoverySnapshot], None]) -> Callable[[], None]:
        """Subscribe to snapshot changes. Returns an unsubscribe function."""
        self._listeners.append(listener)

        def unsubscribe() -> None:
            if listener in self._listeners:
                self._listeners.remove(listener)

        return unsubscribe

    def dispose(self) -> None:
        for unsubscribe in self._unsubscribes:
            unsubscribe()
        self._unsubscribes = []
        self._listeners.clear()

    # ── User actions ──────────────────────────────────────────────────────────

    def report_exhausted(self, info: RelaysExhaustedInfo) -> None:
        """Open the prompt for an outage the session didn't witness (e.g. a first connect that failed)."""
        self._handle_exhausted(info)

    async def retry(self) -> None:
        if await self._host.reconnect():
            self._update(self._build(HEALTHY))

    async def discover(self) -> None:
        self._update(replace(self._snapshot, phase=DISCOVERING, choices=[], failure_reason=None))
        choices: list[RelayChoice] = await self._collect_choices()
        self._update(replace(self._snapshot, phase=CHOICES if choices else NOTHING_FOUND, choices=choices))

    async def adopt(self, choice: RelayChoice) -> None:
        await self._adopt_core(choice.url)

    async def use_manual(self, url: str) -> None:
        relay: str | None = normalize_relay_url(url)
        if relay is None:
            self._update(
                replace(
                    self._snapshot,
                    phase=FAILED,
                    failure_reason="Not a valid relay URL (expected ws://, wss://, http:// or https://).",
                )
            )
            return
        await self._adopt_core(relay)

    async def clear_override(self) -> None:
        connected: bool = await self._host.clear_relay_override()
        if connected:
            self._update(self._build(HEALTHY))
        else:
            self._update(replace(self._snapshot, active_override=None))

    def dismiss(self) -> None:
        self._update(replace(self._snapshot, phase=DEGRADED, choices=[], failure_reason=None))

    # ── Internals ─────────────────────────────────────────────────────────────

    async def _adopt_core(self, relay: str) -> None:
        self._update(replace(self._snapshot, phase=ADOPTING, adopting=relay, failure_reason=None))
        try:
            connected: bool = await self._host.adopt_relay(relay)
        except Exception as error:
            self._update(
                replace(self._snapshot, phase=FAILED, adopting=None, failure_reason=str(error))
            )
            return
        if connected:
            self._update(self._build(HEALTHY))
        else:
            self._update(
                replace(
                    self._snapshot,
                    phase=FAILED,
                    adopting=None,
                    active_override=self._directory.active_override,
                    failure_reason=f"Could not connect to {relay}.",
                )
            )

    def _build(self, phase: str) -> RelayRecoverySnapshot:
        return RelayRecoverySnapshot(phase=phase, active_override=self._directory.active_override)

    def _update(self, next_snapshot: RelayRecoverySnapshot) -> None:
        retrying: bool = next_snapshot.phase not in (HEALTHY, ADOPTING) and bool(self._host.auto_reconnect)
        self._snapshot = replace(next_snapshot, background_retrying=retrying)
        for listener in list(self._listeners):
            listener(self._snapshot)

    def _handle_disconnected(self, _reason: Any = None) -> None:
        if self._snapshot.phase == HEALTHY:
            self._update(self._build(DEGRADED))

    def _handle_reconnected(self, _welcome: Any = None) -> None:
        self._update(self._build(HEALTHY))

    def _handle_exhausted(self, info: RelaysExhaustedInfo) -> None:
        self._update(
            replace(
                self._build(EXHAUSTED),
                tried_relays=list(info.attempts),
                failed_passes=info.passes,
            )
        )

    async def _collect_choices(self) -> list[RelayChoice]:
        cached: list[DiscoveredRelay] = self._directory.peer_cache.load() if self._directory.peer_cache else []
        urls: list[str] = [u for peer in cached for u in peer.urls]
        urls += self._directory.config.discovery_seeds
        urls += self._host.relays
        urls += self._directory.config.default_relays

        bases: list[str] = []
        for url in urls:
            base: str | None = FederationClient.to_federation_base_url(url)
            if base is not None and base not in bases:
                bases.append(base)
        bases = bases[:_MAX_SEEDS]

        async def query(base: str) -> list[DiscoveredRelay]:
            try:
                hosting, everyone = await asyncio.gather(
                    self._federation.discover_relays_for_app(base),
                    self._federation.list_all_relays(base),
                )
                return [*hosting, *everyone]
            except Exception:
                return []

        live: list[list[DiscoveredRelay]] = await asyncio.gather(*(query(b) for b in bases))

        merged: dict[str, RelayChoice] = {}
        for peer in cached:
            for url in peer.urls:
                merged.setdefault(url, RelayChoice(url, peer.relay_public_key, peer.hosts_requested_app, True))
        for relays in live:
            for relay in relays:
                for url in relay.urls:
                    prior: RelayChoice | None = merged.get(url)
                    hosts: bool = relay.hosts_requested_app or bool(
                        prior is not None and not prior.from_cache and prior.hosts_requested_app
                    )
                    merged[url] = RelayChoice(url, relay.relay_public_key, hosts, False)

        tried: set[str] = {a.relay for a in self._snapshot.tried_relays}
        remaining: list[RelayChoice] = [c for c in merged.values() if c.url not in tried]
        return sorted(remaining, key=lambda c: (not c.hosts_requested_app, c.from_cache))
