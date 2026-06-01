"""Tests for vesta_client.projections — mirrors VestaCore.Tests.Projections."""

from __future__ import annotations

import unittest

from vesta_client import (
    AppendOnlyLog,
    EventReducer,
    LwwMap,
    LwwMapUpdate,
    LwwRegister,
    SequencedEvent,
    VestaEvent,
)


def _evt(event_type: str, payload: dict, ts: str = "2026-01-01T00:00:00.000Z", evt_id: str | None = None) -> VestaEvent:
    return VestaEvent(
        id=evt_id or f"id-{event_type}-{ts}-{hash(repr(payload)) & 0xFFFF:x}",
        channel_id="test",
        timestamp=ts,
        client_id="test-client-id-12345",
        event_type=event_type,
        payload=payload,
    )


def _seq(evt: VestaEvent, sequence: int) -> SequencedEvent:
    return SequencedEvent(event=evt, sequence=sequence, received_at="2026-01-01T00:00:00.000Z")


# ── EventReducer base contract ───────────────────────────────────────────────


class _Counter(EventReducer[int]):
    def __init__(self) -> None:
        super().__init__()
        self._count = 0

    @property
    def state(self) -> int:
        with self._lock:
            return self._count

    def _reduce(self, event: VestaEvent) -> None:
        self._count += 1


class EventReducerTests(unittest.TestCase):
    def test_apply_advances_last_sequence(self) -> None:
        r = _Counter()
        r.apply(_seq(_evt("x", {}), 5))
        self.assertEqual(r.state, 1)
        self.assertEqual(r.last_sequence, 5)

    def test_out_of_order_sequence_does_not_regress(self) -> None:
        r = _Counter()
        r.apply(_seq(_evt("x", {}, evt_id="a"), 5))
        r.apply(_seq(_evt("x", {}, evt_id="b"), 3))
        self.assertEqual(r.last_sequence, 5)
        self.assertEqual(r.state, 2)

    def test_apply_local_does_not_advance_sequence(self) -> None:
        r = _Counter()
        r.apply_local(_evt("x", {}))
        self.assertEqual(r.state, 1)
        self.assertEqual(r.last_sequence, 0)

    def test_apply_batch(self) -> None:
        r = _Counter()
        r.apply_batch([_seq(_evt("x", {}, evt_id=f"e{i}"), i) for i in (1, 2, 3)])
        self.assertEqual(r.state, 3)
        self.assertEqual(r.last_sequence, 3)


# ── AppendOnlyLog ────────────────────────────────────────────────────────────


class AppendOnlyLogTests(unittest.TestCase):
    def _make_log(self) -> AppendOnlyLog[str]:
        return AppendOnlyLog(
            lambda e: e.payload["text"] if e.event_type == "msg" else None
        )

    def test_appends_in_order(self) -> None:
        log = self._make_log()
        log.apply(_seq(_evt("msg", {"text": "hello"}, evt_id="1"), 1))
        log.apply(_seq(_evt("msg", {"text": "world"}, evt_id="2"), 2))
        self.assertEqual(log.state, ["hello", "world"])
        self.assertEqual(log.last_sequence, 2)

    def test_skips_irrelevant_events(self) -> None:
        log = self._make_log()
        log.apply(_seq(_evt("msg", {"text": "a"}, evt_id="1"), 1))
        log.apply(_seq(_evt("other", {"text": "b"}, evt_id="2"), 2))
        log.apply(_seq(_evt("msg", {"text": "c"}, evt_id="3"), 3))
        self.assertEqual(log.state, ["a", "c"])
        self.assertEqual(log.last_sequence, 3)

    def test_dedup_local_then_sequenced(self) -> None:
        log = AppendOnlyLog(lambda e: e.payload["text"])
        local = _evt("msg", {"text": "once"}, evt_id="dup-id")
        log.apply_local(local)
        log.apply(_seq(local, 1))
        self.assertEqual(log.state, ["once"])
        self.assertEqual(log.last_sequence, 1)


# ── LwwRegister ──────────────────────────────────────────────────────────────


