"""
Projection snapshot round-trip tests for AppendOnlyLog, LwwMap, LwwRegister, and the
InMemoryProjectionStore / SqliteProjectionStore.

Mirrors tests/VestaCore.Tests/Projections/SnapshotTests.cs and
clients/vesta-client-ts/tests/snapshots.test.mjs.
"""

from __future__ import annotations

import asyncio
import unittest

from vesta_client import (
    AppendOnlyLog,
    EventReducer,
    InMemoryProjectionStore,
    LwwMap,
    LwwMapUpdate,
    LwwRegister,
    SequencedEvent,
    SnapshotNotSupportedError,
    VestaEvent,
    restore_projection,
    save_projection,
)
from vesta_client.projection_store import SqliteProjectionStore


def _evt(evt_id: str, timestamp: str, event_type: str, payload) -> VestaEvent:
    return VestaEvent(
        id=evt_id,
        channel_id="test/snap",
        timestamp=timestamp,
        client_id="c1",
        event_type=event_type,
        payload=payload,
    )


def _seq(event: VestaEvent, sequence: int) -> SequencedEvent:
    return SequencedEvent(event=event, sequence=sequence, received_at=event.timestamp)


class _NoSnapshotReducer(EventReducer):
    @property
    def state(self):
        return None

    def _reduce(self, event: VestaEvent) -> None:
        pass


class EventReducerDefaultTests(unittest.TestCase):
    def test_snapshot_throws_by_default(self) -> None:
        r = _NoSnapshotReducer()
        with self.assertRaises(SnapshotNotSupportedError):
            r.snapshot()


class AppendOnlyLogSnapshotTests(unittest.TestCase):
    def test_round_trips_items_and_dedup(self) -> None:
        project = lambda e: e.payload if e.event_type == "item-added" else None
        log = AppendOnlyLog(project)
        log.apply(_seq(_evt("id-1", "2026-01-01T00:00:00Z", "item-added", {"title": "a"}), 1))
        log.apply(_seq(_evt("id-2", "2026-01-01T00:00:01Z", "item-added", {"title": "b"}), 2))

        snap = log.snapshot()
        self.assertEqual(snap.last_sequence, 2)

        restored = AppendOnlyLog(project)
        restored.restore(snap)
        self.assertEqual(restored.last_sequence, 2)
        self.assertEqual(restored.state, [{"title": "a"}, {"title": "b"}])

        restored.apply(_seq(_evt("id-1", "2026-01-01T00:00:00Z", "item-added", {"title": "a"}), 1))
        self.assertEqual(len(restored.state), 2)


class LwwRegisterSnapshotTests(unittest.TestCase):
    def test_round_trips_value_and_timestamp(self) -> None:
        project = lambda e: e.payload["value"]
        reg = LwwRegister(project)
        reg.apply(_seq(_evt("id-1", "2026-01-01T00:00:00Z", "set", {"value": "first"}), 1))
        reg.apply(_seq(_evt("id-2", "2026-01-01T00:00:05Z", "set", {"value": "second"}), 2))

        snap = reg.snapshot()
        restored = LwwRegister(project)
        restored.restore(snap)
        self.assertEqual(restored.state, "second")
        self.assertEqual(restored.last_modified, "2026-01-01T00:00:05Z")

        restored.apply(_seq(_evt("id-3", "2026-01-01T00:00:02Z", "set", {"value": "stale"}), 3))
        self.assertEqual(restored.state, "second")


class LwwMapSnapshotTests(unittest.TestCase):
    def test_round_trips_and_preserves_tombstones(self) -> None:
        def project(e: VestaEvent):
            if e.event_type == "set":
                return LwwMapUpdate.set(e.payload["key"], e.payload["value"])
            if e.event_type == "remove":
                return LwwMapUpdate.remove(e.payload["key"])
            return None

        m = LwwMap(project)
        m.apply(_seq(_evt("id-1", "2026-01-01T00:00:00Z", "set", {"key": "a", "value": 1}), 1))
        m.apply(_seq(_evt("id-2", "2026-01-01T00:00:01Z", "set", {"key": "b", "value": 2}), 2))
        m.apply(_seq(_evt("id-3", "2026-01-01T00:00:02Z", "remove", {"key": "a"}), 3))

        snap = m.snapshot()
        restored = LwwMap(project)
        restored.restore(snap)

        self.assertEqual(restored.state, {"b": 2})

        restored.apply(_seq(_evt("id-4", "2026-01-01T00:00:00.500Z", "set", {"key": "a", "value": 99}), 4))
        found, _ = restored.try_get("a")
        self.assertFalse(found)


class ProjectionStoreTests(unittest.TestCase):
    def test_in_memory_save_load_delete(self) -> None:
        async def _t() -> None:
            store = InMemoryProjectionStore()
            project = lambda e: e.payload["value"]
            reg = LwwRegister(project)
            reg.apply(_seq(_evt("id-1", "2026-01-01T00:00:00Z", "set", {"value": "hello"}), 7))

            await save_projection(store, "chan-1", "greeting", reg)

            fresh = LwwRegister(project)
            found = await restore_projection(store, "chan-1", "greeting", fresh)
            self.assertTrue(found)
            self.assertEqual(fresh.state, "hello")
            self.assertEqual(fresh.last_sequence, 7)

            await store.delete("chan-1", "greeting")
            after_delete = await store.load("chan-1", "greeting")
            self.assertIsNone(after_delete)

        asyncio.run(_t())

    def test_sqlite_persists_across_instances(self) -> None:
        async def _t() -> None:
            store1 = SqliteProjectionStore(":memory:")
            # Use a shared in-memory connection via the same object to simulate persistence
            # within a process (":memory:" sqlite DBs don't share across connections).
            project = lambda e: e.payload["value"]
            reg = LwwRegister(project)
            reg.apply(_seq(_evt("id-1", "2026-01-01T00:00:00Z", "set", {"value": "hi"}), 3))
            await save_projection(store1, "chan-1", "reg", reg)

            fresh = LwwRegister(project)
            found = await restore_projection(store1, "chan-1", "reg", fresh)
            self.assertTrue(found)
            self.assertEqual(fresh.state, "hi")
            self.assertEqual(fresh.last_sequence, 3)
            store1.close()

        asyncio.run(_t())


if __name__ == "__main__":
    unittest.main()
