// Limit-notice classification + outbox dead-letter tests.
// Mirrors the C# tests around VestaClient.VestaErrorCodes.Classify / VestaConnection.OnLimited.
// Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";

import { classifyErrorCode, InMemoryClientEventStore, VestaConnection } from "../dist/index.js";

// ── classifyErrorCode ─────────────────────────────────────────────────────────

test("classifyErrorCode: RATE_LIMITED is a transient limit, not event-fatal", () => {
    const c = classifyErrorCode("RATE_LIMITED");
    assert.equal(c.isLimit, true);
    assert.equal(c.isTransient, true);
    assert.equal(c.isEventFatal, false);
});

test("classifyErrorCode: QUOTA_EXCEEDED is a fatal limit", () => {
    const c = classifyErrorCode("QUOTA_EXCEEDED");
    assert.equal(c.isLimit, true);
    assert.equal(c.isTransient, false);
    assert.equal(c.isEventFatal, true);
});

test("classifyErrorCode: UNKNOWN_APP, ACCESS_DENIED, APP_NOT_ALLOWED, MESSAGE_QUOTA_EXCEEDED are fatal limits", () => {
    for (const code of ["UNKNOWN_APP", "ACCESS_DENIED", "APP_NOT_ALLOWED", "MESSAGE_QUOTA_EXCEEDED"]) {
        const c = classifyErrorCode(code);
        assert.equal(c.isLimit, true, code);
        assert.equal(c.isTransient, false, code);
        assert.equal(c.isEventFatal, true, code);
    }
});

test("classifyErrorCode: protocol errors are fatal but not limits", () => {
    for (const code of [
        "INVALID_CHANNEL",
        "INVALID_SIGNATURE",
        "SIGNATURE_REQUIRED",
        "CLIENT_ID_MISMATCH",
        "PROTOCOL_NAMESPACE_RESERVED",
        "CHANNEL_DELETED",
    ]) {
        const c = classifyErrorCode(code);
        assert.equal(c.isLimit, false, code);
        assert.equal(c.isEventFatal, true, code);
    }
});

test("classifyErrorCode: unknown codes are conservative (not a limit, not fatal, transient)", () => {
    const c = classifyErrorCode("SOME_FUTURE_CODE");
    assert.equal(c.isLimit, false);
    assert.equal(c.isTransient, true);
    assert.equal(c.isEventFatal, false);
});

// ── Connection wiring ──────────────────────────────────────────────────────────

class FakeSocket {
    constructor() {
        this.readyState = 0;
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
    close() {
        this.readyState = 3;
    }
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

function makeEvent(id) {
    return {
        id,
        channelId: "myapp/chat",
        timestamp: "2026-01-01T00:00:00.000Z",
        clientId: "test-client",
        eventType: "chat.message",
        payload: { text: "hi" },
    };
}

test("ERROR with a fatal limit code dead-letters the matching outbox entry after flush", async () => {
    const store = new InMemoryClientEventStore();
    let socket;
    const conn = new VestaConnection({
        serverUrl: "ws://test",
        clientId: "test-client",
        channels: ["myapp/chat"],
        autoReconnect: false,
        createSocket: () => {
            socket = new FakeSocket();
            return socket;
        },
        localStore: store,
    });

    // Publish while offline — enqueues to the outbox.
    conn.publish(makeEvent("doomed-1"));

    let notice;
    conn.on("limited", (n) => (notice = n));

    conn.connect();
    socket.open();
    socket.inject({ type: "WELCOME", serverId: "s1", channels: ["myapp/chat"] });
    await new Promise((r) => setImmediate(r)); // let flushOutbox() send the pending entry

    socket.inject({
        type: "ERROR",
        code: "QUOTA_EXCEEDED",
        message: "storage quota exceeded",
        eventId: "doomed-1",
        channelId: "myapp/chat",
    });

    assert.ok(notice);
    assert.equal(notice.code, "QUOTA_EXCEEDED");
    assert.equal(notice.isTransient, false);

    const pending = await store.getPendingOutbox();
    assert.equal(pending.length, 0, "rejected entry must not be retried");
});

test("ERROR with RATE_LIMITED emits 'limited' but does not dead-letter (transient)", async () => {
    const store = new InMemoryClientEventStore();
    let socket;
    const conn = new VestaConnection({
        serverUrl: "ws://test",
        clientId: "test-client",
        channels: ["myapp/chat"],
        autoReconnect: false,
        createSocket: () => {
            socket = new FakeSocket();
            return socket;
        },
        localStore: store,
    });

    conn.publish(makeEvent("retryable-1"));

    let notice;
    conn.on("limited", (n) => (notice = n));

    conn.connect();
    socket.open();
    socket.inject({ type: "WELCOME", serverId: "s1", channels: ["myapp/chat"] });
    await new Promise((r) => setImmediate(r));

    socket.inject({
        type: "ERROR",
        code: "RATE_LIMITED",
        message: "slow down",
        eventId: "retryable-1",
        channelId: "myapp/chat",
    });

    assert.ok(notice);
    assert.equal(notice.isTransient, true);

    const pending = await store.getPendingOutbox();
    assert.equal(pending.length, 1, "transient limits must not dead-letter");
    assert.equal(pending[0].status, "sent");
});
