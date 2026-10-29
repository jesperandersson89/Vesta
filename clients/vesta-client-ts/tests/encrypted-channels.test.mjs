// Encrypted channels (relay phase A): WELCOME `features`, `createChannel({ encrypted })` gating,
// `revokeAccess`, and ENCRYPTION_REQUIRED classification. Mirrors the C# EncryptedChannelClientTests.
// Run after `npm run build` — imports the compiled output from `dist/`.

import { test } from "node:test";
import assert from "node:assert/strict";

import { classifyErrorCode, RelayFeatures, VestaConnection } from "../dist/index.js";

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

function connected(welcome) {
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
    socket.inject({ type: "WELCOME", serverId: "s1", channels: [], ...welcome });
    return { conn, socket };
}

test("features from WELCOME are exposed; a relay without the field has none", () => {
    const modern = connected({ features: ["encrypted-channels"] });
    assert.deepEqual([...modern.conn.features], ["encrypted-channels"]);
    assert.equal(modern.conn.supportsFeature(RelayFeatures.EncryptedChannels), true);

    const legacy = connected({});
    assert.deepEqual([...legacy.conn.features], []);
    assert.equal(legacy.conn.supportsFeature(RelayFeatures.EncryptedChannels), false);
});

test("createChannel({ encrypted: true }) sends the flag on a relay that supports it", () => {
    const { conn, socket } = connected({ features: ["encrypted-channels"] });

    conn.createChannel("notes/secret", { visibility: "private", encrypted: true });

    const create = socket.sent.find((m) => m.type === "CREATE_CHANNEL");
    assert.equal(create.channelId, "notes/secret");
    assert.equal(create.visibility, "private");
    assert.equal(create.encrypted, true);
});

test("createChannel without encrypted omits the field, so old relays see the old wire shape", () => {
    const { conn, socket } = connected({ features: ["encrypted-channels"] });

    conn.createChannel("notes/plain");

    const create = socket.sent.find((m) => m.type === "CREATE_CHANNEL");
    assert.equal("encrypted" in create, false);
});

test("createChannel({ encrypted: true }) throws against a relay that does not advertise the feature", () => {
    const { conn, socket } = connected({});

    assert.throws(() => conn.createChannel("notes/secret", { encrypted: true }), /encrypted channels/);
    assert.equal(socket.sent.some((m) => m.type === "CREATE_CHANNEL"), false);
});

test("revokeAccess sends REVOKE_ACCESS", () => {
    const { conn, socket } = connected({ features: ["encrypted-channels"] });

    conn.revokeAccess("notes/secret", "former-member");

    const revoke = socket.sent.find((m) => m.type === "REVOKE_ACCESS");
    assert.deepEqual(revoke, { type: "REVOKE_ACCESS", channelId: "notes/secret", clientId: "former-member" });
});

test("classifyErrorCode: ENCRYPTION_REQUIRED is event-fatal but not a limit", () => {
    const c = classifyErrorCode("ENCRYPTION_REQUIRED");
    assert.equal(c.isLimit, false);
    assert.equal(c.isTransient, false);
    assert.equal(c.isEventFatal, true);
});