class LwwRegisterTests(unittest.TestCase):
    def test_latest_wins(self) -> None:
        reg = LwwRegister(lambda e: e.payload["text"] if e.event_type == "set" else None)
        reg.apply(_seq(_evt("set", {"text": "first"}, ts="2026-01-01T00:00:00.000Z", evt_id="1"), 1))
        reg.apply(_seq(_evt("set", {"text": "second"}, ts="2026-01-01T00:00:10.000Z", evt_id="2"), 2))
        self.assertEqual(reg.state, "second")
        self.assertEqual(reg.last_modified, "2026-01-01T00:00:10.000Z")

    def test_older_ignored(self) -> None:
        reg = LwwRegister(lambda e: e.payload["text"])
        reg.apply(_seq(_evt("set", {"text": "newer"}, ts="2026-01-01T00:00:10.000Z", evt_id="1"), 1))
        reg.apply(_seq(_evt("set", {"text": "older"}, ts="2026-01-01T00:00:00.000Z", evt_id="2"), 2))
        self.assertEqual(reg.state, "newer")

    def test_empty(self) -> None:
        reg: LwwRegister[str] = LwwRegister(lambda _e: None)
        self.assertIsNone(reg.state)
        self.assertEqual(reg.last_modified, "")


# ── LwwMap ───────────────────────────────────────────────────────────────────


def _score_map() -> LwwMap[str, int]:
    def project(e: VestaEvent) -> LwwMapUpdate[str, int] | None:
        if e.event_type == "set":
            return LwwMapUpdate.set(e.payload["key"], e.payload["value"])
        if e.event_type == "remove":
            return LwwMapUpdate.remove(e.payload["key"])
        return None
    return LwwMap(project)


class LwwMapTests(unittest.TestCase):
    def test_set_and_overwrite(self) -> None:
        m = _score_map()
        m.apply(_seq(_evt("set", {"key": "a", "value": 1}, ts="2026-01-01T00:00:00.000Z", evt_id="1"), 1))
        m.apply(_seq(_evt("set", {"key": "a", "value": 99}, ts="2026-01-01T00:00:10.000Z", evt_id="2"), 2))
        self.assertEqual(m.state, {"a": 99})

    def test_earlier_ignored(self) -> None:
        m = _score_map()
        m.apply(_seq(_evt("set", {"key": "a", "value": 100}, ts="2026-01-01T00:00:10.000Z", evt_id="1"), 1))
        m.apply(_seq(_evt("set", {"key": "a", "value": 1}, ts="2026-01-01T00:00:00.000Z", evt_id="2"), 2))
        self.assertEqual(m.state, {"a": 100})

    def test_remove_tombstones(self) -> None:
        m = _score_map()
        m.apply(_seq(_evt("set", {"key": "a", "value": 1}, ts="2026-01-01T00:00:00.000Z", evt_id="1"), 1))
        m.apply(_seq(_evt("remove", {"key": "a"}, ts="2026-01-01T00:00:01.000Z", evt_id="2"), 2))
        self.assertEqual(m.state, {})
        found, _ = m.try_get("a")
        self.assertFalse(found)

    def test_stale_set_after_remove_ignored(self) -> None:
        m = _score_map()
        m.apply(_seq(_evt("remove", {"key": "a"}, ts="2026-01-01T00:00:10.000Z", evt_id="1"), 1))
        m.apply(_seq(_evt("set", {"key": "a", "value": 1}, ts="2026-01-01T00:00:00.000Z", evt_id="2"), 2))
        self.assertEqual(m.state, {})

    def test_reanimate_after_remove(self) -> None:
        m = _score_map()
        m.apply(_seq(_evt("set", {"key": "a", "value": 1}, ts="2026-01-01T00:00:00.000Z", evt_id="1"), 1))
        m.apply(_seq(_evt("remove", {"key": "a"}, ts="2026-01-01T00:00:01.000Z", evt_id="2"), 2))
        m.apply(_seq(_evt("set", {"key": "a", "value": 42}, ts="2026-01-01T00:00:02.000Z", evt_id="3"), 3))
        self.assertEqual(m.state, {"a": 42})

    def test_missing_key(self) -> None:
        m = _score_map()
        found, value = m.try_get("missing")
        self.assertFalse(found)
        self.assertIsNone(value)


if __name__ == "__main__":
    unittest.main()
