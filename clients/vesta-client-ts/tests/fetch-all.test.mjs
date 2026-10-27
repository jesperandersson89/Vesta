// fetchAll: auto-paging FETCH, against a fake relay that clamps page size like Protocol:MaxFetchLimit.
// Mirrors tests/VestaClient.Tests/FetchAllTests.cs and clients/vesta-client-py/tests/test_fetch_all.py.
// Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";

import { VestaConnection } from "../dist/index.js";

const CHANNEL = "myapp/world";

function stored(sequence) {
    return {
        event: {
            id: `evt-${sequence}`,
            channelId: CHANNEL,
            timestamp: "2026-01-01T00:00:00.000Z",
            clientId: "someone",
            eventType: "test.message",
            payload: { n: sequence },
        },
        sequence,
        receivedAt: "2026-01-01T00:00:00.000Z",
    };
}

class FakeRelaySocket {
    constructor({ eventCount, maxPage, refuse = false, silent = false }) {
        this.readyState = 0;
        this.fetches = [];
        this._listeners = new Map();
        this._stored = Array.from({ length: eventCount }, (_, i) => stored(i + 1));
        this._maxPage = maxPage;
        this._refuse = refuse;
        this._silent = silent;
    }
    addEventListener(event, listener) {
        if (!this._listeners.has(event)) this._listeners.set(event, new Set());
        this._listeners.get(event).add(listener);
    }
    removeEventListener(event, listener) {
        this._listeners.get(event)?.delete(listener);
    }
    send(data) {
        const msg = JSON.parse(data);
        if (msg.type !== "FETCH" || this._silent) return;
        this.fetches.push(msg);
        setImmediate(() => {
            if (this._refuse) {
                this.inject({ type: "ERROR", code: "ACCESS_DENIED", message: "Not authorized", channelId: msg.channelId });
                return;
            }
            const limit = Math.min(Math.max(msg.limit ?? 100, 1), this._maxPage);
            const events = this._stored
                .filter((e) => e.sequence >= msg.fromSequence)
                .slice(0, limit)
                .filter((e) => msg.toSequence == null || e.sequence <= msg.toSequence);
            this.inject({ type: "EVENTS_BATCH", channelId: msg.channelId, events });
        });
    }
    close() {
        this.readyState = 3;
    }
    open() {
        this.readyState = 1;
        for (const l of this._listeners.get("open") ?? []) l();
    }
    inject(msg) {
        for (const l of this._listeners.get("message") ?? []) l({ data: JSON.stringify(msg) });
    }
}

function connect(relayOptions) {
    let socket;
    const conn = new VestaConnection({
        serverUrl: "ws://test",
        clientId: "test-client",
        channels: [CHANNEL],
        autoReconnect: false,
        createSocket: () => {
            socket = new FakeRelaySocket(relayOptions);
            return socket;
        },
    });
    conn.connect();
    socket.open();
    socket.inject({ type: "WELCOME", serverId: "s1", channels: [CHANNEL] });
    return { conn, socket };
}

test("fetchAll pages past the relay's clamp and returns everything in order", async () => {
    const { conn, socket } = connect({ eventCount: 10, maxPage: 3 });

    const events = await conn.fetchAll(CHANNEL, { pageSize: 100 });

    assert.deepEqual(events.map((e) => e.sequence), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
    // 3, 3, 3, 1 events and one empty page that ends the loop.
    assert.deepEqual(socket.fetches.map((f) => f.fromSequence), [0, 4, 7, 10, 11]);
});

test("fetchAll with toSequence stops at the upper bound", async () => {
    const { conn } = connect({ eventCount: 10, maxPage: 3 });

    const events = await conn.fetchAll(CHANNEL, { fromSequence: 3, toSequence: 7 });

    assert.deepEqual(events.map((e) => e.sequence), [3, 4, 5, 6, 7]);
});

test("fetchAll on an empty channel resolves empty after one page", async () => {
    const { conn, socket } = connect({ eventCount: 0, maxPage: 3 });

    assert.deepEqual(await conn.fetchAll(CHANNEL), []);
    assert.equal(socket.fetches.length, 1);
});

test("fetchAll rejects with the relay's error code", async () => {
    const { conn } = connect({ eventCount: 5, maxPage: 3, refuse: true });

    await assert.rejects(conn.fetchAll(CHANNEL), /^Error: ACCESS_DENIED/);
});

test("fetchAll rejects when a page never arrives", async () => {
    const { conn } = connect({ eventCount: 5, maxPage: 3, silent: true });

    await assert.rejects(conn.fetchAll(CHANNEL, { pageTimeoutMs: 50 }), /No FETCH page/);
});

test("fetchAll ignores errors and batches for other channels, and rejects a bad pageSize", async () => {
    const { conn, socket } = connect({ eventCount: 2, maxPage: 5 });
    const pending = conn.fetchAll(CHANNEL);
    socket.inject({ type: "ERROR", code: "ACCESS_DENIED", message: "other", channelId: "myapp/other" });
    socket.inject({ type: "EVENTS_BATCH", channelId: "myapp/other", events: [] });

    assert.deepEqual((await pending).map((e) => e.sequence), [1, 2]);
    await assert.rejects(conn.fetchAll(CHANNEL, { pageSize: 0 }), RangeError);
});
