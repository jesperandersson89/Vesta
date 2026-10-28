// getDeviceGroupMembers pages through a group channel larger than one relay page.
// Mirrors tests/VestaClient.Tests/DeviceGroupMembersTests.cs and clients/vesta-client-py/tests/test_device_group_members.py.
// Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";

import { VestaConnection, VestaIdentity, buildAnnounce, buildLink, deviceGroupChannel, generateGroupId } from "../dist/index.js";

class FakeRelaySocket {
    constructor(stored, maxPage) {
        this.readyState = 0;
        this.fetches = [];
        this._listeners = new Map();
        this._stored = stored;
        this._maxPage = maxPage;
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
        if (msg.type !== "FETCH") return;
        this.fetches.push(msg);
        setImmediate(() => {
            const limit = Math.min(Math.max(msg.limit ?? 100, 1), this._maxPage);
            const events = this._stored
                .filter((e) => e.event.channelId === msg.channelId && e.sequence >= msg.fromSequence)
                .slice(0, limit);
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

function connected(stored, maxPage, identity) {
    let socket;
    const conn = new VestaConnection({
        serverUrl: "ws://test",
        identity,
        channels: [],
        autoReconnect: false,
        createSocket: () => (socket = new FakeRelaySocket(stored, maxPage)),
    });
    conn.connect();
    socket.open();
    socket.inject({ type: "WELCOME", serverId: "s1", channels: [] });
    return { conn, socket: () => socket };
}

test("getDeviceGroupMembers reads every page, not just the first batch", async () => {
    const groupId = generateGroupId();
    const founder = VestaIdentity.generate();
    const others = Array.from({ length: 4 }, () => VestaIdentity.generate());
    const events = [buildAnnounce(founder, groupId, "Founder"), ...others.map((o) => buildLink(founder, groupId, o.publicKey))];
    const stored = events.map((event, i) => ({ event, sequence: i + 1, receivedAt: "2026-01-01T00:00:00.000Z" }));
    const { conn, socket } = connected(stored, 2, founder);

    const group = await conn.getDeviceGroupMembers(groupId);

    assert.equal(Object.keys(group.members).length, 5);
    assert.ok(founder.clientId in group.members);
    for (const o of others) assert.ok(o.clientId in group.members);
    assert.ok(socket().fetches.length >= 3);
    assert.ok(socket().fetches.every((f) => f.channelId === deviceGroupChannel(groupId)));
});

test("getDeviceGroupMembers on an empty channel resolves an empty group without waiting for a timeout", async () => {
    const founder = VestaIdentity.generate();
    const { conn } = connected([], 2, founder);

    const group = await conn.getDeviceGroupMembers(generateGroupId(), 2000);

    assert.equal(Object.keys(group.members).length, 0);
});
