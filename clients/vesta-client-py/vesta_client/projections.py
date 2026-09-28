"""
SDK conflict-resolution primitives — Python port.

Mirrors ``VestaCore.Projections`` from the C# SDK so a Python client and a
C# client see the same state for the same event stream. See
``docs/projections.md`` for the picking guide and semantics.

All primitives are thread-safe via an internal lock.
"""

from __future__ import annotations

import json
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Callable, Generic, Iterable, Literal, TypeVar

from vesta_client.types import SequencedEvent, VestaEvent

TState = TypeVar("TState")
T = TypeVar("T")
K = TypeVar("K")
V = TypeVar("V")


@dataclass(frozen=True)
class ProjectionSnapshot:
    """A captured snapshot of a projection's state at a specific server-assigned sequence.

    Matches the C# ``VestaCore.Projections.ProjectionSnapshot`` record shape so persisted
    snapshots are interchangeable across SDKs where the projected state is JSON-compatible.
    """

    last_sequence: int
    state_json: str


class SnapshotNotSupportedError(NotImplementedError):
    """Raised by the default :meth:`EventReducer.snapshot` / :meth:`EventReducer.restore`
    when a subclass hasn't opted in."""

    def __init__(self, reducer_type_name: str) -> None:
        super().__init__(
            f"Reducer {reducer_type_name} does not support snapshotting. "
            "Override snapshot() and _restore_state() to opt in."
        )


class EventReducer(ABC, Generic[TState]):
    """Base class for projections that fold a channel's event stream into typed state.

    Concrete reducers implement :meth:`_reduce` to mutate their internal storage and
    expose a snapshot via :attr:`state`. Applying a :class:`SequencedEvent` advances
    :attr:`last_sequence`; applying a bare :class:`VestaEvent` via :meth:`apply_local`
    does not (used for optimistic local updates before the server has assigned a
    sequence).
    """

    def __init__(self) -> None:
        self._lock = threading.RLock()
        self._last_sequence = 0

    @property
    def last_sequence(self) -> int:
        """Highest server-assigned sequence number this reducer has observed."""
        with self._lock:
            return self._last_sequence

    @property
    @abstractmethod
    def state(self) -> TState:
        """Snapshot of the current projected state. Implementations should return an immutable view."""

    def apply(self, sequenced: SequencedEvent) -> None:
        """Apply a server-confirmed event. Advances :attr:`last_sequence`."""
        with self._lock:
            self._reduce(sequenced.event)
            if sequenced.sequence > self._last_sequence:
                self._last_sequence = sequenced.sequence

    def apply_batch(self, events: Iterable[SequencedEvent]) -> None:
        """Apply a batch of server-confirmed events."""
        for sequenced in events:
            self.apply(sequenced)

    def apply_local(self, event: VestaEvent) -> None:
        """Apply a locally-authored event (optimistic UI). Does NOT advance ``last_sequence``."""
        with self._lock:
            self._reduce(event)

    @abstractmethod
    def _reduce(self, event: VestaEvent) -> None:
        """Implementations mutate their internal state in response to the event."""

    def snapshot(self) -> ProjectionSnapshot:
        """Capture the current state as a :class:`ProjectionSnapshot`. Override in subclasses
        that want snapshot support; the default raises :class:`SnapshotNotSupportedError`."""
        raise SnapshotNotSupportedError(type(self).__name__)

    def restore(self, snapshot: ProjectionSnapshot) -> None:
        """Restore the reducer from a previously captured snapshot. Replaces all internal
        state and sets :attr:`last_sequence` to ``snapshot.last_sequence``."""
        with self._lock:
            self._restore_state(snapshot.state_json)
            self._last_sequence = snapshot.last_sequence

    def _restore_state(self, state_json: str) -> None:
        """Hook for :meth:`restore` — implementations replace internal state from the JSON
        produced by their own :meth:`snapshot`. Called under the reducer's lock."""
        raise SnapshotNotSupportedError(type(self).__name__)


class AppendOnlyLog(EventReducer[list[T]], Generic[T]):
    """Append-only ordered list reducer.

    Every event for which the supplied projector returns a non-None value is
    appended to the log. Events are deduplicated by ``event.id`` so that an
    event seen both via :meth:`EventReducer.apply_local` and later via the
    server's confirmed sequence does not appear twice.
    """

    def __init__(self, project: Callable[[VestaEvent], T | None]) -> None:
        super().__init__()
        self._project = project
        self._items: list[T] = []
        self._seen_ids: set[str] = set()

    @property
    def state(self) -> list[T]:
        with self._lock:
            return list(self._items)

    @property
    def count(self) -> int:
        with self._lock:
            return len(self._items)

    def _reduce(self, event: VestaEvent) -> None:
        if event.id in self._seen_ids:
            return
        projected = self._project(event)
        if projected is None:
            return
        self._seen_ids.add(event.id)
        self._items.append(projected)

    def snapshot(self) -> ProjectionSnapshot:
        with self._lock:
            payload = {"items": self._items, "seenIds": list(self._seen_ids)}
            return ProjectionSnapshot(self._last_sequence, json.dumps(payload))

    def _restore_state(self, state_json: str) -> None:
        payload = json.loads(state_json)
        self._items = list(payload["items"])
        self._seen_ids = set(payload["seenIds"])


