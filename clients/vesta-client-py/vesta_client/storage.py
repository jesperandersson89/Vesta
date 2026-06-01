"""
Client-side local storage abstraction.

Mirrors the C# ``VestaClient.Storage.IClientEventStore`` 1:1: caches sequenced
events received from the server and manages an outbox for events created while
disconnected.

Ships two implementations:

- :class:`InMemoryClientEventStore` — non-persistent, suitable for tests and
  transient sessions.
- :class:`SqliteClientEventStore` — sqlite-backed (stdlib :mod:`sqlite3`),
  persists across process restarts.

The outbox returns both ``"pending"`` (never sent) AND ``"sent"`` (sent but the
process died before the ACK landed) entries from :meth:`get_pending_outbox` —
server-side appends are idempotent on event id, so re-sending a ``"sent"``
entry on reconnect is safe and is what recovers the crash case.
"""

from __future__ import annotations

import asyncio
import json
import sqlite3
import threading
from collections.abc import Iterable
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Literal, Protocol, runtime_checkable

from vesta_client.types import SequencedEvent, VestaEvent

OutboxStatus = Literal["pending", "sent", "confirmed"]


@dataclass
class OutboxEntry:
    """An entry in the client outbox — an event created offline pending sync."""

    event: VestaEvent
    created_at: str  # ISO-8601
    status: OutboxStatus


@runtime_checkable
class ClientEventStore(Protocol):
    """Async protocol for client-side local storage."""

    async def store_event(self, sequenced: SequencedEvent) -> None: ...
    async def store_events(self, events: Iterable[SequencedEvent]) -> None: ...
    async def get_events(
        self, channel_id: str, from_sequence: int, limit: int = 100
    ) -> list[SequencedEvent]: ...
    async def get_latest_sequence(self, channel_id: str) -> int: ...
    async def get_channel_positions(self) -> dict[str, int]: ...

    async def enqueue_outbox(self, event: VestaEvent) -> None: ...
    async def get_pending_outbox(self) -> list[OutboxEntry]: ...
    async def mark_outbox_sent(self, event_id: str) -> None: ...
    async def mark_outbox_confirmed(self, event_id: str) -> None: ...


# ── In-memory implementation ────────────────────────────────────────────────


class InMemoryClientEventStore:
    """In-memory ``ClientEventStore``. Loses state on process restart."""

    def __init__(self) -> None:
        self._events: dict[str, list[SequencedEvent]] = {}
        self._outbox: dict[str, tuple[OutboxEntry, int]] = {}
        self._next_seq = 0
        self._lock = threading.RLock()

    async def store_event(self, sequenced: SequencedEvent) -> None:
        with self._lock:
            channel = sequenced.event.channel_id
            existing = self._events.setdefault(channel, [])
            if any(e.sequence == sequenced.sequence for e in existing):
                return
            existing.append(sequenced)
            existing.sort(key=lambda e: e.sequence)

    async def store_events(self, events: Iterable[SequencedEvent]) -> None:
        for e in events:
            await self.store_event(e)

    async def get_events(
        self, channel_id: str, from_sequence: int, limit: int = 100
    ) -> list[SequencedEvent]:
        with self._lock:
            return [
                e
                for e in self._events.get(channel_id, [])
                if e.sequence >= from_sequence
            ][:limit]

    async def get_latest_sequence(self, channel_id: str) -> int:
        with self._lock:
            lst = self._events.get(channel_id)
            return lst[-1].sequence if lst else 0

    async def get_channel_positions(self) -> dict[str, int]:
        with self._lock:
            return {ch: lst[-1].sequence for ch, lst in self._events.items() if lst}

    async def enqueue_outbox(self, event: VestaEvent) -> None:
        with self._lock:
            if event.id in self._outbox:
                return
            entry = OutboxEntry(
                event=event,
                created_at=datetime.now(timezone.utc).isoformat(),
                status="pending",
            )
            self._outbox[event.id] = (entry, self._next_seq)
            self._next_seq += 1

    async def get_pending_outbox(self) -> list[OutboxEntry]:
        with self._lock:
            items = sorted(
                (v for v in self._outbox.values() if v[0].status in ("pending", "sent")),
                key=lambda v: v[1],
            )
            return [v[0] for v in items]

    async def mark_outbox_sent(self, event_id: str) -> None:
        with self._lock:
            if event_id in self._outbox:
                entry, seq = self._outbox[event_id]
                self._outbox[event_id] = (
                    OutboxEntry(entry.event, entry.created_at, "sent"),
                    seq,
                )

    async def mark_outbox_confirmed(self, event_id: str) -> None:
        with self._lock:
            self._outbox.pop(event_id, None)


