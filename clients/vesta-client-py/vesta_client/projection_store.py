"""
Persistent store for :class:`ProjectionSnapshot`\\ s so projections can resume from the last
known sequence on cold start instead of replaying the entire channel log. Mirrors the C#
``VestaClient.Storage.IProjectionStore``.

Snapshots are keyed by ``(channel_id, projection_id)`` — a single channel may have multiple
projections (e.g. chat history + presence map), each persisted independently.
"""

from __future__ import annotations

import asyncio
import sqlite3
import threading
from typing import Protocol, runtime_checkable

from vesta_client.projections import EventReducer, ProjectionSnapshot


@runtime_checkable
class ProjectionStore(Protocol):
    """Async protocol for persisting projection snapshots."""

    async def save(self, channel_id: str, projection_id: str, snapshot: ProjectionSnapshot) -> None: ...
    async def load(self, channel_id: str, projection_id: str) -> ProjectionSnapshot | None: ...
    async def delete(self, channel_id: str, projection_id: str) -> None: ...


def _key(channel_id: str, projection_id: str) -> tuple[str, str]:
    return (channel_id, projection_id)


class InMemoryProjectionStore:
    """In-memory ``ProjectionStore``. Loses state on process restart."""

    def __init__(self) -> None:
        self._snapshots: dict[tuple[str, str], ProjectionSnapshot] = {}
        self._lock = threading.RLock()

    async def save(self, channel_id: str, projection_id: str, snapshot: ProjectionSnapshot) -> None:
        with self._lock:
            self._snapshots[_key(channel_id, projection_id)] = snapshot

    async def load(self, channel_id: str, projection_id: str) -> ProjectionSnapshot | None:
        with self._lock:
            return self._snapshots.get(_key(channel_id, projection_id))

    async def delete(self, channel_id: str, projection_id: str) -> None:
        with self._lock:
            self._snapshots.pop(_key(channel_id, projection_id), None)


class SqliteProjectionStore:
    """sqlite-backed ``ProjectionStore`` (stdlib :mod:`sqlite3`), persists across restarts."""

    def __init__(self, database: str = ":memory:") -> None:
        self._conn = sqlite3.connect(database, check_same_thread=False)
        self._lock = threading.RLock()
        with self._lock:
            self._conn.execute(
                """
                CREATE TABLE IF NOT EXISTS projection_snapshots (
                    channel_id     TEXT NOT NULL,
                    projection_id  TEXT NOT NULL,
                    last_sequence  INTEGER NOT NULL,
                    state_json     TEXT NOT NULL,
                    PRIMARY KEY (channel_id, projection_id)
                )
                """
            )
            self._conn.commit()

    def close(self) -> None:
        with self._lock:
            self._conn.close()

    async def save(self, channel_id: str, projection_id: str, snapshot: ProjectionSnapshot) -> None:
        def _write() -> None:
            with self._lock:
                self._conn.execute(
                    """
                    INSERT INTO projection_snapshots (channel_id, projection_id, last_sequence, state_json)
                    VALUES (?, ?, ?, ?)
                    ON CONFLICT(channel_id, projection_id)
                    DO UPDATE SET last_sequence = excluded.last_sequence, state_json = excluded.state_json
                    """,
                    (channel_id, projection_id, snapshot.last_sequence, snapshot.state_json),
                )
                self._conn.commit()

        await asyncio.to_thread(_write)

    async def load(self, channel_id: str, projection_id: str) -> ProjectionSnapshot | None:
        def _read() -> ProjectionSnapshot | None:
            with self._lock:
                row = self._conn.execute(
                    "SELECT last_sequence, state_json FROM projection_snapshots "
                    "WHERE channel_id = ? AND projection_id = ?",
                    (channel_id, projection_id),
                ).fetchone()
            return ProjectionSnapshot(row[0], row[1]) if row else None

        return await asyncio.to_thread(_read)

    async def delete(self, channel_id: str, projection_id: str) -> None:
        def _write() -> None:
            with self._lock:
                self._conn.execute(
                    "DELETE FROM projection_snapshots WHERE channel_id = ? AND projection_id = ?",
                    (channel_id, projection_id),
                )
                self._conn.commit()

        await asyncio.to_thread(_write)


async def save_projection(
    store: ProjectionStore, channel_id: str, projection_id: str, reducer: EventReducer
) -> None:
    """Capture a reducer's current state and persist it."""
    await store.save(channel_id, projection_id, reducer.snapshot())


async def restore_projection(
    store: ProjectionStore, channel_id: str, projection_id: str, reducer: EventReducer
) -> bool:
    """Load a snapshot (if any) and restore it into the reducer. Returns True if found."""
    snapshot = await store.load(channel_id, projection_id)
    if snapshot is None:
        return False
    reducer.restore(snapshot)
    return True


__all__ = [
    "InMemoryProjectionStore",
    "ProjectionStore",
    "SqliteProjectionStore",
    "restore_projection",
    "save_projection",
]
