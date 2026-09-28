// Projection snapshot round-trip tests for AppendOnlyLog, LwwMap, LwwRegister, and the
// InMemoryProjectionStore / LocalStorageProjectionStore-style stores.
// Mirrors tests/VestaCore.Tests/Projections/SnapshotTests.cs.
// Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";

import {
    AppendOnlyLog,
    EventReducer,
    InMemoryProjectionStore,
    LwwMap,
    LwwMapUpdate,
    LwwRegister,
    restoreProjection,
    saveProjection,
    SnapshotNotSupportedError,
} from "../dist/index.js";

function evt(id, timestamp, type, payload) {
    return {
        id,
        channelId: "test/snap",
        timestamp,
        clientId: "c1",
        eventType: type,
        payload,
    };
}

function seq(event, sequence) {
    return { event, sequence, receivedAt: event.timestamp };
}

// ── EventReducer default ──────────────────────────────────────────────────────

test("EventReducer.snapshot throws SnapshotNotSupportedError by default", () => {
    class NoSnapshotReducer extends EventReducer {
        get state() {
            return null;
        }
        reduce() {
            // no-op
        }
    }
    const r = new NoSnapshotReducer();
    assert.throws(() => r.snapshot(), SnapshotNotSupportedError);
});

// ── AppendOnlyLog ──────────────────────────────────────────────────────────────

test("AppendOnlyLog: snapshot + restore round-trips items and dedup", () => {
    const project = (e) => (e.eventType === "item-added" ? e.payload : null);
    const log = new AppendOnlyLog(project);
    log.apply(seq(evt("id-1", "2026-01-01T00:00:00Z", "item-added", { title: "a" }), 1));
    log.apply(seq(evt("id-2", "2026-01-01T00:00:01Z", "item-added", { title: "b" }), 2));

    const snap = log.snapshot();
    assert.equal(snap.lastSequence, 2);

    const restored = new AppendOnlyLog(project);
    restored.restore(snap);
    assert.equal(restored.lastSequence, 2);
    assert.deepEqual(restored.state, [{ title: "a" }, { title: "b" }]);

    // Dedup survives restore: re-applying id-1 must not duplicate.
    restored.apply(seq(evt("id-1", "2026-01-01T00:00:00Z", "item-added", { title: "a" }), 1));
    assert.equal(restored.state.length, 2);
});

// ── LwwRegister ────────────────────────────────────────────────────────────────

test("LwwRegister: snapshot + restore round-trips value and timestamp", () => {
    const project = (e) => e.payload.value;
    const reg = new LwwRegister(project);
    reg.apply(seq(evt("id-1", "2026-01-01T00:00:00Z", "set", { value: "first" }), 1));
    reg.apply(seq(evt("id-2", "2026-01-01T00:00:05Z", "set", { value: "second" }), 2));

    const snap = reg.snapshot();
    const restored = new LwwRegister(project);
    restored.restore(snap);
    assert.equal(restored.state, "second");
    assert.equal(restored.lastModified, "2026-01-01T00:00:05Z");

    // A stale update (older timestamp) after restore must be ignored.
    restored.apply(seq(evt("id-3", "2026-01-01T00:00:02Z", "set", { value: "stale" }), 3));
    assert.equal(restored.state, "second");
});

// ── LwwMap ─────────────────────────────────────────────────────────────────────

test("LwwMap: snapshot + restore preserves tombstones", () => {
    const project = (e) => {
        if (e.eventType === "set") return LwwMapUpdate.set(e.payload.key, e.payload.value);
        if (e.eventType === "remove") return LwwMapUpdate.remove(e.payload.key);
        return null;
    };
    const map = new LwwMap(project);
    map.apply(seq(evt("id-1", "2026-01-01T00:00:00Z", "set", { key: "a", value: 1 }), 1));
    map.apply(seq(evt("id-2", "2026-01-01T00:00:01Z", "set", { key: "b", value: 2 }), 2));
    map.apply(seq(evt("id-3", "2026-01-01T00:00:02Z", "remove", { key: "a" }), 3));

    const snap = map.snapshot();
    const restored = new LwwMap(project);
    restored.restore(snap);

    assert.deepEqual([...restored.state.entries()], [["b", 2]]);

    // A stale Set for the tombstoned key (older than the remove) must stay ignored.
    restored.apply(seq(evt("id-4", "2026-01-01T00:00:00.500Z", "set", { key: "a", value: 99 }), 4));
    assert.equal(restored.tryGet("a").found, false);
});

// ── ProjectionStore ────────────────────────────────────────────────────────────

test("InMemoryProjectionStore: save/load/delete round-trip via helpers", async () => {
    const store = new InMemoryProjectionStore();
    const project = (e) => e.payload.value;
    const reg = new LwwRegister(project);
    reg.apply(seq(evt("id-1", "2026-01-01T00:00:00Z", "set", { value: "hello" }), 7));

    await saveProjection(store, "chan-1", "greeting", reg);

    const fresh = new LwwRegister(project);
    const found = await restoreProjection(store, "chan-1", "greeting", fresh);
    assert.equal(found, true);
    assert.equal(fresh.state, "hello");
    assert.equal(fresh.lastSequence, 7);

    await store.delete("chan-1", "greeting");
    const afterDelete = await store.load("chan-1", "greeting");
    assert.equal(afterDelete, null);
});

test("InMemoryProjectionStore: separate (channelId, projectionId) keys don't collide", async () => {
    const store = new InMemoryProjectionStore();
    await store.save("chan-1", "proj-a", { lastSequence: 1, stateJson: "{}" });
    await store.save("chan-1", "proj-b", { lastSequence: 2, stateJson: "{}" });
    await store.save("chan-2", "proj-a", { lastSequence: 3, stateJson: "{}" });

    assert.equal((await store.load("chan-1", "proj-a")).lastSequence, 1);
    assert.equal((await store.load("chan-1", "proj-b")).lastSequence, 2);
    assert.equal((await store.load("chan-2", "proj-a")).lastSequence, 3);
});
