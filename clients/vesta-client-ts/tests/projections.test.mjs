// Mirrors VestaCore.Tests.Projections / tests/test_projections.py.
// Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";

import {
    AppendOnlyLog,
    EventReducer,
    LwwMap,
    LwwMapUpdate,
    LwwRegister,
} from "../dist/index.js";

const evt = (eventType, payload, ts = "2026-01-01T00:00:00.000Z", id) => ({
    id: id ?? `id-${eventType}-${ts}-${JSON.stringify(payload)}`,
    channelId: "test",
    timestamp: ts,
    clientId: "test-client-id-12345",
    eventType,
    payload,
});

const seq = (event, sequence) => ({
    event,
    sequence,
    receivedAt: "2026-01-01T00:00:00.000Z",
});

// ── EventReducer base contract ───────────────────────────────────────────────

class Counter extends EventReducer {
    constructor() {
        super();
        this._n = 0;
    }
    get state() {
        return this._n;
    }
    reduce() {
        this._n++;
    }
}

test("EventReducer.apply advances lastSequence", () => {
    const r = new Counter();
    r.apply(seq(evt("x", {}), 5));
    assert.equal(r.state, 1);
    assert.equal(r.lastSequence, 5);
});

test("EventReducer out-of-order sequence does not regress", () => {
    const r = new Counter();
    r.apply(seq(evt("x", {}, undefined, "a"), 5));
    r.apply(seq(evt("x", {}, undefined, "b"), 3));
    assert.equal(r.lastSequence, 5);
    assert.equal(r.state, 2);
});

test("EventReducer.applyLocal does not advance lastSequence", () => {
    const r = new Counter();
    r.applyLocal(evt("x", {}));
    assert.equal(r.state, 1);
    assert.equal(r.lastSequence, 0);
});

test("EventReducer.applyBatch", () => {
    const r = new Counter();
    r.applyBatch(
        [1, 2, 3].map((i) => seq(evt("x", {}, undefined, `e${i}`), i)),
    );
    assert.equal(r.state, 3);
    assert.equal(r.lastSequence, 3);
});

// ── AppendOnlyLog ────────────────────────────────────────────────────────────

test("AppendOnlyLog appends in order", () => {
    const log = new AppendOnlyLog((e) =>
        e.eventType === "msg" ? e.payload.text : null,
    );
    log.apply(seq(evt("msg", { text: "hello" }, undefined, "1"), 1));
    log.apply(seq(evt("msg", { text: "world" }, undefined, "2"), 2));
    assert.deepEqual(log.state, ["hello", "world"]);
    assert.equal(log.lastSequence, 2);
});

test("AppendOnlyLog skips irrelevant events", () => {
    const log = new AppendOnlyLog((e) =>
        e.eventType === "msg" ? e.payload.text : null,
    );
    log.apply(seq(evt("msg", { text: "a" }, undefined, "1"), 1));
    log.apply(seq(evt("other", { text: "b" }, undefined, "2"), 2));
    log.apply(seq(evt("msg", { text: "c" }, undefined, "3"), 3));
    assert.deepEqual(log.state, ["a", "c"]);
    assert.equal(log.lastSequence, 3);
});

test("AppendOnlyLog dedups local + sequenced", () => {
    const log = new AppendOnlyLog((e) => e.payload.text);
    const local = evt("msg", { text: "once" }, undefined, "dup-id");
    log.applyLocal(local);
    log.apply(seq(local, 1));
    assert.deepEqual(log.state, ["once"]);
    assert.equal(log.lastSequence, 1);
});

// ── LwwRegister ──────────────────────────────────────────────────────────────