# ── SQLite implementation ──────────────────────────────────────────────────


class SqliteClientEventStore:
    """
    sqlite-backed ``ClientEventStore``. Uses the stdlib :mod:`sqlite3` module.

    Synchronous sqlite calls are wrapped in :meth:`asyncio.to_thread` to keep
    the async surface honest. A single connection is reused; all writes go
    through a re-entrant lock so the store is safe to share across tasks.
    """

    def __init__(self, database: str = ":memory:") -> None:
        self._conn = sqlite3.connect(database, check_same_thread=False)
        self._conn.execute("PRAGMA journal_mode=WAL")
        self._lock = threading.RLock()
        self._init_schema()

    def close(self) -> None:
        with self._lock:
            self._conn.close()

    def _init_schema(self) -> None:
        with self._lock:
            self._conn.executescript(
                """
                CREATE TABLE IF NOT EXISTS events (
                    id           TEXT PRIMARY KEY,
                    channel_id   TEXT NOT NULL,
                    sequence     INTEGER NOT NULL,
                    timestamp    TEXT NOT NULL,
                    client_id    TEXT NOT NULL,
                    event_type   TEXT NOT NULL,
                    payload      TEXT NOT NULL,
                    parent_id    TEXT,
                    signature    TEXT,
                    metadata     TEXT,
                    replace_flag INTEGER NOT NULL DEFAULT 0,
                    received_at  TEXT NOT NULL,
                    UNIQUE(channel_id, sequence)
                );
                CREATE INDEX IF NOT EXISTS idx_events_channel_seq
                    ON events(channel_id, sequence);

                CREATE TABLE IF NOT EXISTS outbox (
                    id           TEXT PRIMARY KEY,
                    channel_id   TEXT NOT NULL,
                    timestamp    TEXT NOT NULL,
                    client_id    TEXT NOT NULL,
                    event_type   TEXT NOT NULL,
                    payload      TEXT NOT NULL,
                    parent_id    TEXT,
                    signature    TEXT,
                    metadata     TEXT,
                    replace_flag INTEGER NOT NULL DEFAULT 0,
                    created_at   TEXT NOT NULL,
                    insert_seq   INTEGER NOT NULL,
                    status       TEXT NOT NULL DEFAULT 'pending'
                );
                CREATE INDEX IF NOT EXISTS idx_outbox_status
                    ON outbox(status, insert_seq);
                """
            )
            self._conn.commit()

    # -- helpers ----------------------------------------------------------

    @staticmethod
    def _event_row(evt: VestaEvent) -> tuple:
        return (
            evt.id,
            evt.channel_id,
            evt.timestamp,
            evt.client_id,
            evt.event_type,
            json.dumps(evt.payload),
            evt.parent_id,
            evt.signature,
            json.dumps(evt.metadata) if evt.metadata is not None else None,
            1 if evt.replace else 0,
        )

    @staticmethod
    def _row_to_event(row: sqlite3.Row | tuple) -> VestaEvent:
        # Columns: id, channel_id, timestamp, client_id, event_type, payload,
        # parent_id, signature, metadata, replace_flag
        return VestaEvent(
            id=row[0],
            channel_id=row[1],
            timestamp=row[2],
            client_id=row[3],
            event_type=row[4],
            payload=json.loads(row[5]),
            parent_id=row[6],
            signature=row[7],
            replace=bool(row[9]),
            metadata=json.loads(row[8]) if row[8] is not None else None,
        )

    def _store_event_sync(self, sequenced: SequencedEvent) -> None:
        with self._lock:
            evt = sequenced.event
            self._conn.execute(
                """
                INSERT OR IGNORE INTO events
                    (id, channel_id, timestamp, client_id, event_type, payload,
                     parent_id, signature, metadata, replace_flag, sequence, received_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (*self._event_row(evt), sequenced.sequence, sequenced.received_at),
            )
            self._conn.commit()

    async def store_event(self, sequenced: SequencedEvent) -> None:
        await asyncio.to_thread(self._store_event_sync, sequenced)

    async def store_events(self, events: Iterable[SequencedEvent]) -> None:
        events_list = list(events)
        if not events_list:
            return

        def _bulk() -> None:
            with self._lock:
                self._conn.executemany(
                    """
                    INSERT OR IGNORE INTO events
                        (id, channel_id, timestamp, client_id, event_type, payload,
                         parent_id, signature, metadata, replace_flag, sequence, received_at)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    [
                        (*self._event_row(s.event), s.sequence, s.received_at)
                        for s in events_list
                    ],
                )
                self._conn.commit()

        await asyncio.to_thread(_bulk)

    async def get_events(
        self, channel_id: str, from_sequence: int, limit: int = 100
    ) -> list[SequencedEvent]:
        def _read() -> list[SequencedEvent]:
            with self._lock:
                cursor = self._conn.execute(
                    """
                    SELECT id, channel_id, timestamp, client_id, event_type, payload,
                           parent_id, signature, metadata, replace_flag, sequence, received_at
                    FROM events
                    WHERE channel_id = ? AND sequence >= ?
                    ORDER BY sequence ASC
                    LIMIT ?
                    """,
                    (channel_id, from_sequence, limit),
                )
                rows = cursor.fetchall()
            return [
                SequencedEvent(
                    event=self._row_to_event(r),
                    sequence=r[10],
                    received_at=r[11],
                )
                for r in rows
            ]

        return await asyncio.to_thread(_read)

    async def get_latest_sequence(self, channel_id: str) -> int:
        def _read() -> int:
            with self._lock:
                row = self._conn.execute(
                    "SELECT COALESCE(MAX(sequence), 0) FROM events WHERE channel_id = ?",
                    (channel_id,),
                ).fetchone()
            return row[0] if row else 0

        return await asyncio.to_thread(_read)

    async def get_channel_positions(self) -> dict[str, int]:
        def _read() -> dict[str, int]:
            with self._lock:
                rows = self._conn.execute(
                    "SELECT channel_id, MAX(sequence) FROM events GROUP BY channel_id"
                ).fetchall()
            return {r[0]: r[1] for r in rows}

        return await asyncio.to_thread(_read)

    async def enqueue_outbox(self, event: VestaEvent) -> None:
        def _write() -> None:
            with self._lock:
                # Use COALESCE(MAX(insert_seq), 0) + 1 to assign FIFO order.
                self._conn.execute(
                    """
                    INSERT OR IGNORE INTO outbox
                        (id, channel_id, timestamp, client_id, event_type, payload,
                         parent_id, signature, metadata, replace_flag, created_at,
                         insert_seq, status)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?,
                            (SELECT COALESCE(MAX(insert_seq), 0) + 1 FROM outbox),
                            'pending')
                    """,
                    (
                        *self._event_row(event),
                        datetime.now(timezone.utc).isoformat(),
                    ),
                )
                self._conn.commit()

        await asyncio.to_thread(_write)

    async def get_pending_outbox(self) -> list[OutboxEntry]:
        def _read() -> list[OutboxEntry]:
            with self._lock:
                cursor = self._conn.execute(
                    """
                    SELECT id, channel_id, timestamp, client_id, event_type, payload,
                           parent_id, signature, metadata, replace_flag, created_at, status
                    FROM outbox
                    WHERE status IN ('pending', 'sent')
                    ORDER BY insert_seq ASC
                    """
                )
                rows = cursor.fetchall()
            return [
                OutboxEntry(
                    event=self._row_to_event(r),
                    created_at=r[10],
                    status=r[11],  # type: ignore[arg-type]
                )
                for r in rows
            ]

        return await asyncio.to_thread(_read)

    async def mark_outbox_sent(self, event_id: str) -> None:
        def _write() -> None:
            with self._lock:
                self._conn.execute(
                    "UPDATE outbox SET status = 'sent' WHERE id = ?", (event_id,)
                )
                self._conn.commit()

        await asyncio.to_thread(_write)

    async def mark_outbox_confirmed(self, event_id: str) -> None:
        def _write() -> None:
            with self._lock:
                self._conn.execute("DELETE FROM outbox WHERE id = ?", (event_id,))
                self._conn.commit()

        await asyncio.to_thread(_write)