class LwwRegister(EventReducer["T | None"], Generic[T]):
    """Single-value last-writer-wins register.

    The event with the strictly greater ``timestamp`` wins. Ties preserve the
    existing value (deterministic across clients).
    """

    def __init__(self, project: Callable[[VestaEvent], T | None]) -> None:
        super().__init__()
        self._project = project
        self._value: T | None = None
        self._value_timestamp: str = ""  # empty sorts before any ISO-8601 string

    @property
    def state(self) -> T | None:
        with self._lock:
            return self._value

    @property
    def last_modified(self) -> str:
        """ISO-8601 timestamp of the event that produced the current value, or ``""`` if empty."""
        with self._lock:
            return self._value_timestamp

    def _reduce(self, event: VestaEvent) -> None:
        projected = self._project(event)
        if projected is None:
            return
        if event.timestamp > self._value_timestamp:
            self._value = projected
            self._value_timestamp = event.timestamp

    def snapshot(self) -> ProjectionSnapshot:
        with self._lock:
            payload = {"value": self._value, "valueTimestamp": self._value_timestamp}
            return ProjectionSnapshot(self._last_sequence, json.dumps(payload))

    def _restore_state(self, state_json: str) -> None:
        payload = json.loads(state_json)
        self._value = payload["value"]
        self._value_timestamp = payload["valueTimestamp"]


@dataclass(frozen=True)
class LwwMapUpdate(Generic[K, V]):
    """A single update to an :class:`LwwMap`: either set a key or remove (tombstone) it."""

    key: K
    kind: Literal["set", "remove"]
    value: V | None = None

    @staticmethod
    def set(key: K, value: V) -> "LwwMapUpdate[K, V]":
        return LwwMapUpdate(key=key, kind="set", value=value)

    @staticmethod
    def remove(key: K) -> "LwwMapUpdate[K, V]":
        return LwwMapUpdate(key=key, kind="remove", value=None)


@dataclass
class _Entry(Generic[V]):
    value: V | None
    timestamp: str
    tombstoned: bool


class LwwMap(EventReducer["dict[K, V]"], Generic[K, V]):
    """Key-value map where each key independently follows last-writer-wins.

    The projector returns either an :class:`LwwMapUpdate` or ``None`` when the
    event does not affect the map. A Set with an older timestamp than the
    current entry is ignored; a Remove tombstones the key; a later Set whose
    timestamp is older than the tombstone is also ignored (no zombies).
    """

    def __init__(self, project: Callable[[VestaEvent], LwwMapUpdate[K, V] | None]) -> None:
        super().__init__()
        self._project = project
        self._entries: dict[K, _Entry[V]] = {}

    @property
    def state(self) -> dict[K, V]:
        with self._lock:
            return {
                k: e.value  # type: ignore[misc]
                for k, e in self._entries.items()
                if not e.tombstoned
            }

    def try_get(self, key: K) -> tuple[bool, V | None]:
        """Return ``(found, value)``. ``found`` is False for missing or tombstoned keys."""
        with self._lock:
            entry = self._entries.get(key)
            if entry is None or entry.tombstoned:
                return (False, None)
            return (True, entry.value)

    def _reduce(self, event: VestaEvent) -> None:
        update = self._project(event)
        if update is None:
            return
        existing = self._entries.get(update.key)
        if existing is not None and event.timestamp <= existing.timestamp:
            return  # stale — keep existing
        if update.kind == "remove":
            self._entries[update.key] = _Entry(value=None, timestamp=event.timestamp, tombstoned=True)
        else:
            self._entries[update.key] = _Entry(value=update.value, timestamp=event.timestamp, tombstoned=False)

    def snapshot(self) -> ProjectionSnapshot:
        with self._lock:
            entries = [
                {"key": k, "value": e.value, "timestamp": e.timestamp, "tombstoned": e.tombstoned}
                for k, e in self._entries.items()
            ]
            return ProjectionSnapshot(self._last_sequence, json.dumps(entries))

    def _restore_state(self, state_json: str) -> None:
        entries = json.loads(state_json)
        self._entries = {
            e["key"]: _Entry(value=e["value"], timestamp=e["timestamp"], tombstoned=e["tombstoned"])
            for e in entries
        }


@dataclass(frozen=True)
class ProjectionCheckpoint:
    """Lightweight pair to persist alongside projection snapshots for resumable replay."""

    channel_id: str
    last_sequence: int


__all__ = [
    "AppendOnlyLog",
    "EventReducer",
    "LwwMap",
    "LwwMapUpdate",
    "LwwRegister",
    "ProjectionCheckpoint",
    "ProjectionSnapshot",
    "SnapshotNotSupportedError",
]
