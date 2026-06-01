// Outbox + crash-recovery tests for the TypeScript client.
// Mirrors the C# tests in tests/VestaClient.Tests/OfflineOutboxSyncTests.cs.
// Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";

import { InMemoryClientEventStore, VestaConnection } from "../dist/index.js";

// ── Fake socket ──────────────────────────────────────────────────────────────
// A minimal stand-in for a WebSocket that lets the test drive both sides of
// the conversation. `sent` records messages from client → server; `inject`
// pushes a message from server → client.

class FakeSocket {
    constructor() {
        this.readyState = 0; // CONNECTING
        this.sent = [];
        this._listeners = new Map();
    }
    addEventListener(event, listener) {
        if (!this._listeners.has(event)) this._listeners.set(event, new Set());
        this._listeners.get(event).add(listener);
    }
    removeEventListener(event, listener) {
        this._listeners.get(event)?.delete(listener);
    }
    send(data) {
        if (this.readyState !== 1) throw new Error("socket not open");
        this.sent.push(JSON.parse(data));
    }
    close(_code, reason) {
        this.readyState = 3; // CLOSED
        for (const l of this._listeners.get("close") ?? []) {
            l({ code: 1000, reason: reason ?? "" });
        }
    }
    // Test driver helpers
    open() {
        this.readyState = 1;
        for (const l of this._listeners.get("open") ?? []) l();
    }
    inject(msg) {
        for (const l of this._listeners.get("message") ?? []) {
            l({ data: JSON.stringify(msg) });
        }
    }
}

function makeEvent(channelId, id) {
    return {
        id,
        channelId,
        timestamp: "2026-01-01T00:00:00.000Z",
        clientId: "test-client",
        eventType: "test.evt",
        payload: { hello: "world" },
    };
}

function makeConnection(store, opts = {}) {
    const sockets = [];
    const conn = new VestaConnection({
        serverUrl: "ws://test",
        clientId: "test-client",
        channels: ["test/outbox"],
        autoReconnect: false,
        createSocket: () => {
            const s = new FakeSocket();
            sockets.push(s);
            return s;
        },
        localStore: store,
        ...opts,
    });
    return { conn, sockets };
}

// ── InMemoryClientEventStore ─────────────────────────────────────────────────

test("InMemoryClientEventStore: outbox returns pending + sent", async () => {
    const store = new InMemoryClientEventStore();
    const e1 = makeEvent("test/outbox", "id-1");
    const e2 = makeEvent("test/outbox", "id-2");

    await store.enqueueOutbox(e1);
    await store.enqueueOutbox(e2);
    await store.markOutboxSent("id-1");

    const pending = await store.getPendingOutbox();
    assert.equal(pending.length, 2);
    const sent = pending.find((e) => e.event.id === "id-1");
    const pendingEntry = pending.find((e) => e.event.id === "id-2");
    assert.equal(sent.status, "sent");
    assert.equal(pendingEntry.status, "pending");
});

test("InMemoryClientEventStore: markOutboxConfirmed removes entry", async () => {
    const store = new InMemoryClientEventStore();
    const e = makeEvent("test/outbox", "id-1");
    await store.enqueueOutbox(e);
    await store.markOutboxSent("id-1");
    await store.markOutboxConfirmed("id-1");
    const pending = await store.getPendingOutbox();
    assert.equal(pending.length, 0);
});

test("InMemoryClientEventStore: storeEvent dedups on (channelId, sequence)", async () => {
    const store = new InMemoryClientEventStore();
    const e = makeEvent("test/outbox", "id-1");
    await store.storeEvent({
        event: e,
        sequence: 5,
        receivedAt: "2026-01-01T00:00:00.000Z",
    });
    await store.storeEvent({
        event: e,
        sequence: 5,
        receivedAt: "2026-01-01T00:00:00.000Z",
    });
    const events = await store.getEvents("test/outbox", 0);
    assert.equal(events.length, 1);
});

// ── Connection + outbox integration ──────────────────────────────────────────

test("publish while disconnected enqueues to outbox", async () => {
    const store = new InMemoryClientEventStore();
    const { conn } = makeConnection(store);

    // Never call connect() — connection is offline.
    const e = makeEvent("test/outbox", "id-offline");
    conn.publish(e);

    const pending = await store.getPendingOutbox();
    assert.equal(pending.length, 1);
    assert.equal(pending[0].event.id, "id-offline");
    assert.equal(pending[0].status, "pending");
});

test("publish without store while disconnected throws", () => {
    const conn = new VestaConnection({
        serverUrl: "ws://test",
        clientId: "test-client",
        channels: ["test/outbox"],
        autoReconnect: false,
        createSocket: () => new FakeSocket(),
    });
    assert.throws(() => conn.publish(makeEvent("test/outbox", "id-1")));
});

test("outbox flushes on WELCOME and clears on ACK", async () => {
    const store = new InMemoryClientEventStore();

    // Pre-populate the outbox with one 'pending' and one 'sent' (simulating a
    // previous session that died between SEND and ACK).
    const e1 = makeEvent("test/outbox", "id-pending");
    const e2 = makeEvent("test/outbox", "id-sent");
    await store.enqueueOutbox(e1);
    await store.enqueueOutbox(e2);
    await store.markOutboxSent("id-sent");

    const { conn, sockets } = makeConnection(store);
    conn.connect();
    const socket = sockets[0];
    socket.open();

    // HELLO is sent on open.
    assert.equal(socket.sent[0].type, "HELLO");

    // Inject WELCOME — connection should flush both outbox entries.
    socket.inject({
        type: "WELCOME",
        serverId: "test-server",
        channels: ["test/outbox"],
    });

    // Yield to let the async flush run.
    await new Promise((r) => setImmediate(r));

    const publishes = socket.sent.filter((m) => m.type === "PUBLISH");
    assert.equal(publishes.length, 2);
    const ids = publishes.map((m) => m.event.id).sort();
    assert.deepEqual(ids, ["id-pending", "id-sent"]);

    // Both should now be marked 'sent' in the outbox.
    const pendingAfterFlush = await store.getPendingOutbox();
    assert.equal(pendingAfterFlush.length, 2);
    assert.ok(pendingAfterFlush.every((e) => e.status === "sent"));

    // ACK both events — outbox should drain.
    socket.inject({
        type: "ACK",
        channelId: "test/outbox",
        eventId: "id-pending",
        sequence: 1,
    });
    socket.inject({
        type: "ACK",
        channelId: "test/outbox",
        eventId: "id-sent",
        sequence: 2,
    });

    await new Promise((r) => setImmediate(r));

    const finalOutbox = await store.getPendingOutbox();
    assert.equal(finalOutbox.length, 0);
});

test("ACK after live publish caches event with assigned sequence", async () => {
    const store = new InMemoryClientEventStore();
    const { conn, sockets } = makeConnection(store);
    conn.connect();
    const socket = sockets[0];
    socket.open();
    socket.inject({
        type: "WELCOME",
        serverId: "test-server",
        channels: ["test/outbox"],
    });
    await new Promise((r) => setImmediate(r));

    const e = makeEvent("test/outbox", "id-live");
    conn.publish(e);

    socket.inject({
        type: "ACK",
        channelId: "test/outbox",
        eventId: "id-live",
        sequence: 42,
    });

    await new Promise((r) => setImmediate(r));

    const cached = await store.getEvents("test/outbox", 0);
    assert.equal(cached.length, 1);
    assert.equal(cached[0].sequence, 42);
    assert.equal(cached[0].event.id, "id-live");
});