test("LwwRegister latest wins", () => {
    const reg = new LwwRegister((e) =>
        e.eventType === "set" ? e.payload.text : null,
    );
    reg.apply(
        seq(evt("set", { text: "first" }, "2026-01-01T00:00:00.000Z", "1"), 1),
    );
    reg.apply(
        seq(evt("set", { text: "second" }, "2026-01-01T00:00:10.000Z", "2"), 2),
    );
    assert.equal(reg.state, "second");
    assert.equal(reg.lastModified, "2026-01-01T00:00:10.000Z");
});

test("LwwRegister older ignored", () => {
    const reg = new LwwRegister((e) => e.payload.text);
    reg.apply(
        seq(evt("set", { text: "newer" }, "2026-01-01T00:00:10.000Z", "1"), 1),
    );
    reg.apply(
        seq(evt("set", { text: "older" }, "2026-01-01T00:00:00.000Z", "2"), 2),
    );
    assert.equal(reg.state, "newer");
});

test("LwwRegister empty", () => {
    const reg = new LwwRegister(() => null);
    assert.equal(reg.state, null);
    assert.equal(reg.lastModified, "");
});

// ── LwwMap ───────────────────────────────────────────────────────────────────

const scoreMap = () =>
    new LwwMap((e) => {
        if (e.eventType === "set")
            return LwwMapUpdate.set(e.payload.key, e.payload.value);
        if (e.eventType === "remove") return LwwMapUpdate.remove(e.payload.key);
        return null;
    });

test("LwwMap set + overwrite", () => {
    const m = scoreMap();
    m.apply(
        seq(
            evt("set", { key: "a", value: 1 }, "2026-01-01T00:00:00.000Z", "1"),
            1,
        ),
    );
    m.apply(
        seq(
            evt(
                "set",
                { key: "a", value: 99 },
                "2026-01-01T00:00:10.000Z",
                "2",
            ),
            2,
        ),
    );
    assert.equal(m.state.get("a"), 99);
});

test("LwwMap earlier ignored", () => {
    const m = scoreMap();
    m.apply(
        seq(
            evt(
                "set",
                { key: "a", value: 100 },
                "2026-01-01T00:00:10.000Z",
                "1",
            ),
            1,
        ),
    );
    m.apply(
        seq(
            evt("set", { key: "a", value: 1 }, "2026-01-01T00:00:00.000Z", "2"),
            2,
        ),
    );
    assert.equal(m.state.get("a"), 100);
});

test("LwwMap remove tombstones", () => {
    const m = scoreMap();
    m.apply(
        seq(
            evt("set", { key: "a", value: 1 }, "2026-01-01T00:00:00.000Z", "1"),
            1,
        ),
    );
    m.apply(
        seq(evt("remove", { key: "a" }, "2026-01-01T00:00:01.000Z", "2"), 2),
    );
    assert.equal(m.state.size, 0);
    assert.deepEqual(m.tryGet("a"), { found: false });
});

test("LwwMap stale set after remove ignored", () => {
    const m = scoreMap();
    m.apply(
        seq(evt("remove", { key: "a" }, "2026-01-01T00:00:10.000Z", "1"), 1),
    );
    m.apply(
        seq(
            evt("set", { key: "a", value: 1 }, "2026-01-01T00:00:00.000Z", "2"),
            2,
        ),
    );
    assert.equal(m.state.size, 0);
});

test("LwwMap reanimate after remove", () => {
    const m = scoreMap();
    m.apply(
        seq(
            evt("set", { key: "a", value: 1 }, "2026-01-01T00:00:00.000Z", "1"),
            1,
        ),
    );
    m.apply(
        seq(evt("remove", { key: "a" }, "2026-01-01T00:00:01.000Z", "2"), 2),
    );
    m.apply(
        seq(
            evt(
                "set",
                { key: "a", value: 42 },
                "2026-01-01T00:00:02.000Z",
                "3",
            ),
            3,
        ),
    );
    assert.equal(m.state.get("a"), 42);
});

test("LwwMap missing key", () => {
    const m = scoreMap();
    assert.deepEqual(m.tryGet("missing"), { found: false });
});
