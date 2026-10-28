// Control ACKs (nil event id) reach the "ack" event, like C# VestaConnection.OnAck.
// Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";

import { VestaConnection } from "../dist/index.js";

const NIL = "00000000-0000-0000-0000-000000000000";

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
        for (const l of this._listeners.get("message") ?? []) l({ data: JSON.stringify(msg) });
    }
}

function connected() {
    let socket;
    const conn = new VestaConnection({
        serverUrl: "ws://test",
        clientId: "test-client",
        channels: [],
        autoReconnect: false,
        createSocket: () => (socket = new FakeSocket()),
    });
    conn.connect();
    socket.open();
    socket.inject({ type: "WELCOME", serverId: "s1", channels: [] });
    return { conn, socket };
}

test("a control ACK (nil event id) is emitted as 'ack' without touching sequence tracking", () => {
    const { conn, socket } = connected();
    const acks = [];
    conn.on("ack", (m) => acks.push(m));

    conn.createChannel("myapp/shop", { visibility: "broadcast" });
    socket.inject({ type: "ACK", channelId: "myapp/shop", eventId: NIL, sequence: 0 });

    assert.equal(acks.length, 1);
    assert.equal(acks[0].channelId, "myapp/shop");
    assert.equal(acks[0].eventId, NIL);
    assert.equal(conn.lastSequences["myapp/shop"], undefined);
});

test("a normal publish ACK is still emitted and tracked", () => {
    const { conn, socket } = connected();
    const acks = [];
    conn.on("ack", (m) => acks.push(m));

    socket.inject({ type: "ACK", channelId: "myapp/chat", eventId: "evt-1", sequence: 7 });

    assert.equal(acks.length, 1);
    assert.equal(conn.lastSequences["myapp/chat"], 7);
});
